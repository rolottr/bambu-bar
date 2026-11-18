# Widget state, contract v1

The app writes what each desktop widget shows; the Rainmeter skins only draw it (ledger D3). Writer:
`windows/src/BambuBar.Core/Widgets/WidgetState.cs` (`WidgetStateWriter`). Every text is already translated and
every rule (which line, which colour) already applied, by the Mac's `WidgetState` rules. Lua does three things only:
moves the countdown and progress between writes, ages a stale file to "offline", and animates.

## Files

In `%LOCALAPPDATA%\Bambu Bar\Widgets\State\`:

| File | What |
| --- | --- |
| `<SERIAL>.json` | One printer. A widget set to a printer reads this. |
| `default.json` | The tray's printer (or "set up Bambu Bar"). Widgets without a printer of their own read this. |
| `art-<hash>.png` | Printer art, 196 × 232 (2× the Large widget's 98 × 116 slot). Names change with content (ledger §4.15). |

Files are replaced atomically (temp file + move, §4.16); Lua never sees half a file. Unused art is pruned on each write.
The app writes on every change worth showing and at least every 20 s while printing; after a change a person would
notice it also sends `!Refresh` (or `!UpdateMeasure`) to its own Rainmeter by `WM_COPYDATA` (§4.13).

## Fields

camelCase JSON. `null` means "don't show".

| Field | Type | Meaning |
| --- | --- | --- |
| `v` | 1 | Contract version. A skin that sees a higher one shows "Update Bambu Bar". |
| `savedAt` | unix s | When written. Online but older than 30 min → draw as `offline` (the app has stopped). |
| `serial`, `name`, `model` | string | Printer, title, model name. |
| `url` | string | What a left click opens: `bambubar://printer/<serial>`, or `bambubar://open` with no printer set up. |
| `link` | string | `online`, `connecting`, `offline`, `unauthorized`, `unconfigured`. |
| `live` | bool | A live status is shown. False: caption + headline + stateLine + art only. |
| `state`, `canceled` | string, bool | `idle`, `preparing`, `printing`, `paused`, `finished`, `failed`. |
| `caption`, `stateColor` | string, `#RRGGBB` | The upper-case caption and its colour (amber for a rejected code). |
| `headline` | string | The job, or what's going on. |
| `stateLine` | string | The line under it at `savedAt`; while printing Lua rebuilds it from `strings.left`. |
| `detail` | string | Small widget's second line ("Done 14:32 · 170/305"; when not live "No connection", "Open the app"). |
| `info` | string? | Not live: the Large widget's info box ("Enter the access code again in the app"). |
| `finish` | string? | Printing: when it ends ("5:15 PM", "tomorrow 08:10"); show it through `strings.done`. |
| `offline` | object | `{caption, headline, stateLine, detail, info}` to show when the file is stale. |
| `reason` | string? | Paused: why ("Filament ran out"); preparing: the stage. |
| `tint` | `#RRGGBB` | Ring and bar colour (active filament when it reads on dark glass, else state colour). |
| `progress` | 0–100 | Percent at `updatedAt`. |
| `updatedAt`, `endsAt` | unix s? | Lua projects progress linearly from `progress` at `updatedAt` to 100 at `endsAt`, capped at 99 %, only while `printing`. |
| `remainingMinutes` | int | Paused: shown as is. Printing: `ceil((endsAt − now) / 60)`, at least 0. |
| `layers`, `layer`, `totalLayers` | string?, int | "170/305". |
| `temps[]` | `{label, value, target, color, kind}` | Up to three tiles; `kind` picks the glyph (`nozzle`, `bed`, `chamber`, `fan`). |
| `ams` | `{title, humidity}`? | First AMS unit's header ("AMS 2 Pro A", "16%"). |
| `spools[]` | see below | That unit's slots, in order. |
| `activeSpoolLine` | string? | "PLA Silk+ · A4". |
| `issue` | `{text, severity}`? | Most severe issue; `serious` or `common`. |
| `lastJob` | `{name, outcome, ago, took}`? | `ago` is at `savedAt` ("1h 05m ago"). |
| `art`, `artUnlit` | file name? | Art for this state; `artUnlit` the same with the chamber light off (null when already off). |
| `motion` | bool | False when Windows' animation effects are off: no tweens, no pulses, no spinner (a static glyph). |
| `strings` | object | Patterns with `{0}`/`{1}`: `lessThanMinute`, `minutes`, `hoursMinutes`, `daysHours`, `left`, `done`, `last`, and `leftWord` (the word under the Small countdown; empty where the language has none). Plain words: `doneWord` and `stoppedWord` (the Small dial's figure for a finished or stopped print with no `lastJob`). |

`spools[]`: `{label, color, colors[], name, amount, fraction, active, empty, tooltip}`. `colors` has more than one
entry for multicolour spools (draw a multi-stop gradient); `fraction` is 0–1 or null when not measured; `tooltip` is
the hover text.

Countdown in Lua, as the Mac's `Format.duration`: `m ≤ 0` → `lessThanMinute`; `h = m ÷ 60`; `h = 0` → `minutes`
with `{0}=m`; `h ≥ 24` → `daysHours` with `{0}=h÷24`, `{1}=h mod 24`; else `hoursMinutes` with `{0}=h`,
`{1}=m mod 60` as two digits.

## Motion and hover (the "push further" the owner asked for)

Rainmeter 5.0's ActionTimer runs these; all of them stop when `motion` is false.

- **State change** (Lua compares with the previous file): fade the content out 150 ms, swap, fade in 180 ms; tween
  ring and bar from the old to the new progress over 450 ms (ease-out).
- **Finished**: one pulse out of the ring (700 ms) when `state` becomes `finished`.
- **Chamber light**: when the light switches, crossfade `art` and `artUnlit` over 400 ms.
- **Art**: two Image meters crossfade (300 ms) whenever `art` changes, so the part grows smoothly with progress.
- **Connecting**: a rotating arc at about 20 fps, only while `link` is `connecting`.
- **Hover**: the tile lightens and its border goes to 22 % white; spools and temperature tiles show `tooltip`/values
  as tooltips; the cursor is a hand. Left click opens `bambubar://printer/<serial>`; right click runs
  `BambuBar.exe --widget-menu <instance>` (P2.5).
- Between files Lua updates once a second (countdown, progress), and drops to the animation rate only while an
  animation runs.
