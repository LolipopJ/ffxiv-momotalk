using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using NetStone;
using NetStone.Search.Character;

namespace FFXIVMomoTalk.Chat;

internal sealed class AvatarCache(string directory, ITextureProvider textures, IPluginLog log) : IAsyncDisposable
{
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    private readonly Dictionary<ulong, Lookup> lookups = new();
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
        if (!lookups.TryGetValue(player.ContentId, out var lookup))
        {
            lookup = new Lookup();
            lookups.Add(player.ContentId, lookup);
        }

        if (lookup.Work is { IsCompletedSuccessfully: true } completed)
        {
            var result = completed.Result;
            lookup.Publish(result.Path, result.LodestoneId);
            lookup.RefreshAfter = result.RefreshAfter;
            lookup.Work = null;
        }

        if (lookup.Work is null && DateTimeOffset.UtcNow >= lookup.RefreshAfter)
            lookup.Work = Task.Run(() => LoadAsync(player, lookup, shutdown.Token));

        return lookup.Path is { } path ? textures.GetFromFileAbsolute(path).GetWrapOrDefault() : null;
    }

    public string GetLodestoneUrl(PlayerIdentity player)
    {
        if (lookups.TryGetValue(player.ContentId, out var lookup) && lookup.LodestoneId is { Length: > 0 } id)
            return $"https://na.finalfantasyxiv.com/lodestone/character/{id}/";

        return $"https://na.finalfantasyxiv.com/lodestone/character/?q={Uri.EscapeDataString(player.Name)}&worldname={Uri.EscapeDataString(player.World)}";
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
        return StringComparer.OrdinalIgnoreCase.Equals(name.Trim(), player.Name.Trim())
            && StringComparer.OrdinalIgnoreCase.Equals(homeWorld.Trim(), player.World);
    }

    private async Task<CacheResult> LoadAsync(PlayerIdentity player, Lookup lookup, CancellationToken stopping)
    {
        var prefix = $"{player.ContentId:X16}";
        var metadataPath = Path.Combine(directory, $"{prefix}.json");
        var current = await ReadCacheAsync(metadataPath, stopping).ConfigureAwait(false);
        var currentImagePath = current is not null ? Path.Combine(directory, current.Image) : null;

        if (current is not null && currentImagePath is not null)
        {
            lookup.Publish(currentImagePath, current.LodestoneId);
            if (DateTimeOffset.UtcNow < current.FetchedAt + CacheAge)
                return new CacheResult(currentImagePath, current.LodestoneId, current.FetchedAt + CacheAge);
        }

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
                var avatar = await ResolveAvatarAsync(api, player, current?.LodestoneId, timeout.Token).ConfigureAwait(false);
                if (avatar is not null)
                    return await FetchAndStoreAsync(prefix, metadataPath, current, currentImagePath, avatar, timeout.Token)
                        .ConfigureAwait(false);
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

        return new CacheResult(currentImagePath, current?.LodestoneId, DateTimeOffset.UtcNow + RetryDelay);
    }

    private async Task<CacheResult> FetchAndStoreAsync(
        string prefix,
        string metadataPath,
        CacheEntry? current,
        string? currentImagePath,
        AvatarMatch avatar,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, avatar.Url);
        var sameUrl = current is not null && StringComparer.Ordinal.Equals(current.AvatarUrl, avatar.Url.AbsoluteUri);
        if (sameUrl && current is not null)
        {
            if (!string.IsNullOrEmpty(current.ETag) && EntityTagHeaderValue.TryParse(current.ETag, out var etag))
                request.Headers.IfNoneMatch.Add(etag);

            if (current.LastModified is { } lastModified)
                request.Headers.IfModifiedSince = lastModified;
        }

        using var response = await images.SendAsync(request, HttpCompletionOption.ResponseContentRead, token)
            .ConfigureAwait(false);

        var fetchedAt = DateTimeOffset.UtcNow;
        var nextETag = response.Headers.ETag?.ToString() ?? (sameUrl ? current?.ETag : null);
        var nextLastModified = response.Content.Headers.LastModified ?? (sameUrl ? current?.LastModified : null);

        if (response.StatusCode == HttpStatusCode.NotModified && current is not null && currentImagePath is not null)
        {
            var renewed = current with
            {
                LodestoneId = avatar.Id,
                FetchedAt = fetchedAt,
                AvatarUrl = avatar.Url.AbsoluteUri,
                ETag = nextETag,
                LastModified = nextLastModified,
            };
            await WriteMetadataAsync(metadataPath, renewed, token).ConfigureAwait(false);
            return new CacheResult(currentImagePath, avatar.Id, fetchedAt + CacheAge);
        }

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        var hash8 = Convert.ToHexString(SHA256.HashData(bytes))[..8];
        var nextFileName = $"{prefix}-{hash8}.jpg";
        var nextImagePath = Path.Combine(directory, nextFileName);

        Directory.CreateDirectory(directory);
        if (!File.Exists(nextImagePath))
        {
            var tempPath = nextImagePath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, bytes, token).ConfigureAwait(false);
            File.Move(tempPath, nextImagePath, overwrite: true);
        }

        var metadata = new CacheEntry(avatar.Id, fetchedAt, nextFileName, avatar.Url.AbsoluteUri, nextETag, nextLastModified);
        await WriteMetadataAsync(metadataPath, metadata, token).ConfigureAwait(false);

        if (currentImagePath is not null && !StringComparer.OrdinalIgnoreCase.Equals(currentImagePath, nextImagePath))
            TryDeleteOldImage(currentImagePath);

        return new CacheResult(nextImagePath, avatar.Id, fetchedAt + CacheAge);
    }

    private async Task WriteMetadataAsync(string metadataPath, CacheEntry entry, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var tempPath = metadataPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(entry), token).ConfigureAwait(false);
        File.Move(tempPath, metadataPath, overwrite: true);
    }

    private async Task<CacheEntry?> ReadCacheAsync(string metadataPath, CancellationToken stopping)
    {
        try
        {
            if (!File.Exists(metadataPath))
                return null;

            var json = await File.ReadAllTextAsync(metadataPath, stopping).ConfigureAwait(false);
            var entry = JsonSerializer.Deserialize<CacheEntry>(json);
            if (entry is not { LodestoneId.Length: > 0, Image.Length: > 0 })
                return null;

            if (!StringComparer.Ordinal.Equals(Path.GetFileName(entry.Image), entry.Image))
                return null;

            return File.Exists(Path.Combine(directory, entry.Image)) ? entry : null;
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

    private static async Task<AvatarMatch?> ResolveAvatarAsync(
        LodestoneClient api,
        PlayerIdentity player,
        string? cachedLodestoneId,
        CancellationToken token)
    {
        if (!string.IsNullOrEmpty(cachedLodestoneId))
        {
            var profile = await api.GetCharacter(cachedLodestoneId).WaitAsync(token).ConfigureAwait(false);
            if (profile is not null && ValidateAvatarUrl(profile.Avatar ?? profile.Portrait) is { } directUrl)
                return new AvatarMatch(cachedLodestoneId, directUrl);
        }

        return await FindAvatarAsync(api, player, token).ConfigureAwait(false);
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
                matchedAvatar = ValidateAvatarUrl(profile.Avatar ?? profile.Portrait);
            }

            if (page.CurrentPage >= page.NumPages)
                return matchedId is not null && matchedAvatar is not null ? new AvatarMatch(matchedId, matchedAvatar) : null;

            page = await page.GetNextPage().WaitAsync(token).ConfigureAwait(false);
        }

        return null;
    }

    private static Uri? ValidateAvatarUrl(Uri? candidate) =>
        candidate is { IsAbsoluteUri: true, Scheme: "https" } url
        && url.Host.EndsWith(".finalfantasyxiv.com", StringComparison.OrdinalIgnoreCase)
            ? url
            : null;

    private static void TryDeleteOldImage(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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

    private sealed record CacheEntry(
        string LodestoneId,
        DateTimeOffset FetchedAt,
        string Image,
        string? AvatarUrl,
        string? ETag,
        DateTimeOffset? LastModified);

    private sealed record AvatarMatch(string Id, Uri Url);

    private readonly record struct CacheResult(string? Path, string? LodestoneId, DateTimeOffset RefreshAfter);

    private sealed class Lookup
    {
        private volatile string? path;
        private volatile string? lodestoneId;

        public Task<CacheResult>? Work { get; set; }

        public string? Path => path;

        public string? LodestoneId => lodestoneId;

        public DateTimeOffset RefreshAfter { get; set; }

        public void Publish(string? imagePath, string? id)
        {
            path = imagePath;
            if (!string.IsNullOrEmpty(id))
                lodestoneId = id;
        }
    }
}
