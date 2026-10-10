using MOGTOME.Localization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Numerics;
using System.IO;
using Dalamud.Interface.Utility;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using MOGTOME.Models;
using MOGTOME.Services;
using MOGTOME.IPC;
using MOGTOME.UiDesign;
using AethertekUI;

namespace MOGTOME.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
    private static readonly string CurrentVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
    private readonly Plugin plugin;
    private Vector2? pendingWindowPosition;
    private bool pendingPositionConditionReset;

    public MainWindow(Plugin plugin)
        : base(Ui.T("Window_MOGTOMEStatus") + "###MogtomeMain", ImGuiWindowFlags.HorizontalScrollbar)
    {
        this.plugin = plugin;
        Size = new Vector2(1228, 896);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 300),
            MaximumSize = new Vector2(1500, 1400),
        };
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Cog, Priority = 0, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.ConfigWindow.Toggle(); },
            ShowTooltip = () => UiLayout.SetTooltip(Ui.T("Main_Config")),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.ChartBar, Priority = -10, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.StatsWindow.Toggle(); },
            ShowTooltip = () => UiLayout.SetTooltip(Ui.T("Main_Stats")),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Play, Priority = -20, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) StartFromMain(); },
            ShowTooltip = () => UiLayout.SetTooltip(Ui.T("Main_Start") + (plugin.Engine == null
                ? "\n" + Ui.T("Main_StatusWaitingForAccountAndEngineInitialization")
                : plugin.IsEngineStartQueued ? "\n" + Ui.T("Chat_MOGTOMEStartAlreadyQueued")
                : plugin.Engine.IsRunning ? "\n" + Ui.T("Main_Running") + "\n" + plugin.Engine.Status.Render() : string.Empty)),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Stop, Priority = -30, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) StopFromMain(); },
            ShowTooltip = () => UiLayout.SetTooltip(Ui.T("Main_Stop") + (plugin.IsEngineStartQueued
                ? "\n" + Ui.T("Chat_MOGTOMEStartAlreadyQueued")
                : plugin.Engine == null ? "\n" + Ui.T("Main_StatusWaitingForAccountAndEngineInitialization")
                : "\n" + Ui.T(plugin.Engine.IsRunning ? "Main_Running" : "Main_Stopped") + "\n" + plugin.Engine.Status.Render())),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Clock, Priority = -40, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.Engine?.ToggleStopAfterNextSuccessfulRun(); },
            ShowTooltip = () => UiLayout.SetTooltip(Ui.T(plugin.Engine?.StopAfterNextSuccessfulRunArmed == true
                ? "Main_CancelStopAfterNext" : "Main_StopAfterNextSuccess") + "\n" + Ui.T(plugin.Engine == null
                ? "Main_StatusWaitingForAccountAndEngineInitialization" : plugin.Engine.StopAfterNextSuccessfulRunArmed
                    ? "Main_ArmedStopsAfterASuccessfulRunIs" : "Main_RuntimeOnlyAbortedRunsDoNotConsume")),
        });
    }

    public void Dispose() { }

    public void QueueResetToOrigin()
        => QueueWindowPosition(new Vector2(1f, 1f));

    public void QueueRandomVisibleJump()
        => QueueWindowPosition(GetRandomVisiblePosition());

    public override void PreDraw()
    {
        WindowName = Ui.T("Window_MOGTOMEStatus") + " v" + CurrentVersion + "###MogtomeMain";
        Size = MogtomePresentation.Compact ? new Vector2(1097, 777) : new Vector2(1228, 896);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(Math.Max(380, UiLayout.TitleMinimumWidth(this) / Math.Max(.01f, ImGuiHelpers.GlobalScale)), 300),
            MaximumSize = new Vector2(1500, 1400),
        };
        if (pendingWindowPosition.HasValue)
        {
            Position = pendingWindowPosition.Value;
            PositionCondition = ImGuiCond.Always;
            pendingWindowPosition = null;
            pendingPositionConditionReset = true;
        }
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
        { windowMotion.Restore(this); plugin.Appearance.PaintBrandTitle(this); }

    private void StartFromMain()
    {
        if (plugin.Engine is { IsRunning: false } && !plugin.IsEngineStartQueued)
            plugin.QueueEngineStart("main window", notifyChat: false);
    }

    private void StopFromMain()
    {
        if (plugin.Engine?.IsRunning == true || plugin.IsEngineStartQueued || plugin.MoogleShop?.IsRunning == true || plugin.IsMoogleShopActionQueued || plugin.Blunderville?.IsRunning == true || plugin.IsBlundervilleActionQueued)
            plugin.StopEngine();
    }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        var config = plugin.Configuration;
        var engine = plugin.Engine;
        var scale = ImGuiHelpers.GlobalScale;
        plugin.ConsumableInventoryService.Refresh();
        DrawHeader();
        DrawTeamLeader();
        UiLayout.Panel(() =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var width = UiLayout.AvailableWidth;
            MaterialIcons.Draw(MaterialIcon.Location, origin + new Vector2(0, 4 * scale), 30 * scale, MaterialTheme.Current.Colors.Primary);
            ImGui.SetCursorScreenPos(origin + new Vector2(44 * scale, 0));
            ImGui.BeginGroup();
            Heading("Window_BlundervilleFailer");
            UiLayout.Wrapped(Ui.T("Main_OpenBlunderville"));
            ImGui.EndGroup();
            var buttonWidth = UiLayout.IconButtonWidth(Ui.T("Main_Open"));
            if (width >= ImGui.GetItemRectSize().X + 44 * scale + buttonWidth + 16 * scale)
                ImGui.SetCursorScreenPos(origin + new Vector2(width - buttonWidth, 0));
            if (UiLayout.IconButton(Ui.L("Window_BlundervilleFailer"), MaterialIcon.ArrowRight,
                new Vector2(Math.Min(buttonWidth, UiLayout.AvailableWidth), (MogtomePresentation.Compact ? 36 : 42) * scale), Ui.T("Main_Open")))
                plugin.BlundervilleWindow.IsOpen = true;
        }, minimumHeight: (MogtomePresentation.Compact ? 60 : 74) * scale);
        UiLayout.Panel(() =>
        {
            var origin = ImGui.GetCursorScreenPos();
            var width = UiLayout.AvailableWidth;
            var leftWidth = Math.Min(280 * scale, width * .29f);
            var color = engine?.CurrentState switch
            {
                EngineState.InDuty => new Vector4(.35f, .90f, .50f, 1),
                EngineState.Queueing => new Vector4(1, .85f, .35f, 1),
                EngineState.RepairingOutside => new Vector4(1, .60f, .30f, 1),
                EngineState.Stopping => new Vector4(1, .40f, .40f, 1),
                _ => MaterialTheme.Current.Colors.OnSurfaceVariant,
            };
            ImGui.BeginGroup();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + (width >= 480 * scale ? leftWidth : width));
            UiLayout.Wrapped(Ui.T("Main_StateHeading"));
            using (plugin.Appearance.Font(MogtomePresentation.Compact ? MogtomeFontRole.CompactTitle : MogtomeFontRole.Title))
            {
                var marker = ImGui.GetCursorScreenPos();
                var height = ImGui.GetTextLineHeight();
                ImGui.GetWindowDrawList().AddCircleFilled(marker + new Vector2(14 * scale, height * .5f), 13 * scale, MaterialCanvas.Color(color), 24);
                ImGui.Dummy(new Vector2(28 * scale, height));
                ImGui.SameLine();
                AethertekUI.MaterialText.TextColored(MaterialTheme.Current.Colors.OnSurface, engine == null ? Ui.T("Main_StateInitializing") : Ui.EnumLabel(engine.CurrentState));
            }
            ImGui.PopTextWrapPos();
            ImGui.EndGroup();
            var stateHeight = ImGui.GetItemRectSize().Y;
            if (width >= 480 * scale) ImGui.SetCursorScreenPos(origin + new Vector2(leftWidth + 24 * scale, 0));
            ImGui.BeginGroup();
            UiLayout.Wrapped(Ui.T("Main_StatusHeading"));
            var statusOrigin = ImGui.GetCursorScreenPos();
            var statusHeight = MogtomePresentation.Compact ? 28 : 34;
            var statusColor = engine?.IsRunning == true ? new Vector4(.35f, .90f, .50f, 1) : MaterialTheme.Current.Colors.OnSurfaceVariant;
            ImGui.GetWindowDrawList().AddCircleFilled(statusOrigin + new Vector2(14 * scale, statusHeight * .5f * scale), 13 * scale, MaterialCanvas.Color(statusColor), 24);
            ImGui.Dummy(new Vector2(28 * scale, statusHeight * scale));
            ImGui.SameLine();
            ImGui.BeginGroup();
            using (plugin.Appearance.Font(MogtomePresentation.Compact ? MogtomeFontRole.CompactTitle : MogtomeFontRole.Title))
                UiLayout.Wrapped(Ui.T(engine == null ? "Main_StateInitializing" : engine.IsRunning ? "Main_Running" : "Main_Stopped"));
            var status = engine == null ? Ui.T("Main_StatusWaitingForAccountAndEngineInitialization") : engine.Status.Render();
            if (MogtomePresentation.Compact) UiLayout.SameLineIfFits(AethertekUI.MaterialText.Measure(status).X);
            UiLayout.Wrapped(status);
            ImGui.EndGroup();
            ImGui.EndGroup();
            if (width >= 480 * scale)
            {
                var height = Math.Max(stateHeight, ImGui.GetItemRectSize().Y);
                ImGui.GetWindowDrawList().AddLine(origin + new Vector2(leftWidth + 12 * scale, 0),
                    origin + new Vector2(leftWidth + 12 * scale, height), MaterialCanvas.Color(MaterialTheme.Current.Colors.OutlineVariant));
                ImGui.SetCursorScreenPos(origin);
                ImGui.Dummy(new Vector2(width, height));
            }
        }, minimumHeight: (MogtomePresentation.Compact ? 75 : 112) * scale);
        DrawActions();
        if (engine == null) { FinalizePendingWindowPlacement(); return; }
        var width = ImGui.GetContentRegionAvail().X;
        var paired = width >= 780 * scale;
        var gap = (MogtomePresentation.Compact ? 15 : 18) * scale;
        var dutyWidth = paired ? (width - gap) * (MogtomePresentation.Compact ? 480f / 1045 : 552f / 1164) : width;
        var partyWidth = paired ? width - gap - dutyWidth : width;
        var origin = ImGui.GetCursorScreenPos();
        var dutySize = UiLayout.Panel(() => DrawCard(DrawDuty), dutyWidth, (MogtomePresentation.Compact ? 216 : 260) * scale);
        if (paired) ImGui.SetCursorScreenPos(origin + new Vector2(dutyWidth + gap, 0));
        var partySize = UiLayout.Panel(() => DrawCard(DrawParty), partyWidth, (MogtomePresentation.Compact ? 216 : 260) * scale);
        if (paired)
        {
            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new Vector2(width, Math.Max(dutySize.Y, partySize.Y)));
        }
        UiLayout.Panel(() =>
        {
            Heading("Main_MainWindowSettings");
            CardSeparator();
            var useAdsExperimental = config.UseAdsExperimental;
            if (UiLayout.Checkbox(Ui.L("Main_AIDutySolverADS"), ref useAdsExperimental)) ToggleAdsExperimental(useAdsExperimental);
            if (ImGui.IsItemHovered()) UiLayout.SetTooltip(Ui.T("Main_DutyBackendSelectorEnablingADSImmediatelySends"));
            plugin.ConfigWindow.DrawCombatRotationSelector("Main");
            UiLayout.TextDisabled(config.UseAdsExperimental ? Ui.T("Main_ADSDutyBackendActiveADSAlsoHandles") : Ui.T("Main_AutoDutyDutyBackendActiveADSIsOptional"));
        }, minimumHeight: (MogtomePresentation.Compact ? 100 : 122) * scale);
        if (config.DebugModeEnabled && MaterialText.CollapsingHeader(Ui.L("Main_DebugTools"), ImGuiTreeNodeFlags.NoTreePushOnOpen)) DrawDebug();
        FinalizePendingWindowPlacement();
    }

    private void DrawHeader()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        var width = UiLayout.AvailableWidth;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var imageSize = new Vector2(MogtomePresentation.Compact ? 72 : 78) * scale;
        var koFi = Ui.T("Main_KoFi").Trim('♡', ' ');
        var linkWidth = UiLayout.IconButtonWidth(koFi) + UiLayout.IconButtonWidth("Discord") + spacing;
        var compactWidth = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + AethertekUI.MaterialText.Measure("C").X;
        var transparencyWidth = plugin.Configuration.UiTransparencyVisibleOnMainWindow
            ? ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + AethertekUI.MaterialText.Measure(Ui.T("Transparency")).X : 0;
        var controlsWidth = (plugin.Configuration.UiCompactVisibleOnMainWindow ? compactWidth : 0) + transparencyWidth + linkWidth
            + (plugin.Configuration.UiLanguageVisibleOnMainWindow ? plugin.Appearance.LanguageWidth : 0) + spacing * 3;
        MogtomeAppearance.DrawHeaderArt(origin, imageSize);
        ImGui.Dummy(imageSize);
        ImGui.SameLine(0, 24 * scale);
        ImGui.BeginGroup();
        using (plugin.Appearance.Font(MogtomePresentation.Compact ? MogtomeFontRole.CompactBrand : MogtomeFontRole.Brand))
        {
            var titleSize = AethertekUI.MaterialText.Measure("M.O.G.T.O.M.E.");
            AethertekUI.MaterialText.AddText(ImGui.GetWindowDrawList(), ImGui.GetFont(), ImGui.GetFontSize(),
                ImGui.GetCursorScreenPos(), MaterialCanvas.Color(MaterialTheme.Current.Colors.Primary), "M.O.G.T.O.M.E.");
            ImGui.Dummy(titleSize);
        }
        ImGui.EndGroup();
        var brandSize = ImGui.GetItemRectSize();
        var inline = imageSize.X + 24 * scale + brandSize.X + spacing + controlsWidth <= width;
        ImGui.SetCursorScreenPos(origin + (inline
            ? new Vector2(width - controlsWidth, (MogtomePresentation.Compact ? 14 : 16) * scale)
            : new Vector2(0, Math.Max(imageSize.Y, brandSize.Y) + ImGui.GetStyle().ItemSpacing.Y)));
        ImGui.BeginGroup();
        if (plugin.Configuration.UiCompactVisibleOnMainWindow)
        {
            plugin.Appearance.DrawCompact();
        }
        if (plugin.Configuration.UiTransparencyVisibleOnMainWindow)
        {
            if (plugin.Configuration.UiCompactVisibleOnMainWindow) UiLayout.SameLineIfFits(transparencyWidth);
            plugin.Appearance.DrawTransparencyToggle();
        }
        if (plugin.Configuration.UiCompactVisibleOnMainWindow || plugin.Configuration.UiTransparencyVisibleOnMainWindow)
            UiLayout.SameLineIfFits(linkWidth);
        if (UiLayout.IconButton(Ui.L("Main_KoFi"), MaterialIcon.Heart, new Vector2(0, (MogtomePresentation.Compact ? 36 : 42) * scale), koFi)) Process.Start(new ProcessStartInfo { FileName = "https://ko-fi.com/mcvaxius", UseShellExecute = true });
        if (ImGui.IsItemHovered()) UiLayout.SetTooltip(Ui.T("Main_SupportDevelopmentOnKoFi"));
        UiLayout.SameLineIfFits(UiLayout.IconButtonWidth("Discord"));
        if (UiLayout.IconButton("Discord", MaterialIcon.Chat, new Vector2(0, (MogtomePresentation.Compact ? 36 : 42) * scale))) Process.Start(new ProcessStartInfo { FileName = Plugin.DiscordUrl, UseShellExecute = true });
        if (ImGui.IsItemHovered()) UiLayout.SetTooltip(Plugin.DiscordChannelHint);
        if (plugin.Configuration.UiLanguageVisibleOnMainWindow)
        {
            UiLayout.SameLineIfFits(plugin.Appearance.LanguageWidth);
            plugin.Appearance.DrawLanguage();
        }
        ImGui.EndGroup();
        var controlsBottom = ImGui.GetItemRectMax().Y - origin.Y;
        var subtitle = Ui.T("Main_Subtitle");
        var subtitleY = brandSize.Y + ImGui.GetStyle().ItemSpacing.Y;
        if (!inline || imageSize.X + 24 * scale + AethertekUI.MaterialText.Measure(subtitle).X + spacing + controlsWidth > width)
            subtitleY = Math.Max(subtitleY, controlsBottom + ImGui.GetStyle().ItemSpacing.Y);
        ImGui.SetCursorScreenPos(origin + new Vector2(imageSize.X + 24 * scale, subtitleY));
        UiLayout.SingleLine(subtitle);
        var headerHeight = Math.Max(imageSize.Y, Math.Max(controlsBottom, ImGui.GetItemRectMax().Y - origin.Y));
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, headerHeight));
        if (plugin.CanSelectUiLanguage)
        {
            var character = plugin.ConfigManager.GetCurrentCharacterConfig();
            var name = plugin.Configuration.KrangleNames ? KrangleService.KrangleName(character.CharacterName) : character.CharacterName;
            UiLayout.TextDisabled(Ui.T("Main_CurrentProfile", string.IsNullOrEmpty(character.WorldName) ? name : name + " @ " + character.WorldName));
        }
        else UiLayout.TextDisabled(Ui.T("Language_WaitForProfile"));
        ImGui.Spacing();
    }

    private void DrawActions()
    {
        using var font = plugin.Appearance.Font(MogtomeFontRole.Strong);
        var engine = plugin.Engine;
        var scale = ImGuiHelpers.GlobalScale;
        var totalWidth = ImGui.GetContentRegionAvail().X;
        var rowWidth = Math.Max(1, totalWidth - ImGui.GetStyle().ItemSpacing.X * (MogtomePresentation.Compact ? 5 : 8));
        var secondRow = false;
        float ActionWidth(float weight, bool lowerRow)
        {
            var width = MogtomePresentation.Compact ? rowWidth / 6 : rowWidth * weight / 1076;
            if (MogtomePresentation.Compact && lowerRow && weight != 98) width = width * 2 + ImGui.GetStyle().ItemSpacing.X;
            if (totalWidth < 900 * scale) width = Math.Min(totalWidth, Math.Max(width, 140 * scale));
            return width;
        }
        var stopNextLabel = engine?.StopAfterNextSuccessfulRunArmed == true ? Ui.T("Main_CancelStopAfterNext") : Ui.T("Main_StopAfterNextSuccess");
        var actionHeight = MaterialControlMetrics.Measure(MaterialTheme.Metrics, ImGui.GetTextLineHeight(), MaterialControlContext.Toolbar).Height;
        foreach (var (label, weight, lowerRow) in new[]
        {
            (Ui.T("Main_Start"), 132f, false), (Ui.T("Main_Stop"), 123f, false),
            (Ui.T("Main_Config"), 100f, false), (Ui.T("Main_Reset"), 97f, false),
            (Ui.T("Shop_Button"), 100f, false),
            (Ui.T("Main_Stats"), 92f, false), (Ui.T("Main_UnKrangle"), 103f, false),
            (Ui.T("Main_Krangle"), 103f, false), (stopNextLabel, 205f, true),
            (Ui.T("Main_HelpTitle"), 98f, true), (Ui.T("Config_RefreshPartyState"), 126f, true),
        }) actionHeight = UiLayout.IconButtonHeight(label, ActionWidth(weight, lowerRow), actionHeight);
        bool Action(string raw, MaterialIcon icon, float weight, bool first = false, string? display = null)
        {
            var width = Math.Max(ActionWidth(weight, secondRow), UiLayout.IconButtonWidth(display ?? raw));
            if (!first) UiLayout.SameLineIfFits(width);
            return UiLayout.IconButton(raw, icon, new Vector2(width, actionHeight), display);
        }
        if (engine != null)
        {
            ImGui.BeginDisabled(engine.IsRunning || plugin.IsEngineStartQueued || plugin.MoogleShop.IsRunning || plugin.IsMoogleShopActionQueued || plugin.Blunderville.IsRunning || plugin.IsBlundervilleActionQueued);
            using (var startStyle = new MaterialStyleScope())
            {
                var colors = MaterialTheme.Current.Colors;
                startStyle.Color(ImGuiCol.Button, colors.Primary);
                startStyle.Color(ImGuiCol.ButtonHovered, MaterialColor.Layer(colors.Primary, colors.OnPrimary, .08f));
                startStyle.Color(ImGuiCol.ButtonActive, MaterialColor.Layer(colors.Primary, colors.OnPrimary, .14f));
                startStyle.Color(ImGuiCol.Text, colors.OnPrimary);
                if (Action(Ui.L("Main_Start"), MaterialIcon.Play, 132, true)) StartFromMain();
            }
            ImGui.EndDisabled();
            ImGui.BeginDisabled(!engine.IsRunning && !plugin.IsEngineStartQueued && !plugin.MoogleShop.IsRunning && !plugin.IsMoogleShopActionQueued && !plugin.Blunderville.IsRunning && !plugin.IsBlundervilleActionQueued);
            if (Action(Ui.L("Main_Stop"), MaterialIcon.Stop, 123)) StopFromMain();
            ImGui.EndDisabled();
        }
        if (Action(Ui.L("Main_Config"), MaterialIcon.Settings, 100, engine == null)) plugin.ConfigWindow.Toggle();
        if (Action(Ui.L("Shop_Button"), MaterialIcon.Cart, 100)) plugin.OpenMoogleShop();
        if (Action(Ui.L("Main_Reset"), MaterialIcon.Refresh, 97))
        {
            plugin.State.DutyCounter = 0; plugin.State.DecumanaCounter = 0; plugin.Configuration.DutyCounter = 0;
            plugin.ConfigManager.SaveCurrentAccount();
        }
        if (Action(Ui.L("Main_Stats"), MaterialIcon.Chart, 92)) plugin.StatsWindow.Toggle();
        if (engine == null) return;
        var krangleEnabled = plugin.Configuration.KrangleNames;
        if (Action((krangleEnabled ? Ui.T("Main_UnKrangle") : Ui.T("Main_Krangle")) + "###Krangle", MaterialIcon.Person, 103))
        { plugin.Configuration.KrangleNames = !krangleEnabled; plugin.ConfigManager.SaveCurrentAccount(); KrangleService.ClearCache(); }
        if (ImGui.IsItemHovered()) UiLayout.SetTooltip(Ui.T("Main_ObfuscateNamesWithMilitaryExerciseWordsUseful"));
        if (MogtomePresentation.Compact) secondRow = true;
        if (Action(stopNextLabel + "###StopNext", MaterialIcon.Clock, 205, MogtomePresentation.Compact)) engine.ToggleStopAfterNextSuccessfulRun();
        if (ImGui.IsItemHovered()) UiLayout.SetTooltip(engine.StopAfterNextSuccessfulRunArmed ? Ui.T("Main_ArmedStopsAfterASuccessfulRunIs") : Ui.T("Main_RuntimeOnlyAbortedRunsDoNotConsume"));
        if (Action(Ui.L("Main_HELP"), MaterialIcon.Info, 98, display: Ui.T("Main_HelpTitle"))) plugin.WarningTextWindow.Show(force: true);
        if (Action(Ui.L("Config_RefreshPartyState"), MaterialIcon.Refresh, 126)) engine.RefreshPartyLeaderState();
        if (ImGui.IsItemHovered()) UiLayout.SetTooltip(Ui.T("Config_OneTimeSameWorldLeaderDetectionUse"));
    }

    private void Heading(string key)
    {
        var origin = ImGui.GetCursorScreenPos();
        var icon = key == "Main_PartySummary" ? MaterialIcon.Group
            : key == "Main_MainWindowSettings" ? MaterialIcon.Settings
            : key == "Main_DutyInformation" ? MaterialIcon.Document : MaterialIcon.None;
        if (icon != MaterialIcon.None)
        {
            var iconSize = key == "Main_PartySummary" ? 30 : 24;
            MaterialIcons.Draw(icon, origin, iconSize * ImGuiHelpers.GlobalScale, MaterialTheme.Current.Colors.OnSurface);
            ImGui.Dummy(new Vector2(key == "Main_DutyInformation" ? 36 : 42, iconSize) * ImGuiHelpers.GlobalScale);
            ImGui.SameLine();
        }
        using var heading = plugin.Appearance.Font(MogtomeFontRole.CompactHeading);
        AethertekUI.MaterialText.Text(Ui.T(key));
    }

    private static void DrawCard(Action draw)
    {
        var window = ImGuiP.GetCurrentWindow();
        var previous = window.WorkRect;
        var left = ImGui.GetCursorScreenPos().X;
        var right = left + UiLayout.AvailableWidth;
        window.WorkRect.Min.X = left;
        window.WorkRect.Max.X = right;
        try { draw(); }
        finally { window.WorkRect = previous; }
    }

    private static void CardSeparator()
    {
        var window = ImGuiP.GetCurrentWindow();
        var left = ImGui.GetCursorScreenPos().X;
        var list = ImGui.GetWindowDrawList();
        list.PushClipRect(new Vector2(left, window.ClipRect.Min.Y),
            new Vector2(left + UiLayout.AvailableWidth, window.ClipRect.Max.Y), true);
        try { ImGui.Separator(); }
        finally { list.PopClipRect(); }
    }

    private void DrawDuty()
    {
        var config = plugin.Configuration;
        var state = plugin.State;
        Heading("Main_DutyInformation");
        CardSeparator();
        Field(Ui.T("Main_DutyCurrent"), (state.DutyCounter < config.PraetoriumThreshold ? Ui.Duty(1044) : Ui.Duty(1048)).Render());
        Field(Ui.T("Main_DutyCounter"), Ui.T("Main_CounterValue", state.DutyCounter, config.PraetoriumThreshold));
        Field(Ui.T("Main_DailyDecuHeading"), state.DecumanaCounter.ToString(Ui.Culture));
        var (countdown, localTime) = plugin.DutyTrackerService.GetResetTimeDisplay();
        Field(Ui.T("Main_DailyResetHeading"), state.NextResetTime.HasValue ? Ui.T("Main_ResetUtc", state.NextResetTime.Value) : Ui.T("Time_Unknown"));
        if (MaterialText.CollapsingHeader(Ui.L("Main_DutyDetails"), ImGuiTreeNodeFlags.NoTreePushOnOpen))
        {
            UiLayout.Wrapped(Ui.T("Main_CounterPraeDailyPraetoriumLimit", state.DutyCounter, config.PraetoriumThreshold, config.MaxRuns));
            UiLayout.Wrapped(Ui.T("Main_DailyReset", countdown, localTime));
        }
        if (config.TestingModeUnsynced) AethertekUI.MaterialText.TextColored(new Vector4(1, 1, 0, 1), Ui.T("Main_TESTINGMODEUnsynced"));
        if (state.LastCompletionDuration > 0) UiLayout.Wrapped(Ui.T("Main_LastClearS", state.LastCompletionDuration));
        if (state.IsInDuty) UiLayout.Wrapped(Ui.T("Main_TimeInDutyS", state.TimeInDuty));
        if (plugin.DeathTrackingService.IsActive)
        {
            var deaths = plugin.DeathTrackingService.CurrentSnapshot;
            UiLayout.Wrapped(Ui.T("Main_DeathsThisRunSelfOthersAll", deaths.SelfDeathCount, deaths.OtherDeathCount, deaths.TotalDeathCount));
        }
    }

    private void DrawTeamLeader()
    {
        var config = plugin.Configuration;
        var state = plugin.State;
        var isLeader = config.IsPartyLeader;
        if (UiLayout.Checkbox(Ui.L("Main_TeamLeader"), ref isLeader))
        {
            config.IsPartyLeader = isLeader;
            state.IsPartyLeader = isLeader;
            plugin.Engine?.ApplyConfiguredPartyLeaderState(reason: "main window party role changed");
            plugin.ConfigManager.SaveCurrentAccount();
        }
        if (ImGui.IsItemHovered())
            UiLayout.SetTooltip(Ui.T("Config_RuntimeRoleFollowsThisSavedSettingUse"));
    }

    private void DrawParty()
    {
        var config = plugin.Configuration;
        var state = plugin.State;
        using var rows = new MaterialStyleScope();
        rows.Style(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, (MogtomePresentation.Compact ? 6 : 6.5f) * ImGuiHelpers.GlobalScale));
        Heading("Main_PartySummary");
        CardSeparator();
        if (Plugin.PartyList.Length == 0) UiLayout.Wrapped(Ui.T("Main_NoParty"));
        var memberIndex = 0;
        foreach (var member in Plugin.PartyList)
        {
            var name = member.Name.ToString();
            var jobId = member.ClassJob.RowId;
            var role = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(jobId)?.Role;
            var roleName = role switch { 1 => Ui.T("Role_Tank"), 4 => Ui.T("Role_Healer"), 2 or 3 => Ui.T("Role_Dps"), _ => "" };
            var display = config.KrangleNames ? KrangleService.KrangleName(name) : name;
            var detail = jobId == 0 ? "" : Ui.Job(jobId).Render();
            if (roleName.Length > 0) detail += " · " + roleName;
            DrawPartyRow(++memberIndex, display, detail, role);
        }
        if (MaterialText.CollapsingHeader(Ui.L("Main_PartyDetails"), ImGuiTreeNodeFlags.NoTreePushOnOpen))
        {
            UiLayout.Wrapped(Ui.T("Main_LeaderRuntime", state.IsPartyLeader ? Ui.T("Config_Yes") : Ui.T("Config_No")));
            UiLayout.Wrapped(Ui.T("Main_CrossWorld", config.IsCrossWorldParty ? Ui.T("Config_Yes") : Ui.T("Config_No")));
            UiLayout.TextDisabled(config.IsCrossWorldParty ? Ui.T("Main_SourceConfiguredCrossWorldRole") : Ui.T("Config_SourceConfiguredRoleRefreshPartyStateOnly"));
            DrawStatusLine(Ui.T("Config_Food"), config.FoodItemId > 0 && state.FoodAvailable, FormatConsumableLabel(config.FoodItemId > 0 ? Ui.Item((uint)config.FoodItemId).Render() : string.Empty, config.FoodUseHighQuality, state.FoodExactCount));
            DrawStatusLine(Ui.T("Config_Potions"), config.PotionItemId > 0 && state.PotionsAvailable, FormatConsumableLabel(config.PotionItemId > 0 ? Ui.Item((uint)config.PotionItemId).Render() : string.Empty, config.PotionUseHighQuality, state.PotionExactCount));
            DrawStatusLine("YesAlready", plugin.YesAlreadyIPC.IsPaused, Ui.T("Main_PausedByMOGTOME"));
            DrawStatusLine(Ui.T("Main_DutyBackend"), plugin.DutyAutomationService.GetSubsystemHealthy(), plugin.DutyAutomationService.GetSubsystemStatusText().Render());
            DrawStatusLine(Ui.T("Main_QueueRole"), true, plugin.DutyAutomationService.GetQueueStatusText().Render());
            if (config.UseAdsExperimental) DrawStatusLine(Ui.T("Main_ADSRuntime"), true, plugin.DutyAutomationService.GetAdsRuntimeStatusText().Render());
            DrawStatusLine(Ui.T("Main_Bailout"), true, Ui.T("Main_S", config.BailoutTimeout));
        }
    }

    private void DrawPartyRow(int index, string name, string detail, byte? role)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        var width = UiLayout.AvailableWidth;
        var colors = MaterialTheme.Current.Colors;
        var list = ImGui.GetWindowDrawList();
        var badge = (MogtomePresentation.Compact ? 28 : 32) * scale;
        var gap = 8 * scale;
        var roleWidth = Math.Max(70 * scale, AethertekUI.MaterialText.Measure(detail).X);
        var status = Ui.T("Main_InParty");
        var statusWidth = AethertekUI.MaterialText.Measure(status).X + 24 * scale;
        var nameWidth = width - badge * 2 - gap * 4 - roleWidth - statusWidth;
        var horizontal = nameWidth >= Math.Max(90 * scale, AethertekUI.MaterialText.Measure(name).X);
        var roleColor = role switch
        {
            1 => new Vector4(.25f, .48f, .94f, 1),
            4 => new Vector4(.40f, .70f, .25f, 1),
            2 or 3 => new Vector4(.80f, .27f, .28f, 1),
            _ => colors.OnSurfaceVariant,
        };
        list.AddRectFilled(origin, origin + new Vector2(badge), MaterialCanvas.Color(colors.SurfaceContainerHigh), 3 * scale);
        list.AddRect(origin, origin + new Vector2(badge), MaterialCanvas.Color(colors.OutlineVariant), 3 * scale);
        var number = index.ToString(Ui.Culture);
        MaterialText.AddText(list, origin + (new Vector2(badge) - MaterialText.Measure(number)) * .5f, MaterialCanvas.Color(colors.OnSurface), number);
        var roleOrigin = origin + new Vector2(badge + gap, 0);
        list.AddRectFilled(roleOrigin, roleOrigin + new Vector2(badge), MaterialCanvas.Color(roleColor), 3 * scale);
        if (role == 1)
            MaterialIcons.Draw(MaterialIcon.Shield, roleOrigin + new Vector2(4 * scale), badge - 8 * scale, Vector4.One);
        else if (role == 4)
        {
            list.AddLine(roleOrigin + new Vector2(badge * .5f, badge * .2f), roleOrigin + new Vector2(badge * .5f, badge * .8f), MaterialCanvas.Color(Vector4.One), 5 * scale);
            list.AddLine(roleOrigin + new Vector2(badge * .2f, badge * .5f), roleOrigin + new Vector2(badge * .8f, badge * .5f), MaterialCanvas.Color(Vector4.One), 5 * scale);
        }
        else if (role is 2 or 3)
        {
            list.AddLine(roleOrigin + new Vector2(badge * .2f, badge * .2f), roleOrigin + new Vector2(badge * .8f, badge * .8f), MaterialCanvas.Color(Vector4.One), 3 * scale);
            list.AddLine(roleOrigin + new Vector2(badge * .2f, badge * .8f), roleOrigin + new Vector2(badge * .8f, badge * .2f), MaterialCanvas.Color(Vector4.One), 3 * scale);
        }
        else MaterialIcons.Draw(MaterialIcon.Person, roleOrigin + new Vector2(4 * scale), badge - 8 * scale, Vector4.One);
        var textOrigin = origin + new Vector2(badge * 2 + gap * 2, Math.Max(0, (badge - ImGui.GetTextLineHeight()) * .5f));
        ImGui.SetCursorScreenPos(textOrigin);
        ImGui.BeginGroup();
        UiLayout.SingleLine(name);
        ImGui.EndGroup();
        var height = Math.Max(badge, ImGui.GetItemRectMax().Y - origin.Y);
        if (horizontal) ImGui.SetCursorScreenPos(textOrigin + new Vector2(nameWidth + gap, 0));
        else ImGui.SetCursorScreenPos(origin + new Vector2(badge * 2 + gap * 2, height + ImGui.GetStyle().ItemSpacing.Y));
        ImGui.BeginGroup();
        UiLayout.SingleLine(detail);
        ImGui.EndGroup();
        height = Math.Max(height, ImGui.GetItemRectMax().Y - origin.Y);
        if (horizontal) ImGui.SetCursorScreenPos(origin + new Vector2(width - statusWidth, textOrigin.Y - origin.Y));
        else ImGui.SetCursorScreenPos(origin + new Vector2(badge * 2 + gap * 2, height + ImGui.GetStyle().ItemSpacing.Y));
        var statusOrigin = ImGui.GetCursorScreenPos();
        list.AddCircleFilled(statusOrigin + new Vector2(7 * scale, ImGui.GetTextLineHeight() * .5f), 6 * scale, MaterialCanvas.Color(new(.35f, .90f, .50f, 1)), 20);
        ImGui.SetCursorScreenPos(statusOrigin + new Vector2(24 * scale, 0));
        UiLayout.SingleLine(status);
        height = Math.Max(height, ImGui.GetItemRectMax().Y - origin.Y);
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));
        CardSeparator();
    }

    private void Field(string label, string value)
    {
        var width = UiLayout.AvailableWidth;
        var origin = ImGui.GetCursorScreenPos();
        var inset = (MogtomePresentation.Compact ? 16 : 12) * ImGuiHelpers.GlobalScale;
        var labelWidth = Math.Max(width * .38f, AethertekUI.MaterialText.Measure(label).X + inset);
        var inline = labelWidth + ImGui.GetStyle().ItemSpacing.X + AethertekUI.MaterialText.Measure(value).X <= width;
        ImGui.SetCursorScreenPos(origin + new Vector2(inset, 0));
        ImGui.BeginGroup();
        UiLayout.SingleLine(label);
        ImGui.EndGroup();
        var labelHeight = ImGui.GetItemRectSize().Y;
        ImGui.SetCursorScreenPos(origin + (inline ? new Vector2(labelWidth + ImGui.GetStyle().ItemSpacing.X, 0) : new Vector2(0, labelHeight + ImGui.GetStyle().ItemSpacing.Y)));
        ImGui.BeginGroup();
        using (plugin.Appearance.Font(MogtomeFontRole.Strong)) UiLayout.SingleLine(value);
        ImGui.EndGroup();
        var height = ImGui.GetItemRectMax().Y - origin.Y;
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, Math.Max(height, (MogtomePresentation.Compact ? 25 : 21) * ImGuiHelpers.GlobalScale)));
        CardSeparator();
    }

    private void DrawDebug()
    {
        var config = plugin.Configuration;
        // Debug Section (only visible when debug mode is enabled via /mog debug)
        if (config.DebugModeEnabled && !config.UseAdsExperimental)
        {
            AethertekUI.MaterialText.TextColored(new Vector4(1.0f, 0.5f, 0.0f, 1.0f), Ui.T("Main_DebugTools"));
            ImGui.Indent();

            // Force Path Selection button
            if (UiLayout.Button(Ui.L("Main_FORCEPATHSELECTION"), new Vector2(200, 25)))
            {
                plugin.AutoDutyPathService.ForcePathSelection(config.PraetoriumPathFileName);
            }
            if (ImGui.IsItemHovered())
            {
                var selectedPath = plugin.AutoDutyPathService.GetPraetoriumPathDisplayName(config.PraetoriumPathFileName);
                UiLayout.SetTooltip(Ui.T("Main_ForceAutoDutyToSelectModeLoopingDuty", selectedPath));
            }
            UiLayout.TextDisabled(Ui.T("Main_Result", plugin.AutoDutyPathService.ForceResult));

            // Test Path Index Discovery button
            if (UiLayout.Button(Ui.L("Main_TESTPATHINDEX"), new Vector2(200, 25)))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    var selectedPathFileName = plugin.AutoDutyPathService.ResolvePraetoriumPathFileName(config.PraetoriumPathFileName);
                    var selectedPathName = Path.GetFileNameWithoutExtension(selectedPathFileName) ?? selectedPathFileName;

                    // Test both methods and compare results
                    var correctIndex = plugin.AutoDutyPathService.FindPathIndexFromDictionaryPaths(autoDutyPlugin, selectedPathName);
                    var fallbackIndex = plugin.AutoDutyPathService.FindPathIndexByName(autoDutyPlugin, selectedPathName);
                    
                    Plugin.Log.Information($"[TEST] Selected path target: {selectedPathFileName}");
                    Plugin.Log.Information($"[TEST] DictionaryPaths method result: {correctIndex}");
                    Plugin.Log.Information($"[TEST] PathSelectionsByPath method result: {fallbackIndex}");
                    
                    if (correctIndex != fallbackIndex)
                    {
                        Plugin.Log.Information($"[TEST] *** MISMATCH DETECTED *** DictionaryPaths={correctIndex}, PathSelectionsByPath={fallbackIndex}");
                    }
                    else if (correctIndex >= 0)
                    {
                        Plugin.Log.Information($"[TEST] Both methods agree: Index {correctIndex}");
                    }
                    else
                    {
                        Plugin.Log.Information($"[TEST] Both methods failed to find path");
                    }
                }
                else
                {
                    Plugin.Log.Error("[TEST] Could not find AutoDuty plugin");
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_TestPathIndexDiscoveryMethodsComparesDictionaryPaths"));
            }

            // First row: Core research buttons
            if (UiLayout.SmallButton(Ui.L("Main_LogADStructure")))
            {
                plugin.AutoDutyPathService.LogAutoDutyStructure();
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_DumpAutoDutyPluginStructureToDalamudLog"));
            }

            UiLayout.SameLineIfFits(AethertekUI.MaterialText.Measure(Ui.T("Main_LogConfig")).X + ImGui.GetStyle().FramePadding.X * 2);
            if (UiLayout.SmallButton(Ui.L("Main_LogConfig")))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    var configObj = AutoDutyPathService.GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "Configuration");
                    plugin.AutoDutyPathService.LogAutoDutyStructure(autoDutyPlugin, configObj);
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_DumpAutoDutyConfigurationStructureToDalamudLog"));
            }

            UiLayout.SameLineIfFits(AethertekUI.MaterialText.Measure(Ui.T("Main_LogActions")).X + ImGui.GetStyle().FramePadding.X * 2);
            if (UiLayout.SmallButton(Ui.L("Main_LogActions")))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    var actions = AutoDutyPathService.GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "Actions");
                    plugin.AutoDutyPathService.LogAutoDutyStructure(autoDutyPlugin, actions);
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_DumpAutoDutyActionsListCurrentlyEmptyBut"));
            }

            // Second row: Manager and exploration buttons
            if (UiLayout.SmallButton(Ui.L("Main_LogManager")))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    var actionsManager = AutoDutyPathService.GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "actions");
                    plugin.AutoDutyPathService.LogAutoDutyStructure(autoDutyPlugin, actionsManager);
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_DumpAutoDutyActionsManagerContainsActionsListWithCommand"));
            }

            UiLayout.SameLineIfFits(AethertekUI.MaterialText.Measure(Ui.T("Main_ExplorePaths")).X + ImGui.GetStyle().FramePadding.X * 2);
            if (UiLayout.SmallButton(Ui.L("Main_ExplorePaths")))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    // Try different approaches to find path data
                    plugin.AutoDutyPathService.ExplorePathData(autoDutyPlugin);
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_ExploreDifferentLocationsForPathDataConfiguration"));
            }

            UiLayout.SameLineIfFits(AethertekUI.MaterialText.Measure(Ui.T("Main_LogTuples")).X + ImGui.GetStyle().FramePadding.X * 2);
            if (UiLayout.SmallButton(Ui.L("Main_LogTuples")))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    var actionsManager = AutoDutyPathService.GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "actions");
                    var actionsList = actionsManager == null
                        ? null
                        : AutoDutyPathService.GetMemberValue(actionsManager.GetType(), actionsManager, "actionsList");
                    plugin.AutoDutyPathService.LogActionsListTuples(actionsList);
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_IterateThroughActionsListAndLogEachTuple"));
            }

            // Third row: Path data exploration
            if (UiLayout.SmallButton(Ui.L("Main_PathSelections")))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    var configObj = AutoDutyPathService.GetMemberValue(autoDutyPlugin.GetType(), autoDutyPlugin, "Configuration");
                    var pathSelections = configObj == null
                        ? null
                        : AutoDutyPathService.GetMemberValue(configObj.GetType(), configObj, "PathSelectionsByPath");
                    plugin.AutoDutyPathService.LogPathSelections(pathSelections, plugin.Configuration.PraetoriumPathFileName);
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_ExplorePathSelectionsByPathDictionaryTerritoryPathMappings"));
            }

            UiLayout.SameLineIfFits(AethertekUI.MaterialText.Measure(Ui.T("Main_CheckCurrent")).X + ImGui.GetStyle().FramePadding.X * 2);
            if (UiLayout.SmallButton(Ui.L("Main_CheckCurrent")))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    plugin.AutoDutyPathService.LogCurrentSelection(autoDutyPlugin);
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_CheckWhatPathAutoDutyCurrentlyHasSelected"));
            }

            UiLayout.SameLineIfFits(AethertekUI.MaterialText.Measure(Ui.T("Main_FindMethods")).X + ImGui.GetStyle().FramePadding.X * 2);
            if (UiLayout.SmallButton(Ui.L("Main_FindMethods")))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    plugin.AutoDutyPathService.LogAutoDutyMethods(autoDutyPlugin);
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_FindSaveApplyLoadMethodsInAutoDuty"));
            }

            UiLayout.SameLineIfFits(AethertekUI.MaterialText.Measure(Ui.T("Main_ConfigFields")).X + ImGui.GetStyle().FramePadding.X * 2);
            if (UiLayout.SmallButton(Ui.L("Main_ConfigFields")))
            {
                var autoDutyPlugin = plugin.AutoDutyPathService.FindDalamudPluginInstance("AutoDuty");
                if (autoDutyPlugin != null)
                {
                    plugin.AutoDutyPathService.LogConfigFields(autoDutyPlugin, plugin.Configuration.PraetoriumPathFileName);
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_FindActualConfigFieldNamesForPath"));
            }

            UiLayout.SameLineIfFits(AethertekUI.MaterialText.Measure(Ui.T("Main_CheckRepair")).X + ImGui.GetStyle().FramePadding.X * 2);
            if (UiLayout.SmallButton(Ui.L("Main_CheckRepair")))
            {
                var needsRepair = plugin.RepairService.NeedsRepair();
                var threshold = plugin.ConfigManager.GetActiveConfig().RepairThreshold;
                Plugin.Log.Information($"[DEBUG] Repair Check - Threshold: {threshold}%, Needs Repair: {needsRepair}");
                
                // Check actual equipment durability for detailed info
                try
                {
                    unsafe
                    {
                        var im = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                        if (im != null)
                        {
                            var equippedContainer = im->GetInventoryContainer(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.EquippedItems);
                            if (equippedContainer != null)
                            {
                                var allItems = new List<string>();
                                var lowItems = new List<string>();
                                
                                for (var i = 0; i < equippedContainer->Size; i++)
                                {
                                    var item = equippedContainer->GetInventorySlot(i);
                                    if (item == null || item->ItemId == 0) continue;

                                    var itemName = Ui.T("Main_Item", item->ItemId);
                                    var actualCondition = item->Condition / 300; // Convert from 0-30000 to 0-100%
                                    allItems.Add(string.Create(Ui.Culture, $"{itemName}: {actualCondition}%"));
                                    
                                    if (actualCondition < threshold)
                                    {
                                        lowItems.Add(string.Create(Ui.Culture, $"{itemName}: {actualCondition}%"));
                                    }
                                }
                                
                                Plugin.Log.Information($"[DEBUG] Equipment durability check - Total items: {allItems.Count}");
                                Plugin.Log.Information($"[DEBUG] All items: {string.Join(", ", allItems)}");
                                
                                if (lowItems.Count > 0)
                                {
                                    Plugin.Log.Information($"[DEBUG] Items below {threshold}%: {string.Join(", ", lowItems)}");
                                }
                                else
                                {
                                    Plugin.Log.Information($"[DEBUG] All equipment above {threshold}% durability");
                                }
                            }
                            else
                            {
                                Plugin.Log.Error("[DEBUG] Failed to get equipped container");
                            }
                        }
                        else
                        {
                            Plugin.Log.Error("[DEBUG] Failed to get InventoryManager instance");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"[DEBUG] Equipment check failed: {ex.Message}");
                }
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_CheckEquipmentDurabilityAndRepairStatus"));
            }

            ImGui.Spacing();

            // Unsynced testing mode checkbox (only visible in debug)
            var testMode = config.TestingModeUnsynced;
            if (UiLayout.Checkbox(Ui.L("Main_TestingModeUnsyncedNoStats"), ref testMode))
            {
                config.TestingModeUnsynced = testMode;
                plugin.ConfigManager.SaveCurrentAccount();
            }
            if (testMode)
            {
                AethertekUI.MaterialText.TextColored(new Vector4(1, 1, 0, 1), Ui.T("Main_WARNINGRunningUnsyncedWithoutLevelSync"));
            }

            ImGui.Unindent();
            ImGui.Separator();
        }
        else if (config.DebugModeEnabled && config.UseAdsExperimental)
        {
            var praeSelection = plugin.DutyAutomationService.GetPraetoriumSelectionInfo();

            AethertekUI.MaterialText.TextColored(new Vector4(1.0f, 0.5f, 0.0f, 1.0f), Ui.T("Main_DebugTools"));
            ImGui.Indent();

            if (UiLayout.Button(Ui.L("Main_TESTPRAEINDEX"), new Vector2(200, 25)))
            {
                plugin.DutyAutomationService.LogPraetoriumSelectionInfo();
            }
            if (ImGui.IsItemHovered())
            {
                UiLayout.SetTooltip(Ui.T("Main_LogsPraetoriumCallbackIndexAndMissingOptional"));
            }

            AethertekUI.MaterialText.Text(Ui.T("Main_PraetoriumCallback", praeSelection.CallbackCommand));
            AethertekUI.MaterialText.Text(Ui.T("Main_MissingOptionalUnlocks", praeSelection.MissingUnlockCount));

            foreach (var unlock in praeSelection.Unlocks)
            {
                var color = unlock.IsUnlocked
                    ? new Vector4(0.3f, 1.0f, 0.3f, 1.0f)
                    : new Vector4(1.0f, 0.45f, 0.45f, 1.0f);
                var status = unlock.IsUnlocked ? Ui.T("Main_Unlocked") : Ui.T("Main_Missing");
                AethertekUI.MaterialText.TextColored(color, string.Create(Ui.Culture, $"{Ui.Duty(unlock.TerritoryTypeId).Render()}: {status}"));
                if (ImGui.IsItemHovered())
                {
                    UiLayout.SetTooltip(Ui.T("Main_QuestIDs", unlock.QuestSummary));
                }
            }

            UiLayout.TextDisabled(Ui.T("Main_ADSModeActiveAutoDutyReflectionPathDebug"));
            ImGui.Unindent();
            ImGui.Separator();
        }

        // Debug section (only visible when debug mode is enabled)
        if (config.DebugModeEnabled)
        {
            AethertekUI.MaterialText.Text(Ui.T("Main_DebugTools"));
            ImGui.Indent();
            
            if (UiLayout.Button(Ui.L("Main_LogAllConfiguration")))
            {
                LogAllConfiguration(plugin);
            }
            
            ImGui.SameLine();
            if (UiLayout.Button(Ui.L("Main_LogConfigPath")))
            {
                Plugin.Log.Information($"[Config] Current account ID: {plugin.ConfigManager.CurrentAccountId}");
                Plugin.Log.Information("[Config] Use Config folder button in Stats window to open config folder");
            }
            
            ImGui.Unindent();
            ImGui.Separator();
        }
    }

    private void ToggleAdsExperimental(bool enable)
    {
        plugin.Configuration.UseAdsExperimental = enable;
        plugin.ConfigManager.SaveCurrentAccount();
        plugin.ConfigManager.NotifyConfigurationChanged(force: true);

        if (!enable)
            return;

        _ = Task.Run(async () => await plugin.DutyAutomationService.EnsureAutoDutyDisabledForAdsAsync("ADS toggle"));
    }

    private void QueueWindowPosition(Vector2 position)
    {
        pendingWindowPosition = position;
    }

    private Vector2 GetRandomVisiblePosition()
    {
        var viewport = ImGuiHelpers.MainViewport;
        var currentSize = Size ?? Vector2.Zero;
        var minimumSize = SizeConstraints?.MinimumSize ?? Vector2.Zero;
        var width = MathF.Max(currentSize.X, minimumSize.X);
        var height = MathF.Max(currentSize.Y, minimumSize.Y);
        var maxX = MathF.Max(1f, viewport.Size.X - width - 20f);
        var maxY = MathF.Max(1f, viewport.Size.Y - height - 20f);
        return new Vector2(1f + (Random.Shared.NextSingle() * maxX), 1f + (Random.Shared.NextSingle() * maxY));
    }

    private void FinalizePendingWindowPlacement()
    {
        if (!pendingPositionConditionReset)
            return;

        pendingPositionConditionReset = false;
        Position = null;
        PositionCondition = ImGuiCond.None;
    }

    private static void DrawStatusLine(string label, bool active, string detail)
    {
        var color = active
            ? new Vector4(0.0f, 1.0f, 0.0f, 1.0f)
            : new Vector4(1.0f, 0.0f, 0.0f, 1.0f);
        
        AethertekUI.MaterialText.TextColored(color, string.Create(Ui.Culture, $"{label}:"));
        ImGui.SameLine();
        UiLayout.Wrapped(detail);
    }
    
    private static void LogAllConfiguration(Plugin plugin)
    {
        var config = plugin.Configuration;
        
        Plugin.Log.Information("=== MOGTOME COMPLETE CONFIGURATION DUMP ===");
        
        // Account & Character Info
        Plugin.Log.Information($"[Config] Account ID: {plugin.ConfigManager.CurrentAccountId}");
        
        // Food Settings
        Plugin.Log.Information($"[Config] Food Item ID: {config.FoodItemId}");
        Plugin.Log.Information($"[Config] Food Item Name: '{config.FoodItemName}'");
        Plugin.Log.Information($"[Config] Food HQ: {config.FoodUseHighQuality}");
        Plugin.Log.Information($"[Config] Food Available: {config.FoodItemId > 0}");
        
        // Potion Settings
        Plugin.Log.Information($"[Config] Potion Item ID: {config.PotionItemId}");
        Plugin.Log.Information($"[Config] Potion Item Name: '{config.PotionItemName}'");
        Plugin.Log.Information($"[Config] Potion HQ: {config.PotionUseHighQuality}");
        Plugin.Log.Information($"[Config] Potion Target: {config.PotionTarget}");
        
        // Engine Settings
        Plugin.Log.Information($"[Config] Debug Mode: {config.DebugModeEnabled}");
        Plugin.Log.Information($"[Config] Testing Mode Unsynced: {config.TestingModeUnsynced}");
        Plugin.Log.Information($"[Config] Bailout Timeout: {config.BailoutTimeout}s");
        Plugin.Log.Information($"[Config] Show Debug Runs: {config.ShowDebugRuns}");
        
        // Krangle Settings
        Plugin.Log.Information($"[Config] Krangle Names: {config.KrangleNames}");
        Plugin.Log.Information($"[Config] Stats Krangle Names: {config.StatsKrangleNames}");
        
        // Tracking Settings
        Plugin.Log.Information($"[Config] Enable Detailed Tracking: {config.EnableDetailedTracking}");
        
        Plugin.Log.Information("=== END CONFIGURATION DUMP ===");
    }

    private static string FormatConsumableLabel(string name, bool highQuality, int exactCount)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Ui.T("Main_NotConfigured");

        return Ui.T("Main_X", name, (highQuality ? Ui.T("Main_HQ") : Ui.T("Main_NQ")), Math.Max(0, exactCount));
    }
}
