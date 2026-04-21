using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using MOGTOME.IPC;
using MOGTOME.Models;

namespace MOGTOME.Services;

public sealed record PraetoriumUnlockStatus(string DutyName, bool IsUnlocked, string QuestSummary);

public sealed record PraetoriumSelectionInfo(int SelectionIndex, IReadOnlyList<PraetoriumUnlockStatus> Unlocks)
{
    public int MissingUnlockCount => Unlocks.Count(unlock => !unlock.IsUnlocked);
    public string CallbackCommand => $"ContentsFinder true 3 {SelectionIndex}";
}

public sealed class DutyAutomationService
{
    private const string AdsInternalName = "ADS";
    private const string AdsCommand = "/ads";
    private const string AdsStartOutsideCommand = "/ads outside";
    private const string AdsStartInsideCommand = "/ads inside";
    private const string AdsLeaveCommand = "/ads leave";
    private const string AdsStopCommand = "/ads stop";
    private const string AdsEnterInnCommand = "/ads enterinn";
    private const string AdsSelfRepairCommand = "/ads selfrepair";
    private const string AdsNpcRepairCommand = "/ads npcrepair";
    private const int AdsVisiblePollDelayMs = 500;
    private const int AdsVisiblePollAttempts = 20;
    private const int AdsInitialSettleDelayMs = 1500;
    private const int AdsStepDelayMs = 700;
    private const int AdsPostJoinDelayMs = 1500;
    private const int AdsMaxSequenceAttempts = 3;
    private const int QueueConditionIndex = 91;
    private const int WaitingForDutyConditionIndex = 55;
    private const int WaitingForDutyFinderConditionIndex = 59;

    private enum AdsQueuedDuty
    {
        Unknown,
        Praetorium,
        Decumana,
    }

    private readonly IPluginLog log;
    private readonly ConfigManager configManager;
    private readonly AutoDutyIPC autoDutyIPC;
    private readonly AutoDutyPathService autoDutyPathService;
    private readonly ConflictPluginService conflictPluginService;
    private readonly ICommandManager commandManager;
    private readonly RunHistoryService runHistoryService;
    private readonly RotationService rotationService;
    private int adsQueueOperationId = 0;
    private AdsQueuedDuty lastAdsQueuedDuty = AdsQueuedDuty.Unknown;
    private static readonly PraetoriumUnlockDefinition[] PraetoriumOptionalUnlocks =
    [
        new("Sunken Temple of Qarn", [764]),
        new("Cutter's Cry", [921]),
        new("Dzemael Darkhold", [979, 1128, 1129, 1130]),
        new("The Aurum Vale", [1014, 1131, 1132, 1133]),
        new("The Wanderer's Palace", [870]),
    ];

    private Configuration Config => configManager.GetActiveConfig();

    public DutyAutomationService(
        IPluginLog log,
        ConfigManager configManager,
        AutoDutyIPC autoDutyIPC,
        AutoDutyPathService autoDutyPathService,
        ConflictPluginService conflictPluginService,
        ICommandManager commandManager,
        RunHistoryService runHistoryService,
        RotationService rotationService)
    {
        this.log = log;
        this.configManager = configManager;
        this.autoDutyIPC = autoDutyIPC;
        this.autoDutyPathService = autoDutyPathService;
        this.conflictPluginService = conflictPluginService;
        this.commandManager = commandManager;
        this.runHistoryService = runHistoryService;
        this.rotationService = rotationService;
    }

    public bool UseAdsExperimental
        => Config.UseAdsExperimental;

    public string ActiveBackendDisplayName
        => UseAdsExperimental ? "ADS" : "AutoDuty";

    public string StopCommandLabel
        => UseAdsExperimental ? AdsStopCommand : "/ad stop";

    public bool IsAdsLoaded()
        => IsPluginLoaded(AdsInternalName);

    public bool IsAutoDutyLoaded()
        => IsPluginLoaded("AutoDuty");

