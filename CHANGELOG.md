2026-10-08 - Dedicated Window appearance settings (I505)

- Move colour, language, compact mode and transparency controls into their own settings tab or sidebar page. Retain the existing controls, native IDs, saved preferences and actions; keep normal settings visible without an appearance block above them. Versions and client configuration are unchanged. Local build checks and game visual acceptance are recorded separately in the selected task checkpoint.

2026-10-08 - Shopping list CLEAR and Moogle menu fix

- Add CLEAR to the Moogle and Blunderville lists. Reset only desired quantities for the active account, retain every catalog row and ownership indicator, and disable clearing during queued or active automation.
- Handle the Moogle's native icon-choice menu as well as plain choices, select only the exact current exchange, and cancel through the translated Cancel option. A manual Buy can resume a recognized trader menu; unchanged menus time out without repeated selection or interaction.
- Read displayed menu text from the custom script sheet's text column and wait for menu readiness. Validate fixed-quantity confirmations through their native item, reward quantity and currency amounts instead of requiring a quantity editor.

2026-10-08 - GitHub Actions Backpack dependency

- Publish the shared Backpack icon used by shopping-grid headers before MOGTOME builds. Retain the read-only, non-expiring SSH library checkout.

2026-10-08 - Moogle shopping list (development)

- Add Main's SHOP button and `/mog shop` / `/mogtome shop` for a separate account-scoped Moogle shopping list with a configurable Limsa, Ul'dah or Gridania trader.
- Show the current festival-eligible exchange in the accepted single-row item/cart/backpack/price/total/registration grid, retaining every literal currency cost and bag balance. Buy fills deficits in whole exchange bundles; opening and reload remain passive.
- Validate native offers, funds, capacity and exact acquisition/spending before continuing. Close the trader to consolidate partial tomestone stacks, verify one native move at a time, and retain a review hold on uncertainty. Stop/logout/unload cancel owned actions; ordinary farming, Blunderville and Moogle shopping remain exclusive.
- Live purchase and repeat-Buy receipts are recorded in TODO.md. Stack consolidation and other unexercised cases remain pending separately.

2026-10-08 - GitHub Actions shared-library repair

- Build against published AethertekUI main so current shared APIs are available. Retain repository-specific read-only SSH deploy keys, which do not expire, and disabled credential persistence. Publish library APIs before consumer changes.

2026-10-07 - Packaged image branding and operator guidance (I500/I497/I499)

- Use the existing packaged icon in Main and Blunderville Mini branding/titlebars with aspect-ratio fitting and a stable reserved box. Preserve native titlebar controls, saved geometry, motion and complete-window opacity.
- Refresh concise README guidance for appearance, focus fade, titlebar shortcuts and this plugin's existing setup/automation controls.
- Probe the optional Hindi menu caption once per existing font generation at the original locked font sizes. Keep native indexed option identities while disabling unavailable Hindi with an ASCII caption; retain selected-catalog checks and explicit ASCII failure status with Use English saved through the active profile (I499).

2026-10-07 - Blunderville farming and MGF targets (I501, development)

