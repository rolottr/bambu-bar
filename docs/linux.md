# Bambu Bar on Linux

The Linux port lives in `linux/`. It uses GTK 4/libadwaita on GNOME, Qt/Kirigami on KDE, and the installed Omarchy Quickshell controls on Omarchy, with the existing .NET 10 printer core in `windows/src/BambuBar.Core`. The Mac and Windows apps stay in the same repository and use the same version, assets, certificates, reports and string catalog.

## Install

For release downloads and distribution-specific installation, see [LINUX.md](../LINUX.md). This page records developer setup and historical validation; earlier sections describe the revision tested at that time.

The initial validation used local development builds. The consolidated branch produces GitHub-ready Flatpak and companion assets, but does not publish them. On a GNOME, KDE Plasma or Omarchy desktop with Flatpak and Flathub configured:

```sh
flatpak install --user ./Bambu-Bar-2.2.1-linux-x64.flatpak
flatpak run dev.rolottr.BambuBar
```

Find **Bambu Bar** in the app launcher. Closing the window continues monitoring. Open it again from the launcher, the optional tray icon, or a notification. The tray menu and the launcher offer **Quick view** and **Quit**. `Ctrl+Q` in the main window quits completely; Quick view has its own Quit button.

For the native GNOME top-bar popup, also install the companion bundle:

```sh
gnome-extensions install --force ./bambu-bar@rolottr.dev.shell-extension.zip
# Sign out and back in after the first installation or an update.
gnome-extensions enable bambu-bar@rolottr.dev
```

From a source checkout, `linux/scripts/install-gnome-extension.sh` packages, installs and enables it for the next session. GNOME 50 is validated. Clicking its Bambu Bar icon opens live status, cached printer artwork, progress, temperatures, filament, a printer selector when needed, refresh/settings, camera/light, and state-appropriate print controls. The app still checks capabilities and Developer Mode before executing commands; stop confirmation stays in the app. The extension follows the app's Quick view content preferences and translations.

The extension is an optional integration installed outside the sandbox. The app remains usable through its launcher and standard tray without it. While the native panel is enabled, the generic tray item is passive so there is one Bambu Bar icon. Disabling the extension restores the generic tray. The three-item generic tray menu is a fallback, not the GNOME popup.

```sh
flatpak run dev.rolottr.BambuBar --panel
flatpak run dev.rolottr.BambuBar --quit
flatpak uninstall --user dev.rolottr.BambuBar
```

Updates belong to the package channel; this port does not change the Mac/Windows appcast or replace package-manager files. No remote pushes, uploads, submissions or releases were made while developing it.

## Native desktop frontends

The current implementation supersedes the GTK-on-KDE/Hyprland descriptions and measurements in the historical sections below. The Flatpak remains the printer/credential/decoder backend. GNOME keeps its GTK/libadwaita main window and native Shell extension. KDE main and camera windows use Qt/Kirigami; its Plasma applet supplies the panel popup and desktop widgets. Omarchy main and camera windows use Quickshell and import the installed `Ui` and `Commons` components. The Omarchy plugin supplies an anchored `Ui.PopupCard` and Small/Medium/Large layer-shell desktop cards. There is no GTK positioning-helper warning in these native surfaces.

The native companion is a separate release asset, `Bambu-Bar-native-desktop.tar.gz`, installed on KDE/Omarchy alongside the Flatpak. See [the user installation guide](../LINUX.md) for runtime dependencies and desktop setup. The installer validates toolkit availability before changing files, backs up its files, and installs a user D-Bus activation entry. Omarchy setup adds one bar entry and scoped floating-window rules while preserving the rest of the user's shell/Hyprland configuration. The main Omarchy window starts at 960×700 logical pixels rather than filling a tiled workspace.

The Plasma panel uses its own tooltip and a separate Qt menu window: a redundant QML tooltip could intercept icon clicks, and an embedded context menu could clip at the bottom panel edge. Left-click Quick view, middle-click reopening and the complete right-click menu were checked after correcting both issues.

`linux/native/common/` shares layout and content while `kde/Kit.qml` uses native Qt controls and `omarchy/Kit.qml` uses installed Omarchy buttons, toggles, dropdowns, text fields and confirmation dialogs. All eight pages, multi-printer overview and four lower Printer detail cards are implemented. Main-window camera data travels over an owner-only Unix socket (0700 directory, 0600 socket), never the desktop companion D-Bus API. That public API remains sanitized and actions are allowlisted. Slicer files are read into memory and credentials are saved through Secret Service; no temporary credential files are written. A bounded transport coalesces camera frames/state without discarding lifecycle messages. Each backend/demo has an independent endpoint and native-window lock.

Qt Quick 3D uses the same six shared printer GLBs as the other apps. `linux/scripts/generate-qt-models.py` preconverts them with Qt's `balsam` into the checked-in `shared/Printers/qt-models.tar.gz`; this preserves named parts and independently colored spools on the tested Qt 6.10 runtime. Regenerate that archive when the GLBs change. `linux/tests/native-assets.test.py` verifies source hashes, required nodes and mesh references. Models unload when hidden/minimized or fully offscreen. Motion follows Kirigami’s animation duration or Omarchy’s actual reduced-motion setting. The Qt scene avoids multisample antialiasing to reduce software-rendering cost. Desktop popups/widgets use static printer artwork.

System color mode follows each native desktop. Omarchy watches the actual current-theme directory and reloads its installed color/style components; live Tokyo Night → Catppuccin Latte → Tokyo Night switching was visually exercised in main and popup surfaces. An explicit opposite mode adapts foreground/background and control-state colors locally without changing the desktop. KDE explicit modes use installed Breeze Dark/Light schemes and the `KDE_COLOR_SCHEME_PATH` application integration; a generic Qt color-scheme hint alone did not update Kirigami correctly. Main and camera share their application's palette; shell popups/widgets follow the shell. GTK theme IDs and all earlier appearance preferences remain stored.

New native validation artifacts live under `build/linux/evidence/native/`, separately from the earlier GTK evidence. Real P2S MQTT status, AMS colors and RTSPS camera rendering were exercised in the native KDE and Omarchy windows without sending print/hardware commands. Closing the camera released its stream connection while MQTT monitoring continued. GNOME was checked after a real session restart and keyring unlock. Omarchy was also rebooted with the managed Open at login entry enabled: its background process started automatically, native activation reopened the compact window, and the real MQTT connection returned. The original disabled-autostart preference was then restored. Notification click-to-open and outside-click popup dismissal were exercised in Omarchy. The earlier historical figures below must not be presented as native frontend resource usage.

