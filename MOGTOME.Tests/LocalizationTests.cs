using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text.Evaluator;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Text.ReadOnly;
using MOGTOME.Localization;
using MOGTOME.Models;
using MOGTOME.Services;
using ActionType = FFXIVClientStructs.FFXIV.Client.Game.ActionType;

// Plugin services and UI selection are process-wide; restore them after each case.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MOGTOME.Tests;

public sealed class LocalizationTests : IDisposable
{
    private readonly UiLanguage originalLanguage = Ui.Language;
    private readonly Dictionary<PropertyInfo, object?> originalServices = [];
    // Captured from the installed game sheets, 2026-09-12. No game process is used.
    internal static readonly Dictionary<string, Dictionary<uint, string>> Templates =
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<uint, string>>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GameTextFixtures.json")))!;

    public static IEnumerable<object[]> Languages =>
        from client in Enum.GetValues<ClientLanguage>()
        from ui in Enum.GetValues<UiLanguage>()
        select new object[] { client, ui };

    [Theory]
    [MemberData(nameof(Languages))]
    public void ClientPromptsAndQueueErrorsStayIndependentOfUi(ClientLanguage client, UiLanguage ui)
    {
        Ui.SetLanguage(ui);
        var evaluator = new SheetEvaluator(client);
        SetService("ClientState", Fake<IClientState>((method, _) =>
            method.Name == "get_ClientLanguage" ? client : Default(method.ReturnType)));
        SetService("SeStringEvaluator", evaluator);
        foreach (var kind in Enum.GetValues<GamePrompt>())
        foreach (var row in GameText.Rows(kind))
        {
            var macro = Templates[client.ToString()][row];
            var expected = evaluator.Text(row);
            Assert.True(GameText.HasSupportedBindings(row, macro));
            var evaluated = GameText.EvaluateAddon(row, client, evaluator, macro, SheetEvaluator.Raiser);
            Assert.NotNull(evaluated);
            Assert.Equal(GameText.Normalize(expected), GameText.Normalize(evaluated));
            string? Evaluate(uint id, ClientLanguage language) => GameText.EvaluateAddon(
                id, language, evaluator, Templates[language.ToString()][id], SheetEvaluator.Raiser);
            Assert.True(GameText.MatchesPrompt(expected, kind, client, Evaluate));
            Assert.False(GameText.MatchesPrompt("Unrelated " + expected, kind, client, Evaluate));
            Assert.False(GameText.MatchesPrompt(expected + "?", kind, client, Evaluate));
            foreach (var other in Enum.GetValues<GamePrompt>().Where(other => other != kind))
                Assert.False(GameText.MatchesPrompt(expected, other, client, Evaluate));
            foreach (var otherClient in Enum.GetValues<ClientLanguage>().Where(other => other != client))
                Assert.False(GameText.MatchesPrompt(new SheetEvaluator(otherClient).Text(row), kind, client, Evaluate));
        }

        // Vote-abandon and unrelated prompts must never become leave evidence.
        foreach (var prompt in new[] { "Are you sure you wish to abandon the duty?", "Abandon duty?", "Accept this party invitation?" })
            Assert.False(GameText.MatchesPrompt(prompt, GamePrompt.LeaveDuty, client,
                (row, _) => evaluator.Text(row)));
        foreach (var row in new uint[] { 877, 880 })
        {
            Assert.True(GameText.MatchesLogMessage(evaluator.Text(row), row));
            Assert.True(GameText.MatchesLogMessage(evaluator.ChatText(row), row));
            Assert.False(GameText.MatchesLogMessage("Unrelated " + evaluator.Text(row), row));
            foreach (var otherClient in Enum.GetValues<ClientLanguage>().Where(other => other != client))
                Assert.False(GameText.MatchesLogMessage(new SheetEvaluator(otherClient).Text(row), row));
        }
        Assert.False(GameText.MatchesLogMessage(evaluator.Text(877), 881));
        Assert.Equal(ui, Ui.Language);
        Assert.Equal(Ui.Resources.GetString("Language_Label", Ui.CultureFor(ui)), Ui.T("Language_Label"));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void MissingAndUnknownPromptParametersFailClosed(ClientLanguage client, UiLanguage ui)
    {
        Ui.SetLanguage(ui);
        var evaluator = new SheetEvaluator(client);
        var rows = Templates[client.ToString()];
        Assert.Null(GameText.EvaluateAddon(3787, client, evaluator, rows[3787], null));
        Assert.Null(GameText.EvaluateAddon(3787, client, evaluator, rows[3787], " "));
        Assert.Null(GameText.EvaluateAddon(3787, client, evaluator, rows[3787].Replace("lstr1", "lstr2"), SheetEvaluator.Raiser));
        Assert.Null(GameText.EvaluateAddon(112, client, evaluator, rows[112] + "<string(lstr2)>", null));
        Assert.False(GameText.HasSupportedBindings(999, rows[112]));
        evaluator.Destination = "";
        foreach (var row in GameText.Rows(GamePrompt.Return))
            Assert.Null(GameText.EvaluateAddon(row, client, evaluator, rows[row], null));
        evaluator.Destination = "<string(gstr56)>";
        Assert.Null(GameText.EvaluateAddon(118, client, evaluator, rows[118], null));
        evaluator.DropBoundValue = true;
        Assert.Null(GameText.EvaluateAddon(3787, client, evaluator, rows[3787], SheetEvaluator.Raiser));
        evaluator.Throw = true;
        Assert.Null(GameText.EvaluateAddon(112, client, evaluator, rows[112], null));
    }

    [Fact]
    public void NormalizationIgnoresOnlyFormattingAndReturnDelayKeepsBoundary()
    {
        var binary = new Lumina.Text.SeStringBuilder().AppendMacroString("Accepter<nbsp>?").ToReadOnlySeString();
        Assert.Equal("Accepter ?", GameText.Normalize(GameText.ReadVisibleText(binary.Data.Span)));
        Assert.True(GameText.MatchesEvaluated(" \tCafé\u00a0\nretour\u00ad\u200b\ufeff ", "Cafe\u0301 retour"));
        Assert.False(GameText.MatchesEvaluated("Return?", "return?"));
        Assert.False(GameText.MatchesEvaluated("", ""));
        Assert.False(GameText.MatchesEvaluated("Unrelated", null));
        var config = JsonSerializer.Deserialize<Configuration>("{}")!;
        Assert.Equal(60, config.ReturnToEntranceDelaySeconds);
        Assert.False(DialogHandlerService.ReturnDelayElapsed(TimeSpan.FromSeconds(59.999), config.ReturnToEntranceDelaySeconds));
        Assert.True(DialogHandlerService.ReturnDelayElapsed(TimeSpan.FromSeconds(60), config.ReturnToEntranceDelaySeconds));
        Assert.True(DialogHandlerService.ReturnDelayElapsed(TimeSpan.FromSeconds(61), config.ReturnToEntranceDelaySeconds));

        config.ReturnToEntranceDelaySeconds = 120;
        Assert.False(DialogHandlerService.ReturnDelayElapsed(TimeSpan.FromSeconds(119.999), config.ReturnToEntranceDelaySeconds));
        Assert.True(DialogHandlerService.ReturnDelayElapsed(TimeSpan.FromSeconds(120), config.ReturnToEntranceDelaySeconds));
        Assert.True(DialogHandlerService.ReturnDelayElapsed(TimeSpan.FromSeconds(121), config.ReturnToEntranceDelaySeconds));

        foreach (var delay in new[] { 1, 0, -1 })
        {
            Assert.False(DialogHandlerService.ReturnDelayElapsed(TimeSpan.FromSeconds(0.999), delay));
            Assert.True(DialogHandlerService.ReturnDelayElapsed(TimeSpan.FromSeconds(1), delay));
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void QueueErrorsRespectOperationGateAndPartyAdvice(ClientLanguage client, UiLanguage ui)
    {
        Ui.SetLanguage(ui);
        var evaluator = new SheetEvaluator(client);
        SetService("ClientState", Fake<IClientState>((method, _) =>
            method.Name == "get_ClientLanguage" ? client : Default(method.ReturnType)));
        SetService("SeStringEvaluator", evaluator);
        var service = new DutyAutomationService(Fake<IPluginLog>(), null!, null!, null!, null!, null!, null!);
        service.HandleAdsQueueChatMessage(evaluator.Text(877));
        Assert.Equal(0, evaluator.Evaluations);
        Assert.False(service.TryGetAdsQueueFailure(out _, out _));

        var operation = (int)typeof(DutyAutomationService).GetMethod("BeginAdsQueueOperation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [Enum.Parse(typeof(DutyAutomationService).GetNestedType("SelectedMogtomeDuty", BindingFlags.NonPublic)!, "Praetorium"), "The Praetorium"])!;
        service.HandleAdsQueueChatMessage(evaluator.Text(880));
        Assert.False(service.TryGetAdsQueueFailure(out _, out _));
        Assert.Equal(operation, GetField(service, "lastPartyRequirementsLoggedOperationId"));
        Assert.Equal(operation, GetField(service, "activeAdsQueueOperationId"));
        service.HandleAdsQueueChatMessage("unrelated queue error");
        Assert.False(service.TryGetAdsQueueFailure(out _, out _));
        service.HandleAdsQueueChatMessage(evaluator.Text(877));
        Assert.True(service.TryGetAdsQueueFailure(out var reason, out var seconds));
        Assert.Equal("No duty selected chat error", reason);
        Assert.True(seconds > 0);
        Assert.Equal(0, GetField(service, "activeAdsQueueOperationId"));
        Assert.NotEqual(operation, GetField(service, "adsQueueOperationId"));
        var count = evaluator.Evaluations;
        service.HandleAdsQueueChatMessage(evaluator.Text(877));
        Assert.Equal(count, evaluator.Evaluations);
    }

    [Fact]
    public void InnRecognitionUsesBaseAndSpawnIdentityAndIntendedUse()
    {
        foreach (var id in new uint[] { 1000102, 1000974, 1001976, 1011193, 1018981, 1027231, 1037293, 1048375 })
        {
            Assert.True(InnEntryService.IsInnkeeper(id));
            Assert.True(InnEntryService.IsSelectedInnkeeper(100, id, 100, id));
            Assert.False(InnEntryService.IsSelectedInnkeeper(101, id, 100, id));
            Assert.False(InnEntryService.IsSelectedInnkeeper(100, id, 100, id + 1));
        }
        Assert.False(InnEntryService.IsInnkeeper(0));
        Assert.False(InnEntryService.IsInnkeeper(1000103));
        for (uint intended = 0; intended < 50; ++intended)
            Assert.Equal(intended == 2, GameHelpers.IsInnIntendedUse(intended));
    }

    [Fact]
    public void BossActionSelectionPreservesRoleHealthAndGeneralLimitBreak()
    {
        var actions = new List<(uint Id, ActionType Type)>();
        void Use(uint id, ActionType type) => actions.Add((id, type));
        foreach (var role in new byte[] { 0, 1, 2, 3, 4 })
        foreach (var name in new uint[] { 2134, 2135, 2136, 2137, 11285, 999 })
        {
            actions.Clear();
            BossHandlerService.UseBossActions(name, role, 49, 29, Use);
            if (role == 1 && name == 2136)
                Assert.Equal(new[] { (7531u, ActionType.Action), (16140u, ActionType.Action) }, actions);
            else if (role is 2 or 3)
            {
                Assert.Equal((7541u, ActionType.Action), actions[0]);
                if (name is 2137 or 11285) Assert.Equal((3u, ActionType.GeneralAction), actions[1]);
                Assert.Equal(name is 2137 or 11285 ? 2 : 1, actions.Count);
            }
            else Assert.Empty(actions);
        }
        foreach (var (hp, count) in new[] { (5f, 0), (5.001f, 2), (49.999f, 2), (50f, 1), (94.999f, 1), (95f, 0) })
        {
            actions.Clear();
            BossHandlerService.UseBossActions(2136, 1, 100, hp, Use);
            Assert.Equal(count, actions.Count);
        }
        actions.Clear();
        BossHandlerService.UseBossActions(2137, 2, 50, 30, Use);
        Assert.Empty(actions);
        BossHandlerService.UseBossActions(11285, 2, 100, 100, Use);
        Assert.Equal((3u, ActionType.GeneralAction), Assert.Single(actions));
        SetService("Framework", Fake<IFramework>());
        SetService("Log", Fake<IPluginLog>());
        Assert.False(GameHelpers.TryUseCombatAction(3, ActionType.GeneralAction)); // No native call off the framework thread.
    }

    [Fact]
    public void ResourcesAreCompleteAndFormatsWorkInBuiltSatellites()
    {
        var neutral = Ui.Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var baseline = neutral.Cast<DictionaryEntry>().ToDictionary(x => (string)x.Key, x => (string)x.Value!);
        Assert.True(baseline.Count > 600);
        foreach (var language in Enum.GetValues<UiLanguage>())
        {
            var culture = language == UiLanguage.English ? CultureInfo.InvariantCulture : Ui.CultureFor(language);
            var resourceSet = Ui.Resources.GetResourceSet(culture, true, false);
            Assert.NotNull(resourceSet); // No parent fallback hiding a missing satellite.
            var translated = resourceSet.Cast<DictionaryEntry>().ToDictionary(x => (string)x.Key, x => (string)x.Value!);
            Assert.Equal(baseline.Keys.Order(), translated.Keys.Order());
            foreach (var (key, value) in translated)
            {
                Assert.False(string.IsNullOrWhiteSpace(value), key);
                var originalFormat = CompositeFormat.Parse(baseline[key]);
                var format = CompositeFormat.Parse(value);
                Assert.Equal(originalFormat.MinimumArgumentCount, format.MinimumArgumentCount);
                static IEnumerable<string> Placeholders(string s) => Regex.Matches(s, @"(?<!\{)\{(\d+)(?:[^}]*)\}")
                    .Select(match => match.Groups[1].Value).Order();
                Assert.Equal(Placeholders(baseline[key]), Placeholders(value));
                var args = Enumerable.Repeat<object>(123, format.MinimumArgumentCount).ToArray();
                foreach (Match placeholder in Regex.Matches(value, @"\{(\d+):([^}]+)\}"))
                    if (placeholder.Groups[2].Value.Contains("HH"))
                        args[int.Parse(placeholder.Groups[1].Value, CultureInfo.InvariantCulture)] = new DateTime(2026, 9, 12, 13, 14, 15);
                _ = Ui.Format(key, language, args);
            }
            if (language == UiLanguage.Japanese)
                Assert.Contains(translated["Language_Label"], c => c >= '\u3000');
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ProfileDefaultOverrideReloadAndSwitchPreserveOtherSettings(ClientLanguage client, UiLanguage ui)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MogtomeLocalizationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var log = Fake<IPluginLog>();
            SetService("Log", log);
            var pi = Fake<IDalamudPluginInterface>((method, _) => method.Name == "get_ConfigDirectory"
                ? new DirectoryInfo(directory) : Default(method.ReturnType));
            var clientState = Fake<IClientState>((method, _) => method.Name == "get_ClientLanguage"
                ? client : Default(method.ReturnType));
            ConfigManager Create() => new(log, Fake<IPlayerState>(), clientState, pi);
            var manager = Create();
            manager.SetUiLanguage(ui); // No profile: selection cannot persist.
            Assert.Empty(Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories));
            Assert.Equal(Ui.FromClient(client), Ui.Language);
            Assert.True(manager.EnsureAccountSelected(1, "Test Character", "Test World"));
            var config = manager.GetActiveConfig();
            Assert.Equal(Ui.FromClient(client), config.UiLanguage);
            config.DutyCounter = 17;
            config.PotionItemId = 123;
            config.PotionItemName = "historical name";
            config.IsPartyLeader = true;
            var before = JsonSerializer.SerializeToNode(config)!.AsObject();
            var notifications = 0;
            manager.ConfigurationChanged += _ => notifications++;
            manager.SetUiLanguage(ui);
            Assert.Same(config, manager.GetActiveConfig());
            Assert.Equal(0, notifications); // Language selection must not reconfigure running services.
            Assert.Equal(ui, Ui.Language);
            var after = JsonSerializer.SerializeToNode(config)!.AsObject();
            before.Remove("UiLanguage"); after.Remove("UiLanguage");
            Assert.Equal(before.ToJsonString(), after.ToJsonString());
            manager = Create();
            Assert.True(manager.EnsureAccountSelected(1, "Test Character", "Test World"));
            Assert.Equal(ui, Ui.Language);
            Assert.Equal(17, manager.GetActiveConfig().DutyCounter);
            // Upgrade a profile whose JSON predates UiLanguage, then switch back.
            var oldPath = Path.Combine(directory, "MOGTOME", "2_MOGTOME.json");
            File.WriteAllText(oldPath, "{\"DutyCounter\":23}");
            Assert.True(manager.EnsureAccountSelected(2, "Test Character", "Test World"));
            Assert.Equal(Ui.FromClient(client), Ui.Language);
            Assert.Equal(Ui.FromClient(client), Configuration.LoadFromFile(oldPath).UiLanguage);
            Assert.Equal(23, manager.GetActiveConfig().DutyCounter);
            Assert.True(manager.EnsureAccountSelected(1, "Test Character", "Test World"));
            Assert.Equal(ui, Ui.Language);
            Assert.Equal(Ui.FromClient(client), Ui.ResolveLanguage((UiLanguage)99, client));
            var invalidPath = Path.Combine(directory, "MOGTOME", "3_MOGTOME.json");
            File.WriteAllText(invalidPath, "{\"UiLanguage\":99,\"DutyCounter\":7}");
            Assert.True(manager.EnsureAccountSelected(3, "Test Character", "Test World"));
            Assert.Equal(Ui.FromClient(client), Configuration.LoadFromFile(invalidPath).UiLanguage);
            Assert.Equal(7, manager.GetActiveConfig().DutyCounter);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PersistentMessagesRerenderWithoutChangingEnglishDiagnosticsOrCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        var message = Ui.M("Startup_DutyStartupPending", Ui.M("Startup_StartupInSOfContinuousReadiness", 1.5));
        var english = message.English;
        var engine = (MogtomeEngine)RuntimeHelpers.GetUninitializedObject(typeof(MogtomeEngine));
        SetField(engine, "<Status>k__BackingField", message);
        SetField(engine, "<CurrentState>k__BackingField", EngineState.InDuty);
        foreach (var language in Enum.GetValues<UiLanguage>())
        {
            Ui.SetLanguage(language);
            Assert.Equal(english, engine.StatusMessage);
            Assert.True(engine.IsRunning);
            Assert.Equal(EngineState.InDuty, engine.CurrentState);
            Assert.Equal(english, message.ToString());
            Assert.Equal(message.Render(language), message.Render());
            if (language != UiLanguage.English) Assert.NotEqual(english, message.Render());
            Assert.Equal("Language_Label", Ui.L("Language_Label").Split("###")[1]);
            Assert.Same(culture, CultureInfo.CurrentCulture);
            var service = new AdsDutyIpcService(() => true, () => false, () => "", () => false, () => DateTime.UtcNow);
            service.Refresh(true, 1044, 16, true);
            Assert.Equal("ADS.GetStatusJson returned an empty payload", service.CurrentDutyDetail);
        }
    }

    [Fact]
    public void InnRepairMigrationIsSavedOnceAndPreservesLaterSelections()
    {
        SetService("Log", Fake<IPluginLog>());
        Assert.Equal(AdsRepairMode.NpcYesInn, new Configuration().AdsRepairMode);
        Assert.Equal(2, new Configuration().Version);
        var directory = Path.Combine(Path.GetTempPath(), "MogtomeRepairMigrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var selfRepair in new[] { false, true })
            foreach (var threshold in new[] { -1, 0, 25, 80 })
            foreach (var includeVersion in new[] { false, true })
            {
                var path = Path.Combine(directory, "profile.json");
                var legacy = new Dictionary<string, object>
                {
                    ["UseAdsSelfRepair"] = selfRepair,
                    ["RepairThreshold"] = threshold,
                    ["UseAdsExperimental"] = false,
                    ["AutoDutyPathInstalled"] = true,
                    ["ReturnToEntranceDelaySeconds"] = 91,
                    ["MaxRuns"] = 123,
                    ["FoodItemId"] = 456,
                };
                if (includeVersion)
                    legacy["Version"] = 1;
                File.WriteAllText(path, JsonSerializer.Serialize(legacy));

                var migrated = Configuration.LoadFromFile(path);
                Assert.Equal(selfRepair ? AdsRepairMode.Self : AdsRepairMode.NpcYesInn, migrated.AdsRepairMode);
                var saved = File.ReadAllText(path);
                Assert.Equal(2, JsonSerializer.Deserialize<Configuration>(saved)!.Version);
                Assert.Equal(migrated.AdsRepairMode, JsonSerializer.Deserialize<Configuration>(saved)!.AdsRepairMode);
                var reloaded = Configuration.LoadFromFile(path);
                Assert.Equal(migrated.AdsRepairMode, reloaded.AdsRepairMode);
                Assert.Equal(saved, File.ReadAllText(path));

                // Even a legacy Self flag must not override a choice made after migration.
                foreach (var selection in Enum.GetValues<AdsRepairMode>())
                {
                    reloaded.AdsRepairMode = selection;
                    reloaded.SaveToFile(path);
                    for (var reload = 0; reload < 2; reload++)
                    {
                        reloaded = Configuration.LoadFromFile(path);
                        Assert.Equal(2, reloaded.Version);
                        Assert.Equal(selection, reloaded.AdsRepairMode);
                        Assert.Equal(threshold, reloaded.RepairThreshold);
                        Assert.False(reloaded.UseAdsExperimental);
                        Assert.True(reloaded.AutoDutyPathInstalled);
                        Assert.Equal(91, reloaded.ReturnToEntranceDelaySeconds);
                        Assert.Equal(123, reloaded.MaxRuns);
                        Assert.Equal(456, reloaded.FoodItemId);
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private void SetService(string name, object value)
    {
        var property = typeof(Plugin).GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        originalServices.TryAdd(property, property.GetValue(null));
        property.SetValue(null, value);
    }
    private static object? GetField(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Default(Type type) => type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
    private static T Fake<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var value = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)value).Handler = handler;
        return value;
    }
    public class Stub : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?>? Handler;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => Handler == null ? Default(method!.ReturnType) : Handler(method!, args);
    }
    public void Dispose()
    {
        foreach (var (property, value) in originalServices) property.SetValue(null, value);
        Ui.SetLanguage(originalLanguage);
    }

    // Simulate the evaluator boundary with captured sheets; assert every call explicitly
    // supplies the client language. This does not emulate native Dalamud/global state.
    private sealed class SheetEvaluator(ClientLanguage client) : ISeStringEvaluator
    {
        internal const string Raiser = "Test Raiser";
        internal string Destination = "Test Destination";
        internal bool DropBoundValue;
        internal bool Throw;
        internal int Evaluations;
        private string Macro(uint row, string raiser = Raiser)
            => Templates[client.ToString()][row]
                .Replace("<string(lstr1)>", DropBoundValue ? "" : raiser)
                .Replace("<string(gstr56)>", DropBoundValue ? "" : Destination);
        private static ReadOnlySeString Build(string macro)
            => new Lumina.Text.SeStringBuilder().AppendMacroString(macro).ToReadOnlySeString();
        internal string Text(uint row, string raiser = Raiser)
            => GameText.ReadVisibleText(Build(Macro(row, raiser)).Data.Span);
        internal string ChatText(uint row)
            => Dalamud.Game.Text.SeStringHandling.SeString.Parse(Build(Macro(row)).Data.Span).TextValue;
        private ReadOnlySeString Result(string text, ClientLanguage? language)
        {
            Assert.Equal(client, language);
            ++Evaluations;
            if (Throw) throw new InvalidOperationException("injected evaluator failure");
            return Build(text);
        }
        public ReadOnlySeString EvaluateFromAddon(uint row, Span<SeStringParameter> localParameters = default, ClientLanguage? language = null)
        {
            if (row == 3787)
            {
                Assert.Equal(1, localParameters.Length);
                Assert.True(localParameters[0].IsString);
                return Result(Macro(row, localParameters[0].StringValue.ToString()), language);
            }
            Assert.Empty(localParameters.ToArray());
            return Result(Macro(row), language);
        }
        public ReadOnlySeString EvaluateFromLogMessage(uint row, Span<SeStringParameter> localParameters = default, ClientLanguage? language = null)
            => Result(Macro(row), language);
        public ReadOnlySeString EvaluateMacroString(string macroString, Span<SeStringParameter> localParameters = default, ClientLanguage? language = null)
        {
            Assert.Equal("<string(gstr56)>", macroString);
            return Result(Destination, language);
        }
        public ReadOnlySeString Evaluate(ReadOnlySeString str, Span<SeStringParameter> localParameters = default, ClientLanguage? language = null) => throw new NotSupportedException();
        public ReadOnlySeString Evaluate(ReadOnlySeStringSpan str, Span<SeStringParameter> localParameters = default, ClientLanguage? language = null) => throw new NotSupportedException();
        public ReadOnlySeString EvaluateMacroString(ReadOnlySpan<byte> macroString, Span<SeStringParameter> localParameters = default, ClientLanguage? language = null) => throw new NotSupportedException();
        public ReadOnlySeString EvaluateFromLobby(uint row, Span<SeStringParameter> localParameters = default, ClientLanguage? language = null) => throw new NotSupportedException();
        public string EvaluateActStr(ActionKind kind, uint id, ClientLanguage? language = null) => throw new NotSupportedException();
        public string EvaluateObjStr(ObjectKind kind, uint id, ClientLanguage? language = null) => throw new NotSupportedException();
    }
}
