using System;

namespace MOGTOME.Models;

public class DutyState
{
    // --- Duty Tracking ---
    public int DutyCounter { get; set; } = 0;
    public int DecumanaCounter { get; set; } = 0;
    public DateTime? NextResetTime { get; set; } = null;
    public bool IsInDuty { get; set; } = false;
    public bool IsInCombat { get; set; } = false;
    public bool IsPartyLeader { get; set; } = false;
    public ushort CurrentTerritory { get; set; } = 0;

    // --- Territory IDs ---
    public const ushort PraetoriumTerritoryId = 1044;
    public const ushort DecumanaTerritoryId = 1048;
    public const ushort LimsaLowerTerritoryId = 129;

    // --- Duty IDs for ContentsFinder ---
    public int PraetoriumDutyIndex { get; set; } = 16;
    public const int DecumanaDutyId = 830;

    // --- Timing ---
    public DateTime? DutyStartTime { get; set; }
    public DateTime? LastCompletionTime { get; set; }
    public float LastCompletionDuration { get; set; } = 0;
    public float MaxContentTime { get; set; } = 0;
    public float TimeInDuty { get; set; } = 0;

    // --- Stuck Detection ---
    public float LastPositionX { get; set; } = 0;
    public float LastPositionY { get; set; } = 0;
    public float LastPositionZ { get; set; } = 0;
    public int StuckTickCount { get; set; } = 0;

    // --- State Flags ---
    public bool HasEnteredDuty { get; set; } = false;
    public bool AutoQueueDisabledForRepair { get; set; } = false;
    public bool PotionsAvailable { get; set; } = true;
    public bool FoodAvailable { get; set; } = true;
    public bool IsRunning { get; set; } = false;
    public bool ShouldStop { get; set; } = false;

    // --- BossMod ---
    public string WhichBossMod { get; set; } = "vbm";

    // --- Repair State ---
    public bool NeedsRepair { get; set; } = false;
    public int RepairCheckCounter { get; set; } = 0;

    // --- Calculated Timeouts (based on timedilation) ---
    public int MaxJiggle { get; set; } = 30;
    public int MaxRes { get; set; } = 15;

    public void CalculateTimeouts(float timedilation)
    {
        if (timedilation <= 0) timedilation = 2.0f;
        MaxJiggle = (int)Math.Floor(2.0f / timedilation * 30);
        MaxRes = (int)Math.Floor(2.0f / timedilation * 15);
    }

    public void Reset()
    {
        IsInDuty = false;
        IsInCombat = false;
        DutyStartTime = null;
        MaxContentTime = 0;
        TimeInDuty = 0;
        StuckTickCount = 0;
        HasEnteredDuty = false;
    }
}
