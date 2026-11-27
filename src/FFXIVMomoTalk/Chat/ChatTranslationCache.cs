using System.Collections.Concurrent;

namespace FFXIVMomoTalk.Chat;

internal readonly record struct TranslationCacheKey(ulong OwnerContentId, long MessageId);

internal readonly record struct CachedMessageTranslation(string SourceBody, string Translation);

internal sealed class ChatTranslationCache(int maxEntries = ChatTranslationCache.DefaultMaxEntries)
{
    public const int DefaultMaxEntries = 1000;

    private readonly int capacity = Math.Max(1, maxEntries);
    private readonly Lock writeGate = new();
    private readonly ConcurrentDictionary<TranslationCacheKey, CachedMessageTranslation> entries = new();
    private readonly LinkedList<TranslationCacheKey> order = new();
    private readonly Dictionary<TranslationCacheKey, LinkedListNode<TranslationCacheKey>> nodes = new();

    public int Count => entries.Count;

    public string? TryGet(ulong ownerContentId, long messageId, string currentBody)
    {
        var key = new TranslationCacheKey(ownerContentId, messageId);
        return entries.TryGetValue(key, out var cached)
            && StringComparer.Ordinal.Equals(cached.SourceBody, currentBody)
            ? cached.Translation
            : null;
    }

    public void Set(ulong ownerContentId, long messageId, string sourceBody, string translation)
    {
        var key = new TranslationCacheKey(ownerContentId, messageId);
        var value = new CachedMessageTranslation(sourceBody, translation);
        lock (writeGate)
        {
            entries[key] = value;
            if (nodes.TryGetValue(key, out var existingNode))
            {
                order.Remove(existingNode);
                order.AddLast(existingNode);
            }
            else
            {
                nodes[key] = order.AddLast(key);
            }

            while (order.Count > capacity && order.First is { } oldest)
            {
                order.RemoveFirst();
                nodes.Remove(oldest.Value);
                entries.TryRemove(oldest.Value, out _);
            }
        }
    }

    public void Remove(ulong ownerContentId, long messageId)
    {
        var key = new TranslationCacheKey(ownerContentId, messageId);
        lock (writeGate)
        {
            entries.TryRemove(key, out _);
            if (nodes.Remove(key, out var existingNode))
                order.Remove(existingNode);
        }
    }

    public void Clear()
    {
        lock (writeGate)
        {
            entries.Clear();
            order.Clear();
            nodes.Clear();
        }
    }
}