Current native VM measurements (60-second settled intervals, four logical CPUs, llvmpipe, scale 1):

| Desktop | Cold hidden CPU / RSS | Main Printer CPU / RSS | Warm hidden after 24 visits CPU / RSS |
| --- | --- | --- | --- |
| GNOME | 1.74% / 174.5 MiB | 1.31% / 410.2 MiB | 0.53% / 505.4 MiB |
| KDE | 1.64% / 202.2 MiB | 2.75% / 584.0 MiB | 1.30% / 621.8 MiB |
| Omarchy | 1.32% / 256.3 MiB | 1.54% / 700.2 MiB | 0.71% / 767.0 MiB |

CPU is percent of one logical CPU. Totals include the app's backend/UI/bridge/activation processes; whole desktop shells and compositors are separately recorded. RSS sums double-count shared mappings; PSS and process identities are in `build/linux/evidence/native/resources.md` and the raw JSON. These cold/main/warm samples preceded the final minor form/clipboard corrections. Cold RSS is not representative of a warm process. Three Omarchy desktop cards measured 1.23% app-process CPU / 768.3 MiB RSS after navigation; the shared desktop Quickshell process was separately 931.1 MiB RSS, including all its other modules.

Qt animation remains expensive with llvmpipe: after removing MSAA, the isolated printing demo used 55.49% CPU / 680.0 MiB RSS on KDE and 64.81% / 734.1 MiB on Omarchy with animations temporarily enabled. Scrolling the same model fully offscreen reduced those to 1.02% / 679.0 MiB and 1.13% / 723.6 MiB. The original MSAA UI process alone exceeded 130% CPU. Physical-GPU performance is unverified. Omarchy's recorded desktop has animations disabled; the app now follows that setting instead of animating against the user's preference. With that actual reduced-motion setting, the visible printing demo measured 1.36% CPU / 724.01 MiB RSS.

Native smoke test (run inside the logged-in desktop session):

```sh
python3 linux/tests/native-ui-smoke.py
python3 linux/tests/native-assets.test.py
# Optional: pass a demo's distinct ui-<id>.sock to the smoke test.
```

For interactive VM testing with `shared/scripts/fleet/fleet.py`, stop ordinary monitoring and run `flatpak run dev.rolottr.BambuBar --emulator /var/data/virtual-printers/BambuStudio.conf --add-all --test-desktop`. The simulator config must be copied into that app-data directory first. `--test-desktop` requires `--emulator` and uses the ordinary single-instance launcher, native panel and widget endpoints, while printers, credentials, preferences and history remain in memory. The windows identify simulated printers. Pause/resume, speed and light controls talk to the simulator; it supplies no camera. Quit this session with `flatpak run dev.rolottr.BambuBar --quit`, then launch normally to recover the unchanged saved printer setup. Ordinary `--demo` and `--emulator` runs without this explicit flag retain their independent endpoints and do not replace desktop companions' data.

The native pass ran the portable core executable on all three hosts: 274 tests passed, two optional exporters were skipped (276 total). All 1,230 tray checks, seven GNOME widget tests, extension syntax checks and native model-asset checks passed. Paused six-printer German snapshots exercised long labels and independent demo cleanup. Native capture waits for a mapped window; snapshot demos quit after success or failure. Omarchy was visually checked at 125% scale, at the 720×460 minimum, with keyboard navigation, and when reopening from another workspace. Scale, theme, motion, camera, widget sharing and autostart test settings were restored. Physical mixed-monitor testing remains outside this native pass.

Known scope: Omarchy requires its installed Quickshell component/plugin API, not merely Hyprland. Desktop-card placement uses the first Quickshell output and corner stacks, without free dragging; dense layouts on very small outputs and physical multi-monitor placement remain unverified. Qt/QML uses the shared translations where supplied; some new native labels still fall back to English. ARM64, X11, other distro/toolkit combinations and physical-GPU performance remain unverified. Native printer controls are implemented but were not exercised against a real print. Historical GNOME/Plasma behavior checks are distinguished from current native VM checks in the evidence.

## Consolidated Linux branch validation

The local `linux` branch merges the completed GNOME → KDE → Omarchy history with the newer main-checkout changes, including version 2.2.1 and the portable save-failure fixes. The translation catalog retains both Mac and Linux notification strings: all 697 entries pass the translation check across 17 languages.

The same committed source was built and packaged independently on all three recorded Linux hosts. Builds used new `~/bambu-bar-linux-consolidated` directories, with no destructive synchronization of the earlier checkouts. Each build produced the Flatpak, all three companion archives, checksums and source identity. The packages were installed and launched in each actual desktop session; Settings captures verified native GNOME, Breeze and Omarchy styling and the compact 960×700 Omarchy window.

| Host | Environment | Build and checks |
| --- | --- | --- |
| Ubuntu GNOME | Ubuntu 26.04.1, GNOME 50.1, x86_64 Wayland | Flatpak + all companions; 273 core tests passed, 2 optional exporters skipped; 1,230 tray checks passed. |
| Ubuntu KDE | Ubuntu 26.04.1, Plasma 6.6.6, x86_64 Wayland | Flatpak + all companions; 273 core tests passed, 2 optional exporters skipped; 1,230 tray checks passed. |
| Omarchy | Omarchy 4.5, Hyprland 0.56, x86_64 Wayland | Flatpak + all companions; 273 core tests passed, 2 optional exporters skipped; 1,230 tray checks passed; 329 palette checks and 4 helper tests passed. |

The seven GNOME widget tests and both JavaScript syntax checks also passed. The GNOME/Plasma archives were accepted by their native installation tools in isolated test directories, and the extracted Hyprland archive's installer was checked against an isolated Lua configuration. Existing desktop companions and user settings were preserved. This consolidation pass verifies builds, packages and basic running desktop rendering; the detailed tray/widget/camera/lifecycle validation below belongs to the earlier desktop passes, not a claim that every interaction was repeated.

