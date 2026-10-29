using FFXIVClientStructs.FFXIV.Client.Enums;
using MOGTOME.Localization;
using MOGTOME.Services;
using MOGTOME.Models;
using System.Text.Json;

namespace MOGTOME.Tests;

public sealed class KeyboundBrawlerTests
{
    [Theory]
    [InlineData(false, false, "KB_ProfileUnavailable")]
    [InlineData(false, true, "KB_ProfileUnavailable")]
    [InlineData(true, true, "KB_AutomationBusy")]
    public void AdmissionRequiresAnAvailableProfileAndNoOtherOwner(bool ready, bool busy, string reason)
    {
        using var service = new KeyboundBrawlerService();
        service.Observe(ready, busy, 65);
        Assert.False(service.CanStart);
        Assert.False(service.Start());
        Assert.Equal(reason, service.Status.Key);
    }

    [Fact]
    public void StartArmsForManualQueueAndStopDisposalNeverResume()
    {
        var service = new KeyboundBrawlerService();
        service.Observe(true, false, null);
        Assert.True(service.CanStart);
        Assert.True(service.Start());
        service.Stop();
        service.Observe(true, false, 65);
        Assert.False(service.IsRunning);
        service.Dispose();
        Assert.False(service.Start());
        Assert.False(service.IsRunning);
        Assert.Equal(65u, (uint)TerritoryIntendedUse.KeyboundBrawler);
    }