- Show the complete MGF catalog in one scrollable grid, removing Add item. Use Item/cart/backpack/$/$$/? headers with localized tooltips, editable desired counts and a separate registration column. Zero disables that item without hiding its row; preserve saved targets and missing catalog entries. Add the backpack vector icon to AethertekUI and its existing showcase.
- Show collectible ownership beside shop item names and in the picker using ADS loot's action categories and native registration check: green check for registered or carried items, yellow question mark for an outstanding selected purchase, red X for confirmed missing items, and a neutral question mark when truth is unavailable. Keep each item on one row and leave purchase targets unchanged.
- Size the Blunderville header to its actual content and use the normal text/icon height for Open MOGTOME, removing the reserved vertical gap before the status and controls in both densities.
- Hide reload scenario controls until `/blunderville debug` or `/bv debug` is used, and replace the obsolete farming-unavailable heading in all fifteen catalogs.
- Implement separate account-owned Blunderville farming limits, actual solo/party/cross-world leadership, stationary member entry, idle elimination/spectator exit and independent session cycles. Either enabled run/wallet limit finishes; member wallet completion withdraws and leaves the party.
- Add sheet-derived MGF purchase targets with carried-bag counts, live item/quantity/price validation, funds/capacity checks and inventory-plus-currency verification. Stop on uncertainty, retaining targets and a purchase hold across reloads.
- Ignore empty game-sheet receive slots even when their default quantity is one, so valid MGF trader offers appear in the item/count controls. Preserve rejection of multiple real rewards or extra currencies.
- Retain all NPC identities sharing a Blunderville name and interact with the closest loaded matching NPC, so a later resident row cannot hide the Gold Saucer attendant.
- Replace the stacked catalog with a searchable item picker and selected-item rows showing editable desired/current inventory, MGF prices, outstanding quantities and removal without horizontal table scrolling. Preserve saved targets, including offers missing from the current catalog; new targets start at current inventory plus one.
- Replace the stacked selected-item rows with an AethertekUI grid: one row per item, editable wanted count, on-hand count, per-item MGF and remaining MGF cost. Show the total needed below; satisfied targets cost zero. Widen the panel for the grid and retain original count-editor identities, item targets and removal actions.
- Handle the owned Square entry confirmation using current client-sheet text, cancel it on Stop, and report the attendant's unlock denial instead of repeatedly interacting until timeout.
- Log registration, entry and spectator-exit milestones once per transition through the existing diagnostics, alongside confirmed cycles and purchase results.
- Keep registration submission separate from Commence acceptance, so registering a run does not suppress its later entry prompt.
- Cancel an owned Blunderville queue through the current native queue API after validating its duty identity, and wait for withdrawal before completion shopping or party departure.
- Allow an explicit Start inside Blunderville to resume idle elimination/exit in a new session after Stop or reload, while retaining the guards for shopping and unrelated duties.
- Close the owned MGF trader window on early validation, inventory, funds or capacity failures; record a bounded native-offer diagnostic through existing logging when validation fails.
- Select the requested exchange category through its registered native click and validate sorted visible rows against global native item indexes, including their exact quantities and MGF prices.
- Wait for the owned trader addon and agent to finish opening before validating their populated rows; bound the readiness wait and report insufficient MGF through existing diagnostics.
- Record owned purchase-confirmation mismatches through existing diagnostics before cancelling the dialog and retaining the purchase review hold.
- Clean up a verified purchase's remaining identified confirmation once and wait for the trader to become ready before refreshing targets and submitting another purchase.
- Submit validated purchase, cancellation and party-leave confirmations through their registered native buttons, so the native UI closes along with the accepted action.
- Keep the existing purchase review hold and dialog pause if an owned purchase confirmation cannot be closed, including after receipt verification.
- Re-fetch and close only an identified cancelled purchase popup if it outlives its shop; use the same GameGui addon lookup for Blunderville visibility, validation and callbacks.
- Use Lifestream's existing local inn shortcut for ending travel, including inn entry, and confirm the chosen room before completion. Stop aborts the owned route; no repair action is requested.
- Allow the inn entry transition to finish after Lifestream becomes idle, keeping the existing travel timeout and single submission.
- Wait for vnavmesh readiness before the first NPC movement after travel, avoiding a move submitted while the new map is loading.
- Add Buy, optional shopping after farming, seven separate inn ending destinations with stay-put default, shared `/bv` and `/blunderville` actions, and opt-in once-per-load scenarios. Preserve ordinary farming exclusivity, statistics, existing branding/titlebars, compact mode, account storage and release 2.0.0.2. Localize controls/statuses in all fifteen catalogs.
- Recheck reload cancellation after cleanup, so a Stop or selection callback cannot dispatch an already-consumed action.
- Keep saved reload scenarios on the ordinary Start/Buy paths after removing the temporary R: acceptance overrides.
- Build, regression results, development-copy state and outstanding R: runtime acceptance are recorded in TODO.md. No live-pass claim is made from source or build results.

# Changelog

## Unreleased - Button sizing (I491)

- Use font-aware Toolbar sizing for ordinary buttons and reduce icon-button padding and main/Blunderville action heights to full text/icon content. Preserve fonts, native IDs/actions, weighted full-width slots and small/dense controls.
- Current Debug/x64 compilation passes. Final actual-product native checks pass 9714 assertions across 32 focused scenes and 112 pointer activations, with integer exit 0 in all 2 routes. Coverage uses English/Hindi captions, original exercised font roles, both densities, 100/150 percent scale and enlarged text; game/GPU acceptance remains separate.

## Unreleased - CJK atlas construction

- Request a 4096 x 4096 managed atlas and merge one bundled Noto CJK face per font role for the selected language, including Simplified and Traditional Chinese aliases. Preserve existing font sizes, glyph ranges, Windows and symbol fonts, and font lifecycle.

