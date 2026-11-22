using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using NetStone;
using NetStone.Search.Character;

namespace FFXIVMomoTalk.Chat;

internal sealed class AvatarCache(string directory, ITextureProvider textures, IPluginLog log) : IAsyncDisposable
{
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    private readonly Dictionary<PlayerKey, Lookup> lookups = new();
    private readonly SemaphoreSlim requests = new(1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly HttpClient images = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
        MaxResponseContentBufferSize = 1024 * 1024,
    };

    private Task<LodestoneClient>? client;

    public IDalamudTextureWrap? GetTexture(PlayerIdentity player)
    {
        if (!lookups.TryGetValue(player.Key, out var lookup))
        {
            lookup = new Lookup();
            lookups.Add(player.Key, lookup);
        }

        if (lookup.Work is { IsCompletedSuccessfully: true } completed)
        {
            var result = completed.Result;
            lookup.Path = result.Path;
            lookup.RefreshAfter = result.RefreshAfter;
            lookup.Work = null;
        }

        if (lookup.Work is null && DateTimeOffset.UtcNow >= lookup.RefreshAfter)
            lookup.Work = Task.Run(() => LoadAsync(player, shutdown.Token));

        return lookup.Path is { } path ? textures.GetFromFileAbsolute(path).GetWrapOrDefault() : null;
    }

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(lookups.Values.Select(lookup => lookup.Work ?? Task.FromResult(default(CacheResult))))
            .ConfigureAwait(false);
        images.Dispose();
        requests.Dispose();
        shutdown.Dispose();

        if (client is not null)
            _ = DisposeClientAsync(client);
    }

    internal static bool Matches(PlayerIdentity player, string name, string server)
    {
        var dataCenter = server.IndexOf('[');
        var homeWorld = dataCenter < 0 ? server : server[..dataCenter];
        return PlayerKey.From(name, player.WorldId) == player.Key
            && StringComparer.OrdinalIgnoreCase.Equals(homeWorld.Trim(), player.World);
    }

    internal static string CacheKey(PlayerKey player) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{player.WorldId}:{player.Name}")));

    private async Task<CacheResult> LoadAsync(PlayerIdentity player, CancellationToken stopping)
    {
        var key = CacheKey(player.Key);
        var metadataPath = Path.Combine(directory, $"{key}.json");
        var imagePath = Path.Combine(directory, $"{key}.jpg");
        var current = await ReadCacheAsync(metadataPath, imagePath, stopping).ConfigureAwait(false);
        if (current is { } fresh && DateTimeOffset.UtcNow < fresh.FetchedAt + CacheAge)
            return new CacheResult(imagePath, fresh.FetchedAt + CacheAge);

        try
        {
            await requests.WaitAsync(stopping).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                if (client is null || client.IsFaulted || client.IsCanceled)
                    client = LodestoneClient.GetClientAsync();

                var api = await client.WaitAsync(timeout.Token).ConfigureAwait(false);
                if (await FindAvatarAsync(api, player, timeout.Token).ConfigureAwait(false) is { } avatar)
                {
                    var bytes = await images.GetByteArrayAsync(avatar.Url, timeout.Token).ConfigureAwait(false);
                    Directory.CreateDirectory(directory);
                    await File.WriteAllBytesAsync(imagePath + ".tmp", bytes, timeout.Token).ConfigureAwait(false);
                    File.Move(imagePath + ".tmp", imagePath, overwrite: true);

                    var fetchedAt = DateTimeOffset.UtcNow;
                    var metadata = new CacheEntry(avatar.Id, fetchedAt, Path.GetFileName(imagePath));
                    await File.WriteAllTextAsync(metadataPath + ".tmp", JsonSerializer.Serialize(metadata), timeout.Token)
                        .ConfigureAwait(false);
                    File.Move(metadataPath + ".tmp", metadataPath, overwrite: true);
                    return new CacheResult(imagePath, fetchedAt + CacheAge);
                }
            }
            finally
            {
                requests.Release();
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            log.Debug("Lodestone avatar lookup failed ({FailureType}).", exception.GetType().Name);
        }

        return new CacheResult(current is not null ? imagePath : null, DateTimeOffset.UtcNow + RetryDelay);
    }

    private async Task<CacheEntry?> ReadCacheAsync(string metadataPath, string imagePath, CancellationToken stopping)
    {
        try
        {
            if (!File.Exists(metadataPath) || !File.Exists(imagePath))
                return null;

            var json = await File.ReadAllTextAsync(metadataPath, stopping).ConfigureAwait(false);
            var entry = JsonSerializer.Deserialize<CacheEntry>(json);
            return entry is { LodestoneId.Length: > 0 } && entry.Image == Path.GetFileName(imagePath) ? entry : null;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Debug("Lodestone avatar cache could not be read ({FailureType}).", exception.GetType().Name);
            return null;
        }
    }

    private static async Task<AvatarMatch?> FindAvatarAsync(LodestoneClient api, PlayerIdentity player, CancellationToken token)
    {
        var query = new CharacterSearchQuery { CharacterName = player.Name, World = player.World };
        var page = await api.SearchCharacter(query).WaitAsync(token).ConfigureAwait(false);
        string? matchedId = null;
        Uri? matchedAvatar = null;
        var checkedIds = new HashSet<string>(StringComparer.Ordinal);

        while (page is not null)
        {
            foreach (var entry in page.Results)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(player.Name, entry.Name.Trim())
                    || entry.Id is not { } id || !checkedIds.Add(id))
                    continue;

                var profile = await api.GetCharacter(id).WaitAsync(token).ConfigureAwait(false);
                if (profile is null || !Matches(player, profile.Name, profile.Server))
                    continue;

                if (matchedId is not null)
                    return null;

                matchedId = id;
                matchedAvatar = profile.Avatar ?? profile.Portrait;
            }

            if (page.CurrentPage >= page.NumPages)
            {
                return matchedId is not null && matchedAvatar is { IsAbsoluteUri: true, Scheme: "https" } url
                    && url.Host.EndsWith(".finalfantasyxiv.com", StringComparison.OrdinalIgnoreCase)
                    ? new AvatarMatch(matchedId, url)
                    : null;
            }

            page = await page.GetNextPage().WaitAsync(token).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task DisposeClientAsync(Task<LodestoneClient> initialization)
    {
        try
        {
            (await initialization.ConfigureAwait(false)).Dispose();
        }
        catch (Exception)
        {
            // Failed initialization never produced a client to dispose.
        }
    }

    private sealed record CacheEntry(string LodestoneId, DateTimeOffset FetchedAt, string Image);

    private sealed record AvatarMatch(string Id, Uri Url);

    private readonly record struct CacheResult(string? Path, DateTimeOffset RefreshAfter);

    private sealed class Lookup
    {
        public Task<CacheResult>? Work { get; set; }

        public string? Path { get; set; }

        public DateTimeOffset RefreshAfter { get; set; }
    }
}
