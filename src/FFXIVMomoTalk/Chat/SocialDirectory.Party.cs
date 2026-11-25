using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace FFXIVMomoTalk.Chat;

internal sealed unsafe partial class SocialDirectory
{
    private ActiveParty? ReadActiveParty(PlayerSession session)
    {
        var worlds = data.GetExcelSheet<World>();
        var mainMembers = ReadMainGroupParty(session, worlds);
        var crossMembers = InfoProxyCrossRealm.IsCrossRealmParty()
            ? ReadCrossRealmParty(session, worlds)
            : null;

        var selected = crossMembers is not null && crossMembers.Count > mainMembers.Count
            ? crossMembers
            : mainMembers;

        if (selected.Count < 2 || !selected.ContainsKey(session.ContentId))
            return null;

        var ordered = selected.Values
            .OrderBy(m => m.ContentId == session.ContentId ? 0 : 1)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.ContentId)
            .ToArray();
        return new ActiveParty(ConversationKey.BuildStaticRosterKey(selected.Keys), ordered);
    }

    private static Dictionary<ulong, PlayerIdentity> ReadMainGroupParty(PlayerSession session, ExcelSheet<World> worlds)
    {
        var members = new Dictionary<ulong, PlayerIdentity>();
        var groupManager = GroupManager.Instance();
        if (groupManager is null)
            return members;

        var group = groupManager->GetGroup();
        if (group is null || group->MemberCount == 0)
            return members;

        var count = Math.Min((int)group->MemberCount, group->PartyMembers.Length);
        for (var i = 0; i < count; i++)
        {
            ref var member = ref group->PartyMembers[i];
            if (member.ContentId == 0)
                continue;

            if (member.ContentId == session.ContentId)
            {
                members[session.ContentId] = session.Player;
                continue;
            }

            var name = member.NameString.Trim();
            if (name.Length == 0)
                continue;

            var worldId = (uint)member.HomeWorld;
            members[member.ContentId] = new PlayerIdentity(member.ContentId, name, worldId, ResolveWorldName(worlds, worldId));
        }

        return members;
    }

    private static Dictionary<ulong, PlayerIdentity> ReadCrossRealmParty(PlayerSession session, ExcelSheet<World> worlds)
    {
        var members = new Dictionary<ulong, PlayerIdentity>();
        var crossRealm = InfoProxyCrossRealm.Instance();
        if (crossRealm is null)
            return members;

        var groupIndex = crossRealm->LocalPlayerGroupIndex;
        if (groupIndex >= crossRealm->CrossRealmGroups.Length)
            return members;

        ref var group = ref crossRealm->CrossRealmGroups[groupIndex];
        var count = Math.Min((int)group.GroupMemberCount, group.GroupMembers.Length);
        for (var i = 0; i < count; i++)
        {
            ref var member = ref group.GroupMembers[i];
            if (member.ContentId == 0)
                continue;

            if (member.ContentId == session.ContentId)
            {
                members[session.ContentId] = session.Player;
                continue;
            }

            var name = member.NameString.Trim();
            if (name.Length == 0)
                continue;

            var worldId = member.HomeWorld > 0 ? (uint)(ushort)member.HomeWorld : 0u;
            members[member.ContentId] = new PlayerIdentity(member.ContentId, name, worldId, ResolveWorldName(worlds, worldId));
        }

        return members;
    }

    private void SyncPartyMembers(PlayerSession owner, ActiveParty party)
    {
        List<PlayerIdentity>? changed = null;
        foreach (var member in party.Members)
        {
            if (syncedPartyMembers.TryGetValue(member.ContentId, out var prev) && prev == member)
                continue;

            syncedPartyMembers[member.ContentId] = member;
            changed ??= [];
            changed.Add(member);
        }

        if (changed is null || statics.IncompatibleOwner == owner.ContentId)
            return;

        ObserveTask(statics.SyncMemberIdentitiesAsync(owner.ContentId, changed), "Failed to sync static member names.");
    }

    private static bool PartyChanged(ActiveParty? previous, ActiveParty? next)
    {
        if (previous is null && next is null)
            return false;
        if (previous is null || next is null)
            return true;
        if (!StringComparer.Ordinal.Equals(previous.RosterKey, next.RosterKey)
            || previous.Members.Count != next.Members.Count)
            return true;

        for (var i = 0; i < previous.Members.Count; i++)
        {
            if (previous.Members[i] != next.Members[i])
                return true;
        }

        return false;
    }
}
