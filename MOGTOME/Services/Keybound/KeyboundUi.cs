// Based on Auto Keybound Brawler by XeldarAlz (AGPL-3.0-or-later).
// Copyright (c) 2026 Xeldar Alz. See docs/keybound-attribution.md.
using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Numerics;

namespace MOGTOME.Services.Keybound;

internal static unsafe class KeyboundUi
{
    private const int MaxAddonInstances = 16;
    private const ushort FirstComponentNodeType = 1000;
    private const byte PayloadStart = 0x02;
    private const byte PayloadEnd = 0x03;

    // KTGEWord.uld: text node 20 carries the phrase at font size 21, node 22 the same slot at size 12
    // for phrases too long for the large line.
    private const uint LargePhraseTextNodeId = 20;
    private const uint SmallPhraseTextNodeId = 22;
    private const uint ObscuringOverlayNodeId = 10;

    // KTGEnemyCastBar.uld: node 3 is the "Interrupted!" label, node 4 the spell name, node 5 the hits
    // still needed to interrupt (drawn under the bar), node 6 the 128 px wide fill.
    private const uint CastInterruptedTextNodeId = 3;
    private const uint CastNameTextNodeId = 4;
    private const uint CastHitsTextNodeId = 5;
    private const uint CastFillImageNodeId = 6;
    private const float CastFillFullWidth = 128f;

    // Each enemy's HP gauge (KTGIGaugeHp) hangs just under its phrase box; a phrase with no gauge
    // under it belongs to the base barrier, which is what Barrier Busters ask the party to type.
    // Distances are in UI units before scaling.
    private const float GaugeReachX = 90f;
    private const float GaugeReachAbove = 30f;
    private const float GaugeReachBelow = 150f;
    private const float CastReach = 450f;

    // Barrier phrases are drawn over the barrier band the input bar sits on, so a phrase without a
    // gauge only counts as one this close to the input bar; the boss at the far edge never does.
    private const float BarrierZoneAbove = 260f;
    private const float BarrierZoneBelow = 120f;

    public static void Read(BattleSnapshot snapshot, TextSlotCache cache, PhraseMemory memory)
    {
        snapshot.Clear();
        var unitManager = RaptureAtkUnitManager.Instance();
        if (unitManager == null)
        {
            return;
        }

        ReadInput(unitManager, snapshot, cache);
        ReadPhrases(unitManager, snapshot, cache, memory);
        ReadCasts(unitManager, snapshot, cache);
        ReadGauges(unitManager, snapshot);
        LinkPhrases(snapshot);
    }

    private static void ReadInput(RaptureAtkUnitManager* unitManager, BattleSnapshot snapshot, TextSlotCache cache)
    {
        var addon = unitManager->GetAddonByName("KTGInput"u8, 1);
        if (addon == null || !addon->IsVisible || !addon->IsReady)
        {
            return;
        }

        var textInput = FindTextInput(addon);
        if (textInput == null || textInput->OwnerNode == null || !textInput->Enabled || !IsShown((AtkResNode*)textInput->OwnerNode))
        {
            return;
        }

        snapshot.InputVisible = true;
        snapshot.Stage = KeyboundActivity.ReadStage(addon->Id);
        snapshot.InputIdentity = (nint)textInput;
        snapshot.InputScreenY = addon->RootNode == null ? 0f : CenterOf(addon->RootNode, out _).Y;
        snapshot.InputText = cache.Decode(BattleSnapshot.InputSlot, textInput->RawString.AsSpan(), singleLine: false);
        ReadFocus(textInput, snapshot);
    }

    // The game routes typed characters to whichever text input the module targets, so the plugin
    // only types while that target is the brawler's own input bar and never into the chat box.
    private static void ReadFocus(AtkComponentTextInput* textInput, BattleSnapshot snapshot)
    {
        var module = RaptureAtkModule.Instance();
        if (module == null || !module->IsTextInputActive())
        {
            return;
        }

        var ours = module->AtkModule.TextInput.TargetTextInputEventInterface == &textInput->AtkTextInputEventInterface;
        snapshot.InputFocused = ours;
        snapshot.OtherInputFocused = !ours;
    }

