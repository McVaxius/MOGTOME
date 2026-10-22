using FFXIVClientStructs.FFXIV.Client.Enums;
using MOGTOME.Localization;
using MOGTOME.Services;
using MOGTOME.Models;
using System.Text.Json;

namespace MOGTOME.Tests;

public sealed class KeyboundBrawlerTests
{
    [Theory]
    [InlineData(false, false, null, "KB_ProfileUnavailable")]
    [InlineData(false, true, 65u, "KB_ProfileUnavailable")]
    [InlineData(true, true, 65u, "KB_AutomationBusy")]
    [InlineData(true, false, null, "KB_OutsideEvent")]
    [InlineData(true, false, 0u, "KB_OutsideEvent")]
    [InlineData(true, false, 64u, "KB_OutsideEvent")]
    [InlineData(true, false, 65u, "KB_GameplayUnverified")]
    [InlineData(true, false, 66u, "KB_OutsideEvent")]
    public void AdmissionRejectsEveryUnverifiedStateEvenInsideTheNativeTerritoryCategory(
        bool ready, bool busy, uint? intendedUse, string reason)
    {
        using var service = new KeyboundBrawlerService();
        service.Observe(ready, busy, intendedUse);
        Assert.Equal(reason, service.UnavailableReason.Key);
        Assert.False(service.CanStart);
        Assert.False(service.Start());
        Assert.False(service.IsRunning);
        Assert.Equal(reason, service.Status.Key);
    }

    [Fact]
    public void StopDepartureReentryAndDisposalNeverArmOrResumeARound()
    {
        var service = new KeyboundBrawlerService();
        service.Observe(true, false, (uint)TerritoryIntendedUse.KeyboundBrawler);
        Assert.False(service.Start());
        service.Stop();
        Assert.Equal("KB_Stopped", service.Status.Key);
        service.Observe(false, false, null); // Logout or transition.
        service.Observe(true, true, 65); // Another owner.
        service.Observe(true, false, 65); // Re-entry must not start.
        Assert.False(service.IsRunning);
        Assert.False(service.CanStart);
        Assert.Equal("KB_Stopped", service.Status.Key);
        service.Dispose();
        service.Observe(true, false, 65);
        Assert.Equal("KB_ProfileUnavailable", service.UnavailableReason.Key);
        Assert.False(service.Start());
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void NativeSymbolIsATerritoryCategoryAndStatusRendersInEveryUiLanguage()
    {
        Assert.Equal("FFXIVClientStructs", typeof(TerritoryIntendedUse).Assembly.GetName().Name);
        Assert.Equal(65u, (uint)TerritoryIntendedUse.KeyboundBrawler);
        using var service = new KeyboundBrawlerService();
        service.Observe(true, false, 65);
        service.Start();
        foreach (var language in Enum.GetValues<UiLanguage>())
        {
            Assert.False(string.IsNullOrWhiteSpace(service.Status.Render(language)));
            Assert.NotEqual("KB_GameplayUnverified", service.Status.Render(language));
        }
    }

    [Fact]
    public void ShopPreparationRoundTripsSeparatelyFromBlundervilleAndOrdinaryDuties()
    {
        var config = JsonSerializer.Deserialize<Configuration>("{\"DutyCounter\":42,\"MaxRuns\":99}")!;
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
        Assert.False(service.Start()); // Saved farming/shopping choices must not arm the pending feature.
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
        var labels = new[] { "KB_Help", "KB_ShopPending", "KB_WalletTarget", "KB_TargetCurrency", "KB_NoCatalog" };
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
