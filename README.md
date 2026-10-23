# Bambu Bar

![Bambu Bar window touring three pages: the Printer page with a turning 3D P2S printing in gold PLA Silk+ at 65% with time left, controls, temperatures and fans; the Filament page with Bambu Lab spools by colour name and grams left, a SUNLU spool and the external spool; and All Printers, six printers with the paused one first](.github/assets/showcase.webp)

<p align="center">
  <img src=".github/assets/menubar.webp" width="600" alt="Animated menu bar item showing 66% and 54 minutes left, with its panel open: the 3D printer model, progress, temperatures and AMS spools in grams; the print pauses because the filament ran out, then the panel switches to the list of all six printers">
</p>

**Your Bambu Lab printer, one click away in the menu bar.**

Bambu Bar is a native macOS app that shows your 3D printer's progress, temperatures and filament without opening Bambu Studio or Bambu Handy. Glance at the menu bar for percent and time left, click for everything else, or keep a widget on your desktop.

Built with Swift and SwiftUI. No account, no cloud service, no analytics.

[Getting started](#getting-started) · [Features](#features) · [Widgets](#desktop-widgets) · [Compatibility](#compatibility) · [Building](#building-from-source)

> Independent, community-developed software. Not affiliated with or endorsed by Bambu Lab. A P2S is the development printer; other Bambu Lab printers that speak the same LAN protocol (X1, P1, A1, H2) should work.

## Why this app?

The printer is in another room, and the slicer is a big app to keep open just to see how long is left. Bambu Bar keeps the answer in the menu bar: **64% · 1h 02m**. Click it and a panel shows the job, the stage, temperatures, the spool in use and how much filament is left, with pause, stop, light and speed controls right there.

It talks to the printer directly over your network, the same way Bambu Studio does in LAN mode, so it works with the printer still bound to your Bambu account and nothing goes through a cloud.

## Features

| | What you get |
| --- | --- |
| **Menu bar** | A printer glyph that fills as the print grows, with progress and time left beside it (or just one, or only the icon), and a pause or alert mark when the printer needs you. |
| **Panel** | The job, its stage ("Heating hotend", "Paused: filament ran out"), percentage, layers, time left and finish time; nozzle, bed and chamber temperatures; AMS spools with what's left and the humidity; printer errors in plain English. |
| **Camera** | The printer's live view on the Printer page, in a collapsible preview in the panel, and in its own resizable window that can stay on top. It streams only while it's on screen, unless you choose in Settings to keep it connected during prints so it shows at once. X1, H2, P2S and X2D use RTSPS (turn on **LAN Only Liveview** on the printer); P1 and A1 use their JPEG stream. |
| **Grams left** | Bambu Lab spools tell the AMS their net weight, so Bambu Bar shows filament left in grams (≈ 810 g), not just a percentage, and how much you have on hand in total. |
| **Named filament** | Bambu Lab spools show their colour by name (Grass Green, Charcoal). Other spools show the maker and product of the preset you set the slot to on the printer or in Bambu Studio, such as SUNLU PLA Matte. |
| **Controls** | Pause, resume, stop (with a confirmation), print speed (Silent, Standard, Sport, Ludicrous) and a chamber light switch. |
| **3D printer models** | Each printer family is modelled in Blender: the P2S/P1S/X1 enclosure, the wider dual-nozzle H2, the open P1P frame, the A1's arch with its AMS Lite, the A1 mini. In the app it's a live 3D model you can turn with the mouse: the print grows layer by layer in the filament's colour, the bed drops (or the A1's gantry rises) as it goes, the toolhead moves while printing, the AMS spools take their real colours and the chamber light follows the printer's. Widgets use studio renders of the same models. |
| **Knows your model** | What each model has comes from the printer profiles Bambu Lab publishes with Bambu Studio, so you only see what your printer can report: no chamber temperature on an A1 or P1S, no door or chamber light on the A series, both nozzles on an H2D, heated-chamber targets on the H2 and X1E, and the build volume and fans on the Printer page. |
| **Window** | A larger view plus fans, nozzle, door, network, storage and firmware; a Filament page with every spool; a Widgets page that tours every widget size and state. |
| **Many printers** | Bambu Bar watches all of them at once. **All Printers** in the window and the grid button in the panel list every printer, the ones that need you first (failed, paused), then by soonest finish. Switch from the printer name at the top of the panel or the window's sidebar, and choose which one the menu bar shows (or let it pick whichever is busy). |
| **Desktop widgets** | Small, medium and large, one per printer if you like, counting down between updates. |
| **Notifications** | Print finished, failed or canceled, paused (filament runout and the like), new printer errors, and optionally when a print starts. |
| **Easy setup** | Finds printers on your network and reads the access codes from Bambu Studio or OrcaSlicer, on this Mac or from a settings file copied from another one. When the slicer knows several printers, **Add All** sets them all up at once. |
| **Mac integration** | Open at Login, no Dock icon while it lives in the menu bar (the icon and menus appear while its window is open), keyboard shortcuts, and it follows the printer when DHCP gives it a new address. |
| **Updates** | Checks GitHub once a day for a new release (turn it off in Settings) and offers the download; **Check for Updates…** in the app menu asks right away. It never installs anything by itself. |

