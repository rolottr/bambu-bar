#!/bin/sh
# The shell requires a real plugin directory in the user's configuration.
# Leave plugins from manual installs alone. Refresh ours on package updates.
register_widget() (
    set -eu
    plugins="$HOME/.config/omarchy/plugins"
    target="$plugins/dev.rolottr.BambuBar"
    source=/usr/share/bambu-bar-omarchy
    stamp=.bambu-bar-package
    mkdir -p "$plugins"
    if [ -d "$target" ] && [ ! -L "$target" ] && [ -f "$target/$stamp" ]; then
        if ! cmp -s "$source/$stamp" "$target/$stamp"; then
            cp -R --no-preserve=ownership "$source/." "$target/"
        fi
    elif [ ! -e "$target" ] && [ ! -L "$target" ]; then
        stage=$(mktemp -d "$plugins/.bambu-bar.XXXXXXXX")
        trap 'rm -rf "$stage"' EXIT
        cp -R --no-preserve=ownership "$source/." "$stage/"
        mv -nT "$stage" "$target"
    fi
)
if [ -d /usr/share/omarchy/shell/Ui ] && [ "$(id -u)" != 0 ]; then
    register_widget || echo 'Bambu Bar could not register its Omarchy widget.' >&2
fi
exec /usr/lib/bambu-bar/bambu-bar "$@"
