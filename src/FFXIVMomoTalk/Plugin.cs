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
    internal static ITextureProvider Textures { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windows = new("FFXIVMomoTalk");
    private readonly Configuration config;
    private readonly FriendDirectory directory;
    private readonly HistoryStore history;
    private readonly ChatCapture capture;
    private readonly TellSender sender;
    private readonly AvatarCache avatars;
    private readonly MomoTalkWindow mainWindow;
    private readonly FloatingEntryWindow entryWindow;
    private bool attached;

    public Plugin()
    {
        var root = PluginInterface.GetPluginConfigDirectory();
        config = Configuration.Load(PluginInterface, Log);
        history = new HistoryStore(Path.Combine(root, "history"));
        directory = new FriendDirectory(ClientState, PlayerState, Data, Interop, history, Log);
        capture = new ChatCapture(Chat, Framework, directory, history, Log);
        sender = new TellSender(Framework, Chat, directory, capture, Log);
        avatars = new AvatarCache(Path.Combine(root, "avatars"), Textures, Log);
        mainWindow = new MomoTalkWindow(directory, history, capture, sender, avatars, config, SaveConfig, Textures);
        entryWindow = new FloatingEntryWindow(directory, config, SaveConfig, Textures, ToggleMainWindow);
    }

    public Task LoadAsync(CancellationToken cancellationToken) => Framework.RunOnFrameworkThread(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        attached = true;
        directory.Attach();
        capture.Attach();
        sender.Attach();
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
            await Task.WhenAll(history.DisposeAsync().AsTask(), avatars.DisposeAsync().AsTask()).ConfigureAwait(false);
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
        sender.Dispose();
        capture.Dispose();
        directory.Dispose();
    }

    private void Update(IFramework framework)
    {
        var snapshot = directory.Update();
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
