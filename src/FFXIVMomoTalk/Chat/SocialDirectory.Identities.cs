using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace FFXIVMomoTalk.Chat;

internal sealed unsafe partial class SocialDirectory
{
    // Must be called on the Dalamud framework thread.
    public PlayerIdentity? ResolveGroupSender(
        SocialSnapshot snapshot,
        XivChatType chatType,
        ConversationKind kind,
        TellPeer? peer,
        bool isLocalPlayer,
        out bool outgoing)
    {
        outgoing = isLocalPlayer;
        if (snapshot.Session is not { } session)
            return null;

        if (isLocalPlayer)
            return session.Player;

        if (peer is not { } parsed)
            return null;

        var name = parsed.Name.Trim();
        if (name.Length == 0)
            return null;

        var worlds = data.GetExcelSheet<World>();
        var worldId = parsed.WorldId;
        var worldName = worldId != 0 ? ResolveWorldName(worlds, worldId) : string.Empty;

        if (worldId == 0 && !string.IsNullOrWhiteSpace(parsed.World))
        {
            if (TryFindWorldByName(worlds, parsed.World.Trim(), out var matchedWorld))
            {
                worldId = matchedWorld.RowId;
                worldName = matchedWorld.Name.ToString();
            }
            else
            {
                worldName = parsed.World.Trim();
            }
        }

        // The game omits PlayerPayload for the local player's own chat lines and may abbreviate the display name.
        if (!parsed.HasPlayerPayload
            && (worldId == 0 || worldId == session.Player.WorldId)
            && SocialSnapshot.MatchesCharacterName(session.Player.Name, name, allowAbbreviation: true))
        {
            outgoing = true;
            return session.Player;
        }

        // In Static/Party, match against current party first before defaulting world.
        var lookupPeer = new TellPeer(name, worldId, worldName.Length > 0 ? worldName : null, parsed.HasPlayerPayload);
        if (kind == ConversationKind.Static && snapshot.ResolvePartyMember(lookupPeer) is { } partyMember)
        {
            outgoing = partyMember.ContentId == session.ContentId;
            return outgoing ? session.Player : partyMember;
        }

        if (worldId == 0 && (kind is ConversationKind.FreeCompany or ConversationKind.Linkshell or ConversationKind.Static))
        {
            worldId = session.Player.WorldId;
            worldName = session.Player.World;
            lookupPeer = new TellPeer(name, worldId, worldName, parsed.HasPlayerPayload);
        }

        if (SocialSnapshot.MatchesCharacterName(session.Player.Name, name, allowAbbreviation: !parsed.HasPlayerPayload)
            && (worldId == 0 || worldId == session.Player.WorldId))
        {
            outgoing = true;
            return session.Player;
        }

        if (snapshot.ResolvePartyMember(lookupPeer) is { } activePartyMatch)
        {
            outgoing = activePartyMatch.ContentId == session.ContentId;
            return outgoing ? session.Player : activePartyMatch;
        }

        if (snapshot.ResolveTellPeer(lookupPeer) is { } friendMatch)
            return friendMatch;

        var proxyCid = LookupMemberProxyContentId(kind, name, worldId);
        if (proxyCid != 0)
            return ResolveIdentityByContentId(snapshot, session, proxyCid, name, worldId, worldName, out outgoing);

        var logCid = LookupLogSourceContentId(chatType, name, worldId);
        if (logCid != 0)
            return ResolveIdentityByContentId(snapshot, session, logCid, name, worldId, worldName, out outgoing);

        return new PlayerIdentity(0, name, worldId, worldName);
    }

    private static PlayerIdentity ResolveIdentityByContentId(
        SocialSnapshot snapshot,
        PlayerSession session,
        ulong contentId,
        string fallbackName,
        uint worldId,
        string worldName,
        out bool outgoing)
    {
        if (contentId == session.ContentId)
        {
            outgoing = true;
            return session.Player;
        }

        outgoing = false;
        if (snapshot.Party?.Members.FirstOrDefault(member => member.ContentId == contentId) is { } partyMember)
            return partyMember;

        if (snapshot.Friends is not null && snapshot.Friends.TryGetValue(contentId, out var friend))
            return friend;

        return new PlayerIdentity(contentId, fallbackName, worldId, worldName);
    }