Local evidence is in `build/linux/evidence/consolidation/`. Before removing the three Linux worktrees, their complete `build/linux/` artifacts were preserved under `build/linux/archives/linux-gnome/`, `linux-kde/` and `linux-omarchy/`, with checksummed comparisons. Their commits remain reachable from `linux`. Unrelated worktrees and unfinished main-checkout edits are preserved. No remote push or release publication is part of consolidation.

## What is ported

- Network discovery, manual connection/identity lookup, settings-file import, saved access codes, several printers and automatic selection for the tray.
- Live status, job stages, progress/layers/time left, model-specific temperature/fan/sensor fields, AMS and external filament, names/colors/weight/humidity, HMS notices and history.
- Pause/resume, stop confirmation, speed and chamber light through the same command/acknowledgement logic as Windows. Controls honor the printer's Developer Mode lock.
- Native OpenGL printer models from the shared `.glb` files: drag to turn, colored spools, growing print and moving bed/toolhead. Geometry is uploaded once. Motion respects reduced-motion settings and stops when hidden/minimized.
- JPEG and RTSPS cameras using the portable transport and decoder. Camera must be enabled globally and per printer. Collapsible preview and separate camera window; feeds stop when hidden, collapsed or scrolled away, unless the user elects to keep them connected during prints.
- Notifications, test notification, printer selection on notification click, settings, supporter license activation/deactivation and About with version/commit. License/vault operations run away from GTK's thread.
- Quick view with temperatures, filament and optional fans; status-aware colors, prebuilt controls and cached printer art. Optional StatusNotifier/DBusMenu tray; a normal launcher/window works without a tray host.
- Single instance and argument forwarding through GApplication, background portal registration/status, start at login, XDG paths, shared translations and demo/screenshot modes.

## Deliberate desktop differences

In the earlier GTK-only implementation, one GTK build served all three desktops, with a companion Shell extension for GNOME's rich panel popup. Tray IPC probes services. Desktop-specific widget instructions and the KDE icon-theme fallback use `XDG_CURRENT_DESKTOP`. GNOME 50 and KDE Plasma 6.6 have been exercised on Ubuntu 26.04 VMs; see the separate KDE results below. Omarchy 4.5 / Hyprland 0.56 is validated below.

The companion GNOME Shell extension now supplies desktop widgets. In **Widgets**, add a Small, Medium or Large card, choose a printer (or Automatic), and pick a desktop corner. Cards update from the existing printer core, stay beneath normal windows, hide in the overview/lock screen, and open their printer when clicked. The app must remain running and the extension enabled. Widget settings survive app restarts. Placement currently uses the primary monitor and corner stacks; free dragging is not implemented by the GNOME companion. Plasma uses its own native draggable widgets (below); The earlier generic Hyprland implementation had no widgets; the current Omarchy Quickshell integration supplies native desktop cards.

Standalone Quick view remains available as a small, scrollable GTK window. The Shell popup and the Menu Bar settings preview share content preferences for temperatures, chamber temperature, fans, filament and print controls.

The native Shell integration receives a small versioned JSON snapshot and sends explicit actions over a dedicated session D-Bus interface. It never receives access codes or video frames. Updates are coalesced, identical snapshots are suppressed, the closed popup does not rebuild its contents, and printer artwork is generated off GTK's thread only when the popup is open and its scene changes. There is no polling timer in the extension. This uses the documented [GNOME popup components](https://gjs.guide/extensions/topics/popup-menu.html) and [GJS D-Bus proxy API](https://gjs.guide/guides/gio/dbus.html).

## Build locally on Linux

Install the .NET 10 SDK, `build-essential`, `nasm`, `curl`, `xz-utils`, `python3`, `flatpak` and the GNOME 50 runtime and SDK. For native development also install GTK 4, libadwaita and `libsecret-tools`.

```sh
flatpak remote-add --user --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo
flatpak install --user flathub org.gnome.Platform//50 org.gnome.Sdk//50
linux/scripts/build.sh
```

`DOTNET=/path/to/dotnet` selects an SDK outside PATH. The script publishes a self-contained x64 or ARM64 app, builds the pinned minimal LGPL FFmpeg shared libraries, copies notices, and creates a local Flatpak bundle under `build/linux/`. Every build also packages all three desktop companions and stages the complete release set, checksums and source identity under `build/linux/release/<rid>/`. x86_64 was built and run; ARM64 is available in the recipe but not validated on hardware. The FFmpeg version/source/hash are shared with Windows; its Linux build uses pthreads and no Windows toolchain. It is compiled inside the GNOME SDK so host glibc changes cannot break the Flatpak camera. No web view or browser engine is embedded.

The Flatpak grants network access for the printers, Wayland/fallback X11, GPU rendering, a Secret Service fallback and optional StatusNotifier watcher access. It can read the native slicers' two config directories; other files use the file chooser portal. It has no general home-directory access. The GNOME runtime supplies `secret-tool`, GTK and libadwaita. Native development can be installed with `linux/scripts/install.sh <publish-folder>`; installation switches an immutable build directory atomically.

## Data and credentials

Native: `$XDG_CONFIG_HOME/bambu-bar/settings.json`, `$XDG_STATE_HOME/bambu-bar/history.json` and `app.log`. Defaults use `~/.config` and `~/.local/state`.

Flatpak: those paths are inside `~/.var/app/dev.rolottr.BambuBar/`. libsecret uses the encrypted, application-scoped Secret portal store where available, and Secret Service elsewhere. Native and Flatpak credentials are separate. Access codes and licenses are never written into settings, logs, snapshots or the repository. Demo/developer/emulator runs use memory and do not change saved printers or credentials.

## Development commands

```sh
flatpak run dev.rolottr.BambuBar --demo printing --fleet
flatpak run dev.rolottr.BambuBar --demo paused --lang de --snapshot /var/data/screenshots
flatpak run dev.rolottr.BambuBar --screenshot /var/data/window.png
flatpak run dev.rolottr.BambuBar --page Filament
```

`--snapshot` captures each page in the desktop session and exits in a demo run. A screenshot of the app is distinct from a full desktop capture. The portable `--probe` and `--probe-camera` commands also work; `keyring` in the access-code position reads the saved code without putting it in process arguments. `Ctrl+1` onward navigates, `Ctrl+R` refreshes, `Ctrl+L` changes the light, `Ctrl+K` opens the camera, `Ctrl+,` opens settings, and `Ctrl+W` closes the current window.

