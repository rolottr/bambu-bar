# Bambu Bar on Linux

The GNOME port lives in `linux/`. It uses GTK 4/libadwaita through GirCore and the existing, tested .NET 10 printer core in `windows/src/BambuBar.Core`. The Mac and Windows apps stay in the same repository and use the same version, assets, certificates, reports and string catalog.

## Install

The first port is a **local development build**, not a published release. On a GNOME desktop with Flatpak and Flathub configured:

```sh
flatpak install --user ./Bambu-Bar-2.2.0-linux-x64.flatpak
flatpak run dev.rolottr.BambuBar
```

Find **Bambu Bar** in the app launcher. Closing the window continues monitoring. Open it again from the launcher, the optional tray icon, or a notification. The tray menu and the launcher offer **Quick view** and **Quit**. `Ctrl+Q` quits completely.

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

One GTK build serves all three desktops, with a companion Shell extension for GNOME's rich panel popup. It probes services instead of branching on `XDG_CURRENT_DESKTOP`. **Only GNOME is validated in this port.** KDE Plasma and Omarchy still need their own application integration/look rounds; their VM displays were set to 1920×1080 as requested.

The companion GNOME Shell extension now supplies desktop widgets. In **Widgets**, add a Small, Medium or Large card, choose a printer (or Automatic), and pick a desktop corner. Cards update from the existing printer core, stay beneath normal windows, hide in the overview/lock screen, and open their printer when clicked. The app must remain running and the extension enabled. Widget settings survive app restarts. Placement currently uses the primary monitor and corner stacks; free dragging and Plasma/Hyprland integration are not implemented.

Standalone Quick view remains available as a small, scrollable GTK window. The Shell popup and the Menu Bar settings preview share content preferences for temperatures, chamber temperature, fans, filament and print controls.

The native Shell integration receives a small versioned JSON snapshot and sends explicit actions over a dedicated session D-Bus interface. It never receives access codes or video frames. Updates are coalesced, identical snapshots are suppressed, the closed popup does not rebuild its contents, and printer artwork is generated off GTK's thread only when the popup is open and its scene changes. There is no polling timer in the extension. This uses the documented [GNOME popup components](https://gjs.guide/extensions/topics/popup-menu.html) and [GJS D-Bus proxy API](https://gjs.guide/guides/gio/dbus.html).

## Build locally on Linux

Install the .NET 10 SDK, `build-essential`, `nasm`, `curl`, `xz-utils`, `flatpak` and the GNOME 50 runtime and SDK. For native development also install GTK 4, libadwaita and `libsecret-tools`.

```sh
flatpak remote-add --user --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo
flatpak install --user flathub org.gnome.Platform//50 org.gnome.Sdk//50
linux/scripts/build.sh
```

`DOTNET=/path/to/dotnet` selects an SDK outside PATH. The script publishes a self-contained x64 or ARM64 app, builds the pinned minimal LGPL FFmpeg shared libraries, copies notices, and creates a local Flatpak bundle under `build/linux/`. x86_64 was built and run; ARM64 is available in the recipe but not validated on hardware. The FFmpeg version/source/hash are shared with Windows; its Linux build uses pthreads and no Windows toolchain. It is compiled inside the GNOME SDK so host glibc changes cannot break the Flatpak camera. No web view or browser engine is embedded.

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

Menu Bar uses four radio choices and a sample print with a top-bar example, translucent cards, timing, temperatures, spools, print actions and footer. Its switches update the sample directly without controlling the real printer. The columns wrap on narrower windows. Settings follows General, Language, Camera and Updates, with Linux preferences below; updates accurately describe the Linux package source instead of exposing Mac-only updater switches.

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