    private static ulong LookupMemberProxyContentId(ConversationKind kind, string name, uint worldId)
    {
        InfoProxyCommonList* proxy = null;
        switch (kind)
        {
            case ConversationKind.FreeCompany:
                var fc = InfoProxyFreeCompanyMember.Instance();
                if (fc is not null)
                    proxy = &fc->InfoProxyCommonList;
                break;
            case ConversationKind.Linkshell:
                var ls = InfoProxyLinkshellMember.Instance();
                if (ls is not null)
                    proxy = &ls->InfoProxyCommonList;
                break;
            case ConversationKind.CrossWorldLinkshell:
                var cwls = InfoProxyCrossWorldLinkshellMember.Instance();
                if (cwls is not null)
                    proxy = &cwls->InfoProxyCommonList;
                break;
        }

        return QueryCommonList(proxy, name, (ushort)worldId);
    }

    private static ulong QueryCommonList(InfoProxyCommonList* proxy, string name, ushort worldId) =>
        proxy is null || proxy->EntryCount == 0 || proxy->CharData is null
            ? 0
            : QueryCommonListSpan(proxy->CharDataSpan, name, worldId);

    private static ulong QueryCommonListSpan(
        ReadOnlySpan<InfoProxyCommonList.CharacterData> span,
        string name,
        ushort worldId)
    {
        ulong matched = 0;
        foreach (ref readonly var entry in span)
        {
            if (entry.ContentId == 0)
                continue;

            if (worldId != 0 && entry.HomeWorld != worldId)
                continue;

            if (!StringComparer.OrdinalIgnoreCase.Equals(entry.NameString.Trim(), name))
                continue;

            if (matched != 0 && matched != entry.ContentId)
                return 0;

            matched = entry.ContentId;
        }

        return matched;
    }

    private static ulong LookupLogSourceContentId(XivChatType chatType, string senderName, uint worldId)
    {
        var logModule = RaptureLogModule.Instance();
        if (logModule is null || logModule->MsgSourceArray is null)
            return 0;

        var length = logModule->MsgSourceArrayLength;
        if (length is <= 0 or > 4096)
            return 0;

        var currentIndex = (int)logModule->GetCurrentLogIndex();
        var minIndex = Math.Max(0, currentIndex - 4);
        var bestIndex = -1;
        ulong bestCid = 0;

        for (var i = 0; i < length; i++)
        {
            var entry = logModule->MsgSourceArray[i];
            if (entry.ContentId == 0
                || entry.LogMessageIndex < minIndex
                || entry.LogMessageIndex > currentIndex + 1
                || entry.ChatType != (short)chatType)
            {
                continue;
            }

            if (worldId != 0 && entry.World > 0 && (uint)(ushort)entry.World != worldId)
                continue;

            if (entry.LogMessageIndex < bestIndex)
                continue;

            if (logModule->GetLogMessageDetail(entry.LogMessageIndex, out var rawSender, out _, out _, out _, out _, out _)
                && rawSender.Length > 0)
            {
                var parsed = ChatCapture.ReadPeer(SeString.Parse(rawSender));
                if (parsed is null
                    || !SocialSnapshot.MatchesCharacterName(parsed.Value.Name, senderName, allowAbbreviation: true))
                {
                    continue;
                }
            }

            bestIndex = entry.LogMessageIndex;
            bestCid = entry.ContentId;
        }

        return bestCid;
    }

    private static string ResolveWorldName(ExcelSheet<World> worlds, uint worldId) =>
        worldId != 0 && worlds.TryGetRow(worldId, out var world) ? world.Name.ToString() : string.Empty;

    private static bool TryFindWorldByName(ExcelSheet<World> worlds, string name, out World matched)
    {
        foreach (var world in worlds)
        {
            if (world.IsPublic && StringComparer.OrdinalIgnoreCase.Equals(world.Name.ToString(), name))
            {
                matched = world;
                return true;
            }
        }

        matched = default;
        return false;
    }
}
