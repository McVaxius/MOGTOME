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
    public bool Character(char value) => GameKeyboard.PostCharacter(value);
    public bool KeyDown(GameKey key) => GameKeyboard.PostKeyDown(key);
    public bool KeyUp(GameKey key) => GameKeyboard.PostKeyUp(key);
}
