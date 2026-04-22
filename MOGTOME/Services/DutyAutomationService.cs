using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
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
    private const int AdsStableVisiblePollsRequired = 2;
    private const int AdsStableVisiblePollDelayMs = 250;
    private const int AdsStableVisiblePollAttempts = 20;
    private const int AdsJoinFailureCooldownSeconds = 5;
    private const string NoDutySelectedErrorText = "No duty has been selected.";
    private const string PartyRequirementsErrorText = "One of your party members does not meet the requirements for this duty.";
    private const int QueueConditionIndex = 91;
    private const int WaitingForDutyConditionIndex = 55;
    private const int WaitingForDutyFinderConditionIndex = 59;

    private enum SelectedMogtomeDuty
    {
        Unknown,
        Praetorium,
        Decumana,
    }

    private enum AdsRuntimeRole
    {
        None,
        QueueLeader,
        Follower,
    }

    private enum AdsFollowerState
    {
        None,
        OutsideArmed,
        WaitingForEntry,
        InsideObserved,
        Leaving,
        Recovered,
    }

    private readonly IPluginLog log;
    private readonly ConfigManager configManager;
    private readonly AutoDutyIPC autoDutyIPC;
    private readonly AutoDutyPathService autoDutyPathService;
    private readonly ConflictPluginService conflictPluginService;
    private readonly ICommandManager commandManager;
    private readonly RunHistoryService runHistoryService;
    private readonly RotationService rotationService;
    private readonly object adsQueueStateLock = new();
    private int adsQueueOperationId = 0;
    private int activeAdsQueueOperationId = 0;
    private int lastNoDutySelectedLoggedOperationId = 0;
    private int lastPartyRequirementsLoggedOperationId = 0;
    private bool adsQueueFailurePending = false;
    private string adsQueueFailureReason = string.Empty;
    private DateTime adsQueueFailureCooldownUntilUtc = DateTime.MinValue;
    private SelectedMogtomeDuty lastConfirmedSelectedDuty = SelectedMogtomeDuty.Unknown;
    private AdsRuntimeRole adsRuntimeRole = AdsRuntimeRole.None;
    private AdsFollowerState adsFollowerState = AdsFollowerState.None;
    private bool adsStartingInsideDutyRecovery = false;
    private bool adsLeaderOutsideOwned = false;
    private bool adsLeaderInsideOwned = false;
    private bool adsLeaveRequested = false;
    private bool adsLeaveConfirmationObserved = false;
    private DateTime adsLastOutsideArmUtc = DateTime.MinValue;
    private DateTime adsLastLeaveRequestUtc = DateTime.MinValue;
    private const float AdsFollowerOutsideArmRetrySeconds = 5.0f;
    private const float AdsFollowerOutsideArmVisibleSeconds = 1.0f;
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

            SetAdsRuntimeRole(isLeader, startingInsideDuty);
            //if (!isLeader && !startingInsideDuty)
                EnsureFollowerOutsideArmed("startup prep");

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

        adsRuntimeRole = AdsRuntimeRole.QueueLeader;
        adsLeaderOutsideOwned = true;
        adsLeaderInsideOwned = false;
        ResetAdsLeaveTracking();
        log.Information($"[MOGTOME][ADS] Leader queue-control path selected for {dutyName}; claiming /ads outside before ContentsFinder queue");
        commandManager.ProcessCommand(AdsStartOutsideCommand);
        QueueDutyViaContentsFinder(
            GetContentFinderConditionId(isPraetorium),
            dutyName,
            isPraetorium ? SelectedMogtomeDuty.Praetorium : SelectedMogtomeDuty.Decumana);
    }

    public void StartDutyInside(bool isLeader)
    {
        if (!UseAdsExperimental)
        {
            autoDutyIPC.StartDuty();
            return;
        }

        CapturePartySnapshot("ADS");
        RefreshCombatStack("ADS");

        if (isLeader)
        {
            adsRuntimeRole = AdsRuntimeRole.QueueLeader;
            adsLeaderOutsideOwned = false;
            adsLeaderInsideOwned = true;
            ResetAdsLeaveTracking();
            log.Information($"[MOGTOME][ADS] Leader entered duty; taking inside ownership via {AdsStartInsideCommand}");
            commandManager.ProcessCommand(AdsStartInsideCommand);
            adsStartingInsideDutyRecovery = false;
            return;
        }

        adsRuntimeRole = AdsRuntimeRole.Follower;
        if (adsStartingInsideDutyRecovery)
        {
            adsFollowerState = AdsFollowerState.InsideObserved;
            ResetAdsLeaveTracking();
            log.Warning($"[MOGTOME][ADS] Follower started inside duty; using {AdsStartInsideCommand} as manual recovery path");
            commandManager.ProcessCommand(AdsStartInsideCommand);
            adsStartingInsideDutyRecovery = false;
            return;
        }

        var previousState = adsFollowerState;
        adsFollowerState = AdsFollowerState.InsideObserved;
        ResetAdsLeaveTracking();
        adsStartingInsideDutyRecovery = false;
        log.Information($"[MOGTOME][ADS] Follower observed duty entry after /ads outside pre-arm; skipping normal {AdsStartInsideCommand} (previous state: {previousState})");
    }

    public void StopDuty()
    {
        if (!UseAdsExperimental)
        {
            autoDutyIPC.StopDuty();
            return;
        }

        InvalidateAdsQueueOperations("ads stop");
        ResetAdsLeaveTracking();
        adsLeaderOutsideOwned = false;
        adsLeaderInsideOwned = false;
        if (adsRuntimeRole == AdsRuntimeRole.Follower)
            adsFollowerState = AdsFollowerState.Recovered;
        log.Information($"[MOGTOME][ADS] Stopping via {AdsStopCommand}");
        commandManager.ProcessCommand(AdsStopCommand);
    }

    public void RequestDutyLeave(string reason, DateTime dutyCompletedUtc, int attemptNumber)
    {
        if (!UseAdsExperimental)
            return;

        adsLeaveRequested = true;
        adsLeaveConfirmationObserved = false;
        adsLastLeaveRequestUtc = DateTime.UtcNow;
        adsLeaderInsideOwned = false;
        if (adsRuntimeRole == AdsRuntimeRole.Follower)
            adsFollowerState = AdsFollowerState.Leaving;

        var deltaSeconds = dutyCompletedUtc == DateTime.MinValue
            ? -1.0
            : (adsLastLeaveRequestUtc - dutyCompletedUtc).TotalSeconds;
        var deltaText = deltaSeconds < 0 ? "n/a" : $"+{deltaSeconds:F1}s";
        log.Information($"[MOGTOME][ADS] /ads leave request #{attemptNumber} ({GetAdsRoleLabel()}) at {deltaText} from duty-complete - {reason}");
        commandManager.ProcessCommand(AdsLeaveCommand);
    }

    public void RequestDutyLeave()
        => RequestDutyLeave("ADS leave requested.", DateTime.MinValue, 1);

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
        => UseAdsExperimental
            ? adsRuntimeRole switch
            {
                AdsRuntimeRole.QueueLeader => "Queue leader",
                AdsRuntimeRole.Follower => "Follower waiting on leader",
                _ => "ADS",
            }
            : ActiveBackendDisplayName;

    public string GetAdsRuntimeStatusLabel()
    {
        if (!UseAdsExperimental)
            return ActiveBackendDisplayName;

        if (adsLeaveRequested)
        {
            return adsLeaveConfirmationObserved
                ? "leave requested, waiting for zone-out"
                : "leave requested";
        }

        return adsRuntimeRole switch
        {
            AdsRuntimeRole.QueueLeader when adsLeaderInsideOwned => "ADS inside-owned",
            AdsRuntimeRole.QueueLeader when adsLeaderOutsideOwned => "ADS outside-owned",
            AdsRuntimeRole.QueueLeader => "leader-ready",
            AdsRuntimeRole.Follower => adsFollowerState switch
            {
                AdsFollowerState.OutsideArmed => "outside-armed",
                AdsFollowerState.WaitingForEntry => "waiting-for-entry",
                AdsFollowerState.InsideObserved => "inside-observed",
                AdsFollowerState.Leaving => "leaving",
                AdsFollowerState.Recovered => "recovered",
                _ => "waiting-for-entry",
            },
            _ => "idle",
        };
    }

    public string GetSubsystemStatusLabel()
        => UseAdsExperimental ? "ADS experimental mode" : autoDutyPathService.GetPraetoriumPathDisplayName(Config.PraetoriumPathFileName);

    public bool GetSubsystemHealthy()
        => UseAdsExperimental ? IsAdsLoaded() : autoDutyPathService.PathExists(Config.PraetoriumPathFileName);

    public void EnsureFollowerOutsideArmed(string reason)
    {
        if (!UseAdsExperimental || adsRuntimeRole != AdsRuntimeRole.Follower || Plugin.Condition[ConditionFlag.BoundByDuty])
            return;

        if (adsFollowerState == AdsFollowerState.OutsideArmed &&
            (DateTime.UtcNow - adsLastOutsideArmUtc).TotalSeconds >= AdsFollowerOutsideArmVisibleSeconds)
        {
            adsFollowerState = AdsFollowerState.WaitingForEntry;
            return;
        }

        if (adsFollowerState is AdsFollowerState.WaitingForEntry or AdsFollowerState.InsideObserved)
            return;

        if ((DateTime.UtcNow - adsLastOutsideArmUtc).TotalSeconds < AdsFollowerOutsideArmRetrySeconds)
            return;

        adsLastOutsideArmUtc = DateTime.UtcNow;
        adsFollowerState = AdsFollowerState.OutsideArmed;
        ResetAdsLeaveTracking();
        log.Information($"[MOGTOME][ADS] Follower arming via {AdsStartOutsideCommand} while outside duty ({reason})");
        commandManager.ProcessCommand(AdsStartOutsideCommand);
    }

    public void UpdateFollowerWaitingForEntry()
    {
        if (!UseAdsExperimental || adsRuntimeRole != AdsRuntimeRole.Follower || Plugin.Condition[ConditionFlag.BoundByDuty])
            return;

        if (adsFollowerState == AdsFollowerState.OutsideArmed &&
            (DateTime.UtcNow - adsLastOutsideArmUtc).TotalSeconds >= AdsFollowerOutsideArmVisibleSeconds)
        {
            adsFollowerState = AdsFollowerState.WaitingForEntry;
        }
    }

    public void ObserveLeaveConfirmationEvidence(string evidence)
    {
        if (!UseAdsExperimental || !adsLeaveRequested || adsLeaveConfirmationObserved)
            return;

        adsLeaveConfirmationObserved = true;
        var elapsed = adsLastLeaveRequestUtc == DateTime.MinValue
            ? 0.0
            : (DateTime.UtcNow - adsLastLeaveRequestUtc).TotalSeconds;
        log.Information($"[MOGTOME][ADS] First leave confirmation evidence after {elapsed:F1}s: {evidence}");
    }

    public void NotifyDutyLeft()
    {
        if (!UseAdsExperimental)
            return;

        if (adsLeaveRequested)
        {
            var elapsed = adsLastLeaveRequestUtc == DateTime.MinValue
                ? 0.0
                : (DateTime.UtcNow - adsLastLeaveRequestUtc).TotalSeconds;
            log.Information($"[MOGTOME][ADS] Duty left after leave request in {elapsed:F1}s ({GetAdsRoleLabel()})");
        }

        ResetAdsLeaveTracking();
        adsLeaderOutsideOwned = false;
        adsLeaderInsideOwned = false;
        if (adsRuntimeRole == AdsRuntimeRole.Follower)
            adsFollowerState = AdsFollowerState.Recovered;
    }

    public void HandleAdsQueueChatMessage(string messageText)
    {
        if (!UseAdsExperimental)
            return;

        var text = messageText.Trim();
        if (!string.Equals(text, NoDutySelectedErrorText, StringComparison.Ordinal) &&
            !string.Equals(text, PartyRequirementsErrorText, StringComparison.Ordinal))
        {
            return;
        }

        var operationId = GetActiveAdsQueueOperationId();
        if (operationId == 0)
            return;

        if (string.Equals(text, NoDutySelectedErrorText, StringComparison.Ordinal))
        {
            if (Interlocked.Exchange(ref lastNoDutySelectedLoggedOperationId, operationId) != operationId)
                log.Warning($"[MOGTOME][DutyQueue] Queue selection failed from chat error; aborting operation {operationId}");

            MarkAdsQueueAttemptFailed(operationId, "No duty selected chat error", invalidateOperation: true);
            return;
        }

        if (Interlocked.Exchange(ref lastPartyRequirementsLoggedOperationId, operationId) != operationId)
        {
            log.Information($"[MOGTOME][DutyQueue] Party requirements chat evidence during operation {operationId}; not blocking for cross-world compatibility: {PartyRequirementsErrorText}");
        }
    }

    public bool TryGetAdsQueueFailure(out string reason, out double cooldownRemainingSeconds)
    {
        lock (adsQueueStateLock)
        {
            reason = adsQueueFailureReason;
            cooldownRemainingSeconds = Math.Max(0, (adsQueueFailureCooldownUntilUtc - DateTime.UtcNow).TotalSeconds);
            return adsQueueFailurePending;
        }
    }

    public void ClearAdsQueueFailure()
    {
        lock (adsQueueStateLock)
        {
            adsQueueFailurePending = false;
            adsQueueFailureReason = string.Empty;
            adsQueueFailureCooldownUntilUtc = DateTime.MinValue;
        }
    }

    public bool IsAdsQueueRetryCooldownActive(out double cooldownRemainingSeconds, out string reason)
    {
        if (!UseAdsExperimental)
        {
            cooldownRemainingSeconds = 0;
            reason = string.Empty;
            return false;
        }

        lock (adsQueueStateLock)
        {
            reason = adsQueueFailureReason;
            cooldownRemainingSeconds = Math.Max(0, (adsQueueFailureCooldownUntilUtc - DateTime.UtcNow).TotalSeconds);
            return cooldownRemainingSeconds > 0;
        }
    }

    public void ConfirmQueueRegistration(bool isPraetorium)
    {
        if (!UseAdsExperimental)
            return;

        var targetDuty = isPraetorium ? SelectedMogtomeDuty.Praetorium : SelectedMogtomeDuty.Decumana;
        var previousDuty = SelectedMogtomeDuty.Unknown;
        lock (adsQueueStateLock)
        {
            previousDuty = lastConfirmedSelectedDuty;
            lastConfirmedSelectedDuty = targetDuty;
            activeAdsQueueOperationId = 0;
            adsQueueFailurePending = false;
            adsQueueFailureReason = string.Empty;
            adsQueueFailureCooldownUntilUtc = DateTime.MinValue;
        }

        if (previousDuty != targetDuty)
            log.Debug($"[MOGTOME][DutyQueue] Engine confirmed queue registration; last selected duty={targetDuty}");
    }

    public void InvalidateAdsQueueOperations(string reason)
    {
        if (!UseAdsExperimental)
            return;

        Interlocked.Increment(ref adsQueueOperationId);
        var invalidatedOperationId = 0;
        lock (adsQueueStateLock)
        {
            invalidatedOperationId = activeAdsQueueOperationId;
            activeAdsQueueOperationId = 0;
        }

        if (invalidatedOperationId != 0)
            log.Information($"[MOGTOME][DutyQueue] Invalidated queued ContentsFinder callbacks for operation {invalidatedOperationId} ({reason})");
        else
            log.Debug($"[MOGTOME][DutyQueue] Queue callback invalidation requested with no active operation ({reason})");
    }

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

        log.Information($"[MOGTOME][DutyQueue] Praetorium callback test -> {selectionInfo.CallbackCommand} (missing optional unlocks: {selectionInfo.MissingUnlockCount})");
        foreach (var unlock in selectionInfo.Unlocks)
        {
            log.Information($"[MOGTOME][DutyQueue] Praetorium unlock check: {unlock.DutyName} -> {(unlock.IsUnlocked ? "unlocked" : "missing")} (quests: {unlock.QuestSummary})");
        }

        Plugin.ChatGui.Print($"[MOGTOME] Praetorium callback test: {selectionInfo.CallbackCommand}");
        Plugin.ChatGui.Print($"[MOGTOME] Missing optional unlocks: {missingSummary}");
    }

    private static string GetDutyName(bool isPraetorium)
        => isPraetorium ? "The Praetorium" : "The Porta Decumana";

    private static uint GetContentFinderConditionId(bool isPraetorium)
        => isPraetorium ? 16u : DutyState.DecumanaDutyId;

    private void SetAdsRuntimeRole(bool isLeader, bool startingInsideDuty)
    {
        adsRuntimeRole = isLeader ? AdsRuntimeRole.QueueLeader : AdsRuntimeRole.Follower;
        adsStartingInsideDutyRecovery = startingInsideDuty;
        adsLeaderOutsideOwned = false;
        adsLeaderInsideOwned = false;
        ResetAdsLeaveTracking();

        if (isLeader)
        {
            adsFollowerState = AdsFollowerState.None;
            log.Information($"[MOGTOME][ADS] ADS mode choice: queue leader keeps queue-control ownership path (startingInsideDuty={startingInsideDuty})");
            return;
        }

        adsFollowerState = AdsFollowerState.Recovered;
        log.Information($"[MOGTOME][ADS] ADS mode choice: follower uses {AdsStartOutsideCommand} before duty and treats {AdsStartInsideCommand} as leader/manual-recovery only (startingInsideDuty={startingInsideDuty})");
    }

    private void ResetAdsLeaveTracking()
    {
        adsLeaveRequested = false;
        adsLeaveConfirmationObserved = false;
        adsLastLeaveRequestUtc = DateTime.MinValue;
    }

    private string GetAdsRoleLabel()
        => adsRuntimeRole switch
        {
            AdsRuntimeRole.QueueLeader => "queue leader",
            AdsRuntimeRole.Follower => "follower",
            _ => "ads",
        };

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
                log.Error($"[MOGTOME][DutyQueue] AgentContentsFinder is null while trying to queue {dutyName}");
                return false;
            }

            agent->OpenRegularDuty(contentFinderConditionId);
            log.Information($"[MOGTOME][DutyQueue] Opened duty finder for {dutyName} (CFC {contentFinderConditionId})");
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][DutyQueue] Failed to open duty finder for {dutyName}: {ex.Message}");
            return false;
        }
    }

    private void QueueDutyViaContentsFinder(uint contentFinderConditionId, string dutyName, SelectedMogtomeDuty targetDuty)
    {
        if (IsAdsQueueRetryCooldownActive(out var cooldownRemainingSeconds, out var cooldownReason))
        {
            log.Information($"[MOGTOME][DutyQueue] Queue attempt for {dutyName} held for ContentsFinder callback cooldown ({Math.Ceiling(cooldownRemainingSeconds):F0}s remaining; {cooldownReason})");
            return;
        }

        if (!OpenDutyFinderForDuty(contentFinderConditionId, dutyName))
            return;

        var operationId = BeginAdsQueueOperation(targetDuty, dutyName);
        _ = Task.Run(() => ExecuteAdsQueueSequenceAsync(operationId, targetDuty, dutyName));
    }

    private async Task ExecuteAdsQueueSequenceAsync(int operationId, SelectedMogtomeDuty targetDuty, string dutyName)
    {
        try
        {
            if (!await WaitForContentsFinderStableVisibleAsync(operationId, dutyName, "queue start", AdsVisiblePollAttempts).ConfigureAwait(false))
            {
                log.Warning($"[MOGTOME][DutyQueue] Operation {operationId}: ContentsFinder never became stable-visible while queueing {dutyName}; queue watchdog will retry");
                return;
            }

            var selectionRequired = IsDutySelectionRequired(targetDuty);
            log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: ContentsFinder stable-visible for {dutyName}; running {GetSequenceLabel(targetDuty, selectionRequired)}");
            await Task.Delay(AdsInitialSettleDelayMs).ConfigureAwait(false);

            if (!IsCurrentAdsQueueOperation(operationId))
                return;

            var selectionDebugText = selectionRequired
                ? $", selectionCallback=ContentsFinder true 3 {GetFinalSelectionCallbackIndex(targetDuty)}"
                : ", selected duty already confirmed";

            if (selectionRequired)
            {
                log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: target duty changed; selecting {GetShortDutyName(targetDuty)} before Join");
                if (!await RunAdsDutySelectionSequenceAsync(operationId, targetDuty, dutyName).ConfigureAwait(false))
                {
                    log.Warning($"[MOGTOME][DutyQueue] Operation {operationId}: {dutyName} selection sequence aborted; queue watchdog will retry");
                    return;
                }
            }
            else
            {
                log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: opening ContentsFinder for same-duty requeue; firing Join only");
            }

            if (!IsCurrentAdsQueueOperation(operationId))
                return;

            if (!await WaitForContentsFinderStableVisibleAsync(operationId, dutyName, "Join", AdsStableVisiblePollAttempts).ConfigureAwait(false))
            {
                log.Warning($"[MOGTOME][DutyQueue] Operation {operationId}: ContentsFinder was not stable-visible before Join for {dutyName}; queue watchdog will retry");
                return;
            }

            if (!IsCurrentAdsQueueOperation(operationId))
                return;

            log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: firing Join callback for {dutyName}");
            if (!GameHelpers.TryFireAdsAddonCallback(operationId, "ContentsFinder", true, 12, 0))
            {
                log.Warning($"[MOGTOME][DutyQueue] Operation {operationId}: Join callback failed; selected-duty readback unavailable in this SDK. Target={dutyName}{selectionDebugText}");
                MarkAdsQueueAttemptFailed(operationId, "Join callback could not be fired", invalidateOperation: true);
                return;
            }

            log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: Join callback sent; waiting for queue registration");
            await Task.Delay(AdsPostJoinDelayMs).ConfigureAwait(false);

            if (!IsCurrentAdsQueueOperation(operationId))
                return;

            if (IsQueueRegistrationActive() || GameHelpers.IsAddonVisible("ContentsFinderConfirm"))
            {
                MarkQueueRegistrationConfirmed(operationId, targetDuty);
                log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: queue registration confirmed for {dutyName}; last selected duty={targetDuty}");
                return;
            }

            log.Warning($"[MOGTOME][DutyQueue] Operation {operationId}: Join callback did not confirm queue registration; selected-duty readback unavailable in this SDK. Target={dutyName}{selectionDebugText}");
            MarkAdsQueueAttemptFailed(operationId, "Join callback did not produce queue registration", invalidateOperation: true);
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][DutyQueue] Operation {operationId}: ContentsFinder queue sequence failed with exception: {ex.Message}");
            MarkAdsQueueAttemptFailed(operationId, $"queue sequence exception: {ex.Message}", invalidateOperation: true);
        }
    }

    private async Task<bool> WaitForContentsFinderStableVisibleAsync(int operationId, string dutyName, string gateName, int maxPolls)
    {
        var visiblePolls = 0;
        for (var attempt = 1; attempt <= maxPolls; attempt++)
        {
            if (!IsCurrentAdsQueueOperation(operationId))
                return false;

            if (GameHelpers.IsAddonVisible("ContentsFinder"))
            {
                visiblePolls++;
                if (visiblePolls >= AdsStableVisiblePollsRequired)
                {
                    log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: ContentsFinder stable-visible before {gateName} for {dutyName} ({visiblePolls} consecutive polls)");
                    return true;
                }
            }
            else
            {
                visiblePolls = 0;
            }

            var delayMs = gateName == "queue start" ? AdsVisiblePollDelayMs : AdsStableVisiblePollDelayMs;
            await Task.Delay(delayMs).ConfigureAwait(false);
        }

        log.Warning($"[MOGTOME][DutyQueue] Operation {operationId}: timed out waiting for stable ContentsFinder before {gateName} while queueing {dutyName}");
        return false;
    }

    private async Task<bool> RunAdsDutySelectionSequenceAsync(int operationId, SelectedMogtomeDuty targetDuty, string dutyName)
    {
        var praetoriumSelectionIndex = GetPraetoriumSelectionInfo().SelectionIndex;
        var previousDuty = GetLastConfirmedSelectedDuty();

        switch (targetDuty)
        {
            case SelectedMogtomeDuty.Praetorium:
                if (previousDuty == SelectedMogtomeDuty.Decumana)
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

            case SelectedMogtomeDuty.Decumana:
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

        if (!await WaitForContentsFinderStableVisibleAsync(operationId, dutyName, stepName, AdsStableVisiblePollAttempts).ConfigureAwait(false))
        {
            log.Warning($"[MOGTOME][DutyQueue] Operation {operationId}: ContentsFinder was not stable before {stepName} for {dutyName}");
            return false;
        }

        log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: {stepName} for {dutyName} via ContentsFinder true {arg1} {arg2}");
        if (!GameHelpers.TryFireAdsAddonCallback(operationId, "ContentsFinder", true, arg1, arg2))
        {
            MarkAdsQueueAttemptFailed(operationId, $"selection callback failed: ContentsFinder true {arg1} {arg2}", invalidateOperation: true);
            return false;
        }

        await Task.Delay(AdsStepDelayMs).ConfigureAwait(false);

        return IsCurrentAdsQueueOperation(operationId);
    }

    private int BeginAdsQueueOperation(SelectedMogtomeDuty targetDuty, string dutyName)
    {
        var operationId = Interlocked.Increment(ref adsQueueOperationId);
        lock (adsQueueStateLock)
        {
            activeAdsQueueOperationId = operationId;
            adsQueueFailurePending = false;
            adsQueueFailureReason = string.Empty;
            adsQueueFailureCooldownUntilUtc = DateTime.MinValue;
        }

        log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: starting queue sequence for {dutyName} ({targetDuty})");
        return operationId;
    }

    private bool IsCurrentAdsQueueOperation(int operationId)
        => Volatile.Read(ref adsQueueOperationId) == operationId
           && Volatile.Read(ref activeAdsQueueOperationId) == operationId;

    private int GetActiveAdsQueueOperationId()
    {
        var operationId = Volatile.Read(ref activeAdsQueueOperationId);
        return operationId != 0 && IsCurrentAdsQueueOperation(operationId) ? operationId : 0;
    }

    private void MarkAdsQueueAttemptFailed(int operationId, string reason, bool invalidateOperation)
    {
        lock (adsQueueStateLock)
        {
            if (activeAdsQueueOperationId != operationId && Volatile.Read(ref adsQueueOperationId) != operationId)
                return;

            adsQueueFailurePending = true;
            adsQueueFailureReason = reason;
            adsQueueFailureCooldownUntilUtc = DateTime.UtcNow.AddSeconds(AdsJoinFailureCooldownSeconds);
            activeAdsQueueOperationId = 0;
        }

        if (invalidateOperation)
            Interlocked.Increment(ref adsQueueOperationId);
    }

    private bool IsDutySelectionRequired(SelectedMogtomeDuty targetDuty)
    {
        lock (adsQueueStateLock)
        {
            return lastConfirmedSelectedDuty != targetDuty;
        }
    }

    private SelectedMogtomeDuty GetLastConfirmedSelectedDuty()
    {
        lock (adsQueueStateLock)
        {
            return lastConfirmedSelectedDuty;
        }
    }

    private void MarkQueueRegistrationConfirmed(int operationId, SelectedMogtomeDuty targetDuty)
    {
        lock (adsQueueStateLock)
        {
            if (activeAdsQueueOperationId != operationId)
                return;

            lastConfirmedSelectedDuty = targetDuty;
            activeAdsQueueOperationId = 0;
            adsQueueFailurePending = false;
            adsQueueFailureReason = string.Empty;
            adsQueueFailureCooldownUntilUtc = DateTime.MinValue;
        }
    }

    private static string GetSequenceLabel(SelectedMogtomeDuty targetDuty, bool selectionRequired)
        => selectionRequired
            ? $"{GetShortDutyName(targetDuty)} selection sequence"
            : $"{GetShortDutyName(targetDuty)} Join-only sequence";

    private static string GetShortDutyName(SelectedMogtomeDuty targetDuty)
        => targetDuty switch
        {
            SelectedMogtomeDuty.Praetorium => "Praetorium",
            SelectedMogtomeDuty.Decumana => "Decumana",
            _ => "unknown duty",
        };

    private int GetFinalSelectionCallbackIndex(SelectedMogtomeDuty targetDuty)
        => targetDuty switch
        {
            SelectedMogtomeDuty.Praetorium => GetPraetoriumSelectionInfo().SelectionIndex,
            SelectedMogtomeDuty.Decumana => 4,
            _ => -1,
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
