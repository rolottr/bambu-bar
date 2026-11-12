# Bambu Bar desktop widgets (Rainmeter 5.0 skins)

The Small, Medium and Large widgets, drawn by the private Rainmeter 5.0 the app bundles (ledger D3, D4). The app
writes what to show (`windows/docs/widget-state.md`, contract v1); these skins only lay it out and animate it.

```
BambuBar/
  Small/Small.ini  Medium/Medium.ini  Large/Large.ini   one skin per size; meters declared, nothing else
  @Resources/
    Variables.inc   StateFile default, margin; includes App.inc and Styles.inc
    App.inc         BambuBarExe: the app rewrites this file with its own path (P2.6)
    Styles.inc      the @Inherit sections every meter starts from (hidden, in the Content container)
    Head.inc        script, animation timer, shadow, card, content container
    Tail.inc        finish pulse, text probes
    Scripts/widget.lua   all the logic: pure functions (BB.*) + the Rainmeter glue. UTF-8 with BOM (required)
    Scripts/json.lua     a small JSON decoder written for this
    Icons/*.svg     Fluent UI System Icons (MIT, LICENSE.txt) + heat, fan, printer (ours)
    Fonts/          Nunito SemiBold and Bold (SIL OFL 1.1, OFL.txt); Rainmeter loads @Resources\Fonts itself
    Images/         shadow-<Size>.png, drawn by rainmeter/tests/make_shadows.py
```

## Instance stubs (written by the app)

Each widget on the desktop is a config `BambuBar\W<n>` holding one `Widget.ini` of three lines:

```ini
[Variables]
@Include=#@#..\Medium\Medium.ini
StateFile=C:\Users\<user>\AppData\Local\Bambu Bar\Widgets\State\01P00A123456789.json
```

- Line 2 picks the size (`Small`, `Medium` or `Large`). `#@#` is `BambuBar\@Resources` for every config under
  `BambuBar`.
- Line 3 is the absolute path of the state file: `<SERIAL>.json` for a widget set to a printer, `default.json` to
  follow the tray's printer. It must come **after** the `@Include`: the included skin sets a default, and the
  later value wins. Art files are read from the same folder.
- `#CURRENTCONFIG#` (`BambuBar\W<n>`) is the instance name the right-click passes to the app.

A size skin loaded on its own reads `#@#..\..\..\State\default.json`, i.e. `Widgets\State` next to `Widgets\Skins`.

`@Resources\App.inc` is the other file the app writes, once per install or update:

```ini
[Variables]
BambuBarExe=C:\Users\<user>\AppData\Local\Programs\Bambu Bar\BambuBar.exe
```

## Clicks (P2.5)

- Left click on the card: the state's `url` (`bambubar://printer/<serial>`, or `bambubar://open` when no printer is
  set up), which the script writes into the card's `LeftMouseUpAction`.
- Right click anywhere: `["#BambuBarExe#" --widget-menu "#CURRENTCONFIG#"]`, a skin-level `RightMouseUpAction`.
  Defining it keeps Rainmeter's own menu away (Ctrl+right-click still opens it, as accepted in the ledger).

## Motion and hover

All driven by `MeasureAnimation` (built-in ActionTimer): list 1 steps every 16 ms while something moves, list 2
every 50 ms while connecting. Nothing runs between changes; the skin updates once a second. With `motion: false`
every change is instant and the connecting arc is static.

| What | When | How long |
| --- | --- | --- |
| Content fades out, swaps, fades in | `link`, `live`, `state` or `canceled` changes | 150 ms + 180 ms |
| Ring, bar and percentage tween (ease-out) | after the swap, or when progress jumps by 2 % or more | 450 ms |
| Finish pulse (ring on Small, status dot on Medium/Large) | `state` becomes `finished`, after the tween | 700 ms |
| Art crossfade | `art` changes | 300 ms |
| Chamber light crossfade | new `art` is the old `artUnlit` (or the reverse) | 400 ms |
| Connecting arc turns | while `link` is `connecting` | 1.1 s a turn, ~20 fps |
| Hover: lighter gradient, border 8 % → 22 %, hand cursor | mouse over the card | 140 ms |

Spools and temperature tiles carry their `tooltip` (or label, value and target) as `ToolTipText`.

## Tests

`rainmeter/tests/run.sh` runs the Lua with LuaJIT against `rainmeter/samples` (stubbed Rainmeter, approximate
text measurement): JSON, countdown, projection, staleness, change detection, every state in every size, the
glue's animations and motion off, and that every meter the script sets is declared in the skins.