    [Fact]
    public void ShopPreparationRoundTripsSeparatelyFromBlundervilleAndOrdinaryDuties()
    {
        var config = JsonSerializer.Deserialize<Configuration>("{\"DutyCounter\":42,\"MaxRuns\":99}")!;
        Assert.Equal(80, config.KeyboundBrawler.Stage12TypingWpm);
        Assert.Equal(80, config.KeyboundBrawler.Stage3TypingWpm);
        Assert.False(config.KeyboundBrawler.RunLimitEnabled);
        Assert.Equal(1, config.KeyboundBrawler.RunLimit);
        Assert.True(config.KeyboundBrawler.WalletTargetEnabled);
        Assert.Equal(5470, config.KeyboundBrawler.WalletTarget);
        Assert.True(config.KeyboundBrawler.ShopWhenFinished);
        Assert.Equal(19, config.KeyboundBrawler.PurchaseTargets.Count);
        Assert.All(config.KeyboundBrawler.PurchaseTargets, target =>
        {
            Assert.True(KeyboundBrawlerShop.IsSelectMissingReward(target.Key));
            Assert.Equal(1, target.Value);
        });
        Assert.False(config.KeyboundBrawler.PurchaseTargets.ContainsKey(KeyboundBrawlerShop.CofferItemId));
        config.Blunderville.PurchaseTargets[52323] = 7;
        config.KeyboundBrawler.PurchaseTargets.Clear();
        config.KeyboundBrawler.PurchaseTargets[52323] = 2;
        config.KeyboundBrawler.PurchaseTargets[KeyboundBrawlerShop.CofferItemId] = 3;
        config.KeyboundBrawler.Stage12TypingWpm = 30.5;
        config.KeyboundBrawler.Stage3TypingWpm = 120;
        config.KeyboundBrawler.PartyRole = KeyboundPartyRole.Member;
        config.KeyboundBrawler.RunLimitEnabled = true;
        config.KeyboundBrawler.RunLimit = 5;
        config.KeyboundBrawler.WalletTargetEnabled = false;
        config.KeyboundBrawler.WalletTarget = 910;
        config.KeyboundBrawler.HideOwned = true;
        config.KeyboundBrawler.ShopWhenFinished = false;
        config.KeyboundBrawler.EndingLocation = BlundervilleEndingLocation.Limsa;
        var restored = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config))!;
        Assert.Equal(42, restored.DutyCounter);
        Assert.Equal(99, restored.MaxRuns);
        Assert.Equal(7, restored.Blunderville.PurchaseTargets[52323]);
        Assert.Equal(2, restored.KeyboundBrawler.PurchaseTargets[52323]);
        Assert.Equal(3, restored.KeyboundBrawler.PurchaseTargets[KeyboundBrawlerShop.CofferItemId]);
        Assert.Equal(2, restored.KeyboundBrawler.PurchaseTargets.Count);
        Assert.Equal(30.5, restored.KeyboundBrawler.Stage12TypingWpm);
        Assert.Equal(120, restored.KeyboundBrawler.Stage3TypingWpm);
        Assert.Equal(KeyboundPartyRole.Member, restored.KeyboundBrawler.PartyRole);
        Assert.True(restored.KeyboundBrawler.RunLimitEnabled);
        Assert.Equal(5, restored.KeyboundBrawler.RunLimit);
        Assert.False(restored.KeyboundBrawler.WalletTargetEnabled);
        Assert.Equal(910, restored.KeyboundBrawler.WalletTarget);
        Assert.True(restored.KeyboundBrawler.HideOwned);
        Assert.False(restored.KeyboundBrawler.ShopWhenFinished);
        Assert.Equal(BlundervilleEndingLocation.Limsa, restored.KeyboundBrawler.EndingLocation);
        Assert.Equal(19, new Configuration().KeyboundBrawler.PurchaseTargets.Count);
        config.KeyboundBrawler.PurchaseTargets.Clear();
        Assert.Empty(JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config))!.KeyboundBrawler.PurchaseTargets);
        using var service = new KeyboundBrawlerService();
        service.Observe(true, false, 65);
        Assert.False(service.IsRunning); // Loading settings alone never arms input.
    }

    [Theory]
    [InlineData("{}", 80)]
    [InlineData("{\"TypingWpm\":61}", 61)]
    [InlineData("{\"TypingWpm\":61,\"PlayMode\":0}", 61)]
    [InlineData("{\"TypingWpm\":61,\"PlayMode\":1}", 30.5)]
    [InlineData("{\"PlayMode\":1,\"TypingWpm\":10}", 5)]
    [InlineData("{\"TypingWpm\":999,\"PlayMode\":1}", 150)]
    [InlineData("{\"TypingWpm\":0,\"PlayMode\":1}", 5)]
    public void LegacySettingsRetainSelectedSpeedInBothStagesAndSaveWithoutTheRemovedMode(string json, double expected)
    {
        var config = JsonSerializer.Deserialize<Configuration>("{\"KeyboundBrawler\":" + json + "}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(expected, config.KeyboundBrawler.Stage12TypingWpm);
        Assert.Equal(expected, config.KeyboundBrawler.Stage3TypingWpm);
        var saved = JsonSerializer.Serialize(config);
        using var document = JsonDocument.Parse(saved);
        var settings = document.RootElement.GetProperty("KeyboundBrawler");
        Assert.False(settings.TryGetProperty("PlayMode", out _));
        Assert.False(settings.TryGetProperty("TypingWpm", out _));
        var restored = JsonSerializer.Deserialize<Configuration>(saved)!;
        Assert.Equal(expected, restored.KeyboundBrawler.Stage12TypingWpm);
        Assert.Equal(expected, restored.KeyboundBrawler.Stage3TypingWpm);
    }

    [Theory]
    [InlineData("{\"Stage12TypingWpm\":25.5,\"Stage3TypingWpm\":150,\"TypingWpm\":80,\"PlayMode\":1}")]
    [InlineData("{\"PlayMode\":1,\"TypingWpm\":80,\"Stage3TypingWpm\":150,\"Stage12TypingWpm\":25.5}")]
    public void ExplicitStageSettingsWinOverLegacyFieldsRegardlessOfJsonOrder(string json)
    {
        var settings = JsonSerializer.Deserialize<KeyboundBrawlerSettings>(json)!;
        Assert.Equal(25.5, settings.Stage12TypingWpm);
        Assert.Equal(150, settings.Stage3TypingWpm);
    }

    [Theory]
    [InlineData("{\"Stage12TypingWpm\":25.5,\"TypingWpm\":61,\"PlayMode\":1}", 25.5, 30.5)]
    [InlineData("{\"TypingWpm\":61,\"PlayMode\":1,\"Stage3TypingWpm\":150}", 30.5, 150)]
    [InlineData("{\"Stage12TypingWpm\":0,\"Stage3TypingWpm\":999}", 5, 300)]
    [InlineData("{\"typingwpm\":61,\"playmode\":1}", 30.5, 30.5)]
    public void MissingStageUsesLegacySpeedAndNewValuesUseTheirOwnBounds(string json, double stage12, double stage3)
    {
        var settings = JsonSerializer.Deserialize<KeyboundBrawlerSettings>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(stage12, settings.Stage12TypingWpm);
        Assert.Equal(stage3, settings.Stage3TypingWpm);
    }

    [Fact]
    public void ShopOffersRequireTheVerifiedMgcShopAndOneUnambiguousCurrencyCost()
    {
        var offer = KeyboundBrawlerShop.ReadOffer(1771021, "", [(52450, 1, false), (0, 1, false)], [(52322, 910, 0, 0)]);
        Assert.NotNull(offer);
        Assert.Equal(52450u, offer.ItemId);
        Assert.Equal(910u, offer.Price);
        Assert.Equal(1u, offer.ReceiveCount);
        Assert.Null(KeyboundBrawlerShop.ReadOffer(1770724, "", [(52450, 1, false)], [(52322, 910, 0, 0)]));
        Assert.Null(KeyboundBrawlerShop.ReadOffer(1771021, "", [(52450, 1, false)], [(41629, 910, 0, 0)]));
        Assert.Null(KeyboundBrawlerShop.ReadOffer(1771021, "", [(52450, 1, false)], [(52322, 910, 0, 0), (29, 1, 0, 0)]));
        Assert.Null(KeyboundBrawlerShop.ReadOffer(1771021, "", [(52450, 1, false), (52283, 1, false)], [(52322, 910, 0, 0)]));
        Assert.Null(KeyboundBrawlerShop.ReadOffer(1771021, "", [(52450, 1, true)], [(52322, 910, 0, 0)]));
        Assert.Null(KeyboundBrawlerShop.ReadOffer(1771021, "", [(52450, 0, false)], [(52322, 910, 0, 0)]));
        Assert.Null(KeyboundBrawlerShop.ReadOffer(1771021, "", [(52450, 1, false)], [(52322, 0, 0, 0)]));
        Assert.Null(KeyboundBrawlerShop.ReadOffer(1771021, "", [(52450, 1, false)], [(52322, 910, 1, 0)]));
        Assert.Null(KeyboundBrawlerShop.ReadOffer(1771021, "", [(52450, 1, false)], [(52322, 910, 0, 3)]));
        Assert.Equal(1820ul, BlundervilleProgress.TotalMgf(new Dictionary<uint, int> { [52450] = 3 },
            new Dictionary<uint, BlundervilleOffer> { [52450] = offer }, _ => 1, out var known));
        Assert.True(known);
    }

    [Fact]
    public void PreparationLabelsRenderInEveryLanguageAndTheNavigationIconIsEmbedded()
    {
        var labels = new[] { "KB_Help", "KB_AutoLoopHelp", "KB_WaitInput", "KB_InputStalled", "KB_WalletTarget", "KB_TargetCurrency", "KB_NoCatalog", "KB_Stage12TypingSpeed", "KB_Stage3TypingSpeed", "KB_TypingSpeedHelp" };
        foreach (var language in Enum.GetValues<UiLanguage>())
        {
            foreach (var key in labels)
            {
                var text = Ui.M(key).Render(language);
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.NotEqual(key, text);
            }
            Assert.Contains("910", Ui.M("KB_TotalCurrency", 910).Render(language));
        }
        using var image = typeof(KeyboundBrawlerService).Assembly.GetManifestResourceStream("MOGTOME.images.keybound-icon.png");
        Assert.NotNull(image);
        Assert.True(image.Length > 0);
        using var blunderville = typeof(KeyboundBrawlerService).Assembly.GetManifestResourceStream("MOGTOME.images.blunderville-icon.png");
        Assert.NotNull(blunderville);
        Assert.True(blunderville.Length > 0);
    }
}