    public async Task<bool> PrepareForStartAsync(bool isLeader, bool startingInsideDuty)
    {
        if (UseAdsExperimental)
        {
            log.Information("[MOGTOME][Automation] Preparing ADS backend");
            await conflictPluginService.EnsureAutoDutyDisabledAsync("MOGTOME ADS start", showPopup: false).ConfigureAwait(false);

            if (!IsAdsLoaded())
            {
                const string message = "ADS experimental mode is enabled, but ADS is not loaded.";
                log.Warning($"[MOGTOME][Automation] {message}");
                Plugin.ChatGui.Print($"[MOGTOME] {message}");
                return false;
            }

            log.Information("[MOGTOME][Automation] ADS backend ready");
            return true;
        }

        log.Information("[MOGTOME][Automation] Preparing AutoDuty backend");
        var autoDutyReady = await autoDutyPathService.WaitForAutoDutyInitializationAsync(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        if (!autoDutyReady)
        {
            const string startupFailure = "AutoDuty is still initializing or faulted; retry MOGTOME after login settles";
            log.Warning($"[MOGTOME][Automation] {startupFailure}");
            Plugin.ChatGui.Print($"[MOGTOME] {startupFailure}");
            return false;
        }

        var bundledPathsReady = await autoDutyPathService.EnsurePathExists().ConfigureAwait(false);
        if (!bundledPathsReady)
        {
            const string pathFailure = "Bundled Praetorium paths could not be installed into AutoDuty.";
            log.Warning($"[MOGTOME][Automation] {pathFailure}");
            Plugin.ChatGui.Print($"[MOGTOME] {pathFailure}");
            return false;
        }

        autoDutyIPC.StopDuty();
        autoDutyIPC.ConfigureForMogtome(isLeader);

        if (!startingInsideDuty)
            autoDutyPathService.ForcePathSelection(Config.PraetoriumPathFileName);

        log.Information("[MOGTOME][Automation] AutoDuty backend ready");
        return true;
    }

    public async Task EnsureAutoDutyDisabledForAdsAsync(string triggerSource)
    {
        if (!UseAdsExperimental)
            return;

        await conflictPluginService.EnsureAutoDutyDisabledAsync(triggerSource, showPopup: false).ConfigureAwait(false);
    }

    public void ApplyQueueConfiguration(bool testingModeUnsynced)
    {
        if (UseAdsExperimental)
            return;

        autoDutyIPC.SetConfig("Unsynced", "true");
        autoDutyIPC.SetConfig("LevelSync", testingModeUnsynced ? "false" : "true");
    }

    public void QueueDuty(bool isPraetorium)
    {
        var dutyName = GetDutyName(isPraetorium);
        if (!UseAdsExperimental)
        {
            autoDutyIPC.QueueDuty(dutyName);
            return;
        }

        log.Information($"[MOGTOME][ADS] Claiming outside ownership, then queueing {dutyName} through AgentContentsFinder");
        commandManager.ProcessCommand(AdsStartOutsideCommand);
        QueueDutyViaContentsFinder(
            GetContentFinderConditionId(isPraetorium),
            dutyName,
            isPraetorium ? AdsQueuedDuty.Praetorium : AdsQueuedDuty.Decumana);
    }

    public void StartDutyInside()
    {
        if (!UseAdsExperimental)
        {
            autoDutyIPC.StartDuty();
            return;
        }

        CapturePartySnapshot("ADS");
        log.Information($"[MOGTOME][ADS] Starting via {AdsStartInsideCommand}");
        commandManager.ProcessCommand(AdsStartInsideCommand);
        RefreshCombatStack("ADS");
    }

    public void StopDuty()
    {
        if (!UseAdsExperimental)
        {
            autoDutyIPC.StopDuty();
            return;
        }

        InvalidateAdsQueueOperations();
        log.Information($"[MOGTOME][ADS] Stopping via {AdsStopCommand}");
        commandManager.ProcessCommand(AdsStopCommand);
    }

    public void RequestDutyLeave()
    {
        if (!UseAdsExperimental)
            return;

        log.Information($"[MOGTOME][ADS] Leaving via {AdsLeaveCommand}");
        commandManager.ProcessCommand(AdsLeaveCommand);
    }

    public void RequestSelfRepair()
    {
        if (!UseAdsExperimental)
        {
            log.Information("[MOGTOME][Repair] Requesting repair via AutoDuty");
            commandManager.ProcessCommand("/ad repair");
            return;
        }

        log.Information($"[MOGTOME][Repair] Requesting self-repair via {AdsSelfRepairCommand}");
        commandManager.ProcessCommand(AdsSelfRepairCommand);
    }

    public void RequestNpcRepair()
    {
        if (!UseAdsExperimental)
        {
            log.Information("[MOGTOME][Repair] Attempting NPC repair via AutoDuty");
            commandManager.ProcessCommand("/ad repair");
            return;
        }

        log.Information($"[MOGTOME][Repair] Requesting NPC repair via {AdsNpcRepairCommand}");
        commandManager.ProcessCommand(AdsNpcRepairCommand);
    }

    public void ReturnToInnIfNeeded()
    {
        try
        {
            var territoryId = (ushort)Plugin.ClientState.TerritoryType;
            var territoryName = GameHelpers.GetTerritoryName(territoryId);
            if (GameHelpers.IsInnTerritory(territoryId))
            {
                log.Information($"[MOGTOME][Repair] Repair completed while already in inn territory {territoryName} ({territoryId})");
                return;
            }

            if (!UseAdsExperimental)
            {
                log.Information($"[MOGTOME][Repair] Repair completed outside inn territory {territoryName} ({territoryId}); sending internal /mog inn auto command");
                GameHelpers.SendChatCommand("/mog inn auto");
                return;
            }

            log.Information($"[MOGTOME][Repair] Repair completed outside inn territory {territoryName} ({territoryId}); sending {AdsEnterInnCommand}");
            commandManager.ProcessCommand(AdsEnterInnCommand);
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Repair] ReturnToInnIfNeeded failed: {ex.Message}");
        }
    }

