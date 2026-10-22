using FFXIVClientStructs.FFXIV.Client.Enums;
using MOGTOME.Localization;
using MOGTOME.Services;

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
}