## Unreleased - Combat settings and titlebar shortcuts

- Populate the manual BossMod preset selector from the selected provider's complete native catalog. Retain literal names and saved selections when the catalog is unavailable or empty; replace a deleted active manual selection only with a valid displayed preset. Manual controls remain available during a run and notify the existing settings lifecycle.
- Capture supported preset, BMR saved AI selector and preferred-distance originals before owned changes. Confirm writes through native readback, retain the original across later settings changes, and conditionally restore owned fields on cleanup while preserving external edits and provider replacement. Apply changed manual settings without restarting duty progression or reopening terminal combat cleanup.
- Read the native AI config through its zero-argument generic accessor and permit startup from a legitimately unset BMR AI selector. Restore null through a catalog-checked non-null clear argument and exact readback. Retain same-provider cleanup failures for explicit recovery, confirm writes after dispatch exceptions, and preserve independent selector/runtime/distance changes and captured-owner boundaries.
- After an explicitly attempted start proves the captured provider was replaced, report the departed cleanup incomplete and leave the replacement untouched; the next explicit start captures its own baseline. Current ownership/lifecycle and ADS-readiness tests pass 155/155 with no failures/skips; unchanged mogtome.bat builds Debug/x64 with zero warnings/errors. Installed-provider and game acceptance remain user-controlled.
- Add Config, Stats, Start, Stop and Stop after next success shortcuts to the native main titlebar. Recheck existing start and cancellation guards on each click, retain all body controls, and reserve translated title and icon width before window motion.

## Unreleased - Community invite

- Update the existing Discord community action to https://discord.gg/ac6gjDvR8R.

## Unreleased - Recent runs readability

- Remove only the Player and Job columns from Recent runs, retaining captured history, Party details and the separate statistics tabs. Show local date/time in the selected UI culture with a separate Duration column, and wrap the complete status/reason in a bounded column without truncating it or widening the whole table.
- Current unchanged-launcher Debug/x64 build passes with zero warnings/errors. Focused English/Russian native checks pass 6,166 assertions across sixteen scenes; root independently reran Russian. Checks retain cultured local dates, durations, complete wrapped statuses through finite scrolling, Party details/fallbacks and unchanged history/configuration. Fifteen current 750-key catalogs pass. Managed-host/game acceptance remains separate.

## Unreleased - GitHub Actions dependency alignment

- Pin the existing AethertekUI Actions checkout to published revision `6c193cf06ac67f954c549cafc2033ac0efdd630a`, which includes the Hindi text host and renderer. This fixes missing-text-API compilation after a consumer is published before its library; local workflow validation and hosted build results are separate.

## Unreleased - Hindi interface

- Append Hindi to the existing language choices and translate all 744 authored interface and status messages. Retain saved language ordinals, account storage, native controls, command tokens and game actions.
- Shape Hindi through the consumer-owned Windows text host across windows, fields, tabs, tables, tooltips and titles. Keep existing font roles, files, symbols and CJK merges. Game-owned chat, toast and command-help text uses English when Hindi is selected.
- Retain native Combo option identities by hashing the original integer indices; the managed pointer-ID overload has a different hash on x64.
- Debug/x64 source compilation passes with no warnings or errors against the frozen AethertekUI core and adapter. Hindi catalog and native appearance/save checks pass, including the nine original Combo option IDs. Offline Main/Blunderville, Settings, Statistics and warning checks retain complete text ink, all eight font roles, native actions and save/callback behavior in both densities and 100%/150% scales. The retained auxiliary matrix covers 324 cases plus 80 Main state/detail cases. Managed-host, game, GPU and IME acceptance remain separate.

## Unreleased - Window appearance and transparency

- Add the Window appearance settings section with retained colour, compact and language controls, independent main-window visibility preferences, and a main transparency switch. Save opacity/fade preferences through the existing configuration: 100% normal, automatic 50% after ten unfocused seconds by default; clamp opacity to 10 to 100% and delay to nonnegative values. Apply opacity once after native End and motion restoration for each window tree, including chrome, owned content and images. Build/configuration checks and game acceptance remain separate.


## 2026-10-05 - Rounded window chrome and native minimize (source adoption)

- Adopt per-window rounded chrome and animated native minimize/restore through the native lifecycle, including font status. Retain ActionWarning and WarningText's NoCollapse while rounding their chrome; preserve control identities, layout, saved geometry and actions.
- Compilation, native interaction and game acceptance for this source adoption remain pending verification.

## 2026-10-04 - Retained window sizing