    public string GetQueueStatusLabel()
        => ActiveBackendDisplayName;

    public string GetSubsystemStatusLabel()
        => UseAdsExperimental ? "ADS experimental mode" : autoDutyPathService.GetPraetoriumPathDisplayName(Config.PraetoriumPathFileName);

    public bool GetSubsystemHealthy()
        => UseAdsExperimental ? IsAdsLoaded() : autoDutyPathService.PathExists(Config.PraetoriumPathFileName);

    public PraetoriumSelectionInfo GetPraetoriumSelectionInfo()
    {
        var unlocks = PraetoriumOptionalUnlocks
            .Select(definition =>
            {
                var isUnlocked = IsAnyQuestComplete(definition.QuestIds);
                var questSummary = string.Join(", ", definition.QuestIds);
                return new PraetoriumUnlockStatus(definition.DutyName, isUnlocked, questSummary);
            })
            .ToArray();

        var missingUnlockCount = unlocks.Count(unlock => !unlock.IsUnlocked);
        var selectionIndex = Math.Clamp(15 - missingUnlockCount, 10, 15);
        return new PraetoriumSelectionInfo(selectionIndex, unlocks);
    }

    public void LogPraetoriumSelectionInfo()
    {
        var selectionInfo = GetPraetoriumSelectionInfo();
        var missingDuties = selectionInfo.Unlocks
            .Where(unlock => !unlock.IsUnlocked)
            .Select(unlock => unlock.DutyName)
            .ToArray();
        var missingSummary = missingDuties.Length > 0
            ? string.Join(", ", missingDuties)
            : "none";

        log.Information($"[MOGTOME][ADS] Praetorium callback test -> {selectionInfo.CallbackCommand} (missing optional unlocks: {selectionInfo.MissingUnlockCount})");
        foreach (var unlock in selectionInfo.Unlocks)
        {
            log.Information($"[MOGTOME][ADS] Praetorium unlock check: {unlock.DutyName} -> {(unlock.IsUnlocked ? "unlocked" : "missing")} (quests: {unlock.QuestSummary})");
        }

        Plugin.ChatGui.Print($"[MOGTOME] Praetorium callback test: {selectionInfo.CallbackCommand}");
        Plugin.ChatGui.Print($"[MOGTOME] Missing optional unlocks: {missingSummary}");
    }

    private static string GetDutyName(bool isPraetorium)
        => isPraetorium ? "The Praetorium" : "The Porta Decumana";

    private static uint GetContentFinderConditionId(bool isPraetorium)
        => isPraetorium ? 16u : DutyState.DecumanaDutyId;

