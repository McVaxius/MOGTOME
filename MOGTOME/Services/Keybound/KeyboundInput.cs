namespace MOGTOME.Services.Keybound;

internal interface IKeyboundInput
{
    void Reset() { }
    void Read(BattleSnapshot snapshot);
    bool Focus();
    bool Character(char value);
    bool KeyDown(GameKey key);
    bool KeyUp(GameKey key);
    bool WakeKeyDown(GameKey key) => false;
    bool WakeKeyUp(GameKey key) => false;
}

internal sealed class KeyboundInput : IKeyboundInput
{
    private readonly TextSlotCache cache = new(BattleSnapshot.TextSlotCount);
    private readonly PhraseMemory memory = new();
    private bool inputLogged;
    public void Reset() { memory.Clear(); inputLogged = false; }
    private static bool UiTextInput => !Dalamud.Bindings.ImGui.ImGui.GetCurrentContext().IsNull &&
        Dalamud.Bindings.ImGui.ImGui.GetIO().WantTextInput;
    public void Read(BattleSnapshot snapshot)
    {
        KeyboundUi.Read(snapshot, cache, memory);
        if (UiTextInput) { snapshot.InputFocused = false; snapshot.OtherInputFocused = true; }
        if (!inputLogged) inputLogged = KeyboundUi.LogInputState(snapshot);
    }
    public bool Focus() => !UiTextInput && KeyboundUi.FocusInput();
    public bool WakeKeyDown(GameKey key)
    {
        if (UiTextInput || !KeyboundUi.CanWakeInput()) return false;
        GameKeyboard.ReclaimForeground();
        var sent = GameKeyboard.SendSystemKey(key, keyUp: false);
        if (sent) NotifyActivity();
        return sent;
    }
    public bool WakeKeyUp(GameKey key) => GameKeyboard.SendSystemKey(key, keyUp: true);
    public bool Character(char value)
    {
        var sent = GameKeyboard.PostCharacter(value);
        if (sent) NotifyActivity();
        return sent;
    }
    public bool KeyDown(GameKey key)
    {
        var sent = GameKeyboard.PostKeyDown(key);
        if (sent) NotifyActivity();
        return sent;
    }
    private static void NotifyActivity()
    {
        // A successfully posted key must still be recorded/released if activity inspection fails.
        try { KeyboundActivity.Pulse(); }
        catch (System.Exception ex) { Plugin.Log?.Warning(ex, "[MOGTOME][KBB] activity inspection failed"); }
    }
    public bool KeyUp(GameKey key) => GameKeyboard.PostKeyUp(key);
}