- Keep complete Settings tab captions scrollable and numeric fields readable without changing native identities or editing behavior. Place the translated unsynced explanation beneath its original checkbox.
- Reflow statistics navigation and size job/player cards from their actual content. Keep history columns readable through native horizontal scrolling, including full translated headings and status details.
- Scale warning actions and choose the initial warning-text size through the native window lifecycle, retaining explicit-choice, close, callback and acknowledgement-save behavior.

## 2026-10-04 - Header artwork and typography

- Draw the approved moogle header artwork with native meshes, retaining the original embedded icon and Blunderville navigation control identity.
- Use separate regular serif and compact bold sans-serif brand fonts to follow each approved reference. Preserve the six original text-role identities, faces, heights and glyph merges.
- Keep the translated header subtitle on one line and reserve space below adjacent controls when needed. Paint the clean HELP caption while retaining its original native action and callback.

## 2026-10-03 - Readable text and single-line controls

- Fit full translated action labels and icons on one line, moving whole controls to another row when needed. Keep party names, roles, status labels and duty field labels on one line; stack complete fields when they exceed the row.
- Match managed font atlas line heights to the approved text proportions in both densities, and scope paragraph wrapping to explicit text. Show the current assembly version in the main title while retaining its native window ID.

## 2026-10-03 - Final UI review

- Validate the captured managed-font generation so an atlas rebuild during glyph inspection remains pending for the next frame.
- Route the retained `make.bat` alias through the pinned direct-plugin launcher, preserving build arguments and exit status.


## 2026-10-02 - Build and release repair

- Pin GitHub builds to SDK 10.0.201 and pass the downloaded Dalamud library path. Restore and build plugin projects with matching configuration, platform and runtime; stop on restore failure.
- Keep build tokens read-only and release writes in a separate job. Use packaged manifest versions for untagged releases.
- Local launchers build the plugin directly in the pinned environment and return its exit status.

All notable changes to MOGTOME will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### UI adoption
- Adopted the approved regular and compact AethertekUI designs for Main and Blunderville, with real engine status, adaptive action rows, duty and observed-party cards, and retained diagnostic actions. Blunderville farming and purchase automation remain disabled; Settings, Statistics, and warning layouts are retained.
- Added account-owned whole-theme colour selection and shared compact mode through the existing account save path. Appearance selectors are mirrored in Settings, and existing language enum values, native control/window IDs, account data, and version 1.2.0.1 remain intact.
- Extended interface resources to fourteen languages by appending Vietnamese, Brazilian Portuguese, Indonesian, Polish and Turkish after the original nine saved enum values. Include 736 complete phrases and satellite resources per language, retain account-owned save paths and native control IDs, and size the selected language name for its measured text. Typed runtime details remain localizable; raw values and client-language game recognition stay independent. Unsupported game-sheet UI languages retain English game names.
- Added managed Segoe/symbol/host Noto CJK fonts with atlas readiness and required-glyph checks, and included AethertekUI.dll plus all satellite resources in plugin artifacts. The Release build and 230 focused localization tests pass; game visual acceptance remains outstanding, and no live client was used.

### Added
- Added the pink Blunderville Failer preview via `/blunderville`, `/bv`, and main-window navigation, with a moogle button back to MOGTOME. Farming Start/Stop and Configure purchases remain disabled, with unavailable-preview labels in all four languages. No farming, ADS purchases, or saved settings are added; runtime appearance remains unverified.
- Added seven localized ADS NPC repair + inn room destinations: Ul’dah, Gridania, Limsa Lominsa, Ishgard, Crystarium, Old Sharlayan, and Tuliyollal. Existing selections, the default, configuration version/migration, thresholds, and AutoDuty behavior are unchanged. Destination handoffs use ADS.StartRepair and wait for its terminal room-entry result; unsupported or rejected requests fail without substituting a destination.
- Added the saved, per-account **Obstacle maps on** switch in Advanced, defaulting to off, with English, French, German, and Japanese labels and tooltips. Accepted startup sends the selected state to BossMod Reborn on the framework thread after readiness/reload and rotation setup, regardless of combat provider. VBM is unaffected; Stop does not restore the setting.
- Added a per-account return-to-entrance delay in the Duty settings tab, translated into all four interface languages. Defaults to 60 seconds for new and existing profiles, with a one-second minimum, for both ADS and AutoDuty.
- Added English, French, German, and Japanese interface resources and a main-window language selector saved per account profile. Profiles without a selection initialize from the game client language; the interface choice remains independent of game-text recognition and can change during automation.
- Added regression coverage for all 16 client/interface language combinations, profile persistence and switching, resource keys and formatting, localized prompts and queue errors, inn identity, and native combat action selection. Live duty/backend and visual smoke tests remain outstanding. Version remains 0.3.1.0.

