using MOGTOME.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using MOGTOME.IPC;
using MOGTOME.Models;

namespace MOGTOME.Services;

public sealed record PraetoriumUnlockStatus(string DutyName, bool IsUnlocked, string QuestSummary)
{
    public uint TerritoryTypeId { get; init; }
}

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
    private const string AdsLeaveCommand = "/ads leave";
    private const string AdsStopCommand = "/ads stop";
    private const string AdsEnterInnCommand = "/ads enterinn";
    private const string AdsSelfRepairCommand = "/ads selfrepair";
    private const string AdsNpcRepairCommand = "/ads npcrepair";
    private const string AdsNpcInnRepairCommand = "/ads npcrepair yesinn";
    private const int AdsVisiblePollDelayMs = 500;
    private const int AdsVisiblePollAttempts = 20;
    private const int AdsInitialSettleDelayMs = 1500;
    private const int AdsStepDelayMs = 700;
    private const int AdsPostRegistrationDelayMs = 1500;
    private const int AdsStableVisiblePollsRequired = 2;
    private const int AdsStableVisiblePollDelayMs = 250;
    private const int AdsStableVisiblePollAttempts = 20;
    private const int AdsRegistrationFailureCooldownSeconds = 5;
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
    private readonly object adsQueueStateLock = new();
    private readonly object adsRepairStateLock = new();
    private int adsQueueOperationId = 0;
    private int activeAdsQueueOperationId = 0;
    private int adsRepairOperationId = 0;
    private int activeAdsRepairOperationId = 0;
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
    private bool adsRepairHandoffActive = false;
    private bool adsRepairWaitingForCompletion = false;
    private bool adsRepairOutsideRestorePending = false;
    private bool adsRepairReturnsToInn;
    private DateTime adsInnRepairRequestedUtc;
    private string adsInnRepairFailure = string.Empty;
    private DateTime adsLastOutsideArmUtc = DateTime.MinValue;
    private DateTime adsLastLeaveRequestUtc = DateTime.MinValue;
    private const float AdsFollowerOutsideArmRetrySeconds = 5.0f;
    private const float AdsFollowerOutsideArmVisibleSeconds = 1.0f;
    private const int AdsStartupStopSettleDelayMs = 1000;
    private const int AdsRepairStopSettleDelayMs = 1000;
    private static readonly PraetoriumUnlockDefinition[] PraetoriumOptionalUnlocks =
    [
        new("Sunken Temple of Qarn", 1267, [764]),
        new("Cutter's Cry", 1303, [921]),
        new("Dzemael Darkhold", 171, [979, 1128, 1129, 1130]),
        new("The Aurum Vale", 172, [1014, 1131, 1132, 1133]),
        new("The Wanderer's Palace", 159, [870]),
    ];

    private Configuration Config => configManager.GetActiveConfig();

    public DutyAutomationService(
        IPluginLog log,
        ConfigManager configManager,
        AutoDutyIPC autoDutyIPC,
        AutoDutyPathService autoDutyPathService,
        ConflictPluginService conflictPluginService,
        ICommandManager commandManager,
        RunHistoryService runHistoryService)
    {
        this.log = log;
        this.configManager = configManager;
        this.autoDutyIPC = autoDutyIPC;
        this.autoDutyPathService = autoDutyPathService;
        this.conflictPluginService = conflictPluginService;
        this.commandManager = commandManager;
        this.runHistoryService = runHistoryService;
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

    public string LastPreparationFailure => PreparationFailure.English;
    public UiText PreparationFailure { get; private set; } = string.Empty;
    internal Func<bool>? QueueEligibility { get; set; }

    public async Task<bool> PrepareForStartAsync(bool isLeader, bool startingInsideDuty, Func<bool> isCurrent)
    {
        if (!isCurrent()) return false;
        PreparationFailure = string.Empty;
        if (UseAdsExperimental)
        {
            log.Information("[MOGTOME][Automation] Preparing ADS backend");
            await conflictPluginService.EnsureAutoDutyDisabledAsync("MOGTOME ADS start", showPopup: true, isCurrent).ConfigureAwait(false);
            if (!isCurrent()) return false;

            if (!IsAdsLoaded())
            {
                var message = Ui.M("DutyAutomationService_AIDutySolverADSExperimentalModeIs");
                PreparationFailure = message;
                log.Warning($"[MOGTOME][Automation] {message}");
                Plugin.ChatGui.Print(Ui.T("Chat_MOGTOME", message));
                return false;
            }

            var sentStartupStop = false;
            await GameHelpers.RunOnFrameworkThreadAsync(() =>
            {
                if (!isCurrent()) return;
                SetAdsRuntimeRole(isLeader, startingInsideDuty);
                CancelAdsRepairHandoff("ads startup reset");
                InvalidateAdsQueueOperations("ads startup reset");
                ResetAdsLeaveTracking();
                adsLeaderOutsideOwned = false;
                adsLeaderInsideOwned = false;

                if (!startingInsideDuty)
                {
                    sentStartupStop = true;
                    log.Information($"[MOGTOME][ADS] Startup reset: sending {AdsStopCommand} while outside duty before MOGTOME handoff");
                    commandManager.ProcessCommand(AdsStopCommand);
                }
                else
                {
                    log.Information($"[MOGTOME][ADS] Startup reset skipped {AdsStopCommand} because MOGTOME started inside duty");
                }
            }).ConfigureAwait(false);

            if (sentStartupStop)
                await Task.Delay(AdsStartupStopSettleDelayMs).ConfigureAwait(false);

            log.Information("[MOGTOME][Automation] ADS backend ready");
            return isCurrent();
        }

        log.Information("[MOGTOME][Automation] Preparing AutoDuty backend");
        var autoDutyReady = await autoDutyPathService.WaitForAutoDutyInitializationAsync(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(500), isCurrent).ConfigureAwait(false);
        if (!isCurrent()) return false;
        if (!autoDutyReady)
        {
            var startupFailure = Ui.M("DutyAutomationService_AutoDutyIsStillInitializingOrFaultedRetry");
            PreparationFailure = startupFailure;
            log.Warning($"[MOGTOME][Automation] {startupFailure}");
            Plugin.ChatGui.Print(Ui.T("Chat_MOGTOME", startupFailure));
            return false;
        }

        var bundledPathsReady = await autoDutyPathService.EnsurePathExists().ConfigureAwait(false);
        if (!isCurrent()) return false;
        if (!bundledPathsReady)
        {
            var pathFailure = Ui.M("DutyAutomationService_BundledPraetoriumPathsCouldNotBeInstalled");
            PreparationFailure = pathFailure;
            log.Warning($"[MOGTOME][Automation] {pathFailure}");
            Plugin.ChatGui.Print(Ui.T("Chat_MOGTOME", pathFailure));
            return false;
        }

        var pathSelectionReady = await GameHelpers.RunOnFrameworkThreadAsync(() =>
        {
            if (!isCurrent()) return false;
            autoDutyIPC.StopDuty();
            autoDutyIPC.ConfigureForMogtome(isLeader);

            return startingInsideDuty || autoDutyPathService.ForcePathSelection(Config.PraetoriumPathFileName);
        }).ConfigureAwait(false);

        if (!pathSelectionReady)
        {
            if (!isCurrent()) return false;
            PreparationFailure = Ui.M("Automation_AutoDutyPreparationFailed", autoDutyPathService.ForceResult);
            log.Warning($"[MOGTOME][Automation] AutoDuty preparation failed: {autoDutyPathService.LastForceResult}");
            Plugin.ChatGui.Print(Ui.T("Chat_MOGTOMEAutoDutyPreparationFailed", autoDutyPathService.ForceResult));
            return false;
        }

        log.Information("[MOGTOME][Automation] AutoDuty backend ready");
        return isCurrent();
    }

    public async Task EnsureAutoDutyDisabledForAdsAsync(string triggerSource)
    {
        if (!UseAdsExperimental)
            return;

        await conflictPluginService.EnsureAutoDutyDisabledAsync(triggerSource, showPopup: true).ConfigureAwait(false);
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
            log.Information($"[MOGTOME][AutoDuty] Queue-control path selected for {dutyName}; registering directly while AutoDuty remains the in-duty backend");
            QueueDutyViaContentsFinder(
                GetContentFinderConditionId(isPraetorium),
                dutyName,
                isPraetorium ? SelectedMogtomeDuty.Praetorium : SelectedMogtomeDuty.Decumana);
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

    internal void ConfirmAdsDutyInside(bool isLeader)
    {
        if (!UseAdsExperimental || adsLeaderInsideOwned || adsFollowerState == AdsFollowerState.InsideObserved)
            return;

        CancelAdsRepairHandoff("duty entry");
        CapturePartySnapshot("ADS");
        adsRuntimeRole = isLeader ? AdsRuntimeRole.QueueLeader : AdsRuntimeRole.Follower;
        adsLeaderOutsideOwned = false;
        adsLeaderInsideOwned = isLeader;
        if (!isLeader)
            adsFollowerState = AdsFollowerState.InsideObserved;
        ResetAdsLeaveTracking();
        adsStartingInsideDutyRecovery = false;
        log.Information("[MOGTOME][ADS] Authoritative inside-duty ownership confirmed");
    }

    public void StopDuty()
    {
        CancelPendingOperations("backend stop");
        if (!UseAdsExperimental)
        {
            autoDutyIPC.StopDuty();
            return;
        }

        CancelAdsRepairHandoff("ads stop");
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
        CancelPendingOperations("duty exit requested");
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

    public void RequestSelfRepair()
    {
        if (!UseAdsExperimental)
        {
            log.Information("[MOGTOME][Repair] Requesting repair via AutoDuty");
            commandManager.ProcessCommand("/ad repair");
            return;
        }

        BeginAdsRepairHandoff(AdsSelfRepairCommand, "self-repair");
    }

    public void RequestNpcRepair()
    {
        if (!UseAdsExperimental)
        {
            log.Information("[MOGTOME][Repair] Attempting NPC repair via AutoDuty");
            commandManager.ProcessCommand("/ad repair");
            return;
        }

        BeginAdsRepairHandoff(Config.AdsRepairMode == AdsRepairMode.NpcYesInn ? AdsNpcInnRepairCommand : AdsNpcRepairCommand, "npc repair");
    }

    public bool IsAdsInnRepairPending(out string failure)
    {
        failure = string.Empty;
        DateTime requestedUtc;
        lock (adsRepairStateLock)
        {
            if (!adsRepairHandoffActive || !adsRepairReturnsToInn)
                return false;
            failure = adsInnRepairFailure;
            if (failure.Length != 0)
                return false;
            if (!adsRepairWaitingForCompletion)
                return true;
            requestedUtc = adsInnRepairRequestedUtc;
        }

        if (requestedUtc == DateTime.MinValue)
        {
            failure = "The character was not ready to start the ADS inn repair trip.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(Plugin.PluginInterface.GetIpcSubscriber<string>("ADS.GetStatusJson").InvokeFunc());
            var root = document.RootElement;
            if (root.TryGetProperty("utilityRunning", out var running) && !running.GetBoolean()
                && root.TryGetProperty("utilityCompletedAtUtc", out var completed)
                && completed.ValueKind == JsonValueKind.String && completed.TryGetDateTime(out var completedUtc)
                && completedUtc >= requestedUtc)
            {
                failure = root.TryGetProperty("utilityLastFailure", out var error) ? error.GetString() ?? string.Empty : string.Empty;
                if (failure.Length == 0 && (!root.TryGetProperty("utilityLastSuccess", out var success) || string.IsNullOrEmpty(success.GetString())))
                    failure = "ADS ended the inn repair trip without a success result.";
                return false;
            }
        }
        catch (Exception ex)
        {
            log.Debug($"[MOGTOME][ADS][Repair] Could not read inn repair status: {ex.Message}");
        }

        // ADS's utility timeout is 120 seconds; allow its terminal status to arrive.
        if (DateTime.UtcNow - requestedUtc > TimeSpan.FromSeconds(150))
        {
            failure = "Timed out waiting for ADS to complete the inn repair trip.";
            return false;
        }
        return true;
    }

    public void RestoreAdsOutsideAfterRepair()
    {
        var operationId = 0;
        lock (adsRepairStateLock)
        {
            if (!adsRepairHandoffActive || !adsRepairOutsideRestorePending)
                return;

            operationId = activeAdsRepairOperationId;
            activeAdsRepairOperationId = 0;
            adsRepairHandoffActive = false;
            adsRepairWaitingForCompletion = false;
            adsRepairOutsideRestorePending = false;
        }

        Interlocked.Increment(ref adsRepairOperationId);
        ResetAdsLeaveTracking();
        adsLeaderOutsideOwned = adsRuntimeRole == AdsRuntimeRole.QueueLeader;
        adsLeaderInsideOwned = false;

        if (adsRuntimeRole == AdsRuntimeRole.Follower)
        {
            adsFollowerState = AdsFollowerState.OutsideArmed;
            adsLastOutsideArmUtc = DateTime.UtcNow;
        }

        log.Information($"[MOGTOME][ADS][Repair] Operation {operationId}: repair complete; restoring {AdsStartOutsideCommand}");
        commandManager.ProcessCommand(AdsStartOutsideCommand);
    }

    public void ReturnToInnIfNeeded()
    {
        try
        {
            var territoryId = Plugin.ClientState.TerritoryType;
            var territoryName = GameHelpers.GetTerritoryName(territoryId);
            if (GameHelpers.IsInnTerritory(territoryId))
            {
                log.Information($"[MOGTOME][Repair] Repair completed while already in inn territory {territoryName} ({territoryId})");
                return;
            }

            log.Information($"[MOGTOME][Repair] Repair completed outside inn territory {territoryName} ({territoryId}); sending {AdsEnterInnCommand}");
            if (!commandManager.ProcessCommand(AdsEnterInnCommand))
            {
                var message = Ui.M("DutyAutomationService_ADSDidNotHandleAdsEnterinnAfter");
                log.Warning($"[MOGTOME][Repair] {message}");
                Plugin.ChatGui.Print(Ui.T("Chat_MOGTOME", message));
            }
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][Repair] ReturnToInnIfNeeded failed: {ex.Message}");
        }
    }

    public string GetQueueStatusLabel() => GetQueueStatusText().English;

    public UiText GetQueueStatusText()
        => UseAdsExperimental
            ? adsRuntimeRole switch
            {
                AdsRuntimeRole.QueueLeader => Ui.M("Automation_QueueLeader"),
                AdsRuntimeRole.Follower => Ui.M("Automation_FollowerWaitingOnLeader"),
                _ => Ui.M("Automation_ADS"),
            }
            : ActiveBackendDisplayName;

    public string GetAdsRuntimeStatusLabel() => GetAdsRuntimeStatusText().English;

    public UiText GetAdsRuntimeStatusText()
    {
        if (!UseAdsExperimental)
            return ActiveBackendDisplayName;

        if (IsAdsRepairHandoffActive)
            return IsAdsRepairWaitingForCompletion ? Ui.M("Automation_RepairWaiting") : Ui.M("Automation_RepairHandoff");

        if (adsLeaveRequested)
        {
            return adsLeaveConfirmationObserved
                ? Ui.M("Automation_LeaveRequestedWaitingForZoneOut")
                : Ui.M("Automation_LeaveRequested");
        }

        return adsRuntimeRole switch
        {
            AdsRuntimeRole.QueueLeader when adsLeaderInsideOwned => Ui.M("Automation_ADSInsideOwned"),
            AdsRuntimeRole.QueueLeader when adsLeaderOutsideOwned => Ui.M("Automation_ADSOutsideOwned"),
            AdsRuntimeRole.QueueLeader => Ui.M("Automation_LeaderReady"),
            AdsRuntimeRole.Follower => adsFollowerState switch
            {
                AdsFollowerState.OutsideArmed => Ui.M("Automation_OutsideArmed"),
                AdsFollowerState.WaitingForEntry => Ui.M("Automation_WaitingForEntry"),
                AdsFollowerState.InsideObserved => Ui.M("Automation_InsideObserved"),
                AdsFollowerState.Leaving => Ui.M("Automation_Leaving"),
                AdsFollowerState.Recovered => Ui.M("Automation_Recovered"),
                _ => Ui.M("Automation_WaitingForEntry"),
            },
            _ => Ui.M("Automation_Idle"),
        };
    }

    public string GetSubsystemStatusLabel() => GetSubsystemStatusText().English;

    public UiText GetSubsystemStatusText()
    {
        if (UseAdsExperimental)
            return IsAdsLoaded() ? Ui.M("Automation_ADSDutyBackendInnReturn") : Ui.M("Automation_ADSMissing");

        var pathName = autoDutyPathService.GetPraetoriumPathDisplayName(Config.PraetoriumPathFileName);
        var adsStatus = IsAdsLoaded() ? Ui.M("Automation_ADSInnReady") : Ui.M("Automation_ADSMissing");
        return Ui.M("Automation_Value", pathName, adsStatus);
    }

    public bool GetSubsystemHealthy()
        => UseAdsExperimental
            ? IsAdsLoaded()
            : IsAutoDutyLoaded() && autoDutyPathService.PathExists(Config.PraetoriumPathFileName) && IsAdsLoaded();

    public bool IsAdsRepairHandoffActive
    {
        get
        {
            lock (adsRepairStateLock)
            {
                return adsRepairHandoffActive;
            }
        }
    }

    public bool IsAdsRepairWaitingForCompletion
    {
        get
        {
            lock (adsRepairStateLock)
            {
                return adsRepairHandoffActive && adsRepairWaitingForCompletion;
            }
        }
    }

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
        var operationId = GetActiveAdsQueueOperationId();
        if (operationId == 0)
            return;

        var noSelection = GameText.MatchesLogMessage(messageText, 877);
        var partyRequirements = GameText.MatchesLogMessage(messageText, 880);
        if (!noSelection && !partyRequirements)
        {
            return;
        }

        if (noSelection)
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
        lock (adsQueueStateLock)
        {
            reason = adsQueueFailureReason;
            cooldownRemainingSeconds = Math.Max(0, (adsQueueFailureCooldownUntilUtc - DateTime.UtcNow).TotalSeconds);
            return cooldownRemainingSeconds > 0;
        }
    }

    public void ConfirmQueueRegistration(bool isPraetorium)
    {
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

    internal void CancelPendingOperations(string reason)
    {
        InvalidateAdsQueueOperations(reason);
        CancelAdsRepairHandoff(reason);
    }

    public PraetoriumSelectionInfo GetPraetoriumSelectionInfo()
    {
        var unlocks = PraetoriumOptionalUnlocks
            .Select(definition =>
            {
                var isUnlocked = IsAnyQuestComplete(definition.QuestIds);
                var questSummary = string.Join(", ", definition.QuestIds);
                return new PraetoriumUnlockStatus(definition.DutyName, isUnlocked, questSummary)
                {
                    TerritoryTypeId = definition.TerritoryTypeId,
                };
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
            .Select(unlock => Ui.Duty(unlock.TerritoryTypeId).Render())
            .ToArray();
        var missingSummary = missingDuties.Length > 0
            ? string.Join(", ", missingDuties)
            : Ui.T("Stats_None");

        log.Information($"[MOGTOME][DutyQueue] Praetorium callback test -> {selectionInfo.CallbackCommand} (missing optional unlocks: {selectionInfo.MissingUnlockCount})");
        foreach (var unlock in selectionInfo.Unlocks)
        {
            log.Information($"[MOGTOME][DutyQueue] Praetorium unlock check: {unlock.DutyName} -> {(unlock.IsUnlocked ? "unlocked" : "missing")} (quests: {unlock.QuestSummary})");
        }

        Plugin.ChatGui.Print(Ui.T("Chat_MOGTOMEPraetoriumCallbackTest", selectionInfo.CallbackCommand));
        Plugin.ChatGui.Print(Ui.T("Chat_MOGTOMEMissingOptionalUnlocks", missingSummary));
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
        log.Information($"[MOGTOME][ADS] ADS mode choice: follower uses {AdsStartOutsideCommand} before duty, then the shared readiness and ownership handoff (startingInsideDuty={startingInsideDuty})");
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
                ? $", targetCfc={GetContentFinderConditionId(targetDuty == SelectedMogtomeDuty.Praetorium)}"
                : ", selected duty already confirmed";

            if (selectionRequired)
            {
                log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: target duty changed; selecting {GetShortDutyName(targetDuty)} before direct registration");
                if (!await RunAdsDutySelectionSequenceAsync(operationId, targetDuty, dutyName).ConfigureAwait(false))
                {
                    log.Warning($"[MOGTOME][DutyQueue] Operation {operationId}: {dutyName} selection sequence aborted; queue watchdog will retry");
                    return;
                }
            }
            else
            {
                log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: opening ContentsFinder for same-duty direct registration");
            }

            if (!IsCurrentAdsQueueOperation(operationId))
                return;

            if (!await WaitForContentsFinderStableVisibleAsync(operationId, dutyName, "direct registration", AdsStableVisiblePollAttempts).ConfigureAwait(false))
            {
                log.Warning($"[MOGTOME][DutyQueue] Operation {operationId}: ContentsFinder was not stable-visible before direct registration for {dutyName}; queue watchdog will retry");
                return;
            }

            if (!IsCurrentAdsQueueOperation(operationId))
                return;

            log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: registering selected duties directly for {dutyName}");
            if (!await GameHelpers.RunOnFrameworkThreadAsync(() => RegisterSelectedDutiesDirect(operationId, targetDuty, dutyName)).ConfigureAwait(false))
            {
                log.Warning($"[MOGTOME][DutyQueue] Operation {operationId}: direct queue registration failed. Target={dutyName}{selectionDebugText}");
                MarkAdsQueueAttemptFailed(operationId, "Direct queue registration failed", invalidateOperation: true);
                return;
            }

            log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: direct queue registration sent; waiting for queue registration");
            await Task.Delay(AdsPostRegistrationDelayMs).ConfigureAwait(false);

            if (!IsCurrentAdsQueueOperation(operationId))
                return;

            if (IsQueueRegistrationActive() || await IsAddonVisibleOnFrameworkAsync("ContentsFinderConfirm").ConfigureAwait(false))
            {
                MarkQueueRegistrationConfirmed(operationId, targetDuty);
                log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: queue registration confirmed for {dutyName}; last selected duty={targetDuty}");
                return;
            }

            log.Warning($"[MOGTOME][DutyQueue] Operation {operationId}: direct queue registration did not confirm. Target={dutyName}{selectionDebugText}");
            MarkAdsQueueAttemptFailed(operationId, "Direct queue registration did not produce queue registration", invalidateOperation: true);
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][DutyQueue] Operation {operationId}: ContentsFinder queue sequence failed with exception: {ex.Message}");
            MarkAdsQueueAttemptFailed(operationId, $"queue sequence exception: {ex.Message}", invalidateOperation: true);
        }
    }

    private unsafe bool RegisterSelectedDutiesDirect(int operationId, SelectedMogtomeDuty targetDuty, string dutyName)
    {
        if (!IsCurrentAdsQueueOperation(operationId) || !Plugin.ClientState.IsLoggedIn
            || DutyStartupService.IsInDuty() || QueueEligibility?.Invoke() == false)
            return false;

        var agent = AgentContentsFinder.Instance();
        if (agent == null)
        {
            log.Error($"[MOGTOME][DutyQueue] Operation {operationId}: AgentContentsFinder is null before direct registration for {dutyName}");
            return false;
        }

        var contentsFinder = ContentsFinder.Instance();
        if (contentsFinder == null)
        {
            log.Error($"[MOGTOME][DutyQueue] Operation {operationId}: ContentsFinder is null before direct registration for {dutyName}");
            return false;
        }

        var queueInfo = contentsFinder->GetQueueInfo();
        if (queueInfo == null)
        {
            log.Error($"[MOGTOME][DutyQueue] Operation {operationId}: ContentsFinderQueueInfo is null before direct registration for {dutyName}");
            return false;
        }

        var expectedDutyId = GetContentFinderConditionId(targetDuty == SelectedMogtomeDuty.Praetorium);
        var selectedDutyId = agent->SelectedDuty.Id;
        var interfaceSelectedDutyId = agent->InterfaceSub.SelectedDutyId;
        var selectedContent = agent->SelectedContent;
        var selectedCount = selectedContent.Count;
        if (selectedCount != 1 || selectedContent.First == null)
        {
            log.Error($"[MOGTOME][DutyQueue] Operation {operationId}: invalid selected-content collection for {dutyName}; count={selectedCount}, first=0x{(nuint)selectedContent.First:X}");
            return false;
        }

        if (selectedDutyId != expectedDutyId && interfaceSelectedDutyId != expectedDutyId)
        {
            log.Error($"[MOGTOME][DutyQueue] Operation {operationId}: selected duty does not match {dutyName}; expected={expectedDutyId}, selected={selectedDutyId}, interfaceSelected={interfaceSelectedDutyId}");
            return false;
        }

        var entries = stackalloc uint[selectedCount];
        for (var index = 0; index < selectedCount; index++)
        {
            var selected = selectedContent[index];
            if (!IsIntendedSelection(expectedDutyId, selectedCount, selected.ContentType == ContentsType.Regular, selected.Id))
            {
                log.Error($"[MOGTOME][DutyQueue] Operation {operationId}: invalid selected content at index {index} for {dutyName}; type={selected.ContentType}, id={selected.Id}");
                return false;
            }

            entries[index] = selected.Id;
        }

        queueInfo->QueueDuties(entries, selectedCount);
        log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: QueueDuties registered {selectedCount} selected content entries for {dutyName}; selected={string.Join(",", new ReadOnlySpan<uint>(entries, selectedCount).ToArray())}");
        return true;
    }

    internal static bool IsIntendedSelection(uint expectedDutyId, int count, bool regular, uint selectedId)
        => count == 1 && regular && selectedId == expectedDutyId && (expectedDutyId == 16 || expectedDutyId == DutyState.DecumanaDutyId);

    private async Task<bool> WaitForContentsFinderStableVisibleAsync(int operationId, string dutyName, string gateName, int maxPolls)
    {
        var visiblePolls = 0;
        for (var attempt = 1; attempt <= maxPolls; attempt++)
        {
            if (!IsCurrentAdsQueueOperation(operationId))
                return false;

            if (await IsAddonVisibleOnFrameworkAsync("ContentsFinder").ConfigureAwait(false))
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
        var praetoriumDutyId = (int)GetContentFinderConditionId(true);
        var decumanaDutyId = (int)GetContentFinderConditionId(false);
        var previousDuty = GetLastConfirmedSelectedDuty();

        switch (targetDuty)
        {
            case SelectedMogtomeDuty.Praetorium:
                if (previousDuty == SelectedMogtomeDuty.Decumana)
                {
                    if (!await FireAdsContentsFinderStepAsync(operationId, dutyName, "switching to Decumana tab to clear prior selection", 1, 4).ConfigureAwait(false) ||
                        !await FireAdsContentsFinderStepAsync(operationId, dutyName, "selecting Decumana to clear prior selection", 3, decumanaDutyId).ConfigureAwait(false) ||
                        !await FireAdsContentsFinderStepAsync(operationId, dutyName, "unselecting Decumana before Praetorium", 3, decumanaDutyId).ConfigureAwait(false))
                    {
                        return false;
                    }
                }

                return await FireAdsContentsFinderStepAsync(operationId, dutyName, "switching to Praetorium tab", 1, 1).ConfigureAwait(false) &&
                       await FireAdsContentsFinderStepAsync(operationId, dutyName, "selecting Praetorium", 3, praetoriumDutyId).ConfigureAwait(false);

            case SelectedMogtomeDuty.Decumana:
                return await FireAdsContentsFinderStepAsync(operationId, dutyName, "switching to Praetorium tab for Decumana pre-clear", 1, 1).ConfigureAwait(false) &&
                       await FireAdsContentsFinderStepAsync(operationId, dutyName, "selecting Praetorium for Decumana pre-clear", 3, praetoriumDutyId).ConfigureAwait(false) &&
                       await FireAdsContentsFinderStepAsync(operationId, dutyName, "unselecting Praetorium before Decumana", 3, praetoriumDutyId).ConfigureAwait(false) &&
                       await FireAdsContentsFinderStepAsync(operationId, dutyName, "switching to Decumana tab", 1, 4).ConfigureAwait(false) &&
                       await FireAdsContentsFinderStepAsync(operationId, dutyName, "selecting Decumana", 3, decumanaDutyId).ConfigureAwait(false);

            default:
                return false;
        }
    }

    private async Task<bool> FireAdsContentsFinderStepAsync(int operationId, string dutyName, string stepName, int arg1, int tabOrDutyId)
    {
        if (!IsCurrentAdsQueueOperation(operationId))
            return false;

        if (!await WaitForContentsFinderStableVisibleAsync(operationId, dutyName, stepName, AdsStableVisiblePollAttempts).ConfigureAwait(false))
        {
            log.Warning($"[MOGTOME][DutyQueue] Operation {operationId}: ContentsFinder was not stable before {stepName} for {dutyName}");
            return false;
        }

        if (!await GameHelpers.RunOnFrameworkThreadAsync(() =>
            {
                if (!IsCurrentAdsQueueOperation(operationId) || !Plugin.ClientState.IsLoggedIn || DutyStartupService.IsInDuty())
                    return false;

                var callbackIndex = tabOrDutyId;
                if (arg1 == 3)
                {
                    // Resolve the visible row immediately before clicking; the player can reverse the list.
                    callbackIndex = -1;
                    unsafe
                    {
                        var agent = AgentContentsFinder.Instance();
                        if (agent == null)
                            return false;

                        for (var index = 0; index < agent->ContentList.Count; index++)
                        {
                            var content = agent->ContentList[index].Value;
                            if (content != null && content->Id.ContentType == ContentsType.Regular && content->Id.Id == tabOrDutyId)
                            {
                                callbackIndex = index + 1;
                                break;
                            }
                        }
                    }

                    if (callbackIndex < 0)
                        return false;
                }

                log.Information($"[MOGTOME][DutyQueue] Operation {operationId}: {stepName} for {dutyName} via ContentsFinder true {arg1} {callbackIndex}");
                return GameHelpers.TryFireAdsAddonCallback(operationId, "ContentsFinder", true, arg1, callbackIndex);
            }).ConfigureAwait(false))
        {
            MarkAdsQueueAttemptFailed(operationId, $"{stepName} failed (tab/duty ID {tabOrDutyId})", invalidateOperation: true);
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

    private static Task<bool> IsAddonVisibleOnFrameworkAsync(string addonName)
        => GameHelpers.RunOnFrameworkThreadAsync(() => GameHelpers.IsAddonVisible(addonName));

    private void BeginAdsRepairHandoff(string repairCommand, string repairLabel)
    {
        var operationId = Interlocked.Increment(ref adsRepairOperationId);
        lock (adsRepairStateLock)
        {
            activeAdsRepairOperationId = operationId;
            adsRepairHandoffActive = true;
            adsRepairWaitingForCompletion = false;
            adsRepairOutsideRestorePending = true;
            adsRepairReturnsToInn = repairCommand == AdsNpcInnRepairCommand;
            adsInnRepairRequestedUtc = DateTime.MinValue;
            adsInnRepairFailure = string.Empty;
        }

        ResetAdsLeaveTracking();
        adsLeaderOutsideOwned = false;
        adsLeaderInsideOwned = false;
        if (adsRuntimeRole == AdsRuntimeRole.Follower)
            adsFollowerState = AdsFollowerState.Recovered;

        log.Information($"[MOGTOME][ADS][Repair] Operation {operationId}: sending {AdsStopCommand}, waiting {AdsRepairStopSettleDelayMs / 1000.0:F1}s, then {repairCommand} ({repairLabel})");
        _ = Task.Run(() => ExecuteAdsRepairHandoffAsync(operationId, repairCommand, repairLabel));
    }

    private async Task ExecuteAdsRepairHandoffAsync(int operationId, string repairCommand, string repairLabel)
    {
        try
        {
            if (!IsCurrentAdsRepairOperation(operationId))
                return;

            await GameHelpers.RunOnFrameworkThreadAsync(() =>
            {
                if (IsCurrentAdsRepairOperation(operationId) && Plugin.ClientState.IsLoggedIn && !DutyStartupService.IsInDuty())
                    commandManager.ProcessCommand(AdsStopCommand);
            }).ConfigureAwait(false);

            await Task.Delay(AdsRepairStopSettleDelayMs).ConfigureAwait(false);

            if (!IsCurrentAdsRepairOperation(operationId))
                return;

            await GameHelpers.RunOnFrameworkThreadAsync(() =>
            {
                if (IsCurrentAdsRepairOperation(operationId) && Plugin.ClientState.IsLoggedIn && !DutyStartupService.IsInDuty())
                {
                    if (repairCommand == AdsNpcInnRepairCommand)
                    {
                        lock (adsRepairStateLock)
                        {
                            adsInnRepairRequestedUtc = DateTime.UtcNow;
                            if (!Plugin.PluginInterface.GetIpcSubscriber<string, bool>("ADS.StartRepair").InvokeFunc("npc-yes-inn"))
                                adsInnRepairFailure = "ADS did not accept NPC repair + inn room.";
                        }
                    }
                    else
                        commandManager.ProcessCommand(repairCommand);
                }
            }).ConfigureAwait(false);

            if (!IsCurrentAdsRepairOperation(operationId))
                return;

            lock (adsRepairStateLock)
            {
                if (activeAdsRepairOperationId != operationId)
                    return;

                adsRepairWaitingForCompletion = true;
            }

            log.Information($"[MOGTOME][ADS][Repair] Operation {operationId}: {repairLabel} requested; waiting for repair completion before {AdsStartOutsideCommand}");
        }
        catch (Exception ex)
        {
            log.Error($"[MOGTOME][ADS][Repair] Operation {operationId}: repair handoff failed: {ex.Message}");
            lock (adsRepairStateLock)
            {
                if (activeAdsRepairOperationId == operationId)
                    adsInnRepairFailure = ex.Message;
            }
        }
    }

    private bool IsCurrentAdsRepairOperation(int operationId)
        => Volatile.Read(ref adsRepairOperationId) == operationId
           && Volatile.Read(ref activeAdsRepairOperationId) == operationId;

    private void CancelAdsRepairHandoff(string reason)
    {
        var hadRepairState = false;
        lock (adsRepairStateLock)
        {
            hadRepairState = activeAdsRepairOperationId != 0
                || adsRepairHandoffActive
                || adsRepairOutsideRestorePending;

            activeAdsRepairOperationId = 0;
            adsRepairHandoffActive = false;
            adsRepairWaitingForCompletion = false;
            adsRepairOutsideRestorePending = false;
        }

        if (!hadRepairState)
            return;

        Interlocked.Increment(ref adsRepairOperationId);
        log.Information($"[MOGTOME][ADS][Repair] Cleared repair handoff state ({reason})");
    }

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
            adsQueueFailureCooldownUntilUtc = DateTime.UtcNow.AddSeconds(AdsRegistrationFailureCooldownSeconds);
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
            : $"{GetShortDutyName(targetDuty)} direct-registration sequence";

    private static string GetShortDutyName(SelectedMogtomeDuty targetDuty)
        => targetDuty switch
        {
            SelectedMogtomeDuty.Praetorium => "Praetorium",
            SelectedMogtomeDuty.Decumana => "Decumana",
            _ => "unknown duty",
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

    private sealed record PraetoriumUnlockDefinition(string DutyName, uint TerritoryTypeId, ushort[] QuestIds);
}
