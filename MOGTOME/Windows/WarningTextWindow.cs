using MOGTOME.Localization;
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MOGTOME.Windows;

public sealed class WarningTextWindow : Window, IDisposable
{
    public const int CurrentWarningVersion = 0;

    private static readonly UiText[] WarningLines =
    [
        Ui.M("WarningText_SorryToMakeYouReadThis"),
        "-",
        Ui.M("WarningText_AFewNotesOnMOGTOMEVersionX"),
        Ui.M("WarningText_ItSExperimentalAndWillBreakOccasionally"),
        Ui.M("WarningText_PleaseProvideLogsSoICanAnalyze"),
        "-",
        Ui.M("WarningText_MultiplayerGuideDiscordLinkAndInfoAt"),
        "-",
		"https://aethertek.io/",
        "-",
        Ui.M("WarningText_ThereAreWaysMOGTOMEIsCurrentlyCrashing"),
        Ui.M("WarningText_IfYouHaveRSRUISimulateThe"),
        "-",
        Ui.M("WarningText_DoNotRunDalamudFromTheSame"),
        Ui.M("WarningText_AUTODUTYRSRNavmeshSometimesWILLHaveRace"),
        Ui.M("WarningText_YouWillNeedToUseMultiInstall"),
        Ui.M("WarningText_ItWillBeTheFirstThingI"),
        "-",
        Ui.M("WarningText_IfYouDonTHaveVeryMuch"),
        Ui.M("WarningText_PlanForGBFreePerClientYou"),
        Ui.M("WarningText_SeeTheMultiplayerGuideOnHttpsAethertek"),
        "---------------------------------------------------------------------------------------------------",
        "-",
        Ui.M("WarningText_NowForSomeTipsAndTricksFor"),
        "-",
        Ui.M("WarningText_IfYouAreSelfRepairingSetRepair"),
		Ui.M("WarningText_AutoDutyRepairIsStillWeirdSometimesADS"),
        "-",
		Ui.M("WarningText_MakeAnAutoDutyProfileJustForThis"),
		Ui.M("WarningText_MOGTOMEChangesSomeSettingsSoThisKeeps"),
        "-",
		Ui.M("WarningText_JoinTheAutoPartyDiscordHttpsDiscordGg"),
        "-",
        Ui.M("WarningText_WhenStartingMogtomeMakeSureYouStart"),
		Ui.M("WarningText_ConfigureWHOThePartyLeaderIsBy"),
        Ui.M("WarningText_IDidThisBecauseItsASeriously"),
		Ui.M("WarningText_IHaveSomeCommentedOutAndRetired"),
    ];

    private readonly Plugin plugin;
    private bool warningAcknowledgedThisOpen;

    public WarningTextWindow(Plugin plugin)
        : base(Ui.T("Window_MOGTOMEWarningText") + "###MOGTOMEWarningText", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560f, 360f),
            MaximumSize = new Vector2(760f, 560f),
        };
    }

    public void Dispose()
    {
    }

    public void Show(bool force = false)
    {
        if (!force && !NeedsAcknowledgement())
            return;

        warningAcknowledgedThisOpen = false;
        IsOpen = true;
    }

    public void ShowIfNeeded()
    {
        if (!NeedsAcknowledgement())
            return;

        warningAcknowledgedThisOpen = false;
        IsOpen = true;
    }

    public override void PreDraw() => WindowName = Ui.T("Window_MOGTOMEWarningText") + "###MOGTOMEWarningText";

    public override void Draw()
    {
        if (ImGui.IsWindowAppearing())
        {
            var viewport = ImGui.GetMainViewport();
            var posX = (viewport.WorkSize.X - 600f) / 2f;
            var posY = (viewport.WorkSize.Y - 420f) / 2f;
            ImGui.SetWindowPos(new Vector2(MathF.Max(1f, posX), MathF.Max(1f, posY)));
        }

        ImGui.TextColored(new Vector4(1.0f, 0.55f, 0.2f, 1.0f), Ui.T("WarningText_ReadThisBeforeRunningMOGTOME"));
        ImGui.SameLine();
        UiLayout.TextDisabled(Ui.T("WarningText_WarningV", CurrentWarningVersion));
        ImGui.Spacing();

        foreach (var line in WarningLines)
        {
            ImGui.TextWrapped(line.Render());
            ImGui.Spacing();
        }

        var buttonWidth = MathF.Max(260f, ImGui.GetContentRegionAvail().X);
        if (UiLayout.Button(Ui.L("WarningText_OKIREADIT"), new Vector2(buttonWidth, 42f)))
        {
            plugin.Configuration.WarningPopupAcknowledgedVersion = CurrentWarningVersion;
            plugin.ConfigManager.SaveCurrentAccount();
            warningAcknowledgedThisOpen = true;
            IsOpen = false;
        }
    }

    public override void OnClose()
    {
        if (!warningAcknowledgedThisOpen && NeedsAcknowledgement())
            IsOpen = true;
    }

    private bool NeedsAcknowledgement()
        => plugin.Configuration.WarningPopupAcknowledgedVersion < CurrentWarningVersion;
}