## Validation

Ubuntu GNOME VM: Ubuntu 26.04.1, GNOME 50.1, x86_64, Wayland, 1920×1080. Mesa llvmpipe software rendering; performance here is not a hardware-GPU claim.

The real P2S was reached over TLS with certificate authority and serial validation enabled. Live MQTT reports, AMS 2 Pro filament/humidity and a maintenance notice matched the printer's reports. Pause/stop/speed were locked by the real printer and were not used. The UI light switch was tested in both directions and restored to off. RTSPS camera: trusted certificate, H.264 1920×1080, 99 frames decoded in five seconds (~19.7 fps). This was a real camera, not an emulator.

Mac unit tests, the portable core suite (269 passed, two optional asset-export tests skipped), and the shared translation check passed. Local builds replace hosted CI in this session because the owner prohibited all remote pushes. The installed Flatpak camera was verified after rebuilding FFmpeg in the SDK: 102/102 frames decoded in four seconds (~25.5 fps). The camera UI displayed live video and its TCP stream closed after the camera window was closed.

Quick view measured 90.1 ms to the first painted frame on its first open after launch and 37.0 ms on a warm open. A 15-second hidden-window sample averaged 0.13% CPU; a subsequent close/camera/theme transition sample averaged 0.67%. These are short VM samples, not a five-minute hardware-GPU benchmark.

Live dark/light switching and 200% scaling were inspected at 1920×1080. The window now constrains itself to the monitor’s logical dimensions and its sidebar scrolls at small sizes. The desktop was restored to 100% scale. The 3D surface requests frames explicitly; remapping requests a fresh frame. Print animation is capped at 15 fps and stops when hidden, suspended or scrolled out of the viewport.

Production license activation and a real print lifecycle were not exercised; no job was interrupted and no production license activation was consumed.

### GNOME menu visual parity pass

The Shell popup now uses a compact dark surface matching the Mac reference: a connection indicator, status pill and Light control in the job card, stage/layer details, an HMS banner with copy/wiki/dismiss actions, temperature tiles, horizontal AMS spool rings with weight and climate readings, and one footer row. Camera and active-print controls remain conditional on the app's capabilities and preferences. The menu uses the cached Linux printer illustration; it does not embed the Mac's interactive 3D renderer. Its dark palette is local to this popup and does not change the desktop theme.

The snapshot adds AMS grouping, external-spool metadata, layer counts, Wi-Fi strength, and HMS identifiers/links without exposing credentials. The existing flat spool field remains for older extension builds. Build and live-desktop verification are performed on the GNOME VM before installation is considered complete.

This pass was built with .NET 10 and visually checked against the real P2S on GNOME 50 at 100% desktop scaling. Settings opened from the header; the live snapshot confirmed the failed job, layer 0/192, AMS colors/amounts and climate readings. The extension reported no load errors. Evidence: `build/linux/evidence/gnome-panel/popup-mac-layout.png`. The updated Flatpak and companion extension bundle are installed locally; nothing was pushed. Print controls were not exercised during this visual pass.


### Application pages and desktop widgets

The next visual pass fixes Shell hover/focus foreground inheritance explicitly and updates all app pages through a shared dark/light style, compact navigation, job/status cards, an HMS banner, temperature/fan cards, grouped printer details and spool cards. Menu Bar has all four tray text modes, content switches and a live preview. The standalone Quick view uses the same cards and rebuilds its structure when opened or when the available printer sensors/spools change. Appearance can be Dark (default), Light or System; this changes only Bambu Bar.

Desktop widgets use the portable `WidgetStateWriter` snapshot contract over the existing session D-Bus interface. They receive no printer access codes. Widget actors and signals are removed on disable; reports update the cards without a polling timer. The extension has three size variants and the app allows up to eight configurations, with primary-monitor corner placement. Large collections need sufficient desktop space.

Validation: .NET Release builds passed, `node --test linux/tests/widget-layout.test.mjs` passed three placement checks, and both extension entry points passed syntax checks. All app pages were captured against the live P2S. Hover over the popup's job card preserved white text. Small, Medium and Large widgets, the enable/disable switch, click-to-open, window stacking and settings persistence across an app restart were exercised on GNOME 50. Light appearance was inspected and restored to Dark. Real print pause/stop commands were not invoked. Evidence is under `build/linux/evidence/parity/`.

### macOS reference alignment

The Linux printer page now has the same four specification cards as the Mac, including readable nozzle material, speed percentage, printer-reported Live View status, build volume, chamber, nozzle/bed limits, fans and filament systems. Unknown telemetry stays unknown rather than appearing as an off/closed sensor.

Menu Bar uses four radio choices and a sample print with a top-bar example, translucent cards, timing, temperatures, spools, print actions and footer. Its switches update the sample directly without controlling the real printer. The columns wrap on narrower windows. Settings groups Theme, General, Language, Camera and Updates; updates accurately describe the Linux package source instead of exposing Mac-only updater switches.

Widgets now includes a wallpaper-backed showcase, selectable sizes and print states, feature chips and Linux installation steps. Actual desktop cards use a progress ring for Small, illustrated job details for Medium, and temperature tiles, spool hubs and one issue message for Large. Artwork is rendered off the UI thread and cached by scene; no camera data or credentials cross the desktop interface. Idle cards expose loaded spools. Page navigation resets scroll position.

Validation: Release builds passed with no warnings; all seven widget state/layout checks and extension syntax checks passed. The installed Flatpak and extension were inspected on GNOME 50 against the real P2S. Verified the four printer-detail cards, Settings sections, sample top-bar modes and chamber switch, widget size/state selection, and all three desktop cards with artwork and an unduplicated maintenance notice. Print commands were not exercised. Screenshots and local packages are under `build/linux/evidence/reference-pass/` and `build/linux/`. Nothing was uploaded or pushed.


### Page-by-page macOS comparison (November 13, 2026)

Reference captures from the installed Mac app were compared individually with the GNOME app, in addition to the supplied screenshots. The local review artifact is `build/linux/evidence/page-audit/comparison.html`; captures are actual application renders, with different window dimensions and demo/live status where indicated.

