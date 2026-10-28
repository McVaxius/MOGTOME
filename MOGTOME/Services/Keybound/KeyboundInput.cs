namespace MOGTOME.Services.Keybound;

internal interface IKeyboundInput
{
    void Read(BattleSnapshot snapshot);
    bool Focus();
    bool Character(char value);
    bool KeyDown(GameKey key);
    bool KeyUp(GameKey key);
}

internal sealed class KeyboundInput : IKeyboundInput
{
    private readonly TextSlotCache cache = new(BattleSnapshot.TextSlotCount);
    public void Read(BattleSnapshot snapshot) => KeyboundUi.Read(snapshot, cache);
    public bool Focus() => KeyboundUi.FocusInput();
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
