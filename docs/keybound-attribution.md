# Keybound typing attribution

Based on Auto Keybound Brawler by XeldarAlz
https://github.com/XeldarAlz/FFXIV-AutoKeyboundBrawler

Copyright (c) 2026 Xeldar Alz. Licensed under AGPL-3.0-or-later.
Reference revision: 872fbebca745d4b63500bb680bf7fae1e8a9e884.

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
uses current localized Register/Leave labels and their actual registered button
events, without adding that package or invoking its private methods. The Square
entry prompt comes from Warp row 131633. Only duty 1095, arena 1359, lobby 1360
and the identified registrar/gatekeeper are automated.

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