    private static bool IsPluginLoaded(string internalName)
    {
        try
        {
            return Plugin.PluginInterface.InstalledPlugins.Any(plugin =>
                plugin.IsLoaded &&
                string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private unsafe bool OpenDutyFinderForDuty(uint contentFinderConditionId, string dutyName)
    {
        try
        {
            var agent = AgentContentsFinder.Instance();
            if (agent == null)
            {
                log.Error($"[MOGTOME][ADS] AgentContentsFinder is null while trying to queue {dutyName}");
                return false;
            }

            agent->OpenRegularDuty(contentFinderConditionId);
            log.Information($"[MOGTOME][ADS] Opened duty finder for {dutyName} (CFC {contentFinderConditionId})");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][ADS] Failed to open duty finder for {dutyName}: {ex.Message}");
            return false;
        }
    }

    private void QueueDutyViaContentsFinder(uint contentFinderConditionId, string dutyName, AdsQueuedDuty targetDuty)
    {
        if (!OpenDutyFinderForDuty(contentFinderConditionId, dutyName))
            return;

        var operationId = Interlocked.Increment(ref adsQueueOperationId);
        _ = Task.Run(() => ExecuteAdsQueueSequenceAsync(operationId, targetDuty, dutyName));
    }

    private async Task ExecuteAdsQueueSequenceAsync(int operationId, AdsQueuedDuty targetDuty, string dutyName)
    {
        if (!await WaitForContentsFinderVisibleAsync(operationId, dutyName).ConfigureAwait(false))
        {
            log.Warning($"[MOGTOME][ADS] ContentsFinder never became visible while queueing {dutyName}; queue watchdog will retry");
            return;
        }

        log.Information($"[MOGTOME][ADS] ContentsFinder visible for {dutyName}; running {GetSequenceLabel(targetDuty)}");
        await Task.Delay(AdsInitialSettleDelayMs).ConfigureAwait(false);

        for (var attempt = 1; attempt <= AdsMaxSequenceAttempts; attempt++)
        {
            if (!IsCurrentAdsQueueOperation(operationId))
                return;

            if (!GameHelpers.IsAddonVisible("ContentsFinder"))
            {
                log.Warning($"[MOGTOME][ADS] ContentsFinder closed before {dutyName} selection finished; queue watchdog will retry");
                return;
            }

            log.Information($"[MOGTOME][ADS] Running {GetSequenceLabel(targetDuty)} (attempt {attempt}/{AdsMaxSequenceAttempts})");
            if (!await RunAdsDutySelectionSequenceAsync(operationId, targetDuty, dutyName).ConfigureAwait(false))
            {
                log.Warning($"[MOGTOME][ADS] {dutyName} selection sequence aborted on attempt {attempt}; queue watchdog will retry");
                return;
            }

            if (!IsCurrentAdsQueueOperation(operationId) || !GameHelpers.IsAddonVisible("ContentsFinder"))
                return;

            log.Information($"[MOGTOME][ADS] Clicking Join for {dutyName} after staged selection");
            GameHelpers.FireAddonCallback("ContentsFinder", true, 12, 0);
            await Task.Delay(AdsPostJoinDelayMs).ConfigureAwait(false);

            if (!IsCurrentAdsQueueOperation(operationId))
                return;

            if (IsQueueRegistrationActive() ||
                GameHelpers.IsAddonVisible("ContentsFinderConfirm") ||
                !GameHelpers.IsAddonVisible("ContentsFinder"))
            {
                lastAdsQueuedDuty = targetDuty;
                log.Information($"[MOGTOME][ADS] {dutyName} queue flow accepted; waiting for duty registration");
                return;
            }

            log.Warning($"[MOGTOME][ADS] {dutyName} join attempt {attempt} did not register queue yet; retrying staged selection");
            await Task.Delay(AdsStepDelayMs).ConfigureAwait(false);
        }

        log.Warning($"[MOGTOME][ADS] {dutyName} queue sequence failed after {AdsMaxSequenceAttempts} attempts; queue watchdog will retry");
    }

    private async Task<bool> WaitForContentsFinderVisibleAsync(int operationId, string dutyName)
    {
        for (var attempt = 1; attempt <= AdsVisiblePollAttempts; attempt++)
        {
            if (!IsCurrentAdsQueueOperation(operationId))
                return false;

            if (GameHelpers.IsAddonVisible("ContentsFinder"))
                return true;

            await Task.Delay(AdsVisiblePollDelayMs).ConfigureAwait(false);
        }

        log.Warning($"[MOGTOME][ADS] Timed out waiting for ContentsFinder while queueing {dutyName}");
        return false;
    }

    private async Task<bool> RunAdsDutySelectionSequenceAsync(int operationId, AdsQueuedDuty targetDuty, string dutyName)
    {
        var praetoriumSelectionIndex = GetPraetoriumSelectionInfo().SelectionIndex;

        switch (targetDuty)
        {
            case AdsQueuedDuty.Praetorium:
                if (lastAdsQueuedDuty == AdsQueuedDuty.Decumana)
                {
                    if (!await FireAdsContentsFinderStepAsync(operationId, dutyName, "switching to Decumana tab to clear prior selection", 1, 4).ConfigureAwait(false) ||
                        !await FireAdsContentsFinderStepAsync(operationId, dutyName, "selecting Decumana to clear prior selection", 3, 4).ConfigureAwait(false) ||
                        !await FireAdsContentsFinderStepAsync(operationId, dutyName, "unselecting Decumana before Praetorium", 3, 4).ConfigureAwait(false))
                    {
                        return false;
                    }
                }

                return await FireAdsContentsFinderStepAsync(operationId, dutyName, "switching to Praetorium tab", 1, 1).ConfigureAwait(false) &&
                       await FireAdsContentsFinderStepAsync(operationId, dutyName, "selecting Praetorium", 3, praetoriumSelectionIndex).ConfigureAwait(false);

            case AdsQueuedDuty.Decumana:
                return await FireAdsContentsFinderStepAsync(operationId, dutyName, "switching to Praetorium tab for Decumana pre-clear", 1, 1).ConfigureAwait(false) &&
                       await FireAdsContentsFinderStepAsync(operationId, dutyName, "selecting Praetorium for Decumana pre-clear", 3, praetoriumSelectionIndex).ConfigureAwait(false) &&
                       await FireAdsContentsFinderStepAsync(operationId, dutyName, "unselecting Praetorium before Decumana", 3, praetoriumSelectionIndex).ConfigureAwait(false) &&
                       await FireAdsContentsFinderStepAsync(operationId, dutyName, "switching to Decumana tab", 1, 4).ConfigureAwait(false) &&
                       await FireAdsContentsFinderStepAsync(operationId, dutyName, "selecting Decumana", 3, 4).ConfigureAwait(false);

            default:
                return false;
        }
    }

    private async Task<bool> FireAdsContentsFinderStepAsync(int operationId, string dutyName, string stepName, int arg1, int arg2)
    {
        if (!IsCurrentAdsQueueOperation(operationId))
            return false;

        if (!GameHelpers.IsAddonVisible("ContentsFinder"))
        {
            log.Warning($"[MOGTOME][ADS] ContentsFinder disappeared before {stepName} for {dutyName}");
            return false;
        }

        log.Information($"[MOGTOME][ADS] {stepName} for {dutyName} via ContentsFinder true {arg1} {arg2}");
        GameHelpers.FireAddonCallback("ContentsFinder", true, arg1, arg2);
        await Task.Delay(AdsStepDelayMs).ConfigureAwait(false);

        return IsCurrentAdsQueueOperation(operationId);
    }

    private void InvalidateAdsQueueOperations()
    {
        Interlocked.Increment(ref adsQueueOperationId);
    }

    private bool IsCurrentAdsQueueOperation(int operationId)
        => Volatile.Read(ref adsQueueOperationId) == operationId;

    private static string GetSequenceLabel(AdsQueuedDuty targetDuty)
        => targetDuty switch
        {
            AdsQueuedDuty.Praetorium => "Praetorium queue sequence",
            AdsQueuedDuty.Decumana => "Decumana queue sequence",
            _ => "ADS queue sequence",
        };

    private static bool IsQueueRegistrationActive()
        => Plugin.Condition[QueueConditionIndex]
           || Plugin.Condition[WaitingForDutyConditionIndex]
           || Plugin.Condition[WaitingForDutyFinderConditionIndex];

    private void CapturePartySnapshot(string backendName)
    {
        try
        {
            runHistoryService.CapturePartySnapshot();
            log.Information($"[MOGTOME][{backendName}] Party snapshot captured before duty start");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[MOGTOME][{backendName}] Failed to capture party snapshot before duty start");
        }
    }

    private void RefreshCombatStack(string backendName)
    {
        try
        {
            rotationService.ForceRotation();
            log.Information($"[MOGTOME][{backendName}] BossMod AI + RSR refreshed after duty start");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[MOGTOME][{backendName}] Failed to refresh BossMod AI + RSR after duty start");
        }
    }

    private static unsafe bool IsAnyQuestComplete(ushort[] questIds)
    {
        if (QuestManager.Instance() == null)
            return false;

        foreach (var questId in questIds)
        {
            if (QuestManager.IsQuestComplete(questId))
                return true;
        }

        return false;
    }

    private sealed record PraetoriumUnlockDefinition(string DutyName, ushort[] QuestIds);
}