### Changed
- Build only the plugin project in GitHub Actions so test and regression projects do not block production artifacts.

- Made the per-account party-size restriction configurable from 1–4 members with a slider beside the existing checkbox, defaulting to 4 and preserving its saved enabled/disabled state. A logged-in solo player counts as one; same-world/synced-mode boundaries and all other queue guards, including forced queues, remain in place. Updated settings, waiting status, and diagnostics, with consistent English, French, German, and Japanese interface resources.
- Replaced the ADS self-repair checkbox with NPC, Self, and NPC repair + inn room choices. New profiles default to inn return; configuration version 2 migrates legacy NPC profiles to inn return once while retaining Self, repair thresholds, disabled repair, AutoDuty, and unrelated settings. Later selections persist across reloads. Inn repair waits for ADS utility completion before retries or duty queues resume.
- Every accepted Start, including DAD starts, disables loaded QSTCompanion through the existing plugin-disable flow and sends `/healbot off` when HealBot (Coppelia) is loaded. Stop leaves both off.
- Replaced English boss/action matching with numeric NPC and action IDs, including GeneralAction 3 for Limit Break. Innkeepers use event-NPC base and spawned-object IDs; inn territories use their intended-use classification.
- Match evaluated Addon and LogMessage text in the client language, refusing unknown or unresolved prompts and vote-abandon confirmation. Preserve immediate raises/sealed-area movement, the configured return delay, queue recovery gates, and party-requirements advice.
- Translate windows, wizard, warnings, statuses, and ordinary chat/toast messages. Resolve displayed game names and item searches in the interface language, preserve English IPC/diagnostics, use stable control/window IDs, and include translation assemblies in the existing artifact-copy flow.

### Fixed
- Send `/bmrai forbidactions off` immediately before enabling BMR AI, including BMR movement alongside RSR, so the selected preset can move into attack range while preserving availability checks, stop guards, passive presets, and dodge clearance.
- Apply DDUCK's 1.5-yalm dodge clearance through Medium cushioning in all six packaged presets and before BMR activation, including BMR movement alongside RSR.
- Bypass the return-to-entrance delay after a confirmed full-party wipe for ADS and AutoDuty, retaining the wipe until the local death ends. Visible Return prompts are accepted on the next normal dialog update; minimized prompts reopen and are checked on the following update. Individual deaths keep the configured delay.
- Accepted manual and DAD starts restore the approved current-job RSR healing settings after startup cleanup and checks, before rotation activation, replacing customized thresholds. IPC failures are logged without blocking Start.
- Correct the one-based Duty Finder selection callback so Praetorium and Decumana selection and deselection target the duty found by ID.
- Select Praetorium and Decumana by duty ID in the current Duty Finder list so reversing its sort order no longer breaks ADS queue selection.
- Removed the outdated pre-1.0 version claim from the warning-window heading in all four interface languages.
- Recover the full selected combat setup after death, raises, and entrance respawns using continuous readiness and the existing retry delay, while preserving confirmed ADS/AutoDuty ownership and run counters.
- Treat timed-out and incomplete duty exits as aborted attempts, keep Mogtome running through party departure and requeue, and count success only after a matching verified completion and confirmed exit. Stop-after-next survives failures; the daily limit and its Quit Command apply only to successful Praetorium clears.
- Cancel queued starts and obsolete startup, repair, queue, and leave callbacks on Stop or session changes. Cleanup continues after individual failures, leave confirmation requires the actual leave-duty prompt, and duty registration rejects additional or mismatched selections.
- Keep uncertain duty identity and logout pending, preserve an in-memory stop reason in status/chat, and label the Praetorium daily limit explicitly.
- Ported FrenRider's continuous duty readiness, live ADS ownership validation, and confirmed-exit handling. ADS and AutoDuty startup now remain pending through loading or combat activation failures and recover without resetting the run or restarting a confirmed backend. Stop, completion, and the experimental opener take precedence over pending startup.
- Fixed AutoDuty path-selection readback throwing an invalid cast for generic dictionaries, preserving selected-path All and other-path None verification.
- Count the configured return delay (60 seconds by default) from detection of an eligible death in an active duty, including time while the prompt is minimized. After the delay, reopen the minimized revive notification and check the restored prompt on the next update before accepting a recognized Return prompt, preserving immediate raises, sealed-area moves, and existing completed-duty leave checks. Combat cleanup failures after duty exit now warn in Echo chat, toast, and log, then continue run recording and requeue handling.
- BST (43) uses the melee role mapping for automatic active and passive BossMod presets. Manual BMR/VBM preset overrides remain in control.
- Restored RSR with automatic passive BossMod support and active role presets for BMR/VBM, preserving manual BMR/VBM preset selection. All six packaged presets are installed after BossMod conflict cleanup, and the selected preset is applied before AI activation in each duty.
- When both BossMod variants are loaded, startup disables VBM and sequentially reloads BMR through native temporary commands, waiting for each completed transition to restore shared IPC. VBM selection changes to BMR and is saved; RSR selection is retained. Cleanup or readiness failure stops startup with a clear reason.
- Combat activation and backend helpers now propagate failures, track enabled components for completion/Stop cleanup, and preserve activation across a later DutyStarted event for the same duty.
- AutoDuty path preparation now requires verified configuration modes, territory/content selection, a valid loaded path index, and a saved job assignment with matching readback. Preparation failure prevents startup from reporting the backend ready; empty runtime path/actions remain valid outside the duty.

