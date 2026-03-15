using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;
using MOGTOME.Models;
using MOGTOME.Services;
using MOGTOME.Windows;

namespace MOGTOME;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyStateService { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    private const string CommandName = "/mogtome";
    private const string AliasCommandName = "/mog";

    public Configuration Configuration { get; init; }
    public DutyState State { get; init; }

    // IPC
    public YesAlreadyIPC YesAlreadyIPC { get; init; }
    public VNavIPC VNavIPC { get; init; }
    public AutoDutyIPC AutoDutyIPC { get; init; }
    public AutomatonIPC AutomatonIPC { get; init; }
    public BossModIPC BossModIPC { get; init; }

    // Services
    public DutyTrackerService DutyTrackerService { get; private set; }
    public DutyQueueService DutyQueueService { get; private set; }
    public RotationService RotationService { get; private set; }
    public BossHandlerService BossHandlerService { get; private set; }
    public RepairService RepairService { get; private set; }
    public FoodService FoodService { get; private set; }
    public DialogHandlerService DialogHandlerService { get; private set; }
    public StuckDetectionService StuckDetectionService { get; private set; }
    public AutoDutyPathService AutoDutyPathService { get; private set; }
    public RunHistoryService RunHistoryService { get; private set; }
    public MogtomeEngine Engine { get; init; }

    // Windows
    public readonly WindowSystem WindowSystem = new("MOGTOME");
    public ConfigWindow ConfigWindow { get; init; }
    public MainWindow MainWindow { get; init; }
    public StatsWindow StatsWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        State = new DutyState();

        // Initialize IPC
        YesAlreadyIPC = new YesAlreadyIPC(Log);
        VNavIPC = new VNavIPC(Log, CommandManager);
        AutoDutyIPC = new AutoDutyIPC(Log, CommandManager);
        AutomatonIPC = new AutomatonIPC(Log);
        BossModIPC = new BossModIPC(Log, CommandManager);

        // Initialize Services
        DutyTrackerService = new DutyTrackerService(Log, Configuration, State);
        DutyQueueService = new DutyQueueService(Log, Configuration, State, AutoDutyIPC, AutomatonIPC, CommandManager, Condition);
        RepairService = new RepairService(Log, Configuration, State, CommandManager, Condition);
        FoodService = new FoodService(Log, Configuration, State, Condition);
        RotationService = new RotationService(Log, Configuration, State, BossModIPC, AutoDutyIPC);
        BossHandlerService = new BossHandlerService(Log, Configuration, State, VNavIPC, CommandManager, Condition);
        StuckDetectionService = new StuckDetectionService(Log, Configuration, State, VNavIPC, CommandManager, Condition);
        DialogHandlerService = new DialogHandlerService(Log, YesAlreadyIPC, CommandManager, GameGui);
        AutoDutyPathService = new AutoDutyPathService(Log);
        RunHistoryService = new RunHistoryService(Log, Configuration, State, PlayerState);

        // Engine
        Engine = new MogtomeEngine(
            Log, Configuration, State,
            DutyTrackerService, DutyQueueService,
            RepairService, FoodService,
            RotationService, BossHandlerService,
            StuckDetectionService, DialogHandlerService,
            AutoDutyPathService, RunHistoryService, // NEW
            AutoDutyIPC, AutomatonIPC, YesAlreadyIPC,
            Condition, ClientState, CommandManager);

        // Windows
        ConfigWindow = new ConfigWindow(this, Log);
        MainWindow = new MainWindow(this);
        StatsWindow = new StatsWindow(this);
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(StatsWindow);

        // Commands
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the MOGTOME main window."
        });
        CommandManager.AddHandler(AliasCommandName, new CommandInfo(OnAliasCommand)
        {
            HelpMessage = "MOGTOME: /mog [start|stop|config] or /mog to open UI."
        });

        // Events
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;
        DutyStateService.DutyStarted += OnDutyStarted;
        DutyStateService.DutyCompleted += OnDutyCompleted;

        Log.Information("=== MOGTOME loaded! ===");
    }

    public void Dispose()
    {
        DutyStateService.DutyCompleted -= OnDutyCompleted;
        DutyStateService.DutyStarted -= OnDutyStarted;
        Framework.Update -= OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        // Stop engine if running
        if (Engine.IsRunning)
            Engine.Stop();
        Engine.Dispose();

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        MainWindow.Dispose();
        StatsWindow.Dispose();

        YesAlreadyIPC.Dispose();
        VNavIPC.Dispose();
        AutoDutyIPC.Dispose();
        AutomatonIPC.Dispose();
        BossModIPC.Dispose();

        CommandManager.RemoveHandler(AliasCommandName);
        CommandManager.RemoveHandler(CommandName);

        Log.Information("=== MOGTOME unloaded! ===");
    }

    private void OnCommand(string command, string args)
    {
        MainWindow.Toggle();
    }

    private void OnAliasCommand(string command, string args)
    {
        var arg = args.Trim().ToLowerInvariant();
        switch (arg)
        {
            case "start":
                if (!Engine.IsRunning)
                {
                    Engine.Start();
                    ChatGui.Print("[MOGTOME] Started");
                }
                else
                {
                    ChatGui.Print("[MOGTOME] Already running");
                }
                break;

            case "stop":
                Engine.Stop();
                ChatGui.Print("[MOGTOME] Stopped");
                break;

            case "config":
                ConfigWindow.Toggle();
                break;

            case "status":
                ChatGui.Print($"[MOGTOME] State: {Engine.CurrentState} | Duty #{State.DutyCounter} | {Engine.StatusMessage}");
                break;

            default:
                MainWindow.Toggle();
                break;
        }
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        Engine.Update();
    }

    private void OnDutyStarted(object? sender, ushort territoryId)
    {
        Log.Information($"[Plugin] DutyStarted event: territory={territoryId}");
    }

    private void OnDutyCompleted(object? sender, ushort territoryId)
    {
        Log.Information($"[Plugin] DutyCompleted event: territory={territoryId}");
    }

    private void ToggleConfigUi() => ConfigWindow.Toggle();
    private void ToggleMainUi() => MainWindow.Toggle();
}
