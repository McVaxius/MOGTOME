# Changelog

All notable changes to MOGTOME will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Fixed
- Fixed AutoDuty path-selection readback throwing an invalid cast for generic dictionaries, preserving selected-path All and other-path None verification.
- Wait 300 seconds before accepting a return-to-start prompt while dead in an active duty, preserving immediate raises, sealed-area moves, and existing completed-duty leave checks. Combat cleanup failures after duty exit now warn in Echo chat, toast, and log, then continue run recording and requeue handling.
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
