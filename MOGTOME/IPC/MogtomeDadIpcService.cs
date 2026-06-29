using System;
using System.Collections.Generic;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace MOGTOME.IPC;

public sealed class MogtomeDadIpcService : IDisposable
{
    private const string IsReadyName = "MOGTOME.IsReady";
    private const string StartRunName = "MOGTOME.StartRun";
    private const string GetRunStatusName = "MOGTOME.GetRunStatus";
    private const string StopRunName = "MOGTOME.StopRun";
    private const string StatusChangedName = "MOGTOME.OnRunStatusChanged";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedDutyPolicies = new(StringComparer.OrdinalIgnoreCase)
    {
        "PresetHandoff",
        "PreservePresetDuty",
        "PinnedDutySelection",
    };

    private readonly Plugin plugin;
    private readonly List<Action> unregister = [];
    private readonly ICallGateProvider<string, object> statusChangedProvider;
    private MogtomeDadRunStatus status = new();
    private int startingDutyCounter;
    private string lastPublishedSignature = string.Empty;
    private DateTime lastNotificationFailureLogUtc = DateTime.MinValue;

    public MogtomeDadIpcService(IDalamudPluginInterface pluginInterface, Plugin plugin)
    {
        this.plugin = plugin;
        Register(pluginInterface.GetIpcProvider<bool>(IsReadyName), () => plugin.Engine != null);
        Register(pluginInterface.GetIpcProvider<string, string>(StartRunName), StartRun);
        Register(pluginInterface.GetIpcProvider<string>(GetRunStatusName), GetRunStatus);
        Register(pluginInterface.GetIpcProvider<string, string>(StopRunName), StopRun);
        statusChangedProvider = pluginInterface.GetIpcProvider<string, object>(StatusChangedName);
    }

    public void Dispose()
    {
        foreach (var action in unregister)
            action();
    }

    public void Update()
    {
        if (!status.DadOwned || status.IsTerminal)
            return;

        if (plugin.Engine == null)
        {
            Fail("MOGTOME engine became unavailable.");
            return;
        }

        status.Ready = true;
        status.IsRunning = plugin.Engine.IsRunning;
        status.EngineState = plugin.Engine.CurrentState.ToString();
        status.CompletedAttempts = Math.Max(0, plugin.State.DutyCounter - startingDutyCounter);
        status.UpdatedAtUtc = DateTime.UtcNow;
        status.Summary = plugin.Engine.StatusMessage;

        if (status.CompletedAttempts >= status.AttemptLimit)
        {
            plugin.Engine.Stop();
            status.IsRunning = false;
            status.IsTerminal = true;
            status.Success = true;
            status.Summary = $"DAD-owned MOGTOME run completed {status.CompletedAttempts}/{status.AttemptLimit} attempt(s).";
        }
        else if (!plugin.Engine.IsRunning &&
                 plugin.Engine.CurrentState == Services.EngineState.Idle)
        {
            Fail($"DAD-owned MOGTOME run stopped after {status.CompletedAttempts}/{status.AttemptLimit} attempt(s).");
            return;
        }

        PublishIfChanged();
    }

    private string StartRun(string json)
    {
        var request = Deserialize<MogtomeDadRunRequest>(json);
        if (request == null)
            return Serialize(Reject("Unreadable MOGTOME DAD request."));
        if (request.SchemaVersion != 1)
            return Serialize(Reject($"Unsupported MOGTOME DAD schema {request.SchemaVersion}."));
        if (string.IsNullOrWhiteSpace(request.DadRunId))
            return Serialize(Reject("DAD run ID is required."));
        if (plugin.Engine == null)
            return Serialize(Reject("MOGTOME engine is not ready."));
        if (!AllowedDutyPolicies.Contains(request.DutyPolicy))
            return Serialize(Reject($"Unsupported duty policy '{request.DutyPolicy}'."));
        if (!string.Equals(request.Role, "QueueLeader", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Role, "Participant", StringComparison.OrdinalIgnoreCase))
        {
            return Serialize(Reject($"Unsupported DAD role '{request.Role}'."));
        }
        if (request.AttemptLimit is < 1 or > 200)
            return Serialize(Reject("Attempt limit must be 1-200."));
        if (status.DadOwned && !status.IsTerminal)
        {
            return Serialize(string.Equals(status.DadRunId, request.DadRunId, StringComparison.OrdinalIgnoreCase)
                ? status
                : Reject($"MOGTOME already owns DAD run {status.DadRunId}."));
        }
        if (plugin.Engine.IsRunning)
            return Serialize(Reject("MOGTOME is already running outside DAD ownership."));

        var isLeader = string.Equals(request.Role, "QueueLeader", StringComparison.OrdinalIgnoreCase);
        plugin.Configuration.IsPartyLeader = isLeader;
        plugin.State.IsPartyLeader = isLeader;
        plugin.ConfigManager.SaveCurrentAccount();

        startingDutyCounter = plugin.State.DutyCounter;
        status = new MogtomeDadRunStatus
        {
            DadRunId = request.DadRunId.Trim(),
            Ready = true,
            Accepted = true,
            DadOwned = true,
            IsRunning = true,
            Role = isLeader ? "QueueLeader" : "Participant",
            Preset = request.Preset?.Trim() ?? string.Empty,
            DutyPolicy = request.DutyPolicy,
            AttemptLimit = request.AttemptLimit,
            EngineState = Services.EngineState.Initializing.ToString(),
            Summary = "DAD-owned MOGTOME run accepted.",
            UpdatedAtUtc = DateTime.UtcNow,
        };
        plugin.Engine.Start();
        PublishIfChanged(force: true);
        return Serialize(status);
    }

