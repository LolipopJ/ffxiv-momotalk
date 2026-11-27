using System.Text;
using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed class ConversationHistory
{
    private readonly MessageStore store;
    private readonly ulong owner;
    private readonly ConversationKey conversation;
    private readonly List<ChatMessage> messages = new();
    private readonly List<ChatMessage> bubbles = new();
    private readonly HashSet<long> loadedIds = new();
    private Task<MessagePage>? page;
    private Task<IReadOnlyList<ChatMessage>>? updates;
    private bool initialized;
    private bool updateFailed;
    private long historyRevision;
    private long latestId;
    private long rejectionRevision;

    public ConversationHistory(MessageStore store, ulong owner, ConversationKey conversation)
    {
        this.store = store;
        this.owner = owner;
        this.conversation = conversation;
        LoadPage();
    }

    public IReadOnlyList<ChatMessage> Messages => bubbles;

    public bool HasOlder { get; private set; }

    public bool IsLoading => page is not null;

    public bool ReadFailed { get; private set; }

    public bool RevealLatest { get; private set; }

    public bool AddedMessages { get; private set; }

    public long? PrependAnchor { get; private set; }

    public void LoadPage()
    {
        if (page is not null)
            return;

        ReadFailed = false;
        if (!initialized)
        {
            historyRevision = store.Revision;
            rejectionRevision = store.RejectionRevision;
        }

        MessageCursor? before = messages.Count > 0 ? messages[0].Cursor : null;
        page = store.ReadPageAsync(owner, conversation, before);
    }

    public void Update()
    {
        if (page is { IsCompleted: true } loaded)
        {
            page = null;
            if (loaded.IsCompletedSuccessfully)
            {
                var result = loaded.Result;
                if (messages.Count > 0)
                    PrependAnchor = messages[0].Id;
                else if (!initialized)
                    RevealLatest = true;

                messages.InsertRange(0, result.Messages.Where(message => loadedIds.Add(message.Id)));
                RebuildBubbles();
                HasOlder = result.HasOlder;

                if (!initialized && messages.Count > 0)
                    latestId = messages.Max(message => message.Id);

                initialized = true;
            }
            else
            {
                _ = loaded.Exception;
                ReadFailed = true;
            }
        }

        if (updates is { IsCompleted: true } refreshing)
        {
            updates = null;
            if (refreshing.IsCompletedSuccessfully)
            {
                ApplyUpdates(refreshing.Result);
            }
            else
            {
                _ = refreshing.Exception;
                updateFailed = true;
                ReadFailed = true;
            }
        }

        if (!ReadFailed && store.Revision != historyRevision && initialized && page is null && updates is null)
        {
            historyRevision = store.Revision;
            var currentRejectionRevision = store.RejectionRevision;
            var latestDisplayedId = bubbles.Count == 0 ? 0 : bubbles[^1].Id;
            updates = store.ReadUpdatesAsync(owner, conversation, latestId, latestDisplayedId,
                currentRejectionRevision != rejectionRevision);
            rejectionRevision = currentRejectionRevision;
        }
    }

    public void Retry()
    {
        if (updateFailed)
        {
            updateFailed = false;
            ReadFailed = false;
            historyRevision = -1;
            rejectionRevision = -1;
        }
        else
        {
            LoadPage();
        }
    }

    public void ClearScrollRequests()
    {
        RevealLatest = false;
        AddedMessages = false;
        PrependAnchor = null;
    }

    private void ApplyUpdates(IReadOnlyList<ChatMessage> incoming)
    {
        MessageCursor? earliest = HasOlder && messages.Count > 0 ? messages[0].Cursor : null;
        var changed = false;
        foreach (var message in incoming)
        {
            latestId = Math.Max(latestId, message.Id);

            if (earliest is { } cutoff && message.Cursor.CompareTo(cutoff) < 0)
                continue;

            if (loadedIds.Add(message.Id))
            {
                messages.Add(message);
            }
            else
            {
                var index = messages.FindIndex(existing => existing.Id == message.Id);
                if (index < 0 || messages[index] == message)
                    continue;

                messages[index] = message;
            }

            changed = true;
        }

        if (changed)
        {
            AddedMessages = true;
            messages.Sort((left, right) => left.Cursor.CompareTo(right.Cursor));
            RebuildBubbles();
        }
    }

    private void RebuildBubbles()
    {
        bubbles.Clear();
        for (var index = 0; index < messages.Count; index++)
        {
            var first = messages[index];
            StringBuilder? mergedBody = null;
            if (first is { Outgoing: true, Rejection: null, Kind: MessageKind.Chat })
            {
                while (index + 1 < messages.Count
                       && messages[index + 1] is { Outgoing: true, Rejection: null, Kind: MessageKind.Chat } next
                       && MessageStore.ShouldMergeInterval(messages[index].SentAt, next.SentAt))
                {
                    mergedBody ??= new StringBuilder(first.Body);
                    mergedBody.Append('\n').Append(next.Body);
                    if (PrependAnchor == next.Id)
                        PrependAnchor = first.Id;
                    index++;
                }
            }

            bubbles.Add(mergedBody is null ? first : first with { Body = mergedBody.ToString() });
        }
    }
}
