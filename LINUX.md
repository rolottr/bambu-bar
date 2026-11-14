# Bambu Bar on Linux

Bambu Bar uses a shared printer backend with a native UI for each desktop: **GTK 4/libadwaita on GNOME, Qt/Kirigami on KDE Plasma, and Quickshell with Omarchy’s own controls on Omarchy**. Install the **Flatpak**, then the matching desktop package below. The native desktop package is required on KDE and Omarchy. You do not need the .NET SDK or a compiler to install a release.

Linux packages will be attached to [GitHub releases](https://github.com/rolottr/bambu-bar/releases). Until a release includes the Linux assets listed below, build them from source using [the developer guide](docs/linux.md#build-locally-on-linux). This branch does not publish a release or add Bambu Bar to Flathub. Flathub supplies its runtime.

## Choose your download

| Release asset | Purpose |
| --- | --- |
| `Bambu-Bar-<version>-linux-x64.flatpak` | Application for Intel/AMD 64-bit PCs; same download for every supported distribution. |
| `bambu-bar@rolottr.dev.shell-extension.zip` | Optional GNOME 50 top-bar popup and desktop widgets. |
| `Bambu-Bar.plasmoid` | Native Plasma panel Quick view and optional desktop widgets. |
| `Bambu-Bar-native-desktop.tar.gz` | Native KDE/Omarchy main windows, camera UI and Omarchy bar/widget plugin. |
| `Bambu-Bar-hyprland.tar.gz` | Legacy GTK popup helper for other Hyprland installations; not the Omarchy native UI. |
| `SHA256SUMS-linux.txt`, `SOURCE-linux.txt` | Package checksums and source/version identity. |

`uname -m` reports `x86_64` on Intel/AMD PCs. The build recipe also supports `aarch64` → `linux-arm64`, but ARM64 has not been validated and is not part of the currently tested release set. Do not install the x64 package on ARM. Separate `.deb`, `.rpm`, AppImage and AUR packages are not produced.

## 1. Install Flatpak for your distribution

Run the command for your distribution if Flatpak is not already installed:

| Distribution family | Command |
| --- | --- |
| Ubuntu, Kubuntu, Debian, Linux Mint, Pop!_OS | `sudo apt install flatpak` |
| Fedora Workstation, Silverblue, Kinoite | Flatpak is included. On other conventional Fedora installations: `sudo dnf install flatpak`. |
| Arch, EndeavourOS, Manjaro, Omarchy | `sudo pacman -S --needed flatpak` |
| openSUSE Leap / Tumbleweed | `sudo zypper install flatpak` |
| Other distributions | Follow the distribution's [Flatpak setup instructions](https://flathub.org/en/setup). |

These are installation paths, not claims that every listed distribution has been tested. Actual validation covers Ubuntu 26.04.1 GNOME 50.1, Ubuntu 26.04.1 Plasma 6.6.6, and Omarchy 4.5 / Hyprland 0.56, all x86_64 Wayland. Fedora, openSUSE, other desktop versions, X11 and ARM64 have not had the same VM validation.

Distribution setup references: [Ubuntu](https://flathub.org/en/setup/Ubuntu), [Debian](https://flathub.org/en/setup/Debian), [Fedora](https://flathub.org/en/setup/Fedora), [Arch](https://flathub.org/en/setup/Arch), [openSUSE](https://flathub.org/en/setup/openSUSE).

After first installing Flatpak, sign out and back in (or restart) so your desktop discovers its application and icon exports. Keep your desktop's portal and credential service installed: GNOME normally supplies GNOME Keyring, and Plasma uses KWallet. Unlock the wallet/keyring when prompted; do not enter printer access codes into terminal commands.

## 2. Install the downloaded application

Download the Flatpak and checksums from the **same release**. In that download directory:

```sh
# Verifies the release files you downloaded; skips optional companions not present.
sha256sum --ignore-missing -c SHA256SUMS-linux.txt

flatpak remote-add --user --if-not-exists flathub \
  https://flathub.org/repo/flathub.flatpakrepo
flatpak install --user flathub org.gnome.Platform//50

# Example version: replace it with the version on your downloaded release.
version=2.2.1
flatpak install --user "./Bambu-Bar-$version-linux-x64.flatpak"
flatpak run dev.rolottr.BambuBar --version
flatpak run dev.rolottr.BambuBar
```

Find **Bambu Bar** in your application launcher. Add your printer on the same LAN; saved Bambu Studio/OrcaSlicer settings can supply its access code, or enter the code shown on the printer. Credentials are stored through the desktop's encrypted secret service, not in settings files. Printer status and camera availability depend on your printer model and network settings.

Closing the main window keeps monitoring in the background. Reopen it from the launcher, tray or notification. **Quit**, or `flatpak run dev.rolottr.BambuBar --quit`, stops monitoring. Enable **Settings → Open at login** if desired. The camera is off initially; enable it both in Settings and for the printer.

## 3. Add your desktop integration

The desktop determines integration, independently of the distribution: Kubuntu uses the Plasma instructions, for example. Companions are installed for your user, outside the Flatpak sandbox. Install their matching release versions.

### GNOME 50

For the native top-bar popup and Small/Medium/Large desktop widgets:

```sh
gnome-extensions install --force ./bambu-bar@rolottr.dev.shell-extension.zip
# Sign out and back in, then:
gnome-extensions enable bambu-bar@rolottr.dev
```

On Ubuntu/Debian, `sudo apt install gnome-shell-extension-prefs` supplies the extension tools if missing. On other distributions use their GNOME Shell extension manager. Only GNOME 50 is currently declared compatible; do not disable Shell's version checks to force installation on another version.

The native extension replaces the generic tray item while enabled, avoiding a duplicate icon. In **Widgets**, enable sharing, add a size, choose a printer and select a desktop corner. Placement uses primary-monitor corner stacks, not free dragging. Keep the app running. Without the extension the launcher still works; a generic tray requires a StatusNotifier host such as your desktop's AppIndicator extension.

### KDE Plasma 6.6+

Install the native window package and the Plasma panel/widget package from the same release. On Ubuntu/Kubuntu:

```sh
sudo apt install python3-gi python3-pyqt6 python3-pyqt6.qtquick python3-pyqt6.qtqml \
  qml6-module-org-kde-kirigami qml6-module-qtquick3d \
  qml6-module-qtquick-controls qml6-module-qtquick-dialogs \
  qml6-module-qtquick-layouts qml6-module-qtquick-window \
  qml6-module-qtqml-workerscript
tar -xzf Bambu-Bar-native-desktop.tar.gz
bash bambu-bar-native/linux/scripts/install-native-desktop.sh
kpackagetool6 --type Plasma/Applet --install ./Bambu-Bar.plasmoid
# Use --upgrade for an already-installed Plasma widget.
```

In Plasma Edit Mode, add **Bambu Bar** to your panel. Its icon opens a native Plasma popup; middle click opens the Qt/Kirigami main window. The generic tray item becomes passive while the native panel item is present. The same applet can be added to the desktop in Small, Medium or Large sizes; enable widget sharing in Bambu Bar and configure each desktop widget through Plasma. Plasma controls popup placement and dismissal.

Other Plasma distributions need Python/PyQt6 with QtQml and QtQuick, PyGObject, Kirigami, and Qt Quick 3D. The installer checks the Python Qt bindings. Only the Ubuntu package names and the recorded Plasma VM have been validated; distro package names can differ.

### Omarchy 4.5 / Hyprland 0.56

The tested Omarchy installation has a **Quickshell plugin API** and shared `Ui`/`Commons` modules. Bambu Bar uses its actual buttons, toggles, dropdowns, theme tokens, bar icon and anchored popup. It also supplies native layer-shell desktop cards. The main and camera windows run as a separate Quickshell application.

```sh
sudo pacman -S --needed python python-gobject qt6-quick3d
tar -xzf Bambu-Bar-native-desktop.tar.gz
bash bambu-bar-native/linux/scripts/install-native-desktop.sh
python3 bambu-bar-native/linux/scripts/install-omarchy-plugin.py
```

The plugin installer backs up `shell.json`, preserves its contents, and adds only the Bambu Bar bar entry. It installs under `~/.config/omarchy/plugins/dev.rolottr.BambuBar/`. Restart the Omarchy shell if an updated plugin remains cached. No GTK popup-positioning helper is involved. **Widgets** controls sharing and primary-monitor corner placement; choose Small, Medium or Large. The native desktop package handles the fixed Bambu Bar login command because Omarchy’s portals have no Background interface.

Omarchy’s native controls are loaded from the installed Omarchy shell, rather than copied or approximated. This requires the tested plugin/component API; a generic Hyprland installation without that API is not equivalent to Omarchy support.

### Themes

GNOME uses GTK/libadwaita and the Shell extension inherits GNOME Shell’s colors. Existing GTK theme IDs and Dark / Light / System preferences remain saved. KDE uses its Qt desktop style and Kirigami palette. Omarchy uses the active shell palette, font, borders, spacing and corner treatment. Its main application watches theme changes and reloads the installed Omarchy color/style components; the bar plugin shares the shell’s live theme directly. Filament colors remain printer data.

Main and camera windows retain the independent **Dark / Light / System** choice. KDE System follows the desktop; explicit modes load the installed Breeze Dark/Light color schemes through KDE’s application palette integration. Omarchy System uses the exact active palette; explicit opposite modes adapt its foreground/background and interactive state colors for contrast while retaining the theme’s accent, font and native control geometry. Native shell popups and widgets always follow their desktop, independently of the main-window mode. Earlier GTK theme IDs remain saved for GTK sessions. Desktop companions receive sanitized state and explicit actions; **credentials and camera frames never enter their D-Bus interface**. The separate main UI uses an owner-only local socket. Camera images stay in memory, and imported slicer settings are parsed without writing temporary credential files.

The Flatpak reads only the two discovered Omarchy current-theme directories to resolve an explicit color-mode adaptation; portal preferences cannot supply the theme’s palette. It has no general home-directory access. Native host packages run outside the sandbox, like desktop extensions, and depend on the desktop’s installed toolkit.

## Updates and removal

Linux app updates are downloaded from GitHub; the Mac/Windows in-app updater does not manage them. Quit the app, install the newer downloaded Flatpak with `flatpak install --user ./<downloaded-file>.flatpak`, then launch again. Settings and credentials are retained. Update your companion from the same release too; GNOME requires signing out/in to load changed extension code. `flatpak update --user` updates runtime components, but does not fetch new Bambu Bar bundles from GitHub.

Before uninstalling, turn **Open at login** and widget sharing off, remove desktop widgets, then quit:

```sh
flatpak run dev.rolottr.BambuBar --quit
flatpak uninstall --user dev.rolottr.BambuBar
```

Flatpak keeps app data unless you explicitly request deletion. Remove optional companions with `gnome-extensions uninstall bambu-bar@rolottr.dev` or `kpackagetool6 --type Plasma/Applet --remove dev.rolottr.BambuBar`. Remove the native main-window package by deleting only `~/.local/share/bambu-bar-native/` and its `~/.local/share/dbus-1/services/dev.rolottr.BambuBar.Native.service`. On Omarchy, remove the `dev.rolottr.BambuBar` entry from `shell.json` and its plugin directory; remove only the Bambu Bar include and managed `bambu-bar-native.lua` rule from Hyprland. Sign out/in to stop the native activation service. Backups are under `~/.local/state/bambu-bar/backups/`; do not restore an old whole-desktop backup over later personal changes.

For the legacy generic Hyprland helper, remove the installed `~/.local/share/dbus-1/services/dev.rolottr.BambuBar.Hyprland.service` and `~/.local/share/bambu-bar-hyprland/anchor.py`; sign out/in to stop a running helper. To undo the compact rule, remove only the Bambu Bar `dofile(...)` include from `~/.config/hypr/hyprland.lua`, then its `bambu-bar.lua`, and reload Hyprland. Do not replace the entire configuration.

## Troubleshooting and development

- **No tray icon after installation:** sign out/in after first installing Flatpak; check the desktop has a StatusNotifier host or enable the GNOME companion. Do not copy temporary icons into your theme.
- **Printer offline after login:** unlock the desktop wallet/keyring and check LAN reachability. A locked KWallet can delay startup; the app waits for credential access.
- **Quick view unavailable on Omarchy:** install the native package and bar plugin from the same release, then restart the Omarchy shell to clear cached plugin code. On Plasma, add the Bambu Bar applet to the panel.
- **High CPU while animating:** the VMs use llvmpipe software rendering. Scrolling the model fully offscreen or hiding the window stops its animation work. VM figures do not predict hardware-GPU performance.
- **Need logs:** Flatpak stores app configuration/state under `~/.var/app/dev.rolottr.BambuBar/`. Do not post access codes or keyring files.

See [developer/build instructions and desktop validation](docs/linux.md) and [release packaging](docs/releasing.md#linux-release-assets). The GTK app, native Qt/Quickshell UIs and desktop companions are in `linux/`; the portable printer core remains in `windows/src/BambuBar.Core`.
