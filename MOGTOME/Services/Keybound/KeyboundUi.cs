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
    private const float CastReach = 260f;

    // Barrier phrases are drawn over the barrier band the input bar sits on, so a phrase without a
    // gauge only counts as one this close to the input bar; the boss at the far edge never does.
    private const float BarrierZoneAbove = 260f;
    private const float BarrierZoneBelow = 120f;

    public static void Read(BattleSnapshot snapshot, TextSlotCache cache)
    {
        snapshot.Clear();
        var unitManager = RaptureAtkUnitManager.Instance();
        if (unitManager == null)
        {
            return;
        }

        ReadInput(unitManager, snapshot, cache);
        ReadPhrases(unitManager, snapshot, cache);
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
        if (textInput == null || !textInput->Enabled || (textInput->ComponentFlags & 2) == 0 || !IsShown((AtkResNode*)textInput->OwnerNode))
        {
            return;
        }

        snapshot.InputVisible = true;
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

    // Does what a mouse press on the input bar does: focusing its collision node makes the text
    // input the module's typing target again.
    public static bool FocusInput()
    {
        var unitManager = RaptureAtkUnitManager.Instance();
        var stage = AtkStage.Instance();
        if (unitManager == null || stage == null || stage->AtkInputManager == null)
        {
            return false;
        }

        var addon = unitManager->GetAddonByName("KTGInput"u8, 1);
        if (addon == null || !addon->IsVisible || !addon->IsReady)
        {
            return false;
        }

        var textInput = FindTextInput(addon);
        if (textInput == null || textInput->CollisionNode == null || !textInput->Enabled || (textInput->ComponentFlags & 2) == 0)
        {
            return false;
        }

        return stage->AtkInputManager->SetFocus(&textInput->CollisionNode->AtkResNode, addon, 0);
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

    private static void ReadPhrases(RaptureAtkUnitManager* unitManager, BattleSnapshot snapshot, TextSlotCache cache)
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

            if (!ReadAddonPhrases(addon, snapshot, cache))
            {
                return;
            }
        }
    }

    private static bool ReadAddonPhrases(AtkUnitBase* addon, BattleSnapshot snapshot, TextSlotCache cache)
    {
        var nodeCount = addon->UldManager.NodeListCount;
        for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
        {
            var component = ComponentOf(addon->UldManager.NodeList[nodeIndex]);
            if (component == null)
            {
                continue;
            }

            var textNode = VisiblePhraseNode(component);
            if (textNode == null)
            {
                continue;
            }

            var slot = snapshot.PhraseCount;
            var phrase = cache.Decode(slot, textNode->NodeText.AsSpan(), singleLine: true);
            if (phrase.Length == 0)
            {
                continue;
            }

            var center = CenterOf(&textNode->AtkResNode, out var scale);
            snapshot.Phrases[slot] = phrase;
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

            var component = FirstComponent(addon);
            var fill = component == null ? null : component->GetImageNodeById(CastFillImageNodeId);
            if (fill == null || !IsShown(&fill->AtkResNode))
            {
                continue;
            }

            var slot = snapshot.CastCount;
            var center = CenterOf(&fill->AtkResNode, out _);
            snapshot.CastAddons[slot] = (nint)addon;
            snapshot.CastNames[slot] = ReadText(component->GetTextNodeById(CastNameTextNodeId), BattleSnapshot.FirstCastSlot + slot, cache);
            snapshot.CastScreenX[slot] = center.X;
            snapshot.CastScreenY[slot] = center.Y;
            snapshot.CastProgress[slot] = Math.Clamp(fill->AtkResNode.Width * fill->AtkResNode.ScaleX / CastFillFullWidth, 0f, 1f);
            snapshot.CastHits[slot] = ReadNumber(component->GetTextNodeById(CastHitsTextNodeId));
            snapshot.CastInterrupted[slot] = IsShown(component->GetTextNodeById(CastInterruptedTextNodeId));
            snapshot.CastCount++;
            if (snapshot.CastCount == BattleSnapshot.MaxCasts)
            {
                return;
            }
        }
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
            if (!addon->IsVisible || addon->RootNode == null)
            {
                continue;
            }

            var slot = snapshot.GaugeCount;
            var center = CenterOf(addon->RootNode, out _);
            snapshot.GaugeScreenX[slot] = center.X;
            snapshot.GaugeScreenY[slot] = center.Y;
            snapshot.GaugeSignature[slot] = DrawnSignature(FirstComponent(addon));
            snapshot.GaugeCount++;
            if (snapshot.GaugeCount == BattleSnapshot.MaxGauges)
            {
                return;
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
                && InBarrierZone(snapshot, phrase, scale);
            snapshot.PhraseCast[phrase] = NearestCast(snapshot, phrase, scale);
        }
    }

    // Only judged while some enemy gauge is on screen, so a layout the reader cannot see gauges in
    // never turns every phrase into a barrier phrase.
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

    private static int NearestCast(BattleSnapshot snapshot, int phrase, float scale)
    {
        var best = BattleSnapshot.NoCast;
        var bestDistance = CastReach * scale;
        for (var cast = 0; cast < snapshot.CastCount; cast++)
        {
            var distance = Vector2.Distance(
                new Vector2(snapshot.CastScreenX[cast], snapshot.CastScreenY[cast]),
                new Vector2(snapshot.PhraseScreenX[phrase], snapshot.PhraseScreenY[phrase]));
            if (distance <= bestDistance)
            {
                best = cast;
                bestDistance = distance;
            }
        }

        return best;
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
