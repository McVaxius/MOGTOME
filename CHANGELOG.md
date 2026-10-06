# Changelog

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
