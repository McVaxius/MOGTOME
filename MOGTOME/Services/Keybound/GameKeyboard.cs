// Based on Auto Keybound Brawler by XeldarAlz (AGPL-3.0-or-later).
// Copyright (c) 2026 Xeldar Alz. Adapted for MOGTOME; see docs/keybound-attribution.md.
using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace MOGTOME.Services.Keybound;

internal readonly record struct GameKey(byte VirtualKey, byte ScanCode)
{
    public static readonly GameKey Enter = new(0x0D, 0x1C);
    public static readonly GameKey Backspace = new(0x08, 0x0E);
}

internal static unsafe partial class GameKeyboard
{
    private const uint KeyDownMessage = 0x0100;
    private const uint KeyUpMessage = 0x0101;
    private const uint CharMessage = 0x0102;
    private const int ScanCodeShift = 16;
    private const long SingleRepeat = 1;
    private const long KeyUpTransitionBits = 0xC0000000;

    public static bool PostCharacter(char character) => Post(CharMessage, character, SingleRepeat);

    public static bool PostKeyDown(GameKey key) => Post(KeyDownMessage, key.VirtualKey, SingleRepeat | ((long)key.ScanCode << ScanCodeShift));

    public static bool PostKeyUp(GameKey key) => Post(KeyUpMessage, key.VirtualKey, SingleRepeat | ((long)key.ScanCode << ScanCodeShift) | KeyUpTransitionBits);

    private static bool Post(uint message, nuint wParam, long lParam)
    {
        var framework = Framework.Instance();
        if (framework == null || framework->GameWindow == null)
        {
            return false;
        }

        var windowHandle = framework->GameWindow->WindowHandle;
        return windowHandle != 0 && PostMessage(windowHandle, message, wParam, (nint)lParam);
    }

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint windowHandle, uint message, nuint wParam, nint lParam);
}
