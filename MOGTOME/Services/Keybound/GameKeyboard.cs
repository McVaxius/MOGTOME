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
    // KEYBDINPUT uses the virtual key; a non-Latin reading starts with Space and is corrected from echo.
    public static GameKey Wake(char value) => new(value is >= 'a' and <= 'z' ? (byte)char.ToUpperInvariant(value)
        : value is >= 'A' and <= 'Z' or >= '0' and <= '9' ? (byte)value : (byte)0x20, 0);
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

    public static bool SendSystemKey(GameKey key, bool keyUp)
    {
        var framework = Framework.Instance();
        var window = framework == null || framework->GameWindow == null ? 0 : framework->GameWindow->WindowHandle;
        if (!keyUp && (window == 0 || GetForegroundWindow() != window)) return false;
        var input = new KeyboardInput { Type = 1, VirtualKey = key.VirtualKey, ScanCode = key.ScanCode, Flags = keyUp ? 2u : 0u };
        return SendInput(1, &input, 40) == 1;
    }

    public static void ReclaimForeground()
    {
        var framework = Framework.Instance();
        var window = framework == null || framework->GameWindow == null ? 0 : framework->GameWindow->WindowHandle;
        var foreground = GetForegroundWindow();
        if (window == 0 || foreground == 0 || foreground == window) return;
        GetWindowThreadProcessId(foreground, out var process);
        // A detached Dalamud window can return focus to its game; another application retains it.
        if (process == (uint)Environment.ProcessId) SetForegroundWindow(window);
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort ScanCode;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nuint ExtraInfo;
    }

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, KeyboardInput* inputs, int size);
    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint windowHandle);

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
