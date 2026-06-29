using System;

namespace MOGTOME.IPC;

public sealed class MogtomeDadRunRequest
{
    public int SchemaVersion { get; set; } = 1;
    public string DadRunId { get; set; } = string.Empty;
    public string Role { get; set; } = "Participant";
    public string Preset { get; set; } = "Daily MSQ";
    public string DutyPolicy { get; set; } = "PresetHandoff";
    public int AttemptLimit { get; set; } = 1;
}

public sealed class MogtomeDadStopRequest
{
    public int SchemaVersion { get; set; } = 1;
    public string DadRunId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class MogtomeDadRunStatus
{
    public int SchemaVersion { get; set; } = 1;
    public string DadRunId { get; set; } = string.Empty;
    public bool Ready { get; set; }
    public bool Accepted { get; set; }
    public bool DadOwned { get; set; }
    public bool IsRunning { get; set; }
    public bool IsTerminal { get; set; }
    public bool Success { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Preset { get; set; } = string.Empty;
    public string DutyPolicy { get; set; } = string.Empty;
    public int AttemptLimit { get; set; }
    public int CompletedAttempts { get; set; }
    public string EngineState { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
