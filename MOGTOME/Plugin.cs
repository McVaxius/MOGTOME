using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ImGuiNET;
using MOGTOME.Services;
using MOGTOME.Windows;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace MOGTOME;

public sealed class Plugin : IDalamudPlugin, IDisposable
{
    private const string CommandName = "/mogtome";
    
    private readonly WindowSystem windowSystem = new("MOGTOME");
    private Configuration configuration;
    private MainWindow mainWindow;
    private ConfigWindow configWindow;
    
    // Services
    private DutyManager dutyManager;
    private MaintenanceManager maintenanceManager;
    private StuckDetection stuckDetection;
    private ProgressTracker progressTracker;
    private PartyManager partyManager;
    
    // Update timer
    private DateTime lastUpdate = DateTime.Now;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        try
        {
            PluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
            
            // Initialize services
            Service.PluginInterface = PluginInterface;
            Service.Chat = PluginInterface.GetChatGui();
            Service.CommandManager = PluginInterface.GetCommandManager();
            Service.ClientState = PluginInterface.GetClientState();
            Service.Condition = PluginInterface.GetCondition();
            Service.GameGui = PluginInterface.GetGameGui();
            Service.ObjectTable = PluginInterface.GetObjectTable();
            Service.TargetManager = PluginInterface.GetTargetManager();
            Service.DataManager = PluginInterface.GetDataManager();
            Service.Log = PluginInterface.GetPluginLog();
            Service.Framework = PluginInterface.GetFramework();
            Service.PartyList = PluginInterface.GetPartyList();
            
            // Load configuration
            configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            
            // Initialize windows
            mainWindow = new MainWindow(this, configuration);
            configWindow = new ConfigWindow(this, configuration);
            
            windowSystem.AddWindow(mainWindow);
            windowSystem.AddWindow(configWindow);
            
            // Register command
            Service.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens MOGTOME interface. Use 'config' for settings, 'start' to begin automation, 'stop' to end automation."
            });
            
            // Initialize services
            dutyManager = new DutyManager(configuration);
            maintenanceManager = new MaintenanceManager(configuration);
            stuckDetection = new StuckDetection(configuration);
            progressTracker = new ProgressTracker(configuration);
            partyManager = new PartyManager(configuration);
            
            // Subscribe to events
            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUI;
            
            // Subscribe to framework update for main loop
            Service.Framework.Update += OnFrameworkUpdate;
            
            Service.Log.Information("M.O.G.T.O.M.E. plugin loaded successfully");
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Failed to initialize MOGTOME plugin: {ex.Message}");
            throw;
        }
    }

    public string Name => "M.O.G.T.O.M.E.";
    public IDalamudPluginInterface PluginInterface { get; private set; }
    
    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            var now = DateTime.Now;
            
            // Throttle updates based on configuration
            if ((now - lastUpdate).TotalMilliseconds < configuration.UpdateInterval)
            {
                return;
            }
            
            lastUpdate = now;
            
            // Update configuration
            configuration.Initialize();
            
            // Update services
            partyManager.UpdatePartyStatus();
            stuckDetection.UpdatePosition();
            progressTracker.UpdateSessionTime();
            
            // Main automation logic
            if (configuration.Enabled && configuration.CurrentState != FarmingState.Idle)
            {
                Task.Run(async () => await UpdateAutomation());
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in framework update: {ex.Message}");
        }
    }
    
    private async Task UpdateAutomation()
    {
        try
        {
            switch (configuration.CurrentState)
            {
                case FarmingState.PreDutyCheck:
                    await HandlePreDutyCheck();
                    break;
                    
                case FarmingState.Queueing:
                    await HandleQueueing();
                    break;
                    
                case FarmingState.InDuty:
                    await HandleInDuty();
                    break;
                    
                case FarmingState.Completing:
                    await HandleCompleting();
                    break;
                    
                case FarmingState.PostDuty:
                    await HandlePostDuty();
                    break;
                    
                case FarmingState.StuckRecovery:
                    await HandleStuckRecovery();
                    break;
                    
                case FarmingState.Error:
                    await HandleError();
                    break;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in automation update: {ex.Message}");
            configuration.CurrentState = FarmingState.Error;
        }
    }
    
    private async Task HandlePreDutyCheck()
    {
        try
        {
            Service.Log.Debug("Handling pre-duty check");
            
            // Perform maintenance checks
            var maintenancePerformed = await maintenanceManager.CheckAndPerformMaintenance();
            if (maintenancePerformed)
            {
                await Task.Delay(2000); // Wait for maintenance to complete
            }
            
            // Check if we can proceed
            if (configuration.CanStartAutomation())
            {
                configuration.CurrentState = FarmingState.Queueing;
                Service.Log.Info("Pre-duty check complete, ready to queue");
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in pre-duty check: {ex.Message}");
            configuration.CurrentState = FarmingState.Error;
        }
    }
    
    private async Task HandleQueueing()
    {
        try
        {
            Service.Log.Debug("Handling queueing");
            
            // Coordinate with party if needed
            if (!await partyManager.CoordinateWithParty())
            {
                return; // Wait for party coordination
            }
            
            // Queue for duty
            if (await dutyManager.QueueForDuty())
            {
                Service.Log.Info("Successfully queued for duty");
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in queueing: {ex.Message}");
            configuration.CurrentState = FarmingState.Error;
        }
    }
    
    private async Task HandleInDuty()
    {
        try
        {
            Service.Log.Debug("Handling in-duty state");
            
            // Check if we're still in duty
            if (!dutyManager.IsInDuty())
            {
                Service.Log.Info("No longer in duty, transitioning to post-duty");
                dutyManager.HandleDutyCompleted();
                return;
            }
            
            // Check for stuck detection
            if (stuckDetection.IsStuck())
            {
                if (stuckDetection.ShouldAttemptRecovery())
                {
                    configuration.CurrentState = FarmingState.StuckRecovery;
                    Service.Log.Info("Stuck detected, attempting recovery");
                }
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in in-duty handling: {ex.Message}");
            configuration.CurrentState = FarmingState.Error;
        }
    }
    
    private async Task HandleCompleting()
    {
        try
        {
            Service.Log.Debug("Handling duty completion");
            
            if (await dutyManager.LeaveDuty())
            {
                Service.Log.Info("Successfully left duty");
                configuration.CurrentState = FarmingState.PostDuty;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in duty completion: {ex.Message}");
            configuration.CurrentState = FarmingState.Error;
        }
    }
    
    private async Task HandlePostDuty()
    {
        try
        {
            Service.Log.Debug("Handling post-duty state");
            
            // Wait a moment before next action
            await Task.Delay(5000);
            
            // Check if we should continue
            if (configuration.DailyCounter < configuration.DailyTarget)
            {
                configuration.CurrentState = FarmingState.PreDutyCheck;
                Service.Log.Info("Ready for next duty");
            }
            else
            {
                configuration.CurrentState = FarmingState.Idle;
                Service.Log.Info("Daily target completed, stopping automation");
                Service.Chat.Print("Daily target completed! Automation stopped.");
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in post-duty handling: {ex.Message}");
            configuration.CurrentState = FarmingState.Error;
        }
    }
    
    private async Task HandleStuckRecovery()
    {
        try
        {
            Service.Log.Debug("Handling stuck recovery");
            
            var recoveryAction = stuckDetection.GetRecoveryAction();
            var success = await stuckDetection.ExecuteRecovery(recoveryAction);
            
            if (success)
            {
                Service.Log.Info("Stuck recovery successful");
                stuckDetection.ResetStuckDetection();
                configuration.CurrentState = FarmingState.PreDutyCheck;
            }
            else
            {
                Service.Log.Error("Stuck recovery failed");
                configuration.CurrentState = FarmingState.Error;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in stuck recovery: {ex.Message}");
            configuration.CurrentState = FarmingState.Error;
        }
    }
    
    private async Task HandleError()
    {
        try
        {
            Service.Log.Debug("Handling error state");
            
            // Wait before attempting recovery
            await Task.Delay(10000);
            
            // Try to reset to idle state
            configuration.CurrentState = FarmingState.Idle;
            Service.Log.Info("Reset to idle state after error");
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error in error handling: {ex.Message}");
        }
    }

    private void OnCommand(string command, string args)
    {
        try
        {
            var parts = args?.Trim().ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            
            if (parts.Length == 0 || parts[0] == "window")
            {
                mainWindow.IsOpen = !mainWindow.IsOpen;
                if (mainWindow.IsOpen)
                {
                    Service.Log.Information("M.O.G.T.O.M.E. main window opened");
                }
            }
            else if (parts[0] == "config")
            {
                configWindow.IsOpen = !configWindow.IsOpen;
                if (configWindow.IsOpen)
                {
                    Service.Log.Information("M.O.G.T.O.M.E. configuration window opened");
                }
            }
            else if (parts[0] == "start")
            {
                if (configuration.CanStartAutomation())
                {
                    configuration.CurrentState = FarmingState.PreDutyCheck;
                    Service.Chat.Print("M.O.G.T.O.M.E. automation started!");
                    Service.Log.Information("M.O.G.T.O.M.E. automation started by user command");
                }
                else
                {
                    Service.Chat.Print("Cannot start automation - check conditions");
                    Service.Log.Warning("Cannot start automation - conditions not met");
                }
            }
            else if (parts[0] == "stop")
            {
                configuration.CurrentState = FarmingState.Idle;
                Service.Chat.Print("M.O.G.T.O.M.E. automation stopped");
                Service.Log.Information("M.O.G.T.O.M.E. automation stopped by user command");
            }
            else if (parts[0] == "status")
            {
                var status = $"State: {configuration.CurrentState} | Progress: {configuration.DailyCounter}/{configuration.DailyTarget} | Duty: {configuration.CurrentDuty}";
                Service.Chat.Print($"M.O.G.T.O.M.E. Status: {status}");
                Service.Log.Information($"M.O.G.T.O.M.E. status: {status}");
            }
            else if (parts[0] == "reset")
            {
                configuration.DailyCounter = 0;
                configuration.Save();
                Service.Chat.Print("M.O.G.T.O.M.E. daily counter reset");
                Service.Log.Information("M.O.G.T.O.M.E. daily counter reset by user");
            }
            else if (parts[0] == "debug")
            {
                configuration.DebugMode = !configuration.DebugMode;
                configuration.Save();
                Service.Chat.Print($"M.O.G.T.O.M.E. debug mode: {(configuration.DebugMode ? "ON" : "OFF")}");
                Service.Log.Information($"M.O.G.T.O.M.E. debug mode toggled: {configuration.DebugMode}");
            }
            else
            {
                Service.Chat.Print($"Unknown MOGTOME command: {parts[0]}");
                Service.Chat.Print("Available commands: window, config, start, stop, status, reset, debug");
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error handling command '{command} {args}': {ex.Message}");
            Service.Chat.Print($"Error: {ex.Message}");
        }
    }

    private void DrawUI()
    {
        try
        {
            windowSystem.Draw();
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error drawing UI: {ex.Message}");
        }
    }

    private void OpenConfigUI()
    {
        try
        {
            configWindow.IsOpen = true;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error opening config UI: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            // Unsubscribe from events
            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUI;
            Service.Framework.Update -= OnFrameworkUpdate;
            
            // Unregister command
            Service.CommandManager.RemoveHandler(CommandName);
            
            // Dispose services
            dutyManager?.Dispose();
            maintenanceManager?.Dispose();
            stuckDetection?.Dispose();
            progressTracker?.Dispose();
            partyManager?.Dispose();
            
            // Dispose windows
            windowSystem.RemoveAllWindows();
            mainWindow?.Dispose();
            configWindow?.Dispose();
            
            Service.Log.Information("M.O.G.T.O.M.E. plugin disposed successfully");
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error disposing MOGTOME plugin: {ex.Message}");
        }
    }
}

// Service locator for easy access to Dalamud services
public static class Service
{
    public static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    public static IChatGui Chat { get; set; } = null!;
    public static ICommandManager CommandManager { get; set; } = null!;
    public static IClientState ClientState { get; set; } = null!;
    public static ICondition Condition { get; set; } = null!;
    public static IGameGui GameGui { get; set; } = null!;
    public static IObjectTable ObjectTable { get; set; } = null!;
    public static ITargetManager TargetManager { get; set; } = null!;
    public static IDataManager DataManager { get; set; } = null!;
    public static IPluginLog Log { get; set; } = null!;
    public static IFramework Framework { get; set; } = null!;
    public static IPartyList PartyList { get; set; } = null!;
}
