using FFXIVMomoTalk.Chat;

namespace FFXIVMomoTalk.Windows;

internal sealed class ConversationHistory
{
    private readonly MessageStore store;
    private readonly ulong owner;
    private readonly ConversationKey conversation;
    private readonly List<ChatMessage> messages = new();
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

    public IReadOnlyList<ChatMessage> Messages => messages;

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
            updates = store.ReadUpdatesAsync(owner, conversation, latestId, currentRejectionRevision != rejectionRevision);
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
        foreach (var message in incoming)
        {
            latestId = Math.Max(latestId, message.Id);

            if (earliest is { } cutoff && message.Cursor.CompareTo(cutoff) < 0)
                continue;

            if (loadedIds.Add(message.Id))
            {
                messages.Add(message);
                AddedMessages = true;
            }
            else if (message.Rejection is not null)
            {
                var index = messages.FindIndex(existing => existing.Id == message.Id);
                if (messages[index].Rejection == message.Rejection)
                    continue;

                messages[index] = message;
                AddedMessages = true;
            }
        }

        if (AddedMessages)
            messages.Sort((left, right) => left.Cursor.CompareTo(right.Cursor));
    }
}
