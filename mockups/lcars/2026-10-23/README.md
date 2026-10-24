# MOGTOME LCARS concepts

Approved LCARS design concepts. Implementation is selected for the repositories in Z:\myrepos.txt, with the rollout tracked in Z:\!todo\2026-10-22GOATFUCK0002.md. These images are design references, not runtime or test evidence.

- [Main window comparison](01-main-regular-compact.png)
- [Settings and supporting owners](02-settings-mini-and-auxiliary.png)
- [Exact source-checked prompts](PROMPTS.md)

The image links are the planned sheet filenames; root review publishes the final generated artwork. Regular/compact compare spacing at the same readable text size. A separate Mini, wizard, access loader or browser view stays a separate owner. Selected supporting views are shown at useful scale; other source windows and conditional states retain their existing layouts and actions, rather than each receiving an illegible thumbnail. Generated text and sample values are proposals, not test results.

## Source scope

Source commit: `634285b9eab0c7d0529480d4fb6ad9b55c15f334`. Existing dirty paths were inspected by count (1); none are included or changed by this mockup task.

Config draws Advanced before Window appearance even though its premeasurement label array lists the reverse. Main has distinct activity windows rather than activity tabs.

## Verified controls and owners

- MOGTOME/Windows/MainWindow.cs:190 — duty/party panels and Main Window Settings; actions at 301; activity cards at 376.
- MOGTOME/Windows/ConfigWindow.cs:182 — actual drawn tab order; Window appearance at 234.
- MOGTOME/Localization/Strings.resx — English values for Main_Config=Config, Shop_Button=SHOP, Main_StopAfterNextSuccess=Stop After Next Success, Config_RefreshPartyState=Refresh Party State.
- MOGTOME/Windows/StatsWindow.cs; MOGTOME/Windows/MoogleShopWindow.cs — distinct auxiliary owners.

## Existing UI source inventory

- `MOGTOME/Windows/ActionWarningWindow.cs`
- `MOGTOME/Windows/BlundervilleWindow.cs`
- `MOGTOME/Windows/ConfigWindow.cs`
- `MOGTOME/Windows/KeyboundBrawlerWindow.cs`
- `MOGTOME/Windows/MainWindow.cs`
- `MOGTOME/Windows/MoogleShopWindow.cs`
- `MOGTOME/Windows/StatsWindow.cs`
- `MOGTOME/Windows/WarningTextWindow.cs`

## Shared appearance proposal

Auto / Picker / LCARS remain in that order, with LCARS third. Preserve native actions, IDs, saved scope and semantic status colours. Krangle obfuscates identifying text. Proposed shared UI tones default off; titlebar speaker and Settings use the same value, with no volume slider. Browser controls remain in the browser header; existing script-free owners do not gain unsupported timed fade. This appearance contract is approved for the allowlisted rollout; implementation and local verification remain separate from artwork approval.
