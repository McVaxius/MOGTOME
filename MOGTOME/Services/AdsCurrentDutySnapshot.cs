using MOGTOME.Localization;
using System;
using System.Text.Json;
using MOGTOME.Models;

namespace MOGTOME.Services;

public sealed record AdsCurrentDutySnapshot(
    string DutyName,
    uint TerritoryTypeId,
    uint ContentFinderConditionId,
    AdsDutyCategory Category,
    string SupportLevel,
    string ClearanceStatus,
    int ClearanceLevel,
    DateTime CapturedAtUtc)
{
    public bool MatchesIdentity(uint territoryTypeId, uint contentFinderConditionId)
        => TerritoryTypeId == territoryTypeId
           && ContentFinderConditionId == contentFinderConditionId;

    public static bool TryParseStatusJson(
        string json,
        DateTime capturedAtUtc,
        out AdsCurrentDutySnapshot? snapshot,
        out string failure)
    {
        var success = TryReadSnapshot(json, capturedAtUtc, out snapshot, out var message);
        failure = message.English;
        return success;
    }

    internal static bool TryReadSnapshot(string json, DateTime capturedAtUtc,
        out AdsCurrentDutySnapshot? snapshot, out UiText failure)
    {
        snapshot = null;
        failure = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            failure = Ui.M("Ads_Empty");
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!TryReadBoolean(root, "inInstancedDuty", out var inInstancedDuty)
                || !inInstancedDuty)
            {
                failure = Ui.M("Ads_Inactive");
                return false;
            }

            if (!TryReadBoolean(root, "hasCatalogMetadata", out var hasCatalogMetadata)
                || !hasCatalogMetadata)
            {
                failure = Ui.M("Ads_NoCatalog");
                return false;
            }

            if (!TryReadString(root, "duty", out var dutyName))
            {
                failure = Ui.M("Ads_NoName");
                return false;
            }

            if (!TryReadUInt32(root, "territoryTypeId", out var territoryTypeId)
                || territoryTypeId == 0)
            {
                failure = Ui.M("Ads_NoTerritory");
                return false;
            }

            if (!TryReadUInt32(root, "contentFinderConditionId", out var contentFinderConditionId))
            {
                failure = Ui.M("Ads_NoCfc");
                return false;
            }

            if (!TryReadString(root, "dutyCategory", out var categoryName)
                || !TryParseCategory(categoryName, out var category))
            {
                failure = Ui.M("Ads_Category", categoryName);
                return false;
            }

            if (!TryReadString(root, "supportLevel", out var supportLevel)
                || !IsKnownSupportLevel(supportLevel))
            {
                failure = Ui.M("Ads_Support", supportLevel);
                return false;
            }

            if (!TryReadString(root, "clearanceStatus", out var clearanceStatus)
                || !TryParseClearance(clearanceStatus, out var clearanceLevel))
            {
                failure = Ui.M("Ads_Clearance", clearanceStatus);
                return false;
            }

            snapshot = new AdsCurrentDutySnapshot(
                dutyName,
                territoryTypeId,
                contentFinderConditionId,
                category,
                supportLevel,
                clearanceStatus,
                clearanceLevel,
                capturedAtUtc);
            return true;
        }
        catch (JsonException ex)
        {
            failure = Ui.M("Ads_InvalidJson", ex.Message);
            return false;
        }
    }

    private static bool TryReadBoolean(JsonElement root, string propertyName, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadUInt32(JsonElement root, string propertyName, out uint value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetUInt32(out value);
    }

    private static bool TryReadString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryParseCategory(string value, out AdsDutyCategory category)
    {
        category = value switch
        {
            "Solo" => AdsDutyCategory.Solo,
            "FourMan" => AdsDutyCategory.FourMan,
            "EightMan" => AdsDutyCategory.EightMan,
            "AllianceRaid" => AdsDutyCategory.Alliance,
            "GuildHest" => AdsDutyCategory.GuildHest,
            "DeepDungeon" => AdsDutyCategory.DeepDungeon,
            "TreasureDungeon" => AdsDutyCategory.TreasureDungeon,
            "Other" => AdsDutyCategory.Other,
            _ => (AdsDutyCategory)(-1),
        };
        return category >= AdsDutyCategory.Solo && category <= AdsDutyCategory.Other;
    }

    private static bool IsKnownSupportLevel(string value)
        => value is "Unsupported" or "PassiveOnly" or "ActiveSupported";

    private static bool TryParseClearance(string value, out int clearanceLevel)
    {
        clearanceLevel = value switch
        {
            "NotCleared" => 0,
            "OnePlayerUnsyncCleared" => 1,
            "OnePlayerDutySupport" => 2,
            "FourPlayerSyncCleared" => 3,
            _ => -1,
        };
        return clearanceLevel >= 0;
    }
}
