# Astronomy II shop preparation

Prepared 2026-10-20 from the extracted game sheets. This adds support ahead of the event; it does not establish a start date.

## Catalog and currency

- Irregular Tomestone of Astronomy II: item **51751**, stack size 999.
- Newest Irregular Tomestone Exchange: SpecialShop **1769929**, RequiredFestival **88**, RequiredFestivalPhase **0**, UseCurrencyType **2**.
- This shop contains **30 Astronomy II offers and 30 retained Astronomy I offers**. The existing catalog reader loads the rows when the festival permits this exchange. Prices, quantities and quest requirements remain game-sheet driven.
- When that active newest exchange contains exactly currencies 51750 and 51751, the farming target uses **51751**. Single-currency catalogs retain their existing behavior; other ambiguous currency combinations remain unresolved.
- Uolon Horn requires **100 Astronomy II + 10 Uolon Horn Tokens (51752)**. The Astronomy I purchase remains a separate offer with its own costs.
- Uolon Horn Tokens are also offered individually for **30 Astronomy II**. They remain separate from the tomestone currency.

## Additional Select Missing equipment

- Include Company Hat **2650** and Company Tabard **2993** (crafted), and Qiqirn Earring **12986** (Metal Gears Revengeance 2).
- Skip Coat of the Daring Duelist **13168**, Coat of the Lost Thief **13180**, Plague Doctor's Coat **13192**, and Plague Bringer's Coat **13198** (Pharos Sirius (Hard) / Saint Mocianne's Arboretum drops). They remain available for manual targets.
- Retain all Astronomy I classifications. Selection still respects owned copies, eligibility, larger desired quantities and unrelated targets.
- Casual Attire Coffer remains a manual target under the existing coffer classification; this change adds equipment source rules only.

## Astronomy II offers

Quantities and prices below are transcribed from SpecialShop, rather than assumed from a web checklist. In this snapshot Dancing Mad - Movement IV costs **3** tomestones.

| Item ID | Reward | Quantity | Astronomy II | Additional cost | Required quest ID |
| --- | --- | ---: | ---: | --- | --- |
| 50440 | Uolon Horn | 1 | 100 | 10 Uolon Horn Token | — |
| 51752 | Uolon Horn Token | 1 | 30 | — | — |
| 23023 | Dodo Horn | 1 | 100 | — | — |
| 33685 | Diamond Gwiber Trumpet | 1 | 50 | — | — |
| 33118 | Promises to Keep Orchestrion Roll | 1 | 50 | — | — |
| 2993 | Company Tabard | 1 | 50 | — | — |
| 2650 | Company Hat | 1 | 30 | — | — |
| 12986 | Qiqirn Earring | 1 | 30 | — | — |
| 22462 | Ballroom Etiquette - Unseemly Exertions | 1 | 30 | — | — |
| 32856 | Drippy | 1 | 30 | — | — |
| 20559 | Ala Mhigan Barding | 1 | 30 | — | — |
| 20550 | Reveling Kamuy Fife | 1 | 30 | — | — |
| 16784 | MGP Platinum Card | 1 | 30 | — | — |
| 38617 | Countertop Coffee Service | 1 | 20 | — | — |
| 38591 | Side-tied Curtain | 1 | 20 | — | — |
| 38592 | Tatami Loft | 1 | 20 | — | — |
| 13198 | Plague Bringer's Coat | 1 | 15 | — | — |
| 13168 | Coat of the Daring Duelist | 1 | 15 | — | — |
| 13180 | Coat of the Lost Thief | 1 | 15 | — | — |
| 13192 | Plague Doctor's Coat | 1 | 15 | — | — |
| 39918 | Conceivably Special Timeworn Map | 1 | 15 | — | — |
| 44349 | Presumably Special Timeworn Map | 1 | 15 | — | — |
| 33144 | Casual Attire Coffer | 1 | 10 | — | — |
| 20243 | M'naago Card | 1 | 10 | — | 67985 |
| 21183 | Qiqirn Meateater Card | 1 | 10 | — | 68353 |
| 22387 | Hancock Card | 1 | 7 | — | 68008 |
| 17524 | Pegasus Colt | 1 | 7 | — | — |
| 23037 | Lunar Barding | 1 | 5 | — | — |
| 24304 | Dancing Mad - Movement IV Orchestrion Roll | 1 | 3 | — | — |
| 25005 | Magicked Prism (Job Mastery) | 10 | 1 | — | — |

## Sources and validation

- [SpecialShop game-sheet snapshot, commit a67c23b](https://github.com/xivapi/ffxiv-datamining/blob/a67c23b00fe8cb254855d06b59845958b55d28f3/csv/en/SpecialShop.csv)
- [Item game sheet](https://github.com/xivapi/ffxiv-datamining/blob/master/csv/en/Item.csv) and [Recipe game sheet](https://github.com/xivapi/ffxiv-datamining/blob/master/csv/en/Recipe.csv): Company Hat recipe 31093; Company Tabard recipe 31156.
- [Company Tabard recipe and acquisition](https://ffxiv.consolegameswiki.com/wiki/Company_Tabard), [Company Hat](https://ffxiv.consolegameswiki.com/wiki/Company_Hat), [Qiqirn Earring FATE source](https://ffxiv.consolegameswiki.com/wiki/Qiqirn_Earring).
- Regression coverage exercises mixed Astronomy I/II offers, Uolon Horn dual-currency costs, token purchase costs, unknown currency combinations, and both events' equipment selection with existing targets.
- Live game and upcoming-event purchase verification remain pending. The event is not activated early and no release version is changed.