### Added
- Added Combat rotation beside the main-window backend setting and above the configuration tabs, sharing the selector and preset controls with the setup wizard. Combat editing is disabled while running, and RSR dependency readiness includes BMR or VBM passive support.
- Added an ADS-only, opt-in experimental Praetorium first-room opener. It maps PLD/WAR/DRK/GNB AoE and invulnerability actions, retries Magitek Terminal `2012811` until local descent, and falls back to ADS within ten seconds on any experimental failure.
- Added a per-account advisory Setup Wizard with ADS as the primary backend, AutoDuty as an alternative, README-aligned required checks, optional settings guidance, completion-version persistence, and a rerun entry point.

### Changed
- Made dependency labels consistent with the ADS-primary workflow: YesAlready is required; Lifestream and TextAdvance are optional.
- Added the Advanced `Experimental first room skip` setting, defaulting to off and inactive outside ADS/Praetorium.

### Fixed
- Refresh all six packaged BossMod presets for BossMod-backed rotations after conflict cleanup at engine startup, including manual-preset selection. Wrath retains its existing combat behavior.
- Excluded unsynced testing/debug runs from summary and detailed statistics unless `Show debug runs` is enabled
- Recomputed JSON summary stats from the same filtered run set used by the stats UI to prevent hidden runs from leaking back in
- Repaired party-size persistence so grouped runs keep their stored party count even after leaving duty
- Rebound duty tracking and run history to the active account configuration after login, fixing duty counters being incremented on a temporary pre-login config.
- Reloaded run history after account selection instead of only during pre-login database migration, so persisted SQLite records hydrate the current session.
- Persisted duty counters and recalculated JSON stats immediately when runs are counted/recorded to reduce data loss if the client exits around duty completion.
- Persisted real per-run death counts from duty polling, split into self, others, and all totals for successful and aborted SQLite run records.

### Changed
- Added SQLite backfill for missing `IsDebugRun` metadata and missing party sizes on existing run records
- Revalidated the BossModReborn dependency repo link against the CombatReborn distribution and kept the existing BMR repo target
- Replaced SkipCutscene and SimpleTweaks setup requirements with XA Slave and run `/xa skipcutscenes on` before manual starts
### Project Setup
- Created project structure and documentation
- Defined comprehensive project gameplan
- Established development workflow
- Created how-to-import guide for users

### Documentation
- PROJECT_GAMEPLAN.md - Complete technical specification
- README.md - User-facing documentation
- how-to-import-plugins.md - Installation and setup guide
- .gitignore - Exclude learning docs and build artifacts

---

## [0.0.0.1] - TBD

### Phase 0: Project Setup
- [ ] Copy SamplePlugin template
- [ ] Rename all references to MOGTOME
- [ ] Update MOGTOME.json metadata
- [ ] Build Debug + Release
- [ ] Test plugin loads in-game

---

## Version History Format

### Added
- New features

### Changed
- Changes to existing functionality

### Deprecated
- Soon-to-be removed features

### Removed
- Removed features

### Fixed
- Bug fixes

### Security
- Security fixes

---

*This changelog will be updated with each phase completion and version release.*
