#!/usr/bin/env bash
# Run after build.sh on an x86_64 Ubuntu or Omarchy builder.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
build="$repo/build/linux"
publish="$build/linux-x64"
flavor="${1:?usage: package-native.sh gnome|plasma|omarchy}"
[[ "$(uname -m)" == x86_64 && "$(uname -s)" == Linux ]]
version="$("$publish/bambu-bar" --version | awk '{print $3}')"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
out="$build/native-release/$version"
mkdir -p "$out"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
root="$work/root"
share="$root/usr/share"
app=dev.rolottr.BambuBar
lib="$root/usr/lib/bambu-bar"
install -d "$lib" "$root/usr/bin"
cp -a "$publish/." "$lib/"
ln -s ../lib/bambu-bar/bambu-bar "$root/usr/bin/bambu-bar"
install -Dm644 "$repo/linux/packaging/$app.desktop" "$share/applications/$app.desktop"
install -Dm644 "$repo/linux/packaging/$app.metainfo.xml" "$share/metainfo/$app.metainfo.xml"
install -Dm644 "$repo/LICENSE" "$share/licenses/bambu-bar/LICENSE"
install -Dm644 "$publish/AppIcon.png" "$share/icons/hicolor/256x256/apps/$app.png"
install -d "$share/icons/hicolor/scalable/status"
cp "$publish"/tray-icons/*.svg "$share/icons/hicolor/scalable/status/"
depends="libadwaita-1-0 (>= 1.9), libgtk-4-1, libegl1, libicu78, libssl3t64, zlib1g, libgssapi-krb5-2, libstdc++6, libsecret-tools"
case "$flavor" in
    gnome)
        "$repo/linux/scripts/package-gnome-extension.sh"
        mkdir -p "$share/gnome-shell/extensions"
        unzip -q "$build/bambu-bar@rolottr.dev.shell-extension.zip" -d "$share/gnome-shell/extensions/bambu-bar@rolottr.dev"
        ;;
    plasma|omarchy)
        native="$share/bambu-bar-native"
        mkdir -p "$native/common/models" "$share/dbus-1/services"
        cp -a "$repo/linux/native/." "$native/"
        cp "$publish/AppIcon.png" "$native/common/"
        tar -xzf "$repo/shared/Printers/qt-models.tar.gz" -C "$native/common/models"
        printf '[D-BUS Service]\nName=%s.Native\nExec=/usr/bin/python3 /usr/share/bambu-bar-native/host.py --system-package\n' "$app" > "$share/dbus-1/services/$app.Native.service"
        if [[ "$flavor" == plasma ]]; then
            "$repo/linux/scripts/package-plasma-widget.sh"
            mkdir -p "$share/plasma/plasmoids"
            unzip -q "$build/Bambu-Bar.plasmoid" -d "$share/plasma/plasmoids/$app"
            depends+=", python3-gi, python3-pyqt6.qtquick, qml6-module-org-kde-kirigami, qml6-module-qtquick3d, qml6-module-qtquick-controls, qml6-module-qtquick-dialogs, qml6-module-qtquick-layouts, qml6-module-qtquick-window, qml6-module-qtqml-workerscript"
        else
            # Quickshell imports these modules through its configuration root.
            ln -s ../common "$native/omarchy/common"
            for module in Ui Commons; do ln -s "/usr/share/omarchy/shell/$module" "$native/omarchy/$module"; done
            plugin="$share/bambu-bar-omarchy"
            mkdir -p "$plugin"
            cp -a "$repo/linux/omarchy/." "$plugin/"
            printf '%s-1\n' "$version" > "$plugin/.bambu-bar-package"
            sed -i "s/\"version\": \"[^\"]*\"/\"version\": \"$version\"/" "$plugin/manifest.json"
            cp "$native/omarchy/Kit.qml" "$plugin/"
            cp "$repo/linux/gnome/widget-layout.js" "$plugin/WidgetLayout.mjs"
            cp -a "$publish/tray-icons" "$plugin/"
            # Use real files here so QML resolves imports from the plugin directory.
            cp -a "$native/common" "$plugin/"
            cp "$native/bridge.py" "$plugin/"
            rm "$root/usr/bin/bambu-bar"
            install -m755 "$repo/linux/packaging/omarchy-launch.sh" "$root/usr/bin/bambu-bar"
        fi
        ;;
    *) echo "Unknown desktop: $flavor" >&2; exit 2 ;;
esac
find "$root" -type d -name __pycache__ -prune -exec rm -r {} +
if [[ "$flavor" == omarchy ]]; then
    archive="Bambu-Bar-$version-omarchy-x86_64.tar.zst"
    tar --owner=0 --group=0 --zstd -cf "$out/$archive" -C "$root" usr
    digest="$(sha256sum "$out/$archive" | cut -d' ' -f1)"
    mkdir -p "$out/aur"
    sed -e "s/@VERSION@/$version/g" -e "s/@SHA256@/$digest/g" "$repo/linux/packaging/arch/PKGBUILD.in" > "$out/aur/PKGBUILD"
    (cd "$out/aur" && makepkg --printsrcinfo > .SRCINFO && SRCDEST="$out" PKGDEST="$out" BUILDDIR="$work" makepkg --force)
else
    mkdir "$root/DEBIAN"
    cat > "$root/DEBIAN/control" <<CONTROL
Package: bambu-bar-$flavor
Version: $version-1
Architecture: amd64
Maintainer: Bambu Bar <rolottr@users.noreply.github.com>
Provides: bambu-bar
Conflicts: bambu-bar
Section: utils
Priority: optional
Depends: $depends
Homepage: https://github.com/rolottr/bambu-bar
Description: Bambu Lab printer monitor for the $flavor desktop
CONTROL
    dpkg-deb --root-owner-group -Zzstd --build "$root" "$out/bambu-bar-${flavor}_${version}-1_amd64.deb"
fi