    // Cursor navigation can select the bar without activating text entry, particularly after a
    // stun or level change. Restore addon/component focus, then use the input's own mouse handler
    // if it still has not become the typing target. Never send a wake key to an unconfirmed target.
    public static bool FocusInput()
    {
        var unitManager = RaptureAtkUnitManager.Instance();
        var stage = AtkStage.Instance();
        var module = RaptureAtkModule.Instance();
        if (unitManager == null || stage == null || stage->AtkInputManager == null || module == null)
        {
            return false;
        }

        var addon = unitManager->GetAddonByName("KTGInput"u8, 1);
        if (addon == null || !addon->IsVisible || !addon->IsReady)
        {
            return false;
        }

        var textInput = FindTextInput(addon);
        if (!CanActivate(textInput) || module->IsTextInputActive() &&
            module->AtkModule.TextInput.TargetTextInputEventInterface != &textInput->AtkTextInputEventInterface)
        {
            return false;
        }

        var collision = &textInput->CollisionNode->AtkResNode;
        addon->Focus();
        // false avoids parking the controller's pointing hand on the bar as a substitute for entry.
        addon->SetFocusNode(collision, false, 0);
        addon->SetComponentFocusNode((AtkComponentBase*)textInput);
        stage->AtkInputManager->SetFocus(collision, addon, 0);
        // Match the native activation step used by upstream; selecting the collision node alone
        // leaves the field inactive after a stun or level transition. Routing is still read back.
        textInput->IsActive = true;
        if (module->IsTextInputActive() &&
            module->AtkModule.TextInput.TargetTextInputEventInterface == &textInput->AtkTextInputEventInterface)
            return true;
        if (module->IsTextInputActive() &&
            module->AtkModule.TextInput.TargetTextInputEventInterface != &textInput->AtkTextInputEventInterface)
            return false;

        // A true dispatch result is only an attempt. The controller still waits for ReadFocus to
        // observe the active input and the exact routing target on a later framework update.
        return ClickInput(textInput);
    }

    internal static bool CanActivate(AtkComponentTextInput* input)
        => input != null && input->CollisionNode != null && input->OwnerNode != null && input->Enabled &&
            IsShown((AtkResNode*)input->OwnerNode) &&
            IsShown(&input->CollisionNode->AtkResNode);