| Surface | Refinement checked against macOS |
| --- | --- |
| Navigation | One **All Printers** entry, first and only with multiple printers; a shared printer selector; compact illustrated footer for one printer. |
| Printer | Separate print controls, filament-colored progress, finish time, equal temperature/fan tiles, sensor icons and the four lower detail cards. |
| Filament | Four AMS slots, matching external-spool width, active-spool treatment, weight hubs and filament-colored remaining bars. |
| Menu Bar | Four display modes, grouped switches, sample panel and top-bar label; the real GNOME printer chooser is named for the selected printer. |
| Notifications | Matching heading and event-toggle card; Linux retains a compact test-notification button. |
| Printers | Printer list, selected printer details, editable name/address, per-printer camera and separate Controls section. |
| Widgets | Small dial beside status/time, medium illustration and job, large sensors/AMS/footer; no desktop-card drop shadow. Failed/finished cards do not show active-print progress. |
| Settings | General, Language, Camera and Updates groups, with native Linux update and appearance behavior. |
| Activity | Added after 3.1.0's Mac page: reported capabilities per printer, the event list with All/Connection/Print/Issues/Camera/Actions filters, Clear and a redacted JSON export (same keys as the Mac's). One C# log in BambuBar.Core feeds the GTK page (GNOME, Hyprland, Ctrl+9) and the Plasma/Omarchy native page; on those, the bridge writes the export only to the file the save dialog chose. |
| About | Horizontal app identity, copy/version and links cards, supporter license, how it works, privacy and credits. |
| Fleet | Two-column cards, attention-first sorting, state colors and one sidebar destination. |

Unchanged widget snapshots no longer trigger layout or restacking; cards only move in the window stack when they cross a real window or the desktop-icons surface. The standalone quick view is now created on demand. Its cleanup callbacks and temporary progress-color providers are released when rebuilt. 3D animation checks viewport visibility and uses explicit render requests. The resource sampler at `linux/scripts/measure-resources.py` reports interval CPU, RSS and PSS separately for Bambu Bar and GNOME Shell; it does not use lifetime `ps %CPU` averages.


The final installed build and extension were checked on the same GNOME VM. Release build: zero warnings/errors; all seven widget model/layout tests and both JavaScript syntax checks passed. The installed application DLL and extension matched the published files, and GNOME reported no extension errors. Temporary demo windows were closed and the original widget configuration restored.

Resource observations (CPU is percent of **one logical CPU**, RSS is resident memory):

| Scenario | Interval | App CPU | App RSS at end | GNOME Shell CPU |
| --- | --- | --- | --- | --- |
| Cold background, three desktop widgets | 60 s | 0.55% | 196.8 MiB | 0.54% |
| Warm background, after 24 page visits without captures | 60 s | 1.14% | 536.9 MiB | 0.42% |
| Simulated printing, 3D model visible | 60 s | 4.45% | 406.2 MiB | 1.98% |
| Same simulated print, model scrolled out of view | 30 s | 1.14% | 407.7 MiB | 2.00% |

The earlier three-widget sample, before skipping redundant restacking, averaged 4.13% GNOME Shell CPU; the final sample averaged 0.54%. These are observed VM intervals, not a controlled hardware benchmark. The demo and real app ran as separate processes during the animation check; the table reports the demo process for those two rows. The 3D model displayed again when scrolled back into view.

Memory remains substantial after opening the UI: the clean navigation run rose from 164.5 MiB at startup to 494.4, 504.4 and 532.6 MiB after successive eight-page cycles. The cold widget figure is not the warm UI footprint, and these short samples do not prove absence of a leak. Raw CPU/RSS/PSS measurements and methodology are saved in `build/linux/evidence/page-audit/performance/`. No builds or application captures ran during the measurement intervals. Nothing was pushed.


### KDE Plasma companion

On Plasma **6.6 or newer**, install the local companion package outside the Flatpak:

```sh
linux/scripts/install-plasma-widget.sh
# Or use the bundle produced by linux/scripts/build.sh:
kpackagetool6 --type Plasma/Applet --install Bambu-Bar.plasmoid
# For subsequent versions:
kpackagetool6 --type Plasma/Applet --upgrade Bambu-Bar.plasmoid
```

Enable **Share printer status with Plasma widgets** in the app's Widgets page. Right-click the desktop, enter **Edit Mode**, then **Add Widgets → Bambu Bar**. Right-click each card and choose **Configure Bambu Bar** to pick Small, Medium or Large and a printer. Automatic follows the active printer. Plasma owns placement, resizing, and per-widget settings; the GNOME corner controls are not shown here. Keep Bambu Bar running. Disabling sharing clears the cards' printer data; remove cards in Plasma Edit Mode.

The native QML companion reads one initial D-Bus snapshot and then listens for state and service-owner changes. It has no polling timer or helper process, and never receives printer credentials or camera frames. It clears live data when the app exits, reconnects when it starts, and uses cached printer artwork. Small cards have a progress ring, state, job and filament; Medium adds printer artwork and temperatures; Large adds AMS colors/amounts, humidity and one issue message. Long text is elided within small cards; open the printer for the complete message.

Plasma's standard StatusNotifier tray is used directly: left-click opens the GTK Quick view, middle-click opens the main window, and right-click offers Quick view, Open and Quit. Plasma does not display the app's inline progress/time text next to the tray icon; the four Menu Bar modes affect its tooltip. The page now states this limitation. On Plasma Wayland, Quick view is a GTK layer-shell surface anchored beside the clicked tray icon, with natural content height and scrolling when the display is short. It is not a Plasma QML popup. GNOME's richer Shell popup remains unchanged.

Implementation follows KDE's [widget configuration](https://develop.kde.org/docs/plasma/widget/configuration/) and [desktop scripting](https://develop.kde.org/docs/plasma/scripting/api/) interfaces, and the installed Plasma 6.6 `org.kde.plasma.workspace.dbus` module. That module's signal handler naming and callback behavior are covered by KDE's [D-Bus tests](https://github.com/KDE/plasma-workspace/tree/Plasma/6.6/components/dbus/autotests).


### KDE validation (November 13, 2026)

Built and installed on `ubuntu-kde`: Ubuntu 26.04.1, Plasma/KWin 6.6.6, Wayland, 1920×1080, two vCPUs and 3904 MiB RAM, using Mesa llvmpipe. The final application code is `a549f23`; the installed DLL matched the published DLL by SHA-256. Evidence, raw measurements and the individual macOS/KDE page comparison are in `build/linux/evidence/kde/` (`results.md` and `comparison.html`).

