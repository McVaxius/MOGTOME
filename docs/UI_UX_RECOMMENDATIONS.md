# M.O.G.T.O.M.E. UI/UX Recommendations

**Review date:** 2026-08-18  
**Scope:** UI code review only; no runtime behaviour or implementation changes are included in this document.

## Product goal

Configure a valid duty-farming stack, start it confidently, understand the current run, and recover from a blocked or unsafe state.

## Reviewed surfaces

- `MOGTOME/Windows/MainWindow.cs`
- `MOGTOME/Windows/ConfigWindow.cs`
- `MOGTOME/Windows/StatsWindow.cs`
- `MOGTOME/Windows/ActionWarningWindow.cs`

## What is already working

- Start/Stop, engine state, stop-after-next-run, backend, party refresh, configuration, and statistics are all reachable from the main window.
- Dependency checks cover required, optional, conflicting plugins, paths, and combat-provider readiness.
- Statistics separate summary and detailed views and already support Krangle and unsynced-run filtering.

## Prioritized recommendations

| Priority | Recommendation | Rationale and completion signal |
| --- | --- | --- |
| P0 | Make readiness authoritative at Start. | Replace the advisory-only dependency message with a readiness summary. Keep Start available only when safe, or require an explicit override that names each unresolved blocker. |
| P0 | Use one dominant run action. | Give Start/Stop and current state the visual priority; move Config, Stats, Reset, support links, and party refresh into secondary controls. |
| P0 | Move debug research controls out of the normal main window. | Force path selection, structure logging, tuple inspection, and configuration probes belong behind a session-only Developer Mode. |
| P1 | Make backend choice deliberate. | Show ADS versus AutoDuty as mutually exclusive cards in setup, with dependencies and behavioural differences. On the main window, show the selected backend as status rather than a high-impact checkbox. |
| P1 | Replace vague or alarming labels. | Rename `->!HELP!<-`, `Reset`, and uppercase test actions to specific outcomes such as `Open setup help`, `Reset current run state`, and `Test AutoDuty path discovery`. |
| P1 | Explain blocked actions at the control. | Start, path installation, repair, and backend actions should expose a same-line reason and a targeted fix instead of relying on colour or logs. |
| P2 | Use real tabs and guarded resets in Statistics. | Replace button-simulated Summary/Detailed navigation with tabs and require a confirmation that states exactly which statistics will be deleted. |

## Suggested information hierarchy

1. Readiness and selected backend
2. Primary run control
3. Current duty/run status
4. Next-run and party options
5. Diagnostics hidden by default

## Validation checklist

- A new user can identify the primary action and current blocker within five seconds.
- Every disabled control has a nearby plain-language reason and, when possible, a direct corrective action.
- Healthy, warning, error, running, and disabled states remain distinguishable without colour.
- The UI remains usable at narrow window widths and common Dalamud UI scales without clipped labels or unreachable controls.
- Destructive, global, or high-impact actions identify their scope and require confirmation or provide a safe undo.
- Empty, loading, stale-data, success, partial-success, and failure states each provide an appropriate next action.
- Settings clearly identify whether they apply globally, per account, per character, per preset, or only for the current session.
- Advanced diagnostics are still reachable but do not compete with the everyday workflow.

## Recommended implementation order

1. Implement P0 items and validate the primary workflow plus blocker recovery.
2. Implement P1 information-architecture and configuration improvements.
3. Apply P2 polish, then test at multiple UI scales with both fresh and mature configurations.
