using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using Consolidator.Game;
using Consolidator.Windows;

namespace Consolidator;

public sealed class Plugin : IDalamudPlugin
{
    public const string Command = "/cons";

    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;

    public static Plugin P { get; private set; } = null!;

    public Configuration Config { get; init; }
    public Sweeper Sweeper { get; init; }
    public ItemTradeHook ItemHook { get; init; }

    public readonly WindowSystem Windows = new("Consolidator");
    private MainWindow MainWindow { get; init; }

    public Plugin(IDalamudPluginInterface pi)
    {
        P = this;
        // ECommons gives us Svc.*, Callback, Chat, throttlers — the same stack
        // AutoRetainer / Lifestream / Dropbox all build on.
        ECommonsMain.Init(pi, this);

        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Repair anything a previous version left in a bad state (notably duplicate
        // gil rows, which made the "Send gil" toggle look like it did nothing).
        if (Config.Normalise())
        {
            Log.Information("Repaired the item list (duplicate or missing entries).");
            Config.Save();
        }

        // Item trading needs a signature-scanned function — there's no addon callback
        // for it. If the signature breaks on a patch, gil still works and the UI says
        // items are unavailable rather than silently doing nothing.
        ItemHook = new ItemTradeHook();
        Game.TradeEngine.ItemHook = ItemHook;
        if (!ItemHook.Available)
            Log.Warning($"Item trading unavailable: {ItemHook.Error}");

        Sweeper = new Sweeper(Config);

        MainWindow = new MainWindow(this);
        Windows.AddWindow(MainWindow);

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Consolidator. /cons start | pause | resume | stop",
        });

        PluginInterface.UiBuilder.Draw += Windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMain;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMain;
        Framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework _)
    {
        try { Sweeper.Tick(); }
        catch (Exception ex)
        {
            Log.Error(ex, "Consolidator tick failed; stopping sweep.");
            // Stop() is the handler of last resort, so it must not throw on its way
            // out — otherwise the exception escapes into Dalamud's framework update.
            try { Sweeper.Stop("Internal error — see /xllog."); }
            catch (Exception inner) { Log.Error(inner, "Stop() also failed."); }
        }
    }

    private void OnCommand(string _, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "start": Sweeper.Start(); break;
            case "stop": Sweeper.Stop(); break;
            case "pause": Sweeper.Pause(); break;
            case "resume": Sweeper.Resume(); break;
            default: ToggleMain(); break;
        }
    }

    private void ToggleMain() => MainWindow.Toggle();

    public void Dispose()
    {
        // Persist wherever we got to, so a reload or crash can be picked up.
        Sweeper.Stop("Plugin unloading.");
        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= Windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMain;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        CommandManager.RemoveHandler(Command);
        Windows.RemoveAllWindows();
        ECommonsMain.Dispose();
    }
}
