# Bambu Bar on Linux

Bambu Bar uses GTK 4/libadwaita with the shared printer core. One **Flatpak** runs on GNOME, KDE Plasma and Omarchy/Hyprland; choose the optional desktop companion separately. You do not need the .NET SDK or a compiler to install a release.

Linux packages will be attached to [GitHub releases](https://github.com/rolottr/bambu-bar/releases). Until a release includes the Linux assets listed below, build them from source using [the developer guide](docs/linux.md#build-locally-on-linux). This branch does not publish a release or add Bambu Bar to Flathub. Flathub supplies its runtime.

## Choose your download

| Release asset | Purpose |
| --- | --- |
| `Bambu-Bar-<version>-linux-x64.flatpak` | Application for Intel/AMD 64-bit PCs; same download for every supported distribution. |
| `bambu-bar@rolottr.dev.shell-extension.zip` | Optional GNOME 50 top-bar popup and desktop widgets. |
| `Bambu-Bar.plasmoid` | Optional KDE Plasma 6.6+ desktop widgets. The standard tray needs no extra widget. |
| `Bambu-Bar-hyprland.tar.gz` | Omarchy/Hyprland popup placement and login-startup helper, with an optional compact-window rule. |
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

The standard system tray works without an add-on: left click opens Quick view, middle click opens the main window, right click opens its menu. Quick view uses a GTK layer-shell surface beside the tray on Plasma Wayland. Plasma shows progress text in the tooltip rather than inline beside the icon.

For optional desktop widgets:

```sh
kpackagetool6 --type Plasma/Applet --install ./Bambu-Bar.plasmoid
# For an existing installation, use --upgrade instead of --install.
```

You can also use Plasma's **Add Widgets → Get New Widgets → Install Widget From Local File**. In Bambu Bar, enable **Widgets → Share printer status with Plasma widgets**, then add Bambu Bar from Plasma's desktop Edit Mode. Configure each widget's size/printer, and move or resize it with Plasma. The companion requires Plasma 6.6's D-Bus QML support.

### Omarchy 4.5 / Hyprland 0.56

The actual tested Omarchy installation uses **Quickshell** for its bar; Waybar's StatusNotifier tray was tested separately. Neither bar configuration is replaced. Install the host helper for correctly anchored Quick view and Open at login: Hyprland withholds the layer-shell protocol from Flatpak clients, and this desktop's portals do not provide Background support.

On Arch/Omarchy:

```sh
sudo pacman -S --needed python python-gobject python-cairo gtk-layer-shell
mkdir -p bambu-bar-hyprland
tar -xzf ./Bambu-Bar-hyprland.tar.gz -C bambu-bar-hyprland
cd bambu-bar-hyprland
bash linux/scripts/install-hyprland-integration.sh --compact-window
hyprctl reload
```

The installer backs up files it changes. `--compact-window` adds one include to your existing Hyprland Lua configuration and a rule matching only Bambu Bar's main window, making it floating, centered and 960×700. Omit the flag to keep compositor-managed tiling. Quick view and camera windows do not match the compact rule. The helper uses Python/PyGObject, Pycairo and GTK3 layer-shell, starts on D-Bus demand, and can position only Bambu Bar's token-identified popup. It does not expose arbitrary compositor commands, printer credentials or camera frames.

Quick view supports Escape, repeated-click and outside-click dismissal, scrolling and mixed monitor scaling. Without the helper, the app explains the missing integration instead of opening Quick view in the screen center. This helper targets Hyprland 0.56's Lua API; other releases are not verified. **Hyprland desktop widgets are not implemented**; the Widgets page says so and does not offer GNOME/Plasma controls.

### Themes

**Settings → Theme → Follow desktop** selects Adwaita on GNOME, Breeze styling on Plasma, and the active Omarchy palette on Omarchy. Explicit GNOME, KDE, Omarchy and Bambu Bar styles remain available. Dark / Light / System is a separate preference.

Omarchy reads the active `colors.toml` and optional `shell.toml` under its current-theme directory, updating main, Quick view and camera within about two seconds of a theme switch. System follows the palette's light/dark mode; explicit Dark or Light adapts it while retaining the accent. Desktop font family is retained; filament colors keep their meaning. This imports palette values, not arbitrary shell CSS/Lua. Flatpak has read-only access to the current-theme parents (`~/.local/state/omarchy/current` and legacy `~/.config/omarchy/current`), not your whole home directory. Missing/malformed theme data falls back to Adwaita.

## Updates and removal

Linux app updates are downloaded from GitHub; the Mac/Windows in-app updater does not manage them. Quit the app, install the newer downloaded Flatpak with `flatpak install --user ./<downloaded-file>.flatpak`, then launch again. Settings and credentials are retained. Update your companion from the same release too; GNOME requires signing out/in to load changed extension code. `flatpak update --user` updates runtime components, but does not fetch new Bambu Bar bundles from GitHub.

Before uninstalling, turn **Open at login** and widget sharing off, remove desktop widgets, then quit:

```sh
flatpak run dev.rolottr.BambuBar --quit
flatpak uninstall --user dev.rolottr.BambuBar
```

Flatpak keeps app data unless you explicitly request deletion. Remove optional companions with `gnome-extensions uninstall bambu-bar@rolottr.dev` or `kpackagetool6 --type Plasma/Applet --remove dev.rolottr.BambuBar`. On Hyprland remove the installed `~/.local/share/dbus-1/services/dev.rolottr.BambuBar.Hyprland.service` and `~/.local/share/bambu-bar-hyprland/anchor.py`; sign out/in to stop a running helper. To undo the compact rule, remove only the Bambu Bar `dofile(...)` include from `~/.config/hypr/hyprland.lua`, then its `bambu-bar.lua`, and reload Hyprland. Do not replace the entire configuration.

## Troubleshooting and development

- **No tray icon after installation:** sign out/in after first installing Flatpak; check the desktop has a StatusNotifier host or enable the GNOME companion. Do not copy temporary icons into your theme.
- **Printer offline after login:** unlock the desktop wallet/keyring and check LAN reachability. A locked KWallet can delay startup; the app waits for credential access.
- **Quick view unavailable on Hyprland:** install/update the helper from the same release, and check the supported Hyprland version above.
- **High CPU while animating:** the VMs use llvmpipe software rendering. Scrolling the model fully offscreen or hiding the window stops its animation work. VM figures do not predict hardware-GPU performance.
- **Need logs:** Flatpak stores app configuration/state under `~/.var/app/dev.rolottr.BambuBar/`. Do not post access codes or keyring files.

See [developer/build instructions and desktop validation](docs/linux.md) and [release packaging](docs/releasing.md#linux-release-assets). The GTK app, GNOME/Plasma companions and Hyprland host bridge are in `linux/`; the portable printer core remains in `windows/src/BambuBar.Core`.
