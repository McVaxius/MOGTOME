using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MOGTOME.Services.Keybound;

namespace MOGTOME.Tests;

public sealed unsafe class KeyboundFocusTests
{
    [ThreadStatic] private static int clicks, parameter, x, y;

    [UnmanagedCallersOnly]
    private static void Receive(AtkEventListener* listener, AtkEventType type, int param, AtkEvent* evt, AtkEventData* data)
    {
        clicks++; parameter = param;
        x = data->MouseData.PosX; y = data->MouseData.PosY;
        evt->Param = 999; // Native handlers must never mutate the persistent registration.
    }

    [Theory]
    [InlineData(AtkEventType.MouseDown, false, false, false, true)]
    [InlineData(AtkEventType.MouseClick, false, false, false, true)]
    [InlineData(AtkEventType.MouseDown, true, false, false, false)]
    [InlineData(AtkEventType.MouseDown, false, true, false, false)]
    [InlineData(AtkEventType.ButtonClick, false, false, false, false)]
    [InlineData(AtkEventType.MouseDown, false, false, true, false)]
    public void ActivationUsesOnlyOwnInputMouseRegistrationAndPreservesItsData(AtkEventType type, bool global,
        bool foreign, bool invalidParam, bool allowed)
    {
        clicks = 0;
        var table = new AtkEventListener.AtkEventListenerVirtualTable { ReceiveEvent = &Receive };
        var inputListener = new AtkEventListener { VirtualTable = &table };
        var listener = &inputListener;
        var other = new AtkEventListener { VirtualTable = &table };
        var collision = new AtkResNode
        {
            NodeFlags = NodeFlags.Visible, ScaleX = 1, ScaleY = 1,
            ScreenX = 100, ScreenY = 200, Width = 80, Height = 20,
        };
        var registration = new AtkEvent
        {
            Listener = foreign ? &other : listener, Param = invalidParam ? uint.MaxValue : 42,
            State = new AtkEventState { EventType = type,
                StateFlags = global ? AtkEventStateFlags.IsGlobalEvent : AtkEventStateFlags.None },
        };
        collision.AtkEventManager.Event = &registration;

        Assert.Equal(allowed, KeyboundUi.ClickInputNode(&collision, listener));
        Assert.Equal(allowed ? 1 : 0, clicks);
        Assert.Equal(invalidParam ? uint.MaxValue : 42u, registration.Param);
        if (allowed)
        {
            Assert.Equal(42, parameter);
            Assert.Equal(140, x);
            Assert.Equal(210, y);
        }
        Assert.False(KeyboundUi.ClickInputNode(null, listener));
    }
}
