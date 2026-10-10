# M.O.G.T.O.M.E.

---

**Help fund my AI overlords' coffee addiction so they can keep generating more plugins instead of taking over the world**

[☕ Support development on Ko-fi](https://ko-fi.com/mcvaxius)

[XA and I have created some Plugins and Guides here at -> aethertek.io](https://aethertek.io/)
### Repo URL:
```
https://aethertek.io/x.json
```

---

**Management Of Grand Tome Operations & Management Engine**

A Dalamud plugin for automated duty farming and tome acquisition in FFXIV.

---

## Overview

MOGTOME automates farming of The Praetorium (99 runs) and The Porta Decumana (until daily reset) for efficient tome acquisition. Converted from the G.O.O.N. SND script to a native Dalamud plugin.

Main's **Team Leader** checkbox directly beneath the header changes the same active-account role setting as
Config > Party. Blunderville determines its role from the actual party independently.
In ADS mode, a successful AutoDuty disable notice is informational and dismissible;
use the manual **Enable AutoDuty** and **Disable AutoDuty** buttons in `/ads mini`
to change its loaded state afterward. A Disable retry appears only while it remains loaded.

Both shopping grids show ownership for collectibles and equippable gear using
native inventory/registration and valid current-character XA Database storage.
Retainer, armoire and glamour-dresser ownership does not increase the carried
inventory count or change a manually configured purchase deficit. Refresh XADB
after recording storage; empty or incomplete storage data leaves ownership unknown.

### Features

- ✅ **Automated Duty Queueing**: Automatically queues for Praetorium (1-99) and Decumana (100+)
- ✅ **Daily Reset Detection**: Auto-resets counters at 7 AM UTC (3 AM EST / 12 AM PST)
- ✅ **Repair Management**: Self-repair with dark matter or NPC repair automation
- ✅ **Food Management**: Auto-consume food when buff expires
- ✅ **Combat Integration**: Select one of BMR, VBM, Rotation Solver Reborn, or Wrath Combo
- ✅ **Filtered Statistics**: Unsynced testing runs stay out of stats unless you explicitly opt in
- ✅ **Persistent Run Counters**: Duty counters and summary stats save against the active account after login and duty completion
- ✅ **Stuck Detection**: Auto-recovery from stuck states
- ✅ **Boss Mechanics**: Tank mitigation, potion usage, limit breaks
- ✅ **Per-Character Configuration**: Separate settings for each character
- ✅ **ADS-primary setup wizard**: Per-account guided setup with an explicit AutoDuty alternative
- ⚠️ **Experimental Praetorium first-room opener**: Optional ADS-only local terminal sequence with immediate ADS fallback

---

## Requirements

### Required Plugins
- **Selected duty backend** - **ADS is the primary backend**, or AutoDuty plus the selected Praetorium path as the alternative
- **Selected combat provider** - BMR, VBM, Rotation Solver Reborn, or Wrath Combo
- **vnavmesh** - Navigation and pathfinding
- **XA Slave** - Provides `/xa skipcutscenes on` startup control
- **YesAlready** - Auto-confirm dialogs
### Optional Plugins
- **ADS (AI Duty Solver)** - Optional in AutoDuty mode; enables `/mog inn` delegation
- **Lifestream** and **TextAdvance** - Optional; neither is required for MOGTOME setup

### Game Configuration (MANDATORY)
- **NOT in controller mode** (causes chat spam)
- **Duty Finder**: Unrestricted Party + Level Sync enabled
- **XA Slave**: Installed and loaded. MOGTOME runs `/xa skipcutscenes on` before each manual start
- **AutoDuty**: "Leave Duty" disabled OR "Only when duty complete"
- **YesAlready**: Configured for repair, exit, sealed area dialogs

See [Configuration](#configuration) for the per-account Setup Wizard.

---

## Installation

### Development Version

1. Clone this repository
2. Open `MOGTOME.sln` in Visual Studio 2022 or JetBrains Rider
3. Build the solution (Debug or Release)
4. Add the DLL path to Dalamud Dev Plugin Locations:
   - `/xlsettings` → Experimental → Dev Plugin Locations
   - Add: `D:\temp\MOGTOME\MOGTOME\bin\x64\Debug\MOGTOME.dll`
5. Enable in `/xlplugins` → Dev Tools → Installed Dev Plugins

### Release Version

1. Open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**
2. Add `https://aethertek.io/x.json`, ensure the repository is enabled, and save
3. Open `/xlplugins` → **All Plugins** → Search for "MOGTOME"
4. Click **Install** and enable the plugin

---

## Usage

### Interface language

**Transparency** applies to the complete plugin window, including its titlebar and popups. Settings provides normal opacity, automatic focus fade, faded opacity and delay; defaults are 100%, fading to 50% after 10 seconds without focus and restoring on focus. The first load of this update applies Compact mode once per account and hides Main's Compact and Transparency controls. Settings retains density, transparency and independent Compact/Transparency/Language visibility choices; subsequent loads preserve your changes.

Main's titlebar opens Settings or Statistics and provides Start, Stop and Stop after the next successful run, with current readiness checks. Main and Blunderville Mini use the existing packaged icon in branding/titlebars, including when collapsed. Blunderville provides readiness-gated Start, Buy and Stop actions; starting also requires a configured session limit.

The main window and Settings share a colour swatch and **Language** selector with **English**, **Français**, **Deutsch**, **日本語**, **Español**, **Italiano**, **Русский**, **한국어**, **简体中文**, **Tiếng Việt**, **Português (Brasil)**, **Bahasa Indonesia**, **Polski**, **Türkçe**, and **हिन्दी**. The choice applies on the next UI frame, including while automation is running, and is saved in the active account profile. New profiles and profiles without a saved choice start with the game client's language. Switching profiles restores that profile's choice. Before a profile is ready, MOGTOME displays the client language and disables appearance editing. Existing saved language values and account files retain their meaning.

Hindi is enabled only when the local font check passes. Otherwise the selector shows disabled **Hindi (unavailable)** while other languages remain usable. A saved Hindi choice that fails its required-font check shows an English status and **Use English**; that button explicitly saves English in the active account profile once the profile is ready. Language choices never change automatically because a font is unavailable.

The **C** checkbox selects compact mode for all windows; Settings also exposes its full label. The colour selector provides teal, blue, pink, and custom RGB choices. Colours update the whole theme relative to the approved gold default while retaining warning and unavailable-state meanings. Main uses a status panel, adaptive action rows, duty and observed-party cards, retained combat settings, and collapsible diagnostics. Blunderville shows its current status, cycle count and wallet alongside its farming and purchase settings. Statistics, Settings, and warnings retain their existing layouts.

The interface language is independent of game-text recognition. For example, French interface text on a Japanese client still recognizes Japanese prompts. Duty, item, job, and NPC labels use the selected interface language when the game provides that language; Spanish, Italian, Russian, Korean, Simplified Chinese, Vietnamese, Brazilian Portuguese, Indonesian, Polish, Turkish, and Hindi UI choices use the existing English game sheets. Item searches refresh when the choice changes. Commands, external plugin identifiers, logs, IPC output, and historical run records retain their existing representation.

Automation uses numeric boss, action, and innkeeper IDs and evaluated client-language game prompts. Unknown or unresolved prompts are ignored. Raises and sealed-area moves remain immediate. The per-account **Return to entrance delay (seconds)** in the **Duty** settings tab starts when MogTome detects an eligible death in an active duty, including time while the return prompt is minimized (default: 60 seconds; minimum: 1), with either ADS or AutoDuty. A confirmed full-party wipe bypasses this delay for the current death, even if another member revives first. Confirmation requires a nonempty party with every member positively identified as dead; missing or unknown member data keeps the delay. Once the delay elapses or a wipe is confirmed, a visible recognized Return prompt is accepted on the next normal dialog update. A minimized revive notification is reopened, then the restored prompt is checked on the following update before acceptance. Only actual duty-exit prompts are accepted for leave confirmation.

Hindi uses the consumer-owned Windows text renderer for natural Devanagari shaping in owned windows. Game-owned chat, toast and command-help surfaces retain English for that selection. The current Debug/x64 build and offline Hindi catalog, appearance/save, Main/Blunderville, Settings, Statistics, warning and supplied-state checks pass with retained native identities and font roles at both densities and 100%/150% scales. Managed-host, game, GPU and IME acceptance remain pending. The UI uses managed Segoe UI and symbol fonts with verified host Noto CJK faces. It waits for font readiness and checks required glyphs after atlas rebuilds. AethertekUI.dll and the fourteen satellite resource assemblies are included in the existing plugin artifact flow; host DLLs and fonts are not plugin payloads. The earlier Release build and 230 focused localization tests passed across the original fourteen interface choices and four client languages; that evidence does not cover the added Hindi choice. Automated checks do not establish game-rendered acceptance. Visual verification of regular/compact layouts, selectors, popups, CJK glyphs, and host display scales remains outstanding; no live client was used for this adoption.

### Moogle shopping list

Open **SHOP** on Main, `/mog shop`, or `/mogtome shop`. Opening the window or reloading never travels or spends. Choose the Itinerant Moogle's city (Limsa Lominsa, Ul'dah, or Gridania) and the displayed tomestone type. The current game-sheet exchange is filtered by active festival requirements; archived previous/past catalogs are excluded. City and desired item counts belong to the active account, separately from Blunderville. The tomestone view selection lasts for the current plugin load.

Every offer uses one row: **Item | cart | backpack | $ | $$ | ?**. Cart is the desired total in your four ordinary bags; 0 disables that target. Prices show all required currencies, joined with `+` when necessary; hover a price for their names and the exchange bundle size. Row totals buy the deficit, rounded up to whole bundles. Balances and total outstanding costs appear separately for each currency. Collectible indicators use the same registration/inventory rules as Blunderville. Retainers, saddlebags, housing storage and placed furniture are excluded.

**Hide Owned** defaults off in each shop independently. It hides confirmed owned collectible/equipment rows; confirmed quest, achievement, duty and event restrictions are hidden regardless of that checkbox. Unknown eligibility stays visible with an explanatory tooltip. Filters preserve every desired quantity and its outstanding cost, including hidden targets. Buy stops visibly when a needed offer is locked or its eligibility cannot be verified.

**CLEAR** resets the entire shopping list to zero desired quantities while keeping catalog rows and ownership indicators visible. It preserves the city and any purchase review hold, and is disabled during queued or active automation.

**Select Missing** is manual in both shops. It sets collectibles confirmed unregistered by the game, with zero carried copies and no known stored copy, to at least one. Larger targets and unrelated entries are preserved; selection does not buy or travel. Unavailable native registration stays skipped. Known XADB copies take precedence, but incomplete storage does not prevent manual selection of an unregistered collectible; inspect stored copies before buying if their data is unavailable. Equipment still requires confirmed ownership absence and a verified PvP source. PvP sources come from the game's Wolf Mark/Trophy Crystal offers; other equipment without verified acquisition evidence is skipped, including dungeon drops, and remains selectable by hand. The result reports unknown ownership, equipment source and eligibility separately. Hide Owned does not change the saved list.

**Buy** starts travel through Lifestream and approaches the Moogle through vnavmesh. It cannot run alongside duty farming or Blunderville. A manual retry can resume the Moogle's open menu after validating the trader, configured city and exact current exchange. Each transaction validates the native item, reward quantity and every currency cost, checks funds/capacity, and confirms both acquisition and spending before continuing. Multiple partial stacks of the selected tomestone are merged up to 999 with the trader closed; each move verifies both slots and the conserved total before reopening and revalidating the offer. Unsupported or changed UI, missing inventory truth, insufficient funds/capacity, or an uncertain result stops visibly and retains targets. Review an uncertain purchase/stack move in your bags before clearing its saved hold. Stop, logout and unload cancel owned shopping, movement and travel; successful shopping stays at the trader.

### Blunderville

Open `/bv` or `/blunderville`. Their `start`, `stop`, and `buy` actions share the window controls. Blunderville travel requires Lifestream; NPC approach uses vnavmesh. Blunderville and ordinary duty farming cannot run together. Settings belong to the active account in a separate Blunderville section; ordinary party preferences and duty statistics are unchanged.

Enable a run limit, an MGF wallet target, or both. Either enabled limit finishes farming. Defaults are one run and no wallet target. Leadership comes from the current party, including cross-world parties; solo characters lead. Leaders travel through the Gold Saucer to the Square and register; members wait where they are and accept entry. Everyone waits without moving until elimination and uses the spectator exit. Start inside Blunderville resumes this idle/exit flow in a new session, including after Stop or reload; other duties remain unavailable. Only confirmed elimination-and-return cycles count. A member meeting its wallet target withdraws and leaves the party before shopping; a run limit preserves party membership. Each client keeps its own targets and progress.

If registration is rejected because a party member is changing areas, farming retries only after confirming the complete game message. Retries share the first registration's 90-second deadline and recheck leadership, limits and queue state. Stop cancels pending recovery; other errors do not trigger retries.

The grid shows every current MGF-trader offer, one item per row. Its headers are **Item | cart | backpack | $ | $$ | ?**: item name, editable desired inventory count, on-hand count, unit MGF price, remaining row cost and registration status. Hover a header for details. Set the cart quantity to 0 to disable purchasing that item; its row stays visible. **CLEAR** resets all Blunderville desired quantities without changing farming limits, automatic shopping, ending location or a purchase hold. The total needed appears below; already-satisfied targets contribute zero. Collectible items show a green check when registered or carried in inventory, a yellow `?` for an outstanding purchase, or a red X when confirmed missing; unavailable truth shows a neutral `?`, and nonregistrable items leave the status blank. These indicators do not change your quantities. Counts use the four ordinary inventory bags, excluding retainers, saddlebags, housing storage, and placed furniture. **Buy** purchases only outstanding quantities while idle. Each transaction validates the live offer, MGF and bag capacity, then checks both acquisition and spending. Unavailable inventory, unexpected UI, insufficient funds/capacity, or uncertain results stops shopping and preserves targets. An uncertain transaction retains a purchase hold across reloads: inspect inventory and MGF before using its acknowledgement control.

Square entry requires progressing through **Just Crowning Around**, starting with Lewena at Entrance Square. The attendant's unlock denial stops visibly; automation does not undertake that quest. Square travel confirms only the owned prompt matching the client's current game sheet.

**Shop when farming finishes** defaults off. **Ending location after shopping** defaults to **Don't go anywhere**, with the seven named inn destinations available separately. Ending travel follows successful shopping, including an already-satisfied list, and does not request repairs. Stop cancels owned farming, shopping, pending reload actions and ending travel; it never starts shopping.

Use `/blunderville debug` (or `/bv debug`) to toggle the hidden reload scenario controls. They start hidden on each plugin load. The optional reload scenario defaults to none. It runs once after account/client readiness on the next plugin load. Changing its selection or stopping cancels pending dispatch; changing the selection does not run it immediately. Development attempts log a distinct compiled marker without changing the release version; the expected marker and runtime results are tracked in [TODO.md](TODO.md).

R: development bundle: `R:\XIVLauncher\devPlugins\MOGTOME-Blunderville\MOGTOME.dll`. Register the client-local path `A:\ff14\XIVLauncher\devPlugins\MOGTOME-Blunderville\MOGTOME.dll` through Dalamud's development UI and disable other MOGTOME load locations. Runtime verification and remaining party/visual acceptance checks are tracked in [TODO.md](TODO.md).

### Starting the Bot

Choose the combat provider in Settings: BMR or VBM handles attacks and movement; RSR handles attacks with the loaded BossMod variant for avoidance; Wrath uses its current attack settings. BMR/VBM can use an existing named preset with **Use manual BossMod preset** enabled, or MOGTOME's packaged preset selected by job role when disabled. The per-account Setup Wizard reviews the selected backend, provider, dependencies and party setup before applying its draft.

1. Ensure you're outside a duty
2. Open MOGTOME: `/mogtome`
3. Click **Start** button
4. MOGTOME sends `/xa skipcutscenes on`, then queues, runs duties, repairs, and consumes food automatically

### Configuration

1. Open config: `/mogtome config`
2. Complete the per-account **Setup Wizard**. It guides backend, combat provider, required checks, party setup, optional settings, and review.
3. Configure any remaining duty counter, repair threshold, food, and potion settings.
4. Settings save automatically. The wizard is advisory and does not block Start or install/configure other plugins.

### Experimental first-room skip

In **Advanced**, `Experimental first room skip` is off by default and is active only with ADS in The Praetorium. Each participating party client must enable it locally; no party synchronization is performed.

- Tanks move through the opening pull, use the mapped AoE and invulnerability, then use Magitek Terminal `2012811`.
- Non-tanks wait one second, then move to that terminal.
- MOGTOME retries the terminal until the local player descends, for up to ten seconds total. On success or any unavailable action, missing target/terminal, movement failure, or timeout, it stops only MOGTOME's vnavmesh movement and immediately hands the duty to ADS.

This sequence is experimental and has not been live-duty validated by this implementation pass.

### Commands

- `/mogtome` - Open status window
- `/mog config` - Open configuration window
- `/mog start` - Start the bot
- `/mog stop` - Stop the bot
- `/mog inn` - Delegate inn entry to ADS with `/ads enterinn`
- `/mog status` - Print current status

---

## How It Works

1. **Queue Phase**: Queues for The Praetorium (runs 1-99) or The Porta Decumana (runs 100+)
2. **Duty Phase**: ADS is the primary selected duty backend; AutoDuty remains an alternative. The selected combat provider handles combat.
3. **Boss Mechanics**: Automatic tank mitigation, potion usage, limit breaks
4. **Completion**: Calculates completion time, leaves duty, increments counter
5. **Maintenance**: Auto-repair when threshold reached, auto-consume food when buff expires
6. **Daily Reset**: Auto-resets counter at 7 AM UTC

---

## Recommended Party Composition

### Standard (Balanced)
- WAR + SCH + 2 DPS

### Fast Clears (9:50-10:00)
- GNB + 3 MCH

### Notes
- GNB has best survivability with automation
- MCH has highest DPS for level 50 content
- SCH > SGE for healer DPS

---

## Known Issues

1. **AutoDuty Path Corruption**: Use 1 client per Dalamud folder
2. **Movement Type Change**: Game sometimes switches Legacy ↔ Standard
3. **Job Change Issues**: `/xlkill` and restart client if issues occur
4. **YesAlready Disabled**: Ensure YesAlready is enabled before starting

---

## Development

### Project Structure

```
MOGTOME/
├── MOGTOME/
│   ├── Models/          # Data models
│   ├── Services/        # Core services
│   ├── Windows/         # UI windows
│   ├── IPC/            # Plugin IPC integrations
│   ├── Configuration.cs
│   └── Plugin.cs
├── PROJECT_GAMEPLAN.md  # Detailed project plan
├── CHANGELOG.md         # Version history
└── README.md           # This file
```

### Building

```bash
dotnet build -c Debug   # Debug build
dotnet build -c Release # Release build
```

### Contributing

See [PROJECT_GAMEPLAN.md](PROJECT_GAMEPLAN.md) for detailed technical documentation and implementation phases.

---

## Credits

- **Original Script**: G.O.O.N. (Generally Ordered Optimized Navigation) by dhogGPT
- **Inspiration**: @Akasha, @Ritsuko for ideas and code
- **Dependencies**: Selected duty backend, vnavmesh, XA Slave, YesAlready, and selected combat provider

---

## License

This project is licensed under the same terms as the Dalamud Plugin Template.

---

## Support

For issues, bugs, or feature requests:
1. Check CHANGELOG.md for recent changes
2. Review PROJECT_GAMEPLAN.md for technical details
3. Check Dalamud log: `/xllog`
4. Report issues with full error logs

Settings > Advanced includes **Copy / ZIP Dalamud log**. It creates one manual snapshot and opens the export folder. At or above 100 MiB, it warns before export because logging may have stopped and recent activity may be missing. Share the ZIP manually and remove exports when finished with them.

---

**Happy tome farming!** 🎮

When XA Slave is loaded, **Open XA Slave log tools** opens its **Utility > XA Mods** panel, which contains Dalamud Log Cleaner. The existing **Copy / ZIP Dalamud log** action remains separate. Opening the panel does not run cleanup or change XA Slave settings.
