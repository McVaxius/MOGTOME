using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MOGTOME.Services.Keybound;
using MOGTOME.Services;

namespace MOGTOME.Tests;

public sealed unsafe class KeyboundFocusTests
{
    [Fact]
    public void ReadyVisibleInputDoesNotRequireTheUndocumentedComponentFlagButDisabledHiddenAndMissingOwnersStayBlocked()
    {
        // The production guard calls the native visibility accessor. Supply that one accessor
        // for these fabricated nodes, then restore its address before leaving the fixture.
        var visibility = AtkResNode.Addresses.IsVisible.Value;
        AtkResNode.Addresses.IsVisible.Value = (nint)(delegate* unmanaged<AtkResNode*, byte>)&Visible;
        try
        {
            var owner = new AtkComponentNode();
            owner.AtkResNode.NodeFlags = NodeFlags.Visible;
            var collision = new AtkCollisionNode();
            collision.AtkResNode.NodeFlags = NodeFlags.Visible;
            var input = new AtkComponentTextInput
            {
                OwnerNode = &owner, CollisionNode = &collision, Enabled = true, ComponentFlags = 1,
            };
            Assert.True(KeyboundUi.CanActivate(&input));
            input.ComponentFlags = 3;
            Assert.True(KeyboundUi.CanActivate(&input));
            input.Enabled = false;
            Assert.False(KeyboundUi.CanActivate(&input));
            input.Enabled = true;
            collision.AtkResNode.NodeFlags = 0;
            Assert.False(KeyboundUi.CanActivate(&input));
            collision.AtkResNode.NodeFlags = NodeFlags.Visible;
            owner.AtkResNode.NodeFlags = 0;
            Assert.False(KeyboundUi.CanActivate(&input));
            owner.AtkResNode.NodeFlags = NodeFlags.Visible;
            input.OwnerNode = null;
            Assert.False(KeyboundUi.CanActivate(&input));
        }
        finally { AtkResNode.Addresses.IsVisible.Value = visibility; }
    }

    [UnmanagedCallersOnly]
    private static byte Visible(AtkResNode* node) => (byte)((node->NodeFlags & NodeFlags.Visible) != 0 ? 1 : 0);

    [ThreadStatic] private static int clicks, parameter, x, y;
    [ThreadStatic] private static nint receivedBy;

    [UnmanagedCallersOnly]
    private static void ReceiveAddon(AtkEventListener* listener, AtkEventType type, int param, AtkEvent* evt, AtkEventData* data)
    {
        clicks++; parameter = param; receivedBy = (nint)listener;
        evt->Param = 999;
    }

    [Fact]
    public void ResultsButtonUsesTheContainingAddonAndTheNativeRegistrationWithoutRequiringATranslatedLabel()
    {
        clicks = 0;
        var table = new AtkEventListener.AtkEventListenerVirtualTable { ReceiveEvent = &ReceiveAddon };
        var addon = new AtkUnitBase { AtkEventListener = new AtkEventListener { VirtualTable = &table } };
        var registration = new AtkEvent
        {
            Param = 42, State = new AtkEventState { EventType = AtkEventType.ButtonClick },
        };
        var owner = new AtkComponentNode();
        owner.AtkResNode.NodeFlags = NodeFlags.Visible | NodeFlags.Enabled;
        owner.AtkResNode.AtkEventManager.Event = &registration;
        var button = new AtkComponentButton { OwnerNode = &owner };
        Assert.True(BlundervilleGameAdapter.ClickAddonButton(&addon, &button));
        Assert.Equal(1, clicks);
        Assert.Equal((nint)(&addon.AtkEventListener), receivedBy);
        Assert.Equal(42, parameter);
        Assert.Equal(42u, registration.Param);
        owner.AtkResNode.NodeFlags = NodeFlags.Visible;
        Assert.False(BlundervilleGameAdapter.ClickAddonButton(&addon, &button));
        Assert.False(BlundervilleGameAdapter.ClickAddonButton(null, &button));
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void MatchingDutyPopUsesNativeButtonRegistrationAndRejectsForeignDisabledOrMissingButtons()
    {
        clicks = 0;
        var table = new AtkEventListener.AtkEventListenerVirtualTable { ReceiveEvent = &Receive };
        var receiver = new AtkEventListener { VirtualTable = &table };
        var registration = new AtkEvent
        {
            Listener = &receiver, Param = 42,
            State = new AtkEventState { EventType = AtkEventType.ButtonClick },
        };
        var owner = new AtkComponentNode();
        owner.AtkResNode.NodeFlags = NodeFlags.Visible | NodeFlags.Enabled;
        owner.AtkResNode.AtkEventManager.Event = &registration;
        var button = new AtkComponentButton { OwnerNode = &owner };
        Assert.False(KeyboundLoopGame.ClickDutyPopButton(false, &button));
        Assert.False(KeyboundLoopGame.ClickDutyPopButton(true, null));
        Assert.Equal(0, clicks);
        Assert.True(KeyboundLoopGame.ClickDutyPopButton(true, &button));
        Assert.Equal(1, clicks);
        Assert.Equal(42, parameter);
        Assert.Equal(42u, registration.Param);
        owner.AtkResNode.NodeFlags = NodeFlags.Visible;
        Assert.False(KeyboundLoopGame.ClickDutyPopButton(true, &button));
        owner.AtkResNode.NodeFlags = NodeFlags.Enabled;
        Assert.False(KeyboundLoopGame.ClickDutyPopButton(true, &button));
        button.OwnerNode = null;
        Assert.False(KeyboundLoopGame.ClickDutyPopButton(true, &button));
        Assert.Equal(1, clicks);
    }

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
