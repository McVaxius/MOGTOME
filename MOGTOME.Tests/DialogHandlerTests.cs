using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Party;
using Dalamud.Plugin.Services;
using MOGTOME.IPC;
using MOGTOME.Models;
using MOGTOME.Services;

namespace MOGTOME.Tests;

public sealed class DialogHandlerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ConfirmedWipeBypassesDelayUntilTheLocalDeathEnds(bool minimized, bool useAds)
    {
        var run = new DialogRun(new Configuration { ReturnToEntranceDelaySeconds = 120, UseAdsExperimental = useAds });
        var dead = DialogRun.Member(true);
        var alive = DialogRun.Member(false);
        run.Visible = !minimized;
        run.Text = DialogRun.Prompt(118);

        // Empty parties, missing members/objects, and individual deaths cannot confirm a wipe.
        foreach (var members in new IPartyMember?[][] { [], [dead, null], [dead, Fake<IPartyMember>()], [dead, alive] })
        {
            run.ObserveParty(members);
            run.Frame();
            Assert.Equal(0, run.Accepts);
            Assert.Equal(0, run.Reopens);
        }

        // Observe between dialog updates; another member returns before the next update.
        var startedAt = run.StartedAt;
        run.ObserveParty(dead, dead);
        run.ObserveParty(dead, alive);
        Assert.Equal(0, run.Accepts);
        Assert.Equal(0, run.Reopens);

        // A recorded wipe still cannot accept unrelated or unreadable prompts.
        foreach (var text in new[] { (string?)null, "", "Accept this party invitation?", DialogRun.Prompt(109) })
        {
            run.Visible = true;
            run.Text = text;
            run.Frame();
            Assert.Equal(0, run.Accepts);
            Assert.Equal(0, run.Reopens);
        }

        run.Visible = !minimized;
        run.Text = DialogRun.Prompt(118);
        run.Frame();
        Assert.Equal(minimized ? 1 : 0, run.Reopens);
        Assert.Equal(minimized ? 0 : 1, run.Accepts);
        if (minimized)
            run.Frame(); // Restored Return is classified on the following update.
        Assert.Equal(1, run.Accepts);
        Assert.Equal(startedAt, run.StartedAt);

        run.Frame(eligible: false); // Local revival/lost eligibility clears the recorded wipe.
        Assert.Null(run.StartedAt);
        run.ObserveParty(dead, alive);
        run.Visible = false;
        run.Frame(); // The next individual death must wait again.
        Assert.NotNull(run.StartedAt);
        Assert.NotEqual(startedAt, run.StartedAt);
        Assert.Equal(minimized ? 1 : 0, run.Reopens);
        run.Visible = true;
        run.Text = DialogRun.Prompt(119); // Avoid the duplicate-prompt cooldown masking a stale wipe.
        run.Frame();
        Assert.Equal(1, run.Accepts);

        run.Visible = false;
        run.Advance(121);
        run.Frame();
        Assert.Equal(minimized ? 2 : 1, run.Reopens);
        run.Frame();
        Assert.Equal(2, run.Accepts);

        run.ObserveParty(dead, dead);
        run.Stop(); // Stop also clears the recorded wipe through the existing reset path.
        Assert.Null(run.StartedAt);
        run.Visible = false;
        run.Frame();
        Assert.Equal(minimized ? 2 : 1, run.Reopens);
    }

    [Theory]
    [InlineData(60, false, false)]
    [InlineData(60, true, false)]
    [InlineData(60, false, true)]
    [InlineData(60, true, true)]
    [InlineData(120, false, false)]
    [InlineData(120, true, true)]
    public void EligibleDeathKeepsOneCountdownAcrossMinimizedAndChangedPrompts(
        int delay, bool minimizedBeforeFirstPrompt, bool useAds)
    {
        var config = new Configuration { ReturnToEntranceDelaySeconds = delay, UseAdsExperimental = useAds };
        var run = new DialogRun(config);
        run.Frame(eligible: false);
        Assert.Null(run.StartedAt);
        Assert.Equal(0, run.Reopens);

        run.Visible = !minimizedBeforeFirstPrompt;
        run.Text = DialogRun.Prompt(118);
        run.Frame(); // Death detection starts timing even when no prompt has been seen.
        Assert.NotNull(run.StartedAt);
        Assert.Equal(0, run.Accepts);
        Assert.Equal(0, run.Reopens);

        run.Advance(delay / 2);
        var startedAt = run.StartedAt;
        run.Visible = false;
        run.Frame();
        Assert.Equal(startedAt, run.StartedAt);
        Assert.Equal(0, run.Reopens);

        // Repeated minimization, a different Return variant, unreadable content,
        // unrelated confirmations, and failed reads must all preserve this death.
        foreach (var text in new[] { DialogRun.Prompt(119), null, "", "Accept this party invitation?" })
        {
            run.Visible = true;
            run.Text = text;
            run.Frame();
            run.Visible = false;
            run.Frame();
            Assert.Equal(startedAt, run.StartedAt);
            Assert.Equal(0, run.Accepts);
            Assert.Equal(0, run.Reopens);
        }
        run.ThrowOnRead = true;
        run.Frame();
        run.ThrowOnRead = false;
        Assert.Equal(startedAt, run.StartedAt);

        run.Advance(delay / 2 + 1);
        startedAt = run.StartedAt;
        run.ReviveVisible = false;
        run.Frame(); // A missing dialog alone never justifies reopening.
        Assert.Equal(0, run.Reopens);
        run.ReviveVisible = true;
        foreach (var text in new[] { (string?)null, "", "Accept this party invitation?", DialogRun.Prompt(109) })
        {
            run.Visible = true;
            run.Text = text;
            run.Frame(); // Do not reopen over an existing unreadable/unrelated dialog.
            Assert.Equal(0, run.Reopens);
            Assert.Equal(0, run.Accepts);
            Assert.Equal(startedAt, run.StartedAt);
        }

        run.Visible = false;
        run.Frame(); // The callback exposes Return immediately in this fixture.
        Assert.Equal(1, run.Reopens);
        Assert.Equal(0, run.Accepts); // Reopening must return without accepting.
        Assert.Equal(startedAt, run.StartedAt);
        run.Visible = false; // Minimized again before the next update.
        run.Frame();
        Assert.Equal(2, run.Reopens);
        Assert.Equal(0, run.Accepts);
        run.Frame(); // Restored Return is classified and accepted, with no second wait.
        Assert.Equal(1, run.Accepts);
        Assert.Equal(startedAt, run.StartedAt);

        run.Frame(eligible: false); // Revival/lost eligibility resets even inside the check cooldown.
        Assert.Null(run.StartedAt);
        run.Visible = false;
        run.Frame(); // Another death starts a fresh wait.
        Assert.NotNull(run.StartedAt);
        Assert.NotEqual(startedAt, run.StartedAt);
        Assert.Equal(2, run.Reopens);

        // Recognized raises and sealed-area moves remain immediate during that wait.
        foreach (var row in new uint[] { 112, 102631 })
        {
            run.Visible = true;
            run.Text = DialogRun.Prompt(row);
            run.Frame();
        }
        Assert.Equal(3, run.Accepts);
        run.Visible = false;
        run.Advance(delay + 1);
        run.Frame();
        Assert.Equal(3, run.Reopens);
        run.Frame();
        Assert.Equal(4, run.Accepts);

        run.Stop();
        Assert.Null(run.StartedAt);
        run.Visible = false;
        run.Frame(eligible: false);
        Assert.Equal(3, run.Reopens);
    }

    // Exercise the real handler with captured game text and in-memory native boundaries.
    // Advancing its existing timestamps avoids sleeps or game/client access.
    private sealed class DialogRun : DialogHandlerService
    {
        internal bool Visible;
        internal string? Text;
        internal bool ReviveVisible = true;
        internal bool ThrowOnRead;
        internal int Reopens;
        internal int Accepts;
        internal long? StartedAt => (long?)Field("eligibleDeathStartedAt").GetValue(this);

        internal DialogRun(Configuration config)
            : base(Fake<IPluginLog>(), new YesAlreadyIPC(Fake<IPluginLog>()),
                Fake<ICommandManager>(), Fake<IGameGui>(), Manager(config)) { }

        internal void ObserveParty(params IPartyMember?[] members)
            => ObservePartyDeaths(Fake<IPartyList>((method, args) => method.Name switch
            {
                "get_Length" => members.Length,
                "get_Item" => members[(int)args![0]!],
                _ => null,
            }));

        internal static IPartyMember Member(bool dead)
            => Fake<IPartyMember>((method, _) => method.Name == "get_GameObject"
                ? Fake<IGameObject>((member, _) => member.Name == "get_IsDead" ? dead : null)
                : null);

        internal void Frame(bool eligible = true)
        {
            if (eligible)
                Field("lastDialogCheck").SetValue(this, DateTime.MinValue);
            Update(eligible);
        }

        internal void Advance(int seconds)
        {
            Assert.NotNull(StartedAt);
            Field("eligibleDeathStartedAt").SetValue(this, StartedAt - seconds * Stopwatch.Frequency);
            Field("lastHandledDialogAt").SetValue(this, DateTime.MinValue);
        }

        internal override (bool Visible, string? Text) ReadYesNoPrompt()
            => ThrowOnRead ? throw new InvalidOperationException("unreadable addon") : (Visible, Text);

        internal override bool IsAddonVisible(string addonName)
        {
            Assert.Equal("_NotificationRevive", addonName);
            return ReviveVisible;
        }

        internal override bool TryFireAddonCallback(string addonName, bool updateState, params object[] args)
        {
            Assert.Equal("_Notification", addonName);
            Assert.True(updateState);
            Assert.Equal(new object[] { 0, 1, 2 }, args);
            ++Reopens;
            Visible = true;
            Text = Prompt(118);
            return true;
        }

        internal override bool ClickYesIfVisible()
        {
            Assert.True(Visible);
            ++Accepts;
            return true;
        }

        internal override bool MatchesPrompt(string text, GamePrompt prompt)
            => GameText.MatchesPrompt(text, prompt, ClientLanguage.English, (row, _) => Prompt(row));

        internal static string Prompt(uint row)
        {
            var macro = LocalizationTests.Templates["English"][row]
                .Replace("<string(gstr56)>", "Test Duty")
                .Replace("<string(lstr1)>", "Test Raiser");
            return GameText.ReadVisibleText(new Lumina.Text.SeStringBuilder()
                .AppendMacroString(macro).ToReadOnlySeString().Data.Span);
        }

        private static FieldInfo Field(string name)
            => typeof(DialogHandlerService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static ConfigManager Manager(Configuration config)
        {
            var manager = (ConfigManager)RuntimeHelpers.GetUninitializedObject(typeof(ConfigManager));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ConfigManager).GetField("currentAccountId", flags)!.SetValue(manager, "temporary");
            typeof(ConfigManager).GetField("accounts", flags)!.SetValue(manager,
                new Dictionary<string, AccountConfig> { ["temporary"] = new() { Settings = config } });
            return manager;
        }
    }

    private static T Fake<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var fake = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)fake).Handler = handler;
        return fake;
    }

    public class Stub : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?>? Handler;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => Handler != null ? Handler(method!, args)
                : method!.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType
                    ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
