using Dalamud.Configuration;
using System;

namespace MOGTOME;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // --- Party Settings ---
    public bool IsPartyLeader { get; set; } = false;
    public bool IsCrossWorldParty { get; set; } = false;

    // --- Duty Settings ---
    public int DutyCounter { get; set; } = 0;
    public int PraetoriumThreshold { get; set; } = 99;
    public int MaxRuns { get; set; } = 9999;
    public string QuitCommand { get; set; } = "/ays m e";

    // --- Repair Settings ---
    public int RepairThreshold { get; set; } = 25;
    public int RepairMaterialId { get; set; } = 33916;
    public bool AutoEquipRecommended { get; set; } = false;

    // --- Food Settings ---
    public int FoodItemId { get; set; } = 0;
    public string FoodItemName { get; set; } = "";

    // --- Rotation Settings ---
    public string BossModPreset { get; set; } = "none";
    public int QueueMethod { get; set; } = 0;

    // --- Potion Settings ---
    public int PotionItemId { get; set; } = 0;
    public string PotionItemName { get; set; } = "";
    public int PotionTarget { get; set; } = 0;

    // --- Timing Settings ---
    public float LoopInterval { get; set; } = 2.0f;
    public int EchoLevel { get; set; } = 3;

    // --- Debug Settings ---
    public int DebugCounter { get; set; } = 0;
    public int BailoutTimeout { get; set; } = 1200;

    // --- UI Settings ---
    public bool IsConfigWindowMovable { get; set; } = true;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
