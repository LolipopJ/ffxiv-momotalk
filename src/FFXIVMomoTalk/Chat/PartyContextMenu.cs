using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;

namespace FFXIVMomoTalk.Chat;

internal sealed class PartyContextMenu(
    IContextMenu contextMenu,
    SocialDirectory directory,
    Action<ActiveParty> onCreateStatic,
    Action<StaticGroup> onOpenStatic) : IDisposable
{
    public void Attach() => contextMenu.OnMenuOpened += OnMenuOpened;

    public void Dispose() => contextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Default)
            return;

        if (args.AddonName is not ("_PartyList" or "PartyMemberList" or "SocialList"))
            return;

        var snapshot = directory.Update(rereadSocial: true);
        if (snapshot.Session is null || snapshot.Party is not { } party || snapshot.Statics is null)
            return;

        if (args.AddonName == "SocialList" && !IsTargetInParty(args.Target, party))
            return;

        if (snapshot.FindMatchedStatic() is { } matched)
        {
            args.AddMenuItem(new MenuItem
            {
                Name = "Open Static Group",
                UseDefaultPrefix = true,
                OnClicked = _ => onOpenStatic(matched),
            });
            return;
        }

        args.AddMenuItem(new MenuItem
        {
            Name = "Create Static Group",
            UseDefaultPrefix = true,
            OnClicked = _ => onCreateStatic(party),
        });
    }

    private static bool IsTargetInParty(MenuTarget target, ActiveParty party)
    {
        if (target is not MenuTargetDefault playerTarget)
            return false;

        if (playerTarget.TargetContentId != 0)
            return party.Members.Any(member => member.ContentId == playerTarget.TargetContentId);

        if (string.IsNullOrWhiteSpace(playerTarget.TargetName))
            return false;

        var targetName = playerTarget.TargetName.Trim();
        var targetWorldId = playerTarget.TargetHomeWorld.RowId;
        return party.Members.Any(member =>
            StringComparer.OrdinalIgnoreCase.Equals(member.Name.Trim(), targetName)
            && (targetWorldId == 0 || member.WorldId == targetWorldId));
    }
}
