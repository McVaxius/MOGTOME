# Keybound typing attribution

Based on Auto Keybound Brawler by XeldarAlz
https://github.com/XeldarAlz/FFXIV-AutoKeyboundBrawler

Copyright (c) 2026 Xeldar Alz. Licensed under AGPL-3.0-or-later.
Reference revision: 872fbebca745d4b63500bb680bf7fae1e8a9e884.
Follow-up mechanics reviewed and adapted from upstream 1.4.2.0 at
74857808beec411100fd5f1f7a410e231ff8c46d (2026-10-28).
Also reviewed 1.4.3.0 at 54e186d50e9ceb47386ae57f19eb8e2809e2190c,
including named controller/mouse timer handling from 339ac49c5c6f8694b718feb831fcc90369bb53c0.

MOGTOME adapts the native phrase reader, snapshot, UTF-8 text cache and Windows
keyboard message route under Services/Keybound. Its controller integrates account
ownership, action cancellation, exact input echo, native input permission checks,
confirmed duty/result limits, barrier/interrupt priorities, named Cursed Gaze guard,
repeat-without-progress detection and the existing MGC shopper. No reference
branding, icon or identity is reused. MOGTOME is independently maintained.

## Original notice

FFXIV-AutoKeyboundBrawler
Copyright (c) 2026 Xeldar Alz

This project is licensed under the GNU Affero General Public License v3.0.
See the LICENSE.md file for the full license text.

Redistributions and derivative works must preserve existing copyright
notices and license information as required by the AGPL.

The AGPL covers the source code. It does not license the project's name or
its identity: "Auto Keybound Brawler", the plugin's internal name
"AutoKeyboundBrawler" and the plugin icon are not part of the grant. A modified
version must carry its own name, its own internal name and its own icon, and
must state what it changed. See TRADEMARK.md.

Additional terms under section 7(b) and 7(c) of the AGPL: any work that
contains this project's code, in whole or in part, must credit the original
author with the line

    Based on Auto Keybound Brawler by XeldarAlz
    https://github.com/XeldarAlz/FFXIV-AutoKeyboundBrawler

in its README or project page, and on its About or credits screen if it has
one. It must not present itself as the original work, or as written,
maintained or endorsed by the original author.

## Automatic event contracts

Native registration and result layout cross-checked against AtmoOmen's published
FFXIVClientStructs.Expansion KTGEnterDialogue and KTGResult definitions. MOGTOME
uses the live-reported registration callback (`KTGEnterDialogue`, update state
true, one Int 0) and the current localized Leave button's registered event,
without adding that package or invoking its private methods. Leader/Solo can use a ready registration
dialog in the Square even if it was opened before Start; it does not
depend on a cached NPC target or a button event found in the addon's node list. The Square
entry prompt comes from Warp row 131633. Only duty 1095, arena 1359, lobby 1360
and the identified registrar/gatekeeper are automated.

Premade members wait in their current territory and accept only the matching
group queue pop. They do not travel to the Square, submit its registration
callback or cancel the party queue. Only Leader/Solo takes the registrar route.

The KBB-specific idle-counter guard uses the published AgentKTGInput layout
(agent 398, IdleSeconds at 80) and verifies its HUD pointer against agent 397
and the current KTGInput addon ID before writing. General input timer resets
also correspond to XeldarAlz commit 5886dbd2748486f76e483316ce37a9039c01e706.
No claim of live AFK-expulsion prevention is made without a loaded-client run.

## Focus recovery

The pointing-hand report exposed the shared focus-only limitation. The follow-up
restores addon and component focus with cursor navigation disabled, as in the
reference's updated focus path, then dispatches only a registered mouse event
belonging to the KBB text input. This activates entry through the native input
handler without guessing a callback or sending a wake character to another text
box. The controller still requires observed active text routing before typing.
The FFXIVClientStructs AtkUnitBase, AtkComponentInputBase, AtkEvent and AtkEventData
contracts were cross-checked. Native recovery after stun/level changes remains
subject to loaded-client verification.

## Upstream 1.4.2 mechanics

The follow-up adapts timed cast holds (3.5 seconds for Cursed Gaze, 0.8 seconds
for other full casts, persisting after the bar disappears), barrier phrase/spot
memory, all-component cast and gauge scanning with one enemy per cast, a
1.5-second recently-submitted-word exclusion, and phrase readings/optional
punctuation. Japanese readings come from KTGTypeWordTextData columns 0/1 in the
client language; language changes invalidate the reading cache. Concealed text
is recalled only when the Cryptic Communications noise overlay is visible.

MOGTOME keeps its own account/session ownership, native focus activation and
input-echo controller. If entry still needs waking, an owned and paced physical
key is used only with a ready KBB field, visible phrases, no other text-input
owner and no cast hold. System key-down requires the game in the foreground;
only a detached window belonging to the same game process can return foreground
to it. Key-up remains owned through Stop and foreground changes. Non-Latin
phrases use Space to wake and continue from the observed input text. Existing
Letterlock/input-disable guards, WPM/Thrower pace, dedicated KBB idle-counter
heartbeat, registration callback and Leader/Member queue policies remain in force.
The 1.4.3 controller/mouse timer extension runs only inside MOGTOME's matched
arena/agent/HUD/addon and timer-territory guard. Only finite nonnegative named
Timer fields are reset; histories, status and undocumented offsets are retained.