Fixed a GTK GL-context crash by dispatching tray actions onto GTK's main context; missing icons caused by Breeze not being inside the GNOME runtime; duplicated tray progress/state text; Quick view height; a retry button incorrectly visible over streaming video; medium desktop-card clipping; and excessive shell/compositor work from a permanently pulsing attention icon. Failed/paused printers now retain a static warning overlay. The native Plasma companion uses the existing sanitized snapshot and leaves the GNOME extension's interface intact.

KDE's background portal also exposed two lifecycle bugs: background registration could remove the saved autostart entry, and a wallet unlock after 30 seconds left startup offline. Registration now always includes the saved autostart value. Interactive credential/license lookup waits for wallet unlock and kills pending helper processes synchronously on quit. Autostart was exercised through a real VM reboot; unlocking KWallet after 60 seconds connected successfully. Automatic login on this VM still requires unlocking KWallet manually.

All eight pages, the four lower Printer cards and the six-printer demo overview were visually inspected. There is one All Printers entry. Tested native tray left/middle/right actions, Quick view options, launch/hide/quit, notification display and click-to-open, settings persistence, live Light/Dark/System changes, 200% scaling, window resizing, all three native widget sizes, printer configuration, drag/resize, click-to-open and clearing/reconnecting. Real P2S MQTT telemetry, four AMS 2 Pro slots plus external spool and humidity were verified. The trusted RTSPS camera decoded 130/130 frames in five seconds (~25.9 fps), displayed live video, and released its TCP connection when closed. No real print command was sent.

The portable core suite reported 269 passed, two opt-in sample exporters skipped, zero failed (271 total), including codec tests using the bundled FFmpeg libraries; all seven GNOME widget state/layout tests and both extension syntax checks passed. A full GNOME desktop visual rerun was not performed for this KDE pass. Production licensing and a real print lifecycle remain untested. Plasma cards require 6.6+ and show cached artwork rather than live video/interactive 3D; the tray uses a separate GTK Quick view and tooltip text, not GNOME's rich anchored popup or inline panel text.

| Scenario (60 seconds each) | App CPU | App RSS, end | Plasma CPU | KWin CPU |
| --- | ---: | ---: | ---: | ---: |
| Cold hidden, three widgets | 1.37% | 191.9 MiB | 0.26% | 0.03% |
| Main Printer view, three widgets | 2.17% | 468.3 MiB | 0.26% | 1.50% |
| Warm hidden, after 24 page visits, three widgets | 0.33% | 533.8 MiB | 0.44% | 5.81% |
| Warm hidden, after capture pass, no widgets | 0.18% | 625.2 MiB | 0.07% | 0.03% |
| Printing demo, model visible, 1120×940 | 21.51% | 426.6 MiB | 0.18% | 6.45% |
| Printing demo, model visible, 1120×606 | 15.66% | 426.9 MiB | 0.12% | 2.35% |
| Same demo, model offscreen, 1120×606 | 0.10% | 426.9 MiB | 0.08% | 0.02% |

CPU percentages are per logical CPU. Cold memory is a fresh background process, not typical warmed-up use. The 24-page run reached 500.2, 544.9 and 544.7 MiB RSS after its three eight-page cycles; it ended the following hidden interval at 533.8 MiB. The no-widget run follows additional page captures and UI checks, so its 625.2 MiB RSS cannot isolate widget memory overhead. The warm KWin sample included a one-second 166.93% spike; this was not attributed to the app. These short llvmpipe VM intervals do not establish hardware-GPU performance or absence of a memory leak. Visible animation is substantially more expensive here than in the GNOME baseline. In the same 1120×606 window, scrolling it completely out of view reduced app CPU from 15.66% to 0.10%; rendering returned when scrolled back.

No builds or application captures ran inside these intervals. Raw process RSS/PSS, peaks and CPU figures are in `build/linux/evidence/kde/performance/`. Test demos/fixtures and temporary desktop cards were removed; camera, autostart and widget sharing were restored off, app appearance to Dark, desktop to BreezeLight and scale to 100%. The real app remains installed and monitoring in the background. Nothing was pushed.


### Plasma Quick view placement correction

The initial KDE pass incorrectly accepted Quick view as a standalone window: it discarded the StatusNotifier activation coordinates, allowing KWin to center it. It also forced a 700-pixel height, leaving empty space below short content. The corrected build retains the tray click position and uses GTK4 Layer Shell 1.3.0 on Plasma Wayland, sizing the surface to its content and available monitor height. A second click, Escape or loss of focus dismisses it. Rounded corners are drawn by GTK because layer surfaces have no window-manager decoration.

The library is pinned by SHA-256 and built inside GNOME SDK 50 by `linux/scripts/build-layer-shell.sh`, then bundled with its MIT license/source notice. It loads before GTK only on Plasma Wayland. GNOME and desktops without layer-shell support retain the ordinary GTK fallback. The standard StatusNotifier protocol reports coordinates using Plasma's `XwaylandClientsScale` setting; the app reads that setting through the desktop settings portal and converts native coordinates to GTK logical coordinates. No additional Flatpak permissions or KWin rules/scripts are installed.

Rebuilt and visually checked on the VM with bottom and top panels, 100% and 200% scale, scroll access to AMS, repeated tray clicks, Escape, outside-click dismissal and opening the main window. Restored bottom-panel placement and 100% scale. Evidence: `build/linux/evidence/kde/tray-placement/`. Multi-monitor mixed-scale placement was not exercised. The prior resource measurements describe the earlier build and were not rerun for this placement correction.


### Linux desktop themes

Settings now starts with **Theme**, with five choices: **Follow desktop**, **GNOME (Adwaita)**, **KDE (Breeze)** and **Bambu Bar**, plus **Omarchy**. Follow desktop is the default and resolves to Omarchy on Omarchy/Hyprland with accessible current-theme files, Breeze on Plasma and Adwaita elsewhere. **Color mode** independently selects Dark, Light or System. Existing light/dark preferences are preserved; new installations follow the system. Both selections persist, and changes apply immediately to the main window, GTK Quick view and camera windows. The four-square Quick view header button is removed on both desktops; tray and shell entry points remain available.

