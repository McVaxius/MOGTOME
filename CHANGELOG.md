# Changelog

All notable changes to MOGTOME will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Fixed
- Excluded unsynced testing/debug runs from summary and detailed statistics unless `Show debug runs` is enabled
- Recomputed JSON summary stats from the same filtered run set used by the stats UI to prevent hidden runs from leaking back in
- Repaired party-size persistence so grouped runs keep their stored party count even after leaving duty
- Rebound duty tracking and run history to the active account configuration after login, fixing duty counters being incremented on a temporary pre-login config.
- Reloaded run history after account selection instead of only during pre-login database migration, so persisted SQLite records hydrate the current session.
- Persisted duty counters and recalculated JSON stats immediately when runs are counted/recorded to reduce data loss if the client exits around duty completion.

### Changed
- Added SQLite backfill for missing `IsDebugRun` metadata and missing party sizes on existing run records
- Revalidated the BossModReborn dependency repo link against the CombatReborn distribution and kept the existing BMR repo target
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