## Getting started

### Requirements

- **macOS 26 Tahoe or later**, Apple silicon or Intel.
- A Bambu Lab printer on the same local network as the Mac.

### Install and connect

1. Download the latest `Bambu-Bar-<version>-macos.zip` from [Releases](https://github.com/rolottr/bambu-bar/releases), unzip it and move **Bambu Bar.app** to your Applications folder. Or [build it yourself](#building-from-source).
2. Open it. The window lists the printers it finds on your network; pick yours. Not listed? Open **Enter address manually** and type the printer's IP address (printer screen → **Settings → Network**); Bambu Bar reads the serial number from the printer. When macOS asks whether Bambu Bar may find devices on your local network, click **Allow**: without it the app can't see or reach any printer, and it says so with a button to the right switch in System Settings.
3. The **access code** usually fills itself in: if Bambu Studio or OrcaSlicer on this Mac has used the printer, Bambu Bar reads the code from its settings. Slicer on another Mac? Drop that Mac's `~/Library/Application Support/BambuStudio/BambuStudio.conf` onto the setup page. Otherwise read it off the printer: **Settings → Network** on its screen shows the 8-character **Access Code**.
4. Click **Connect**. The menu bar starts showing progress as soon as the printer reports in.

The access code is stored in your keychain and only ever sent to the printer. If it changes later (after a factory reset, say) and the slicer has the new one, Bambu Bar offers to use it.

Closing the window keeps Bambu Bar running in the menu bar without a Dock icon. Opened at login, it starts quietly in the menu bar; open it from Spotlight or the menu bar to see the window. **Add Printer…** in the printer picker or in Settings adds another printer.

### About downloaded builds

Builds on the [Releases](https://github.com/rolottr/bambu-bar/releases) page are **universal, Developer ID signed and notarized by Apple**, so they open normally after you download them. Builds you make yourself without a Developer ID certificate, and the artifacts of the [Actions](https://github.com/rolottr/bambu-bar/actions/workflows/build.yml) workflow, are ad-hoc signed: they run on the Mac that built them, but the desktop widget only loads in a Developer ID signed copy.

### Controlling prints

Reading status works in any mode. Pause, resume, stop and speed changes are accepted only when the printer is in **LAN Only** mode with **Developer Mode** on (printer screen → Settings → Network). That's Bambu Lab's rule for third-party apps; without it the printer refuses the command and Bambu Bar says so (the buttons show a lock). The chamber light works either way.

## Desktop widgets

<p align="center">
  <img src=".github/assets/widgets.webp" width="480" alt="The Bambu Bar widget morphing from small to medium to large while a print plays through printing, paused and finished">
</p>

- **Small:** a progress ring with the percentage, time left, the filament going in, and the finish time and layers. Paused, it says why; finished, how long ago and how long it took; idle, the temperatures and loaded spools.
- **Medium:** the rendered printer with the part growing in the filament's colour and the AMS spools in theirs, the job and its progress, finish time, layers and temperatures.
- **Large:** everything: temperature tiles (the chamber where the model has a sensor, otherwise the second nozzle or the part fan), every AMS spool with the grams left, the newest error in plain words and how the last print went.

To add one, right-click the desktop and choose **Edit Widgets…**, search for **Bambu Bar** and drag the size you want onto the desktop or into Notification Center. With more than one printer, right-click a widget and choose **Edit "Printer"** to pick which printer it shows; a widget left empty follows the menu bar printer.

The widget shows what the app last saw, so keep Bambu Bar running (Open at Login does that). While a print runs, the widget projects progress and time left forward on its own between updates.

## Compatibility

| Printer | Status |
| --- | --- |
| **P2S** | Development printer; verified with firmware **01.03.00.00** and an AMS 2 Pro. |
| **X1, P1, A1, H2 families** | Same LAN protocol and certificate authorities; implemented, not verified on hardware. |

The printer can be in cloud mode or LAN Only mode; controls need LAN Only with Developer Mode (see above). The camera on X1, H2, P2S and X2D printers needs **LAN Only Liveview** turned on in the printer's network settings.

If you try another model, [open an issue](https://github.com/rolottr/bambu-bar/issues) with the model, firmware, macOS version and what worked. Run `--probe` (below) for a report; remove the serial number and access code before posting it.

## How it works

Bambu printers run an MQTT broker on port 8883. Bambu Bar connects to it over TLS 1.2 (the P2S doesn't answer TLS 1.3), signs in as `bblp` with the access code, and accepts the printer only if its certificate chains to Bambu Lab's own certificate authorities, which ship with the app, *and* is issued to the printer's serial number, so no other device on the network can collect the code. It asks once for a full report and then merges the changes the printer sends about once a second.

Printers announce themselves on UDP port 2021; that's how Bambu Bar lists them during setup and notices when one moves to a new address. The widgets read a snapshot the app writes to an app group container on this Mac.

## Privacy and local storage

No account, no analytics, no cloud. Bambu Bar talks to your printers on your local network. Its only other request is the optional daily update check, which reads this project's latest release from the GitHub API and sends nothing about you or your printers.

- **Printers (address, serial, name) and preferences:** local macOS user defaults.
- **Access codes:** your login keychain.
- **Last prints** (for "Last: …" and finish times, only what the app saw while running): `~/Library/Application Support/Bambu Bar/history.json`.
- **Widget snapshot:** `~/Library/Group Containers/<team>.dev.rolottr.BambuBar/printers.json`.

## Building from source

Requirements: Xcode 26 or later and [XcodeGen](https://github.com/yonaskolb/XcodeGen) (`brew install xcodegen`). No third-party Swift packages.

```sh
git clone https://github.com/rolottr/bambu-bar.git
cd bambu-bar
./scripts/build.sh               # build/App.noindex/Bambu Bar.app (universal, release)
./scripts/install.sh             # copy to /Applications and open it
./scripts/install.sh --build     # rebuild first
```

`build.sh` generates the Xcode project from `project.yml`, builds the app and its widget extension, and signs both with the first **Developer ID Application** identity in your keychain (override with `SIGN_IDENTITY=…`; set `TEAM_ID` to match its team, which names the app group the widget shares). Without one the app is signed ad hoc and works without the widget. `CONFIG=Debug` and `ARCHS=arm64` are available. Builds go into a `.noindex` folder so Spotlight and Launchpad only ever find the installed copy.

`./scripts/test.sh` runs the unit tests, which cover the camera's protocol parsing (RTSP, SDP, Digest sign-in, H.264/H.265 packets and the JPEG stream) with recorded bytes.

### Package and notarize a release

```sh
NOTARY_PROFILE=<profile> ./scripts/package.sh
```

This makes a universal release build signed with Developer ID, the Hardened Runtime and a secure timestamp, submits it to Apple's notary service, staples the ticket and checks it with Gatekeeper, then writes `build/Bambu-Bar-<version>-macos.zip` and its SHA-256 checksum. The profile is a [notarytool keychain profile](https://developer.apple.com/documentation/security/customizing-the-notarization-workflow) (`xcrun notarytool store-credentials`). Without `NOTARY_PROFILE` it packages the signed build without notarizing.

To verify a download, run this next to both files:

```sh
shasum -a 256 -c Bambu-Bar-1.0.0-macos.zip.sha256
```

The [GitHub Actions workflow](.github/workflows/build.yml) builds and uploads an ad-hoc signed ZIP on pushes and pull requests. It does not sign, notarize or publish a release.

### Developing without a printer

- `--demo [printing|preparing|paused|finished|failed|idle|offline|unconfigured]` runs a simulated P2S; add `--fleet` for six printers. Nothing is saved and no network is used.
- `scripts/emulator/printer.py` runs a pretend P2S on `127.0.0.1:8883` (needs `brew install mosquitto`): a TLS MQTT broker that replays a real P2S report, then prints, heats and obeys pause, resume, stop, speed and light. Connect with
  `"build/App.noindex/Bambu Bar.app/Contents/MacOS/Bambu Bar" --connect 127.0.0.1 22E00EMULATOR0 12345678 --insecure-tls`
- `scripts/fleet/run.sh` starts a mock fleet of 12 printers in Docker (P2S, P1S, P1P, X1C, X1E, H2D, H2S, H2C, A1, A1 mini; printing, paused, failed, finished, idle and one switched off) and opens Bambu Bar against it with `--emulator`. Add `--import` to start on the Add Printer page and try **Add All**, `SSDP=1` to have the printers announce their names and models, `--stop` to stop it. The fleet writes `scripts/fleet/out/BambuStudio.conf` the way Bambu Studio saves LAN printers. Nothing from an `--emulator` run is saved: not the printers, their codes, the history or the widgets' data.
- `--probe <ip> <serial> <access-code|keychain> [seconds] [--raw-filament]` connects to a printer from Terminal and prints what it reports (`keychain` uses the code Bambu Bar saved).
- `--probe-camera <ip> <serial> <access-code> [seconds] [--model P1S | --jpeg | --rtsp]` opens the camera from Terminal, logs the handshake (certificate, RTSP replies, SDP) and frame rate, and saves the first decoded frame to `$TMPDIR/bambubar-camera.png`. The transport follows the model from `--model` or the serial.
- `scripts/showcase.sh <dir>` screenshots the panel, window and widgets in each demo state; `scripts/make-readme-animations.sh` renders the animations above, offscreen, from the demo printer.
- `scripts/make-hms.py` regenerates the bundled error texts.
- `scripts/make-filaments.py Resources/filaments.json` regenerates the filament presets and Bambu Lab colour names from Bambu Studio.
- `artwork/generate_printers.py` builds the printer models in Blender and writes the 3D models (`Resources/Printers/*.usdz`) and the widget render layers. Run it headless with `Blender -b --factory-startup -P artwork/generate_printers.py` (add shape names such as `enclosed mini` to build only those), or from a running Blender through its MCP add-on.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `⌘1` – `⌘5` | Printer, Filament, Widgets, Settings, About. |
| `⌘R` | Ask the printer for a full report. |
| `⌘L` | Turn the chamber light on or off. |
| `⌘K` | Open the camera window. |
| `⌘,` | Settings. |

The **Printer** menu also switches printers, adds one, and pauses, resumes or stops the print.

## Credits and license

Created by [rolottr](https://github.com/rolottr). Released under the [MIT License](LICENSE).

Protocol details, a real P2S report and the English HMS error texts come from [ha-bambulab](https://github.com/greghesp/ha-bambulab) (MIT), which also provided Bambu Lab's certificate authorities that ship with the app. [Bambuddy](https://github.com/maziggy/bambuddy) and [bambu-cli](https://github.com/tobiasbischoff/bambu-cli) helped map the newer printers, and the access-code import follows [Bambu Studio](https://github.com/bambulab/BambuStudio)'s settings format. Each model's capabilities and build volume follow the printer profiles in Bambu Studio's `resources/printers` and `resources/profiles/BBL/machine`, and filament makers and colour names come from its `resources/profiles/BBL/filament`.

The printer models were built for this project in Blender from published outer dimensions; they are visual approximations, not manufacturer CAD, and carry no logos. The generator is in `artwork/`.

Bambu Lab, Bambu Studio, Bambu Handy, AMS and P2S are trademarks of Bambu Lab. This project is not affiliated with Bambu Lab.