Adwaita uses libadwaita's native background, foreground and accent variables, neutral sidebar icons and rounded cards. Breeze uses the KDE light/dark palette, cyan accent and flatter, tighter cards and controls. This is a Breeze-inspired GTK style, not a Qt theme engine or an importer for arbitrary Plasma color schemes. Bambu Bar retains the original colorful sidebar and dark gradient. Shell popups and desktop widgets retain their own shell/companion styling; this chooser changes application windows.

Implementation references: [libadwaita CSS variables](https://gnome.pages.gitlab.gnome.org/libadwaita/doc/1-latest/css-variables.html), KDE's [Breeze Light](https://github.com/KDE/breeze/blob/Plasma/6.6/colors/BreezeLight.colors) and [Breeze Dark](https://github.com/KDE/breeze/blob/Plasma/6.6/colors/BreezeDark.colors) palettes. Support and license-activation icons unavailable in the Flatpak's Adwaita theme were also replaced with available symbolic icons.

Validation on the Ubuntu 26.04.1 Plasma 6.6.6 and GNOME 50.1 VMs: built and installed the same Flatpak, checked the removed header button, all theme choices, light/dark rendering, System color mode and persistence through process restarts. GNOME's System mode changed live with its desktop preference, including Breeze style; native Adwaita picked up Ubuntu's orange accent. Inspected page captures with both native styles, including readable light-mode Printer, Filament, Menu Bar and Widgets previews. Plasma Quick view remained beside the tray with the selected style. Evidence and repeatable steps are under `build/linux/evidence/themes/`. Both VMs were left on Follow desktop with their prior Dark color mode; other settings and real-printer controls were not changed. No performance claim is made for these styling changes; the earlier measurements above were not repeated.


### macOS menu-bar printer glyph on Linux

Both the StatusNotifier tray and GNOME companion now use the same 18-unit printer geometry as `Sources/BambuBar/UI/MenuBarIcon.swift`, replacing the application logo and the old GNOME stacked symbol. The chamber fills in 5% steps during printing/preparing; paused, failed, finished, canceled, offline and serious-alert states follow the Mac's look selection. The app's Menu Bar preview uses the same artwork. The desktop supplies the monochrome foreground, independently of the app's selected theme. The launcher keeps the application icon.

`linux/scripts/generate-tray-icons.py` generates the shared assets during .NET builds and GNOME packaging. Flatpak and native installs expose the names through the standard icon theme. Filled outlines encode the frame, offline dashes and badge cutouts: GTK symbolic recoloring otherwise fills stroked rectangles and masks incorrectly. GNOME receives only a validated icon key and changes its `GIcon` when that key changes; StatusNotifier emits NewIcon only for a changed look, even in Icon only mode. Failed/paused states do not request a permanently animated attention icon.

Validation includes 1,230 state/progress/asset checks (`dotnet run --project linux/tests/TrayIcon.Tests -- build/linux/linux-x64/tray-icons`), the seven existing GNOME widget tests and extension syntax checks. Actual AppKit renders of the Mac implementation and Linux SVG renders are saved side by side in `build/linux/evidence/tray-icon/comparison.html`. Both desktop integrations were inspected on the VMs, including Plasma light/dark panels and tray opening, and the GNOME extension after a session restart. No printer command was sent. Themes and ordinary monitoring were restored after testing.


### Omarchy / Hyprland integration (November 13, 2026)

The tested VM is Omarchy 4.5.0-3, Hyprland 0.56.2-4, x86_64, Wayland, 1920×1080. This installation uses **Quickshell 0.3.2**, not Waybar, for its bar, StatusNotifier host and notification daemon. Waybar is tested separately with a temporary configuration; the user's Quickshell/Hyprland configuration is not replaced. The app retains its GTK/libadwaita layout and the macOS menu-bar printer geometry, including progress and status variants.

On Arch/Omarchy, install the missing packages without an unrelated system upgrade:

```sh
sudo pacman -S --needed dotnet-sdk base-devel nasm curl xz python flatpak python-gobject gtk-layer-shell python-cairo
flatpak remote-add --user --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo
flatpak install --user flathub org.gnome.Platform//50 org.gnome.Sdk//50
BAMBUBAR_COMMIT=$(git rev-parse HEAD) linux/scripts/build.sh
flatpak install --user build/linux/Bambu-Bar-2.2.0-linux-x64.flatpak
linux/scripts/install-hyprland-integration.sh --compact-window
# Reload Hyprland once after installing the optional main-window rule.
hyprctl reload
flatpak run dev.rolottr.BambuBar --version
```

If Flatpak was just installed, sign out/in once so the bar inherits Flatpak's exported icon/desktop search paths from `/etc/profile.d/flatpak.sh`. No copied icons or temporary user icon overrides are required. Packaging works on Arch: native codecs/layer-shell are compiled in GNOME SDK 50, Plasma's optional archive uses Python, and GNOME extension packaging uses Python and works without GNOME tools on the build host.

**Theme:** the installed Omarchy stages the current theme under `~/.local/state/omarchy/current/theme`, replacing the theme directory atomically. The legacy `~/.config/omarchy/current/theme` is also recognized if accessible. The app reads bounded, validated scalar colors from `colors.toml` and the optional `shell.toml`, never theme CSS/Lua or executable content. It uses actual background, foreground, accent, selection, border and surface colors; secondary/status text is contrast-adjusted. The GTK desktop font family remains in effect; square, shadow-free application chrome follows the tested Omarchy defaults. Filament colors retain their physical meaning. This is palette integration, not an interpreter for arbitrary shell layout or Hyprland Lua customization.

**Follow desktop** selects Omarchy automatically. The explicit **Omarchy** choice is appended without changing the existing `system`, `gnome`, `kde`, or `bambu` preference IDs. **System** color mode follows the active palette's declared/inferred light/dark mode. Explicit **Light** or **Dark** remains authoritative: an opposite-mode palette is adapted by exchanging its background/foreground roles, rebuilding neutral surfaces and ensuring text contrast while retaining its accent. A bounded two-second refresh detects theme replacement and applies one CSS provider to main, Quick view and camera windows, without navigation or restarting. Missing/malformed files fall back to native Adwaita. The portal provides a color-scheme preference but not Omarchy's full palette; the Flatpak therefore grants read-only access to the two current-theme parent directories only, not general home/config/theme-library access.

**Tray and Quick view:** Plasma retains its existing GTK4 Layer Shell path and coordinate conversion. Hyprland 0.56 intentionally withholds layer-shell from Flatpak clients ([upstream policy](https://github.com/hyprwm/Hyprland/discussions/14127)). The optional user-local `dev.rolottr.BambuBar.Hyprland` D-Bus service therefore positions a normal GTK Quick view window next to the current tray cursor. It identifies only Bambu Bar's class and a validated random popup token, queries live logical monitor geometry (including scale, rotation and origin), and runs fixed positioning commands. Host GTK3 layer-shell surfaces detect outside clicks, with an input hole over the popup. Escape, focus loss, repeated tray clicks and owner disconnection clear the popup/dismissal surfaces. The sandbox gets neither the compositor command socket nor arbitrary command/window access. Without the helper, opening Quick view explains the installation requirement rather than silently centering it. Main and camera windows remain compositor-managed. This implementation targets the tested Hyprland 0.56 Lua dispatch API; earlier Hyprland releases are not verified.

The helper also manages only Bambu Bar's fixed `flatpak run dev.rolottr.BambuBar --background` XDG autostart entry because this VM's GTK/Hyprland portals have no Background interface. It refuses to overwrite independently managed entries and preserves disabled entries as non-`.desktop` backups. Installation backs up prior helper/service files. The optional `--compact-window` flag also backs up `hyprland.lua`, appends one include, and installs a rule matching only the Bambu Bar main-window class/title. It floats and centers the main window, whose Omarchy default is 960×700; Quick view and camera titles do not match. Remove that include to restore tiling. Without the flag, no Hyprland or bar configuration changes. No Waybar/Quickshell configuration is replaced. It uses Python/PyGObject, GTK3 layer-shell and Pycairo, and starts on D-Bus demand. Remove its two files under `~/.local/share/bambu-bar-hyprland/` and `~/.local/share/dbus-1/services/` to uninstall after turning Open at login off. GNOME/KDE continue using their original Background portal path.

**Widgets:** Hyprland has no Bambu Bar desktop companion. GNOME actors and Plasma QML applets cannot be reused as Hyprland desktop widgets; Quickshell plugins are not interchangeable with Waybar or a stable cross-shell widget API. The Widgets page states the limitation before its illustrative previews and exposes no GNOME/Plasma sharing or placement controls. Quick view provides live status. The existing sanitized printer-status D-Bus boundary is unchanged; neither printer credentials nor camera frames are exposed to companions or the geometry helper.

Evidence and reproducible validation are saved locally under `build/linux/evidence/omarchy/`, including individual macOS comparisons, native desktop screenshots, app captures, build/tests, package/process hashes, reboot checks and 60-second resource samples. This is a local development build; no remote push or publication is part of this work.

### Omarchy verified results and resource use

The main window now opens at a compact 960×700, with an optional floating/centering rule matching only its exact class/title. This fixes Hyprland expanding it across the desktop. Hide/reopen and notification click-to-open retain the compact geometry. Settings remains readable and scrollable at the 760×460 minimum. The rule is an explicit user-requested configuration change; backups are retained.

Visually checked all eight pages, all four lower Printer cards, the six-printer overview (one All Printers entry), Quick view and real camera video. Live Tokyo Night ↔ Catppuccin Latte switching updated main, popup and camera consistently; explicit Dark remained selected. Quickshell and a temporary Waybar host exercised the packaged macOS printer glyph, tooltip and tray actions; popup dismissal, 150%/200% scale, scrolling and a secondary output were checked. Notification click-to-open and real reboot autostart worked. Real P2S MQTT, AMS 2 Pro/external spools and camera video were verified without sending a printer-control command. Camera decoded 432/432 frames in 15 seconds and released its connection when closed.

Portable core: 269 passed, two optional exporters skipped, zero failures. Tray assets/state: 1,230 checks. GNOME: seven widget tests and both JavaScript syntax checks. Omarchy: 329 palette checks over 23 installed themes and four host-bridge tests. Actual GNOME/KDE VM captures verified native page styling; their installed app versions/settings were preserved. This is not a complete repeat of their earlier lifecycle/performance testing.

| Scenario, 60 seconds each | App CPU | App RSS / PSS, MiB | Hyprland CPU | Quickshell CPU |
| --- | ---: | ---: | ---: | ---: |
| Cold hidden | 1.270% | 179.68 / 171.81 | 0.165% | 0.016% |
| Main Printer, initial tiled 1896×1030 | 0.395% | 398.72 / 384.43 | 0.165% | 0.049% |
| Warm hidden after 24 page visits | 0.197% | 555.02 / 529.20 | 0.197% | 0.016% |
| Warm main Printer, compact 960×700 | 0.231% | 561.73 / 549.61 | 0.149% | 0.017% |
| Printing demo, model visible, 960×606 | 16.856% | 368.16 / 354.70 | 59.090% | 0.115% |
| Same demo/window, model offscreen | 0.429% | 390.43 / 376.98 | 1.485% | 0.082% |

CPU is percent of one logical CPU, on four vCPUs with Mesa llvmpipe (LLVM 23.1.3), display 1920×1080 at scale 1. Builds/captures/navigation were stopped during every interval. Samples used app revision `1198d86`, before the compact default and final popup cleanup guards; the compact rule was active for the last three samples. Cold is a fresh background process, not typical warm usage. The first main sample's raw scenario label mistakenly says 1120×940; recorded compositor geometry proves 1896×1030. The compact sample is a separate, verified 960×700 run after further UI visits. Model-offscreen memory includes the extra page content rendered while scrolling; it is the same process, not a fresh low-memory run. Rendering resumed when scrolled back.

The host helper stayed at 0.000% CPU and approximately 31.4 MiB RSS/17.2 MiB PSS. Full process identities, peaks and shell RSS/PSS are in `performance/`; Quickshell reached about 1.2 GiB RSS during the long test session, so desktop overhead should not be attributed solely to Bambu Bar. Waybar was stopped for these production-desktop measurements. No widget-visible sample exists because Hyprland widgets are unsupported. These short software-rendered VM intervals do not establish hardware-GPU performance or absence of a memory leak. Production licensing, a real print lifecycle and manual delayed keyring unlock remain unverified.
