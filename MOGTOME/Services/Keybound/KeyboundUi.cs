// Based on Auto Keybound Brawler by XeldarAlz (AGPL-3.0-or-later).
// Copyright (c) 2026 Xeldar Alz. Adapted for MOGTOME; see docs/keybound-attribution.md.
using System;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MOGTOME.Services.Keybound;

internal static unsafe class KeyboundUi
{
    private const int MaxPhraseAddonInstances = 16;
    private const ushort FirstComponentNodeType = 1000;

    // KTGEWord.uld: text node 20 carries the phrase at font size 21, node 22 the same slot at size 12
    // for phrases too long for the large line.
    private const uint LargePhraseTextNodeId = 20;
    private const uint SmallPhraseTextNodeId = 22;

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
        if (textInput == null || !textInput->Enabled || (textInput->ComponentFlags & 2) == 0 || textInput->CollisionNode == null || !IsShown((AtkResNode*)textInput->OwnerNode))
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
        Span<nint> seen = stackalloc nint[MaxPhraseAddonInstances];
        for (var instance = 1; instance <= MaxPhraseAddonInstances; instance++)
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

            snapshot.Phrases[slot] = phrase;
            snapshot.PhraseScreenY[slot] = textNode->AtkResNode.ScreenY;
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

    private static bool IsShown(AtkTextNode* textNode)
    {
        if (textNode == null || textNode->NodeText.AsSpan().IsEmpty)
        {
            return false;
        }

        return IsShown(&textNode->AtkResNode);
    }

    private static bool IsShown(AtkResNode* node)
    {
        if (node == null) return false;
        for (; node != null; node = node->ParentNode)
        {
            if (!node->IsVisible())
            {
                return false;
            }
        }

        return true;
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
