using MOGTOME.Localization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
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
    public const string DiscordUrl = "https://discord.gg/VsXqydsvpu";
    public static string DiscordChannelHint => Ui.T("Plugin_ScrollDownToTheDumpsterFireChannel");
    public static string StartReminderToastMessage => Ui.T("Plugin_CheckPartyLeaderSettingsIfTheDuty");

    private sealed class ExternalExceptionLogState
    {
        public DateTime LastLoggedUtc { get; set; }
        public int SuppressedCount { get; set; }
        public bool SuppressionBannerLogged { get; set; }
    }

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyStateService { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ISeStringEvaluator SeStringEvaluator { get; private set; } = null!;

    private const string CommandName = "/mogtome";
    private const string AliasCommandName = "/mog";
    private const string AdsEnterInnCommand = "/ads enterinn";
    private const string XaSkipCutscenesCommand = "/xa skipcutscenes on";

    // Per-account configuration management
    public ConfigManager ConfigManager { get; init; }
    public Configuration Configuration => ConfigManager.GetActiveConfig();
    public bool CanSelectUiLanguage => accountInitialized && ClientState.IsLoggedIn;
    public DutyState State { get; init; }

    // IPC
    public YesAlreadyIPC YesAlreadyIPC { get; init; }
    public VNavIPC VNavIPC { get; init; }
    public AutoDutyIPC AutoDutyIPC { get; init; }
    public BossModIPC BossModIPC { get; init; }
    public MogtomeDadIpcService DadIpcService { get; init; }

    // Services
    public DatabaseService DatabaseService { get; private set; }
    public DutyTrackerService DutyTrackerService { get; private set; }
    public DutyQueueService DutyQueueService { get; private set; }
    public RotationService RotationService { get; private set; }
    public BossHandlerService BossHandlerService { get; private set; }
    public RepairService RepairService { get; private set; }
    public FoodService FoodService { get; private set; }
    public ConsumableInventoryService ConsumableInventoryService { get; private set; }
    public DialogHandlerService DialogHandlerService { get; private set; }
    public StuckDetectionService StuckDetectionService { get; private set; }
    public AutoDutyPathService AutoDutyPathService { get; private set; }
    public ConflictPluginService ConflictPluginService { get; private set; }
    public RunHistoryService RunHistoryService { get; private set; }
    public DeathTrackingService DeathTrackingService { get; private set; }
    public DutyAutomationService DutyAutomationService { get; private set; }
    public MogtomeEngine Engine { get; private set; }

    // Windows
    public readonly WindowSystem WindowSystem = new("MOGTOME");
    public ConfigWindow ConfigWindow { get; init; }
    public MainWindow MainWindow { get; init; }
    public StatsWindow StatsWindow { get; init; }
    public ActionWarningWindow ActionWarningWindow { get; init; }
    public WarningTextWindow WarningTextWindow { get; init; }

    private static readonly TimeSpan ExternalExceptionSuppressionWindow = TimeSpan.FromSeconds(10);
    private readonly object externalExceptionLogLock = new();
    private readonly Dictionary<string, ExternalExceptionLogState> externalExceptionLogStates = new(StringComparer.Ordinal);
    private bool pendingEngineStartRequest;
    public bool IsEngineStartQueued => pendingEngineStartRequest || deferredSharedConfigStartRequest;
    private UiText stopReasonBeforeInitialization = Ui.M("Engine_HasNotBeenStartedSinceLoading");
    private string pendingEngineStartSource = string.Empty;
    private bool pendingEngineStartNotifyChat;
    private bool pendingEngineStartDuplicateNotified;
    private FileStream? configDirectoryLock;
    private bool sharedConfigPathDetected;
    private bool sharedConfigWarningAcknowledged;
    private bool deferredSharedConfigStartRequest;
    private string deferredSharedConfigStartSource = string.Empty;
    private bool deferredSharedConfigStartNotifyChat;
    private int matchingProcessCount = 1;

    public Plugin()
    {
        AcquireConfigDirectoryLock();

        // Initialize ConfigManager first
        ConfigManager = new ConfigManager(Log, PlayerState, ClientState, PluginInterface);
        
                
        State = new DutyState();

        // Initialize Services (needed before IPC)
        DatabaseService = new DatabaseService(Log, PluginInterface, ConfigManager);
        DeathTrackingService = new DeathTrackingService(Log, Framework, ObjectTable, PartyList, PlayerState, Condition, ClientState);
        RunHistoryService = new RunHistoryService(Log, Configuration, State, PlayerState, ConfigManager, DatabaseService, DeathTrackingService);

        // Initialize IPC that RotationService needs
        BossModIPC = new BossModIPC(PluginInterface, Log, CommandManager);
        YesAlreadyIPC = new YesAlreadyIPC(Log);
        VNavIPC = new VNavIPC(Log, CommandManager);
        RotationService = new RotationService(Log, ConfigManager, BossModIPC);
        AutoDutyIPC = new AutoDutyIPC(Log, CommandManager, RunHistoryService);
        DadIpcService = new MogtomeDadIpcService(PluginInterface, this);

        // Initialize Services (needs RotationService)
        DutyTrackerService = new DutyTrackerService(Log, Configuration, State, ConfigManager, RunHistoryService);
        AutoDutyPathService = new AutoDutyPathService(Log, PluginInterface);
        ConflictPluginService = new ConflictPluginService(Log, CommandManager);
        DutyAutomationService = new DutyAutomationService(
            Log,
            ConfigManager,
            AutoDutyIPC,
            AutoDutyPathService,
            ConflictPluginService,
            CommandManager,
            RunHistoryService);
        DutyQueueService = new DutyQueueService(Log, State, DutyAutomationService, Condition, ConfigManager);
        RepairService = new RepairService(Log, Configuration, State, DutyAutomationService, Condition);
        ConsumableInventoryService = new ConsumableInventoryService(Log, Configuration, State);
        FoodService = new FoodService(Log, Configuration, State, Condition, ConsumableInventoryService);
        BossHandlerService = new BossHandlerService(Log, Configuration, State, VNavIPC, CommandManager, Condition, ConsumableInventoryService);
        StuckDetectionService = new StuckDetectionService(Log, ConfigManager, State, VNavIPC, Condition);
        DialogHandlerService = new DialogHandlerService(Log, YesAlreadyIPC, CommandManager, GameGui);

        // Wire up configuration change subscriptions
        ConsumableInventoryService.SubscribeToConfigChanges(ConfigManager);
        FoodService.SubscribeToConfigChanges(ConfigManager);
        BossHandlerService.SubscribeToConfigChanges(ConfigManager);
        RepairService.SubscribeToConfigChanges(ConfigManager);

        // Engine will be created in OnFrameworkUpdate after account selection
        Engine = null!;

        // Windows
        ConfigWindow = new ConfigWindow(this, Log);
        MainWindow = new MainWindow(this);
        StatsWindow = new StatsWindow(this);
        ActionWarningWindow = new ActionWarningWindow();
        WarningTextWindow = new WarningTextWindow(this);
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(StatsWindow);
        WindowSystem.AddWindow(ActionWarningWindow);
        WindowSystem.AddWindow(WarningTextWindow);

        // Commands
        mainCommandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = Ui.T("Plugin_CommandMainHelp")
        };
        aliasCommandInfo = new CommandInfo(OnAliasCommand)
        {
            HelpMessage = Ui.T("Plugin_CommandAliasHelp")
        };
        CommandManager.AddHandler(CommandName, mainCommandInfo);
        CommandManager.AddHandler(AliasCommandName, aliasCommandInfo);

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Events
        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        ClientState.Login += OnLoginEvent;
        ChatGui.ChatMessageUnhandled += OnChatMessage;
        Framework.Update += OnFrameworkUpdate;
        DutyStateService.DutyStarted += OnDutyStarted;
        DutyStateService.DutyCompleted += OnDutyCompleted;

        Log.Information("=== MOGTOME loaded! ===");
    }

    public void Dispose()
    {
        CancelQueuedEngineStart();
        DutyStateService.DutyCompleted -= OnDutyCompleted;
        DutyStateService.DutyStarted -= OnDutyStarted;
        Framework.Update -= OnFrameworkUpdate;
        ChatGui.ChatMessageUnhandled -= OnChatMessage;
        ClientState.Login -= OnLoginEvent;

        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        
        // Stop engine if running and dispose if initialized
        if (Engine != null)
        {
            Engine.Dispose();
        }

        RunHistoryService.Dispose();
        DeathTrackingService.Dispose();

        // Save current account configuration before disposing everything
        ConfigManager.SaveCurrentAccount();

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        MainWindow.Dispose();
        StatsWindow.Dispose();
        ActionWarningWindow.Dispose();
        WarningTextWindow.Dispose();

        YesAlreadyIPC.Dispose();
        VNavIPC.Dispose();
        AutoDutyIPC.Dispose();
        BossModIPC.Dispose();
        DadIpcService.Dispose();
        configDirectoryLock?.Dispose();
        configDirectoryLock = null;

        CommandManager.RemoveHandler(AliasCommandName);
        CommandManager.RemoveHandler(CommandName);

        Log.Information("=== MOGTOME unloaded! ===");
    }

    private void OnCommand(string command, string args)
    {
        MainWindow.Toggle();
    }

    private UiLanguage? commandHelpLanguage;
    private readonly CommandInfo mainCommandInfo;
    private readonly CommandInfo aliasCommandInfo;

    private void DrawUi()
    {
        if (!CanSelectUiLanguage)
            Localization.Ui.SetLanguage(Localization.Ui.FromClient(ClientState.ClientLanguage));
        if (commandHelpLanguage != Ui.Language)
        {
            mainCommandInfo.HelpMessage = Ui.T("Plugin_CommandMainHelp");
            aliasCommandInfo.HelpMessage = Ui.T("Plugin_CommandAliasHelp");
            commandHelpLanguage = Ui.Language;
        }
        WindowSystem.Draw();
    }

    private void OnAliasCommand(string command, string args)
    {
        var arg = args.Trim().ToLowerInvariant();
        switch (arg)
        {
            case "start":
                QueueEngineStart("slash command", notifyChat: true);
                break;

            case "stop":
                var stoppedSomething = StopEngine();
                ChatGui.Print(stoppedSomething
                    ? Ui.T("Chat_MOGTOMEStoppedAndClearedPendingStartAnd")
                    : Ui.T("Chat_MOGTOMEAlreadyStopped", Engine?.StopReason ?? stopReasonBeforeInitialization));
                break;

            case "stopnext":
                if (Engine == null)
                {
                    ChatGui.Print(Ui.T("Chat_MOGTOMEEngineIsStillInitializing"));
                    break;
                }

                var armed = Engine.ToggleStopAfterNextSuccessfulRun();
                ChatGui.Print(armed
                    ? Ui.T("Chat_MOGTOMEStopAfterNextSuccessfulRunArmed")
                    : Ui.T("Chat_MOGTOMEStopAfterNextSuccessfulRunCancelled"));
                break;

            case "config":
                ConfigWindow.Toggle();
                break;

            case "inn":
                if (Engine != null && Engine.IsRunning && Engine.CurrentState != EngineState.RepairingOutside)
                {
                    ChatGui.Print(Ui.T("Chat_MOGTOMEMogInnIsOnlyAvailableWhile"));
                    break;
                }

                SendAdsEnterInnCommand("/mog inn", notifyFailure: true);
                break;

            case "inn auto":
                SendAdsEnterInnCommand("/mog inn auto", notifyFailure: false);
                break;

            case "status":
                if (Engine == null)
                {
                    ChatGui.Print(Ui.T("Chat_MOGTOMEEngineIsStillInitializing"));
                    break;
                }

                ChatGui.Print(Ui.T("Chat_MOGTOMEStateDuty", Ui.EnumLabel(Engine.CurrentState), State.DutyCounter, Engine.Status));
                break;

            case "debug":
                var debugMode = !Configuration.DebugModeEnabled;
                Configuration.DebugModeEnabled = debugMode;
                ConfigManager.SaveCurrentAccount();
                
                var status = debugMode ? Ui.M("Config_Enabled") : Ui.M("Config_Disabled");
                ChatGui.Print(Ui.T("Chat_MOGTOMEDebugMode", status));
                if (debugMode)
                {
                    ChatGui.Print(Ui.T("Chat_MOGTOMEDebugCheckboxNowVisibleInStats"));
                }
                break;

            case "ws":
                ResetWindowPositions();
                break;

            case "j":
                JumpWindowsToRandomVisibleLocations();
                break;

            default:
                MainWindow.Toggle();
                break;
        }
    }

    private static bool SendAdsEnterInnCommand(string source, bool notifyFailure)
    {
        try
        {
            Log.Information($"[MOGTOME][Inn] {source} delegated to {AdsEnterInnCommand}");
            if (CommandManager.ProcessCommand(AdsEnterInnCommand))
                return true;

            var message = Ui.M("Plugin_ADSDidNotHandleAdsEnterinnEnsure");
            Log.Warning($"[MOGTOME][Inn] {message}");
            if (notifyFailure)
                ChatGui.Print(Ui.T("Chat_MOGTOME", message));
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, $"[MOGTOME][Inn] Failed to send {AdsEnterInnCommand} from {source}");
            if (notifyFailure)
                ChatGui.Print(Ui.T("Chat_MOGTOMEFailedToSendAdsEnterinnCheck"));
            return false;
        }
    }

    private void ResetWindowPositions()
    {
        MainWindow.QueueResetToOrigin();
        ConfigWindow.QueueResetToOrigin();
        StatsWindow.QueueResetToOrigin();

        MainWindow.IsOpen = true;
        ConfigWindow.IsOpen = true;
        StatsWindow.IsOpen = true;

        ChatGui.Print(Ui.T("Chat_MOGTOMEQueuedMainConfigStatsWindowReset"));
    }

    private void JumpWindowsToRandomVisibleLocations()
    {
        MainWindow.QueueRandomVisibleJump();
        ConfigWindow.QueueRandomVisibleJump();
        StatsWindow.QueueRandomVisibleJump();

        MainWindow.IsOpen = true;
        ConfigWindow.IsOpen = true;
        StatsWindow.IsOpen = true;

        ChatGui.Print(Ui.T("Chat_MOGTOMEQueuedRandomVisibleJumpsForMain"));
    }

    public void ShowStartReminderToast()
    {
        ToastGui.ShowNormal(new SeString(new TextPayload(StartReminderToastMessage)));
    }

    public void QueueEngineStart(string source, bool notifyChat)
    {
        if (Engine == null)
        {
            Log.Information("[Plugin] Start request from {Source} skipped because engine is still initializing", source);
            if (notifyChat)
                ChatGui.Print(Ui.T("Chat_MOGTOMEEngineIsStillInitializingTryAgain"));
            return;
        }

        if (Engine.IsRunning)
        {
            Log.Information("[Plugin] Start request from {Source} skipped because engine is already running", source);
            if (notifyChat)
                ChatGui.Print(Ui.T("Chat_MOGTOMEAlreadyRunning"));
            return;
        }

        if (sharedConfigPathDetected && !sharedConfigWarningAcknowledged)
        {
            deferredSharedConfigStartRequest = true;
            deferredSharedConfigStartSource = source;
            deferredSharedConfigStartNotifyChat = notifyChat;
            ActionWarningWindow.ShowWarning(
                Ui.M("Warning_SharedMOGTOMEConfigPath"),
                Ui.M("Warning_AnotherClientHoldsMOGTOMESConfigDirectory", matchingProcessCount),
                dismissButtonLabel: Ui.M("Warning_CancelStart"),
                onDismiss: CancelDeferredSharedConfigStart,
                acknowledgeButtonLabel: Ui.M("Warning_AcknowledgeAndStart"),
                onAcknowledged: AcknowledgeSharedConfigAndStart,
                explicitChoiceRequired: true);
            Log.Warning("[Plugin] Start request from {Source} deferred for shared-config acknowledgement", source);
            return;
        }

        QueueEngineStartCore(source, notifyChat);
    }

    internal void CancelQueuedEngineStart()
    {
        pendingEngineStartRequest = false;
        pendingEngineStartSource = string.Empty;
        pendingEngineStartNotifyChat = false;
        pendingEngineStartDuplicateNotified = false;
        CancelDeferredSharedConfigStart();
    }

    public bool StopEngine(string? reason = null)
    {
        UiText stopReason = reason == null ? Ui.M("Engine_ManualStop") : (UiText)reason;
        var stopped = IsEngineStartQueued;
        CancelQueuedEngineStart();
        if (Engine?.IsRunning == true)
        {
            Engine.StopWithReason(stopReason);
            return true;
        }
        stopped |= Engine?.ClearStopAfterNextSuccessfulRun() == true;
        if (stopped)
        {
            stopReasonBeforeInitialization = stopReason;
            Engine?.RecordStopReason(stopReason);
        }
        return stopped;
    }

    private void QueueEngineStartCore(string source, bool notifyChat)
    {
        if (pendingEngineStartRequest)
        {
            if (!pendingEngineStartDuplicateNotified)
            {
                pendingEngineStartDuplicateNotified = true;
                Log.Information("[Plugin] Start request from {Source} ignored because a framework-thread start is already queued from {QueuedSource}",
                    source,
                    pendingEngineStartSource);
                if (notifyChat)
                    ChatGui.Print(Ui.T("Chat_MOGTOMEStartAlreadyQueued"));
            }

            return;
        }

        ShowStartReminderToast();
        pendingEngineStartRequest = true;
        pendingEngineStartSource = source;
        pendingEngineStartNotifyChat = notifyChat;
        pendingEngineStartDuplicateNotified = false;
        Log.Information("[Plugin] Start request queued from {Source}; engine start will begin on framework thread", source);
        if (notifyChat)
            ChatGui.Print(Ui.T("Chat_MOGTOMEStartQueued"));
    }

    private void AcknowledgeSharedConfigAndStart()
    {
        sharedConfigWarningAcknowledged = true;
        if (!deferredSharedConfigStartRequest)
            return;

        var source = deferredSharedConfigStartSource;
        var notifyChat = deferredSharedConfigStartNotifyChat;
        CancelDeferredSharedConfigStart();
        QueueEngineStartCore(source, notifyChat);
    }

    private void CancelDeferredSharedConfigStart()
    {
        deferredSharedConfigStartRequest = false;
        deferredSharedConfigStartSource = string.Empty;
        deferredSharedConfigStartNotifyChat = false;
    }

    private void AcquireConfigDirectoryLock()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            matchingProcessCount = Process.GetProcessesByName(current.ProcessName).Length;
            var lockPath = Path.Combine(PluginInterface.ConfigDirectory.FullName, ".mogtome-config.lock");
            configDirectoryLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            sharedConfigPathDetected = false;
            Log.Information("[Plugin] Acquired exclusive MOGTOME config-directory lock: {Path}", lockPath);
        }
        catch (IOException ex)
        {
            sharedConfigPathDetected = true;
            Log.Warning(ex, "[Plugin] Another client appears to use this MOGTOME config path");
        }
        catch (UnauthorizedAccessException ex)
        {
            sharedConfigPathDetected = true;
            Log.Warning(ex, "[Plugin] Could not acquire MOGTOME config-directory lock");
        }
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        // Delayed login detection (LocalPlayer may not be ready immediately)
        if (ClientState.IsLoggedIn && !wasLoggedIn)
        {
            wasLoggedIn = true;
            loginDetectionDelay = 3; // Wait a few frames for LocalPlayer to be ready
        }
        else if (!ClientState.IsLoggedIn && wasLoggedIn)
        {
            wasLoggedIn = false;
            loginDetectionDelay = 0;
            accountInitialized = false; // Reset on logout so we re-detect on next login
        }

        if (loginDetectionDelay > 0)
        {
            loginDetectionDelay--;
            if (loginDetectionDelay == 0)
                OnLogin();
        }
        
        // Initialize engine after account selection (proper dependency order)
        if (Engine == null && accountInitialized)
        {
            try
            {
                Engine = new MogtomeEngine(
                    Log, Configuration, State,
                    DutyTrackerService, DutyQueueService,
                    RepairService, FoodService, ConsumableInventoryService,
                    RotationService, BossHandlerService,
                    StuckDetectionService, DialogHandlerService,
                    DutyAutomationService,
                    AutoDutyPathService, ConflictPluginService, RunHistoryService,
                    DeathTrackingService,
                    AutoDutyIPC, YesAlreadyIPC, VNavIPC,
                    Condition, ClientState, CommandManager, CancelQueuedEngineStart);
                Engine.RecordStopReason(stopReasonBeforeInitialization);
                
                Log.Information("[Plugin] Engine initialized successfully with proper config");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Plugin] Failed to initialize engine");
            }
        }
        
        // Initialize database service on first frame
        if (!databaseInitialized)
        {
            try
            {
                DatabaseService.Initialize();
                databaseInitialized = true;
                Log.Information("[Plugin] Database service initialized successfully");
                
                // Trigger migration for all accounts after initialization
                TriggerMigrationForAllAccounts();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Plugin] Failed to initialize database service");
            }
        }

        if (!ActionWarningWindow.IsOpen &&
            ConflictPluginService.TryTakePendingWarning(out var conflictPopupMessage))
        {
            var isTwistWarning = conflictPopupMessage.English.Contains("Twist of Fayte", StringComparison.OrdinalIgnoreCase);
            var isAutoDutyWarning = conflictPopupMessage.English.Contains("AutoDuty", StringComparison.OrdinalIgnoreCase);
            ActionWarningWindow.ShowWarning(
                isTwistWarning ? Ui.M("Warning_TwistOfFayteConflict") : isAutoDutyWarning ? Ui.M("Warning_AutoDutyConflict") : Ui.M("Warning_PluginConflict"),
                conflictPopupMessage,
                isTwistWarning ? Ui.M("Warning_DisableTwistOfFayte") : isAutoDutyWarning ? Ui.M("Warning_DisableAutoDuty") : null,
                isTwistWarning
                    ? () => _ = ConflictPluginService.EnsureTwistOfFayteDisabledAsync("Warning window", showPopup: false)
                    : isAutoDutyWarning
                        ? () => _ = ConflictPluginService.EnsureAutoDutyDisabledAsync("Warning window", showPopup: false)
                        : null);
        }

        TryConsumeQueuedEngineStart();

        if (accountInitialized)
        {
            WarningTextWindow.ShowIfNeeded();
        }

        // Only update engine if it's initialized
        if (Engine != null)
        {
            Engine.Update();
        }
        DadIpcService.Update();
    }

    private void TryConsumeQueuedEngineStart()
    {
        if (!pendingEngineStartRequest)
            return;

        var source = pendingEngineStartSource;
        var notifyChat = pendingEngineStartNotifyChat;
        pendingEngineStartRequest = false;
        pendingEngineStartSource = string.Empty;
        pendingEngineStartNotifyChat = false;
        pendingEngineStartDuplicateNotified = false;

        if (Engine == null)
        {
            Log.Warning("[Plugin] Framework-thread start skipped for {Source} because engine is still initializing", source);
            if (notifyChat)
                ChatGui.Print(Ui.T("Chat_MOGTOMEEngineIsStillInitializingTryAgain"));
            return;
        }

        if (Engine.IsRunning)
        {
            Log.Information("[Plugin] Framework-thread start skipped for {Source} because engine is already running", source);
            if (notifyChat)
                ChatGui.Print(Ui.T("Chat_MOGTOMEAlreadyRunning"));
            return;
        }

        TryEnableXaSlaveSkipCutscenes(source);
        Log.Information("[Plugin] Framework-thread startup begins for queued request from {Source}", source);
        Engine.Start();
    }

    private bool TryEnableXaSlaveSkipCutscenes(string source)
    {
        try
        {
            if (CommandManager.ProcessCommand(XaSkipCutscenesCommand))
            {
                Log.Information("[Plugin] XA Slave skipcutscenes enabled before start request from {Source}", source);
                return true;
            }

            Log.Warning("[Plugin] XA Slave did not handle {Command} for {Source}; continuing startup", XaSkipCutscenesCommand, source);
            ChatGui.Print(Ui.T("Chat_MOGTOMEWarningXASlaveDidNotHandle", XaSkipCutscenesCommand));
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Plugin] Failed to send {Command} for {Source}; continuing startup", XaSkipCutscenesCommand, source);
            ChatGui.Print(Ui.T("Chat_MOGTOMEWarningFailedToSendContinuingStartup", XaSkipCutscenesCommand));
            return false;
        }
    }

    private void TriggerMigrationForAllAccounts()
    {
        try
        {
            // Get all configured accounts
            var accounts = ConfigManager.GetAllAccountIds();
            
            foreach (var accountId in accounts)
            {
                try
                {
                    DatabaseService.MigrateFromJson(accountId);
                    Log.Information($"[Plugin] Migration completed for account {accountId}");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, $"[Plugin] Migration failed for account {accountId}");
                }
            }
            
            if (accountInitialized)
            {
                RunHistoryService.LoadRunHistoryFromDatabase();
                Log.Information("[Plugin] Run history reloaded after migration");
            }
            else
            {
                Log.Debug("[Plugin] Deferring run history reload until account selection completes");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Plugin] Failed to trigger migrations");
        }
    }

    private void OnDutyStarted(Dalamud.Game.DutyState.IDutyStateEventArgs args)
        => OnDutyStarted(args.TerritoryType.RowId);

    private void OnDutyStarted(uint territoryId)
    {
        Log.Information($"[Plugin] DutyStarted event: territory={territoryId}");
        // The engine validates live territory/CFC before binding its attempt.
    }

    private void OnDutyCompleted(Dalamud.Game.DutyState.IDutyStateEventArgs args)
        => OnDutyCompleted(args.TerritoryType.RowId);

    private void OnDutyCompleted(uint territoryId)
    {
        Log.Information($"[Plugin] DutyCompleted event: territory={territoryId}");
        
        // DutyTracker will handle time calculation and call RecordRun() after calculation
        // This ensures we have the correct completion time before recording
    }

    private void OnLoginEvent()
    {
        // Don't run OnLogin here - Login event fires off main thread.
        // Instead, set a delay so OnFrameworkUpdate picks it up.
        loginDetectionDelay = 3;
    }

    private void OnChatMessage(IChatMessage message)
        => HandleChatMessage(message.Message);

    private void HandleChatMessage(SeString message)
    {
        try
        {
            DutyAutomationService.HandleAdsQueueChatMessage(message.TextValue);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Plugin] queue chat watcher failed");
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogObservedException(ex, "AppDomain exception", $"terminating={e.IsTerminating}");
            return;
        }

        Log.Error($"[Plugin] Unhandled AppDomain exception object (terminating={e.IsTerminating}): {e.ExceptionObject}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogObservedException(e.Exception, "task exception", null);
        e.SetObserved();
    }

    private void LogObservedException(Exception ex, string category, string? extraContext)
    {
        var source = ClassifyExceptionSource(ex);
        var isExternal = !string.Equals(source, "MOGTOME", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(source, "Unknown", StringComparison.OrdinalIgnoreCase);

        if (!isExternal)
        {
            var contextSuffix = string.IsNullOrWhiteSpace(extraContext) ? string.Empty : $", {extraContext}";
            Log.Error(ex, $"[Plugin] {UppercaseFirst(category)} (source={source}{contextSuffix})");
            return;
        }

        var fingerprint = BuildExternalExceptionFingerprint(source, ex, category);
        var now = DateTime.UtcNow;
        string? suppressionBanner = null;
        string? suppressionSummary = null;
        var suppressFullLog = false;

        lock (externalExceptionLogLock)
        {
            PruneExternalExceptionLogState_NoLock(now);

            if (!externalExceptionLogStates.TryGetValue(fingerprint, out var state))
            {
                state = new ExternalExceptionLogState();
                externalExceptionLogStates[fingerprint] = state;
            }

            if (state.LastLoggedUtc != DateTime.MinValue &&
                now - state.LastLoggedUtc < ExternalExceptionSuppressionWindow)
            {
                state.SuppressedCount++;
                suppressFullLog = true;

                if (!state.SuppressionBannerLogged)
                {
                    state.SuppressionBannerLogged = true;
                    suppressionBanner = $"[Plugin] Suppressing repeated external {category} logs from {source} for {ExternalExceptionSuppressionWindow.TotalSeconds:F0}s";
                }
            }
            else
            {
                if (state.SuppressedCount > 0)
                {
                    suppressionSummary = $"[Plugin] Suppressed {state.SuppressedCount} repeated external {category} logs from {source} over the last {ExternalExceptionSuppressionWindow.TotalSeconds:F0}s";
                }

                state.LastLoggedUtc = now;
                state.SuppressedCount = 0;
                state.SuppressionBannerLogged = false;
            }
        }

        if (!string.IsNullOrWhiteSpace(suppressionBanner))
            Log.Warning(suppressionBanner);

        if (suppressFullLog)
            return;

        if (!string.IsNullOrWhiteSpace(suppressionSummary))
            Log.Warning(suppressionSummary);

        var extraSuffix = string.IsNullOrWhiteSpace(extraContext) ? string.Empty : $", {extraContext}";
        Log.Warning(ex, $"[Plugin] External {category} observed via MOGTOME hook (source={source}{extraSuffix})");
    }

    private void PruneExternalExceptionLogState_NoLock(DateTime now)
    {
        List<string>? staleKeys = null;

        foreach (var entry in externalExceptionLogStates)
        {
            if (entry.Value.LastLoggedUtc != DateTime.MinValue &&
                now - entry.Value.LastLoggedUtc <= TimeSpan.FromMinutes(5))
            {
                continue;
            }

            staleKeys ??= [];
            staleKeys.Add(entry.Key);
        }

        if (staleKeys == null)
            return;

        foreach (var key in staleKeys)
            externalExceptionLogStates.Remove(key);
    }

    private static string BuildExternalExceptionFingerprint(string source, Exception ex, string category)
    {
        var root = ex.GetBaseException();
        var stackTrace = root.StackTrace ?? string.Empty;
        var newlineIndex = stackTrace.IndexOfAny(['\r', '\n']);
        var firstFrame = newlineIndex >= 0 ? stackTrace[..newlineIndex] : stackTrace;
        return $"{category}|{source}|{root.GetType().FullName}|{root.Message}|{firstFrame}";
    }

    private static string ClassifyExceptionSource(Exception ex)
    {
        var root = ex.GetBaseException();
        var rootText = BuildExceptionClassificationText(root);
        if (ContainsMogtomeFrame(rootText))
            return "MOGTOME";

        var rootExternalSource = ClassifyKnownExternalSource(rootText);
        if (rootExternalSource != null)
            return rootExternalSource;

        var fullText = BuildExceptionClassificationText(ex);
        var externalSource = ClassifyKnownExternalSource(fullText);
        if (externalSource != null)
            return externalSource;

        if (ContainsMogtomeFrame(fullText))
            return "MOGTOME";

        return GetFallbackExceptionSource(ex);
    }

    private static string BuildExceptionClassificationText(Exception ex)
    {
        var builder = new StringBuilder();
        AppendExceptionClassificationText(builder, ex);

        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                AppendExceptionClassificationText(builder, inner);
                var innerRoot = inner.GetBaseException();
                if (!ReferenceEquals(innerRoot, inner))
                    AppendExceptionClassificationText(builder, innerRoot);
            }
        }

        var root = ex.GetBaseException();
        if (!ReferenceEquals(root, ex))
            AppendExceptionClassificationText(builder, root);

        return builder.ToString();
    }

    private static void AppendExceptionClassificationText(StringBuilder builder, Exception ex)
    {
        builder.AppendLine(ex.GetType().FullName);
        if (!string.IsNullOrWhiteSpace(ex.Source))
            builder.Append("Source: ").AppendLine(ex.Source);

        var targetType = ex.TargetSite?.DeclaringType?.FullName;
        if (!string.IsNullOrWhiteSpace(targetType))
            builder.Append("Target: ").Append(targetType).Append('.').AppendLine(ex.TargetSite!.Name);

        builder.AppendLine(ex.ToString());
    }

    private static bool ContainsMogtomeFrame(string text)
        => text.Contains("MOGTOME.", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Source: MOGTOME", StringComparison.OrdinalIgnoreCase);

    private static string? ClassifyKnownExternalSource(string text)
    {
        if (text.Contains("SomethingNeedDoing", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Something Need Doing", StringComparison.OrdinalIgnoreCase))
        {
            return "SomethingNeedDoing";
        }

        if (text.Contains("AutoDuty", StringComparison.OrdinalIgnoreCase))
            return "AutoDuty";

        if (text.Contains("Dalamud.Plugin.Internal", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Dalamud.Plugin.PluginManager", StringComparison.OrdinalIgnoreCase)
            || text.Contains("PluginManager", StringComparison.OrdinalIgnoreCase)
            || text.Contains("plugin manager", StringComparison.OrdinalIgnoreCase))
        {
            return "Dalamud Plugin Manager";
        }

        if (text.Contains("PandorasBox.", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Pandora's Box", StringComparison.OrdinalIgnoreCase))
        {
            return "Pandora's Box";
        }

        if (text.Contains("RotationSolverReborn", StringComparison.OrdinalIgnoreCase))
            return "RotationSolverReborn";

        if (text.Contains("TwistOfFayte", StringComparison.OrdinalIgnoreCase))
            return "TwistOfFayte";

        return null;
    }

    private static string GetFallbackExceptionSource(Exception ex)
    {
        foreach (var candidate in EnumerateExceptionSources(ex))
        {
            var normalized = NormalizeExceptionSource(candidate);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        return "Unknown";
    }

    private static IEnumerable<string?> EnumerateExceptionSources(Exception ex)
    {
        yield return ex.Source;

        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                yield return inner.Source;
                var innerRoot = inner.GetBaseException();
                if (!ReferenceEquals(innerRoot, inner))
                    yield return innerRoot.Source;
            }
        }

        var root = ex.GetBaseException();
        if (!ReferenceEquals(root, ex))
            yield return root.Source;
    }

    private static string? NormalizeExceptionSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        if (source.Equals("MOGTOME", StringComparison.OrdinalIgnoreCase))
            return "MOGTOME";

        var externalSource = ClassifyKnownExternalSource(source);
        if (externalSource != null)
            return externalSource;

        if (source.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
            || source.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return source;
    }

    private static string UppercaseFirst(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private void OnLogin()
    {
        try
        {
            var localPlayer = Plugin.ObjectTable.LocalPlayer;
            if (localPlayer == null)
            {
                Log.Warning("[Plugin] OnLogin called but LocalPlayer is null");
                return;
            }
            
            var charName = localPlayer.Name.ToString();
            var worldName = localPlayer.HomeWorld.Value.Name.ToString();
            var contentId = PlayerState.ContentId;
            
            Log.Information($"[Plugin] OnLogin: Character={charName}@{worldName}, ContentId={contentId:X16}");
            
            if (ConfigManager.EnsureAccountSelected(contentId, charName, worldName))
            {
                accountInitialized = true;
                ApplyActiveAccountConfiguration();
                if (databaseInitialized)
                    RunHistoryService.LoadRunHistoryFromDatabase();

                Log.Information($"[Plugin] Account selection completed and configuration notified for {charName}@{worldName}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Plugin] Failed during login detection");
        }
    }

    private bool accountInitialized = false;
    private bool databaseInitialized = false;
    private bool wasLoggedIn = false;
    private int loginDetectionDelay = 0;

    private void ApplyActiveAccountConfiguration()
    {
        var activeConfig = Configuration;
        DutyTrackerService.UpdateConfiguration(activeConfig);
        RunHistoryService.UpdateConfiguration(activeConfig);
        ConfigManager.NotifyConfigurationChanged(force: true);
    }

    private void ToggleConfigUi() => ConfigWindow.Toggle();
    private void ToggleMainUi() => MainWindow.Toggle();
}