    internal static bool LogInputState(BattleSnapshot snapshot)
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null) return false;
        var addon = manager->GetAddonByName("KTGInput"u8, 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible) return false;
        var input = FindTextInput(addon);
        Plugin.Log?.Information("[MOGTOME][KBB] Native input: component={Component}, enabled={Enabled}, flags={Flags}, active={Active}, readable={Readable}, focused={Focused}, otherInput={OtherInput}, phrases={Phrases}",
            input != null, input != null && input->Enabled, input == null ? 0u : input->ComponentFlags,
            input != null && input->IsActive, snapshot.InputVisible, snapshot.InputFocused,
            snapshot.OtherInputFocused, snapshot.PhraseCount);
        return true;
    }

    internal static bool CanWakeInput()
    {
        var manager = RaptureAtkUnitManager.Instance();
        var module = RaptureAtkModule.Instance();
        if (manager == null || module == null) return false;
        var addon = manager->GetAddonByName("KTGInput"u8, 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible) return false;
        var input = FindTextInput(addon);
        return CanActivate(input) && (!module->IsTextInputActive() ||
            module->AtkModule.TextInput.TargetTextInputEventInterface == &input->AtkTextInputEventInterface);
    }

    internal static bool ClickInput(AtkComponentTextInput* input)
    {
        if (!CanActivate(input)) return false;
        var listener = (AtkEventListener*)input;
        var collision = &input->CollisionNode->AtkResNode;
        return ClickInputNode(collision, listener) || ClickInputNode((AtkResNode*)input->OwnerNode, listener);
    }

    internal static bool ClickInputNode(AtkResNode* node, AtkEventListener* listener)
    {
        if (node == null || listener == null || listener->VirtualTable == null || listener->VirtualTable->ReceiveEvent == null) return false;
        // Use the real registration, including its parameter and receiver. Do not guess a callback
        // number or dispatch an addon/foreign component's event. Bound malformed linked lists.
        for (var pass = 0; pass < 2; pass++)
        {
            var wanted = pass == 0 ? AtkEventType.MouseDown : AtkEventType.MouseClick;
            var count = 0;
            for (var registered = node->AtkEventManager.Event; registered != null && count++ < 64; registered = registered->NextEvent)
            {
                if (registered->State.EventType != wanted || registered->Listener != listener ||
                    registered->Param > int.MaxValue || registered->State.StateFlags.HasFlag(AtkEventStateFlags.IsGlobalEvent)) continue;
                var click = *registered;
                var center = CenterOf(node, out _);
                if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) ||
                    center.X < short.MinValue || center.X > short.MaxValue || center.Y < short.MinValue || center.Y > short.MaxValue) return false;
                var data = new AtkEventData();
                data.MouseData.PosX = (short)center.X;
                data.MouseData.PosY = (short)center.Y;
                click.Listener->ReceiveEvent(click.State.EventType, (int)click.Param, &click, &data);
                return true;
            }
        }
        return false;
    }

    private static AtkComponentTextInput* FindTextInput(AtkUnitBase* addon)
    {
        var nodeCount = addon->UldManager.NodeListCount;
        for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
        {
            var component = ComponentOf(addon->UldManager.NodeList[nodeIndex]);
            if (component != null && component->GetComponentType() == ComponentType.TextInput)
            {
                return (AtkComponentTextInput*)component;
            }
        }

        return null;
    }

    private static void ReadPhrases(RaptureAtkUnitManager* unitManager, BattleSnapshot snapshot, TextSlotCache cache, PhraseMemory memory)
    {
        Span<nint> seen = stackalloc nint[MaxAddonInstances];
        for (var instance = 1; instance <= MaxAddonInstances; instance++)
        {
            var addon = unitManager->GetAddonByName("KTGEWord"u8, instance);
            if (addon == null || seen[..(instance - 1)].Contains((nint)addon))
            {
                return;
            }

            seen[instance - 1] = (nint)addon;
            if (!addon->IsVisible)
            {
                continue;
            }

            if (!ReadAddonPhrases(addon, snapshot, cache, memory))
            {
                return;
            }
        }
    }

    private static bool ReadAddonPhrases(AtkUnitBase* addon, BattleSnapshot snapshot, TextSlotCache cache, PhraseMemory memory)
    {
        var nodeCount = addon->UldManager.NodeListCount;
        for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
        {
            var component = ComponentOf(addon->UldManager.NodeList[nodeIndex]);
            if (component == null)
            {
                continue;
            }

            var slot = snapshot.PhraseCount;
            var textNode = VisiblePhraseNode(component);
            string? phrase;
            if (textNode != null)
            {
                phrase = cache.Decode(slot, textNode->NodeText.AsSpan(), singleLine: true);
                memory.Remember((nint)component, phrase);
            }
            else if (IsObscured(component))
            {
                textNode = HiddenPhraseNode(component);
                phrase = textNode != null
                    ? cache.Decode(slot, textNode->NodeText.AsSpan(), singleLine: true)
                    : memory.Recall((nint)component);
                if (textNode == null)
                {
                    textNode = component->GetTextNodeById(LargePhraseTextNodeId);
                }
            }
            else
            {
                continue;
            }

            if (textNode == null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(phrase))
            {
                continue;
            }

            var center = CenterOf(&textNode->AtkResNode, out var scale);
            snapshot.Phrases[slot] = phrase;
            snapshot.PhraseTyped[slot] = PhraseReadings.TypedForm(phrase);
            snapshot.PhraseScreenX[slot] = center.X;
            snapshot.PhraseScreenY[slot] = center.Y;
            snapshot.PhraseScale[slot] = scale;
            snapshot.PhraseCount++;
            if (snapshot.PhraseCount == BattleSnapshot.MaxPhrases)
            {
                return false;
            }
        }

        return true;
    }

    // A phrase hidden by Cryptic Communications can still be typed. It is read from the hidden text
    // node, or from what the box last showed, only while the noise overlay is up: a box between enemies
    // or mid-animation hides its text too, and its old phrase must not come back.
    private static bool IsObscured(AtkComponentBase* component)
    {
        var overlay = component->GetNodeById(ObscuringOverlayNodeId);
        return overlay != null && IsShown(overlay);
    }

    private static AtkTextNode* HiddenPhraseNode(AtkComponentBase* component)
    {
        var large = component->GetTextNodeById(LargePhraseTextNodeId);
        if (HoldsText(large))
        {
            return large;
        }

        var small = component->GetTextNodeById(SmallPhraseTextNodeId);
        return HoldsText(small) ? small : null;
    }

    private static bool HoldsText(AtkTextNode* textNode) => textNode != null && !textNode->NodeText.AsSpan().IsEmpty;

    private static AtkTextNode* VisiblePhraseNode(AtkComponentBase* component)
    {
        var large = component->GetTextNodeById(LargePhraseTextNodeId);
        if (IsShown(large))
        {
            return large;
        }

        var small = component->GetTextNodeById(SmallPhraseTextNodeId);
        return IsShown(small) ? small : null;
    }

    private static void ReadCasts(RaptureAtkUnitManager* unitManager, BattleSnapshot snapshot, TextSlotCache cache)
    {
        Span<nint> seen = stackalloc nint[MaxAddonInstances];
        for (var instance = 1; instance <= MaxAddonInstances; instance++)
        {
            var addon = unitManager->GetAddonByName("KTGEnemyCastBar"u8, instance);
            if (addon == null || seen[..(instance - 1)].Contains((nint)addon))
            {
                return;
            }

            seen[instance - 1] = (nint)addon;
            if (!addon->IsVisible)
            {
                continue;
            }

            var nodeCount = addon->UldManager.NodeListCount;
            for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                var component = ComponentOf(addon->UldManager.NodeList[nodeIndex]);
                if (component != null && !ReadCast(component, snapshot, cache))
                {
                    return;
                }
            }
        }
    }

    // One window may carry a cast bar per enemy, so every component with a fill counts as a cast.
    private static bool ReadCast(AtkComponentBase* component, BattleSnapshot snapshot, TextSlotCache cache)
    {
        var fill = component->GetImageNodeById(CastFillImageNodeId);
        if (fill == null || !IsShown(&fill->AtkResNode))
        {
            return true;
        }

        var slot = snapshot.CastCount;
        var center = CenterOf(&fill->AtkResNode, out _);
        snapshot.CastAddons[slot] = (nint)component;
        snapshot.CastNames[slot] = ReadText(component->GetTextNodeById(CastNameTextNodeId), BattleSnapshot.FirstCastSlot + slot, cache);
        snapshot.CastScreenX[slot] = center.X;
        snapshot.CastScreenY[slot] = center.Y;
        snapshot.CastProgress[slot] = Math.Clamp(fill->AtkResNode.Width * fill->AtkResNode.ScaleX / CastFillFullWidth, 0f, 1f);
        snapshot.CastHits[slot] = ReadNumber(component->GetTextNodeById(CastHitsTextNodeId));
        snapshot.CastInterrupted[slot] = IsShown(component->GetTextNodeById(CastInterruptedTextNodeId));
        snapshot.CastCount++;
        return snapshot.CastCount < BattleSnapshot.MaxCasts;
    }

    private static void ReadGauges(RaptureAtkUnitManager* unitManager, BattleSnapshot snapshot)
    {
        Span<nint> seen = stackalloc nint[MaxAddonInstances];
        for (var instance = 1; instance <= MaxAddonInstances; instance++)
        {
            var addon = unitManager->GetAddonByName("KTGIGaugeHp"u8, instance);
            if (addon == null || seen[..(instance - 1)].Contains((nint)addon))
            {
                return;
            }

            seen[instance - 1] = (nint)addon;
            if (!addon->IsVisible)
            {
                continue;
            }

            var nodeCount = addon->UldManager.NodeListCount;
            for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                var node = addon->UldManager.NodeList[nodeIndex];
                var component = ComponentOf(node);
                if (component == null || !IsShown(node))
                {
                    continue;
                }

                var slot = snapshot.GaugeCount;
                var center = CenterOf(node, out _);
                snapshot.GaugeScreenX[slot] = center.X;
                snapshot.GaugeScreenY[slot] = center.Y;
                snapshot.GaugeSignature[slot] = DrawnSignature(component);
                snapshot.GaugeCount++;
                if (snapshot.GaugeCount == BattleSnapshot.MaxGauges)
                {
                    return;
                }
            }
        }
    }

    internal static void LinkPhrases(BattleSnapshot snapshot)
    {
        for (var phrase = 0; phrase < snapshot.PhraseCount; phrase++)
        {
            var scale = snapshot.PhraseScale[phrase];
            snapshot.PhraseGauge[phrase] = GaugeUnder(snapshot, phrase, scale);
            snapshot.PhraseOnBarrier[phrase] = snapshot.GaugeCount > 0 && snapshot.PhraseGauge[phrase] == BattleSnapshot.NoGauge
                && InBarrierZone(snapshot, phrase);
            snapshot.PhraseCast[phrase] = BattleSnapshot.NoCast;
        }

        for (var cast = 0; cast < snapshot.CastCount; cast++)
        {
            LinkCast(snapshot, cast);
        }
    }

    // Each cast bar belongs to the one enemy it sits closest to, so a bar between two enemies never
    // marks both of them as casting.
    private static void LinkCast(BattleSnapshot snapshot, int cast)
    {
        var castPosition = new Vector2(snapshot.CastScreenX[cast], snapshot.CastScreenY[cast]);
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var phrase = 0; phrase < snapshot.PhraseCount; phrase++)
        {
            if (snapshot.PhraseOnBarrier[phrase])
            {
                continue;
            }

            var distance = Vector2.Distance(castPosition, new Vector2(snapshot.PhraseScreenX[phrase], snapshot.PhraseScreenY[phrase]));
            if (distance <= CastReach * snapshot.PhraseScale[phrase] && distance < bestDistance)
            {
                best = phrase;
                bestDistance = distance;
            }
        }

        if (best >= 0 && (snapshot.PhraseCast[best] == BattleSnapshot.NoCast || IsCloser(snapshot, cast, snapshot.PhraseCast[best], best)))
        {
            snapshot.PhraseCast[best] = cast;
        }
    }

    private static bool IsCloser(BattleSnapshot snapshot, int cast, int other, int phrase)
    {
        var phrasePosition = new Vector2(snapshot.PhraseScreenX[phrase], snapshot.PhraseScreenY[phrase]);
        return Vector2.Distance(new Vector2(snapshot.CastScreenX[cast], snapshot.CastScreenY[cast]), phrasePosition)
            < Vector2.Distance(new Vector2(snapshot.CastScreenX[other], snapshot.CastScreenY[other]), phrasePosition);
    }

    // Only judged while some enemy gauge is on screen, so a layout the reader cannot see gauges in
    // never turns every phrase into a barrier phrase.
    internal static bool InBarrierZone(BattleSnapshot snapshot, int phrase)
        => InBarrierZone(snapshot, phrase, snapshot.PhraseScale[phrase]);

    private static bool InBarrierZone(BattleSnapshot snapshot, int phrase, float scale)
    {
        if (!snapshot.InputVisible)
        {
            return false;
        }

        var offset = snapshot.PhraseScreenY[phrase] - snapshot.InputScreenY;
        return offset >= -BarrierZoneAbove * scale && offset <= BarrierZoneBelow * scale;
    }

    private static int GaugeUnder(BattleSnapshot snapshot, int phrase, float scale)
    {
        var x = snapshot.PhraseScreenX[phrase];
        var y = snapshot.PhraseScreenY[phrase];
        for (var gauge = 0; gauge < snapshot.GaugeCount; gauge++)
        {
            var dx = MathF.Abs(snapshot.GaugeScreenX[gauge] - x);
            var dy = snapshot.GaugeScreenY[gauge] - y;
            if (dx <= GaugeReachX * scale && dy >= -GaugeReachAbove * scale && dy <= GaugeReachBelow * scale)
            {
                return gauge;
            }
        }

        return BattleSnapshot.NoGauge;
    }

    private static float DrawnSignature(AtkComponentBase* component)
    {
        if (component == null)
        {
            return 0f;
        }

        var signature = 0f;
        var nodeCount = component->UldManager.NodeListCount;
        for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
        {
            var node = component->UldManager.NodeList[nodeIndex];
            if (node != null && node->IsVisible())
            {
                signature += node->Width * node->ScaleX + node->Height * node->ScaleY;
            }
        }

        return signature;
    }

    private static string ReadText(AtkTextNode* textNode, int slot, TextSlotCache cache)
        => IsShown(textNode) ? cache.Decode(slot, textNode->NodeText.AsSpan(), singleLine: true) : string.Empty;

    private static int ReadNumber(AtkTextNode* textNode)
    {
        if (!IsShown(textNode))
        {
            return 0;
        }

        var bytes = textNode->NodeText.AsSpan();
        var value = 0;
        var inPayload = false;
        for (var index = 0; index < bytes.Length; index++)
        {
            var current = bytes[index];
            if (current == PayloadStart)
            {
                inPayload = true;
            }
            else if (current == PayloadEnd)
            {
                inPayload = false;
            }
            else if (!inPayload && current is >= (byte)'0' and <= (byte)'9')
            {
                value = value * 10 + (current - '0');
            }
        }

        return value;
    }

    private static bool IsShown(AtkTextNode* textNode)
        => textNode != null && !textNode->NodeText.AsSpan().IsEmpty && IsShown(&textNode->AtkResNode);

    private static bool IsShown(AtkResNode* node)
    {
        if (node == null) return false;
        for (var current = node; current != null; current = current->ParentNode)
        {
            if (!current->IsVisible())
            {
                return false;
            }
        }

        return true;
    }

    private static Vector2 CenterOf(AtkResNode* node, out float scale)
    {
        var scaleX = 1f;
        var scaleY = 1f;
        for (var current = node; current != null; current = current->ParentNode)
        {
            scaleX *= current->ScaleX;
            scaleY *= current->ScaleY;
        }

        scale = scaleY;
        return new Vector2(node->ScreenX + node->Width * scaleX * 0.5f, node->ScreenY + node->Height * scaleY * 0.5f);
    }

    private static AtkComponentBase* FirstComponent(AtkUnitBase* addon)
    {
        var nodeCount = addon->UldManager.NodeListCount;
        for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
        {
            var component = ComponentOf(addon->UldManager.NodeList[nodeIndex]);
            if (component != null)
            {
                return component;
            }
        }

        return null;
    }

    private static AtkComponentBase* ComponentOf(AtkResNode* node)
    {
        if (node == null || (ushort)node->Type < FirstComponentNodeType)
        {
            return null;
        }

        return ((AtkComponentNode*)node)->Component;
    }
}
