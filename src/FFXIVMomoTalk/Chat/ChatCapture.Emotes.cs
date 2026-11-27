using Dalamud.Game.Text;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace FFXIVMomoTalk.Chat;

internal sealed partial class ChatCapture
{
    internal static bool TryClassifyTargetedEmote(
        XivChatType logKind,
        XivChatRelationKind sourceKind,
        XivChatRelationKind targetKind,
        out bool outgoing)
    {
        if (logKind != XivChatType.StandardEmote)
        {
            outgoing = false;
            return false;
        }

        if (targetKind == XivChatRelationKind.LocalPlayer && IsOtherPlayerRelation(sourceKind))
        {
            outgoing = false;
            return true;
        }

        if (sourceKind == XivChatRelationKind.LocalPlayer && IsOtherPlayerRelation(targetKind))
        {
            outgoing = true;
            return true;
        }

        outgoing = false;
        return false;
    }

    internal static TellPeer? ReadEmotePeer(
        ReadOnlySeString sender,
        ReadOnlySeString message,
        PlayerIdentity? localPlayer = null)
    {
        CollectEmoteCandidates(sender, message, localPlayer, out var senderCandidates, out var messageCandidates);
        return SelectUniqueEmoteCandidate(senderCandidates, messageCandidates);
    }

    internal TellPeer? TryResolveEmotePeer(
        ReadOnlySeString sender,
        ReadOnlySeString message,
        string body,
        bool outgoing,
        PlayerIdentity? localPlayer)
    {
        CollectEmoteCandidates(sender, message, localPlayer, out var senderCandidates, out var messageCandidates);
        var allCandidates = new List<TellPeer>(senderCandidates.Count + messageCandidates.Count);
        foreach (var candidate in senderCandidates)
            AddUniquePeer(allCandidates, candidate);
        foreach (var candidate in messageCandidates)
            AddUniquePeer(allCandidates, candidate);

        foreach (var candidate in allCandidates)
        {
            if (TryConsumeObservedLog(
                entry => TryGetObservedEmotePeer(entry, outgoing, localPlayer, out var peer)
                    && peer.WorldId == candidate.WorldId
                    && SocialSnapshot.MatchesCharacterName(peer.Name, candidate.Name, allowAbbreviation: false),
                fallbackMatch: null,
                out _))
            {
                return candidate;
            }
        }

        if (SelectUniqueEmoteCandidate(senderCandidates, messageCandidates) is { } uniqueCandidate)
            return uniqueCandidate;

        if (TryConsumeObservedLog(
            entry => TryGetObservedEmotePeer(entry, outgoing, localPlayer, out var peer)
                && body.Contains(peer.Name, StringComparison.OrdinalIgnoreCase),
            entry => TryGetObservedEmotePeer(entry, outgoing, localPlayer, out _),
            out var observed)
            && observed is not null
            && TryGetObservedEmotePeer(observed, outgoing, localPlayer, out var matchedPeer))
        {
            return new TellPeer(matchedPeer.Name, matchedPeer.WorldId, null, HasPlayerPayload: true);
        }

        return null;
    }

    private static bool IsOtherPlayerRelation(XivChatRelationKind kind) =>
        kind is XivChatRelationKind.OtherPlayer or XivChatRelationKind.PartyMember or XivChatRelationKind.AllianceMember;

    private static void CollectEmoteCandidates(
        ReadOnlySeString sender,
        ReadOnlySeString message,
        PlayerIdentity? localPlayer,
        out List<TellPeer> senderCandidates,
        out List<TellPeer> messageCandidates)
    {
        senderCandidates = CollectValidPlayerLinks(sender, localPlayer);
        messageCandidates = CollectValidPlayerLinks(message, localPlayer);
    }

    private static List<TellPeer> CollectValidPlayerLinks(ReadOnlySeString seString, PlayerIdentity? localPlayer)
    {
        var candidates = new List<TellPeer>();
        foreach (var payload in seString)
        {
            if (payload.Type != ReadOnlySePayloadType.Macro
                || payload.MacroCode != MacroCode.Link
                || !payload.TryGetExpression(out var linkTypeExpr, out _, out var worldExpr, out _, out var nameExpr)
                || !linkTypeExpr.TryGetUInt(out var linkType)
                || linkType != (uint)LinkMacroPayloadType.Character
                || !worldExpr.TryGetUInt(out var worldId)
                || !nameExpr.TryGetString(out var nameSe))
            {
                continue;
            }

            var name = nameSe.ExtractText().Trim();
            if (!IsValidGlobalPlayerName(name) || IsLocalPlayerIdentity(name, worldId, localPlayer))
                continue;

            AddUniquePeer(candidates, new TellPeer(name, worldId, null, HasPlayerPayload: true));
        }

        return candidates;
    }

    private static void AddUniquePeer(List<TellPeer> peers, TellPeer candidate)
    {
        foreach (var existing in peers)
        {
            if (existing.WorldId == candidate.WorldId
                && StringComparer.OrdinalIgnoreCase.Equals(existing.Name, candidate.Name))
            {
                return;
            }
        }

        peers.Add(candidate);
    }

    private static TellPeer? SelectUniqueEmoteCandidate(
        List<TellPeer> senderCandidates,
        List<TellPeer> messageCandidates)
    {
        if (senderCandidates.Count == 1)
            return senderCandidates[0];

        if (senderCandidates.Count == 0 && messageCandidates.Count == 1)
            return messageCandidates[0];

        return null;
    }

    private static bool IsLocalPlayerIdentity(string name, uint worldId, PlayerIdentity? localPlayer) =>
        localPlayer is not null
        && worldId == localPlayer.WorldId
        && StringComparer.OrdinalIgnoreCase.Equals(name, localPlayer.Name.Trim());

    private static bool TryGetObservedEmotePeer(
        ObservedLogEntry entry,
        bool outgoing,
        PlayerIdentity? localPlayer,
        out ObservedPlayer peer)
    {
        if (entry.Category != SystemLogCategory.StandardEmote
            || entry.SourcePlayer is not { } source
            || entry.TargetPlayer is not { } target)
        {
            peer = default;
            return false;
        }

        ObservedPlayer local;
        if (outgoing)
        {
            local = source;
            peer = target;
        }
        else
        {
            local = target;
            peer = source;
        }

        if (localPlayer is not null && !MatchesLocalObservedPlayer(local, localPlayer))
        {
            peer = default;
            return false;
        }

        return true;
    }
}