    private string StopRun(string json)
    {
        var request = Deserialize<MogtomeDadStopRequest>(json);
        if (request == null)
            return Serialize(Reject("Unreadable MOGTOME DAD stop request."));
        if (!status.DadOwned ||
            !string.Equals(status.DadRunId, request.DadRunId, StringComparison.OrdinalIgnoreCase))
        {
            return Serialize(Reject("MOGTOME will only stop the matching DAD-owned session."));
        }

        if (plugin.Engine?.IsRunning == true)
            plugin.Engine.Stop();
        status.IsRunning = false;
        status.IsTerminal = true;
        status.Success = false;
        status.Summary = string.IsNullOrWhiteSpace(request.Reason)
            ? "DAD-owned MOGTOME run stopped."
            : request.Reason;
        status.UpdatedAtUtc = DateTime.UtcNow;
        PublishIfChanged(force: true);
        return Serialize(status);
    }

    private string GetRunStatus()
    {
        var engine = plugin.Engine;
        if (engine == null && string.IsNullOrWhiteSpace(status.DadRunId))
            status = Reject("MOGTOME engine is not ready.");
        else if (engine != null && string.IsNullOrWhiteSpace(status.DadRunId))
            status = new MogtomeDadRunStatus
            {
                Ready = true,
                EngineState = engine.CurrentState.ToString(),
                IsRunning = engine.IsRunning,
                Summary = engine.StatusMessage,
            };
        return Serialize(status);
    }

    private void Fail(string reason)
    {
        status.IsRunning = false;
        status.IsTerminal = true;
        status.Success = false;
        status.FailureReason = reason;
        status.Summary = reason;
        status.UpdatedAtUtc = DateTime.UtcNow;
        PublishIfChanged(force: true);
    }

    private MogtomeDadRunStatus Reject(string reason)
        => new()
        {
            Ready = plugin.Engine != null,
            Accepted = false,
            Summary = reason,
            FailureReason = reason,
            UpdatedAtUtc = DateTime.UtcNow,
        };

    private void PublishIfChanged(bool force = false)
    {
        var signature = $"{status.DadRunId}|{status.IsRunning}|{status.IsTerminal}|{status.Success}|{status.CompletedAttempts}|{status.EngineState}|{status.Summary}|{status.FailureReason}";
        if (!force && string.Equals(signature, lastPublishedSignature, StringComparison.Ordinal))
            return;

        lastPublishedSignature = signature;
        try
        {
            statusChangedProvider.SendMessage(Serialize(status));
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow - lastNotificationFailureLogUtc >= TimeSpan.FromSeconds(30))
            {
                lastNotificationFailureLogUtc = DateTime.UtcNow;
                Plugin.Log.Debug(ex, "[MOGTOME][DAD] Status notification failed.");
            }
        }
    }

    private static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, JsonOptions);

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private void Register<TReturn>(ICallGateProvider<TReturn> provider, Func<TReturn> func)
    {
        provider.RegisterFunc(func);
        unregister.Add(provider.UnregisterFunc);
    }

    private void Register<TArg, TReturn>(ICallGateProvider<TArg, TReturn> provider, Func<TArg, TReturn> func)
    {
        provider.RegisterFunc(func);
        unregister.Add(provider.UnregisterFunc);
    }
}
