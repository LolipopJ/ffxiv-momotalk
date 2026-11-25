namespace FFXIVMomoTalk.Chat;

internal sealed partial class SocialDirectory
{
    public Task<StaticGroup?> CreateStaticAsync(PlayerSession owner, ActiveParty party, string rawName)
    {
        var trimmed = rawName.Trim();
        var snapshot = Current;
        if (trimmed.Length == 0 || !ReferenceEquals(snapshot.Session, owner))
            return Task.FromResult<StaticGroup?>(null);

        if (snapshot.Statics?.GetValueOrDefault(party.RosterKey) is { } existing)
            return Task.FromResult<StaticGroup?>(existing);

        var group = new StaticGroup(
            party.RosterKey,
            trimmed,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            party.Members);

        return statics.CreateStaticAsync(owner.ContentId, group).ContinueWith<StaticGroup?>(
            completed => ConfirmStaticWrite(owner, completed.GetAwaiter().GetResult()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public Task<StaticGroup?> RenameStaticAsync(PlayerSession owner, string stableKey, string rawName)
    {
        var trimmed = rawName.Trim();
        var snapshot = Current;
        if (trimmed.Length == 0 || !ReferenceEquals(snapshot.Session, owner)
            || snapshot.Statics?.GetValueOrDefault(stableKey) is not { } existing)
            return Task.FromResult<StaticGroup?>(null);

        return statics.RenameStaticAsync(owner.ContentId, stableKey, trimmed).ContinueWith(
            completed => completed.GetAwaiter().GetResult()
                ? ConfirmStaticWrite(owner, existing with { Name = trimmed })
                : null,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private StaticGroup ConfirmStaticWrite(PlayerSession owner, StaticGroup group)
    {
        confirmedStatics.Enqueue((owner, group));
        ObserveTask(messages.RenameConversationAsync(owner.ContentId, group.Key, group.Name),
            "Failed to update static conversation name.");
        return group;
    }

    private void PollStatics(PlayerSession session)
    {
        if (staticsQuery is { IsCompleted: true } completed)
        {
            var owner = staticsQueryFor;
            staticsQuery = null;
            staticsQueryFor = null;

            if (ReferenceEquals(owner, session))
            {
                if (completed.IsCompletedSuccessfully && staticsRevision == statics.Revision)
                {
                    var loadedStatics = new Dictionary<string, StaticGroup>(completed.Result.Count, StringComparer.Ordinal);
                    foreach (var group in completed.Result)
                        loadedStatics[group.StableKey] = group;

                    Publish(Current with { Statics = loadedStatics });
                }
                else if (completed.Exception is { } exception)
                {
                    log.Error(exception, "Failed to read static groups.");
                }
            }
        }

        while (confirmedStatics.TryDequeue(out var confirmed))
        {
            if (!ReferenceEquals(confirmed.Owner, session))
                continue;

            var snapshot = Current;
            var next = snapshot.Statics is { } groups
                ? new Dictionary<string, StaticGroup>(groups, StringComparer.Ordinal)
                : new Dictionary<string, StaticGroup>(StringComparer.Ordinal);
            var group = confirmed.Group;
            next[group.StableKey] = next.TryGetValue(group.StableKey, out var currentGroup)
                ? currentGroup with { Name = group.Name }
                : group;
            Publish(snapshot with { Statics = next });
        }

        if (staticsQuery is null && staticsRevision != statics.Revision)
        {
            staticsRevision = statics.Revision;
            staticsQueryFor = session;
            staticsQuery = statics.ReadStaticsAsync(session.ContentId);
        }
    }
}
