using System;
using Lumina.Excel.Sheets;
using MOGTOME.Localization;
using TerritoryKind = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace MOGTOME.Services;

/// <summary>Admission only until the event's input, acknowledgement and round-end contracts are verified.</summary>
public sealed class KeyboundBrawlerService : IDisposable
{
    private bool disposed;
    public UiText Status { get; private set; } = Ui.M("KB_Idle");
    public UiText UnavailableReason { get; private set; } = Ui.M("KB_ProfileUnavailable");

    // No action route has been established. A territory category is not an actionable round state.
    // Never acquire an automation owner, post keys, fire callbacks or report completion in this slice.
    // Reviewed 2026-10-21: ClientStructs d6397a76 has TerritoryIntendedUse.KeyboundBrawler (65)
    // and AgentId.TurnBreak (449), but no verified native input/action contract.
    // Lumina.Excel 7.5.1 defines MiniGameTurnBreak* sheets with Unknown* fields. XIVAPI dataset
    // 0874aee069e1e7c4 has populated rows (including Action 1, "Pom Fire"); these are not key bindings.
    // Reviewed 2026-10-22: NumberArrayType.TurnBreak=151 (not a string-array mapping),
    // InstanceContentType.KeyboundBrawler=23; TurnBreak{,Title,Game,Result} are candidate addon names.
    // Datamining a67c23b0 DescriptionString 1681: type the displayed enemy word, then Enter.
    // 1688: Letterlock requires withholding all keypresses. 1761: Escape abandons with a possible penalty.
    // Therefore Stop must release only owned input; it must never press Escape to exit the event.
    // Addon 17839/17840 label input disabled/enabled; 17846 labels Results. These text rows do not
    // establish native node/buffer indices, accepted text, HP/score acknowledgement or result transitions.
    // Gameplay input must preserve the active client's native phrase and language, independently of UiLanguage.
    // Never submit Ui.T translations, English fallbacks or GameText.Normalize output. KTGTypeWordTextData
    // and KTGTypeWordProgram contain localized words/readings and program data, but do not identify the
    // currently actionable word. Verify that native mapping and discard stale word/language state before input.
    // Official availability starts 2026-10-28 08:00 UTC. One user-run round after that date is still
    // needed to identify those exact native fields before the input route can be enabled.
    // Any mechanic or input-permission state not observed in that round must continue withholding input.
    public bool CanStart => false;
    public bool IsRunning => false;

    internal void Observe(bool ready, bool conflictingAutomation, uint? intendedUse)
    {
        UnavailableReason = Ui.M(!ready || disposed ? "KB_ProfileUnavailable"
            : conflictingAutomation ? "KB_AutomationBusy"
            : intendedUse != (uint)TerritoryKind.KeyboundBrawler ? "KB_OutsideEvent"
            : "KB_GameplayUnverified");
    }

    internal static uint? ReadTerritoryIntendedUse()
    {
        try
        {
            return Plugin.DataManager.GetExcelSheet<TerritoryType>()
                .GetRowOrDefault(Plugin.ClientState.TerritoryType)?.TerritoryIntendedUse.RowId;
        }
        catch
        {
            // Unreadable native data cannot identify a gameplay surface.
            return null;
        }
    }

    public bool Start()
    {
        Status = UnavailableReason;
        return false;
    }

    public void Stop() => Status = Ui.M("KB_Stopped");
    public void Dispose() { Stop(); disposed = true; }
}
