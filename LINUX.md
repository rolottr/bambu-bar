# Install Bambu Bar on Linux

Choose one download for your desktop. These packages include the app, its desktop integration and the .NET runtime. They target Intel and AMD 64-bit PCs.

| Desktop | Download | Install |
| --- | --- | --- |
| Ubuntu 26.04 with GNOME 50 | [Ubuntu GNOME .deb](https://github.com/rolottr/bambu-bar/releases/download/linux-v2.3.2-1/bambu-bar-gnome_2.3.2-1_amd64.deb) | Open the download with **App Center** and select **Install**. |
| Kubuntu 26.04 or Ubuntu 26.04 with KDE Plasma 6.6 | [Ubuntu Plasma .deb](https://github.com/rolottr/bambu-bar/releases/download/linux-v2.3.2-1/bambu-bar-plasma_2.3.2-1_amd64.deb) | Open the download with **Discover** and select **Install**. |
| Omarchy 4.5 with its Quickshell bar | [Omarchy package](https://github.com/rolottr/bambu-bar/releases/download/linux-v2.3.2-1/bambu-bar-bin-2.3.2-1-x86_64.pkg.tar.zst) | Open a terminal in the download folder and run the command below. |

```sh
sudo pacman -U ./bambu-bar-bin-2.3.2-1-x86_64.pkg.tar.zst
```

Open **Bambu Bar** from the launcher. Add your printer on the same LAN and enter its access code when prompted.

## Show printer status in the panel

- **GNOME** — Sign out and back in, then enable **Bambu Bar** in the Extensions app.
- **Plasma** — Right-click the panel, enter **Edit Mode**, choose **Add Widgets** and add **Bambu Bar**.
- **Omarchy** — Launch Bambu Bar once to register its widget, then enable **Bambu Bar** in the shell’s bar configuration. If needed, run `omarchy-shell shell rescanPlugins` followed by `omarchy plugin enable dev.rolottr.BambuBar --section right`.

## Updates and other install options

Install a newer download for the same desktop over the existing package. To uninstall, remove **bambu-bar-gnome**, **bambu-bar-plasma** or **bambu-bar-bin** with your package manager. On Omarchy, disable the widget before removing the package. You can then remove the user-local widget directory `~/.config/omarchy/plugins/dev.rolottr.BambuBar/`.

If your Ubuntu file manager opens the package as an archive, open a terminal in the download folder and run `sudo apt install ./bambu-bar-gnome_2.3.2-1_amd64.deb` (use the Plasma filename on KDE).

The GitHub release includes the Arch binary and the checksum-pinned `PKGBUILD` for AUR maintainers. Until an AUR listing is available, use the GitHub download above. [Linux release and checksums](https://github.com/rolottr/bambu-bar/releases/tag/linux-v2.3.2-1)

For Fedora, openSUSE, Debian or another desktop, see the [Flatpak guide](docs/linux-flatpak.md). It includes the local build path when a release has no Flatpak asset. ARM64 remains unvalidated.

If you tested the earlier Flatpak, quit it before launching the native package. The two builds use separate app data. On KDE and Omarchy, remove the earlier user-installed desktop companions as described in the [Flatpak removal instructions](docs/linux-flatpak.md#updates-and-removal) so the desktop loads the packaged versions.
