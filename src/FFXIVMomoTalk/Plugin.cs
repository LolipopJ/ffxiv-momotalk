using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVMomoTalk.Chat;
using FFXIVMomoTalk.Windows;

namespace FFXIVMomoTalk;

public sealed class Plugin : IAsyncDalamudPlugin
{
    private const string CommandName = "/momotalk";

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal static IPlayerState PlayerState { get; private set; } = null!;

    [PluginService]
    internal static IDataManager Data { get; private set; } = null!;

    [PluginService]
    internal static IGameInteropProvider Interop { get; private set; } = null!;

    [PluginService]
    internal static IChatGui Chat { get; private set; } = null!;

    [PluginService]
    internal static IContextMenu ContextMenu { get; private set; } = null!;

    [PluginService]
    internal static ITextureProvider Textures { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windows = new("FFXIVMomoTalk");
    private readonly Configuration config;
    private readonly MessageStore messages;
    private readonly NotesStore notes;
    private readonly StaticStore statics;
    private readonly CacheStore cache;
    private readonly SocialDirectory directory;
    private readonly ChatCapture capture;
    private readonly ChatSender sender;
    private readonly AvatarCache avatars;
    private readonly AiReplyService aiReply;
    private readonly PartyContextMenu partyContextMenu;
    private readonly MomoTalkWindow mainWindow;
    private readonly FloatingEntryWindow entryWindow;
    private bool attached;

    public Plugin()
    {
        var root = PluginInterface.GetPluginConfigDirectory();
        var charactersRoot = Path.Combine(root, "characters");
        config = Configuration.Load(PluginInterface, Log);
        messages = new MessageStore(charactersRoot);
        notes = new NotesStore(charactersRoot);
        statics = new StaticStore(charactersRoot);
        cache = new CacheStore(charactersRoot);
        directory = new SocialDirectory(ClientState, PlayerState, Data, Interop, cache, statics, messages, Log);
        capture = new ChatCapture(Chat, Framework, directory, messages, cache, Log);
        sender = new ChatSender(Framework, Chat, directory, capture, Log);
        avatars = new AvatarCache(Path.Combine(root, "avatars"), Textures, Log);
        aiReply = new AiReplyService(config, capture, messages, Log);
        mainWindow = new MomoTalkWindow(directory, messages, notes, statics, cache, capture, sender, avatars, aiReply, config, SaveConfig, Textures, Log);
        partyContextMenu = new PartyContextMenu(
            ContextMenu,
            directory,
            mainWindow.OpenCreateStaticModal,
            mainWindow.OpenStaticConversation);
        entryWindow = new FloatingEntryWindow(directory, config, SaveConfig, Textures, ToggleMainWindow);
    }

    public Task LoadAsync(CancellationToken cancellationToken) => Framework.RunOnFrameworkThread(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        attached = true;
        directory.Attach();
        capture.Attach();
        aiReply.Attach();
        sender.Attach();
        partyContextMenu.Attach();
        Framework.Update += Update;

        windows.AddWindow(entryWindow);
        windows.AddWindow(mainWindow);
        mainWindow.PreloadAssets();
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the FFXIV MomoTalk window.",
        });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
    });

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Framework.IsFrameworkUnloading)
                Detach();
            else
                await Framework.RunOnFrameworkThread(Detach).ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(
                messages.DisposeAsync().AsTask(),
                notes.DisposeAsync().AsTask(),
                statics.DisposeAsync().AsTask(),
                cache.DisposeAsync().AsTask(),
                avatars.DisposeAsync().AsTask(),
                aiReply.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
    }

    private void Detach()
    {
        if (!attached)
            return;

        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        CommandManager.RemoveHandler(CommandName);
        windows.RemoveAllWindows();

        Framework.Update -= Update;
        partyContextMenu.Dispose();
        sender.Dispose();
        aiReply.Detach();
        capture.Dispose();
        directory.Dispose();
    }

    private void Update(IFramework framework)
    {
        var snapshot = directory.Update();
        aiReply.SyncSession(snapshot.Session);
        sender.Update(snapshot.Session);
        capture.Update(snapshot);
    }

    private void OnCommand(string command, string arguments) => ToggleMainWindow();

    private void ToggleMainWindow() => mainWindow.Toggle();

    private void OpenMainUi()
    {
        mainWindow.IsOpen = true;
        mainWindow.BringToFront();
    }

    private void OpenConfigUi() => mainWindow.OpenSettings();

    private void SaveConfig() => config.Save(PluginInterface);
}
