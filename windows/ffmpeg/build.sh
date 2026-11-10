#!/usr/bin/env bash
# Builds the minimal FFmpeg the Windows camera decodes with (ledger D17: LGPL, dynamically linked): FFmpeg's source
# tarball, cross-compiled with llvm-mingw for win-x64 and win-arm64, shared, with only the h264, hevc and mjpeg
# decoders, libswscale and libavutil. Every download is pinned with its SHA-256 in version.txt beside this script,
# and so are the configure flags. Runs on macOS (arm64 or x86_64) and Linux x86_64; needs curl, tar, xz, make and
# a host C compiler (for nasm, which assembles FFmpeg's x86 SIMD code).
#
#   windows/ffmpeg/build.sh <work-dir> <out-dir> [win-x64] [win-arm64]
#
# <work-dir> holds the downloads (one folder each), the toolchain and the build trees; keep it outside the
# repository, and reuse it to skip downloads that are already there and verified. <out-dir>/<rid>/ gets what the
# app ships in its ffmpeg\ folder: the DLLs, LICENSE.txt (LGPL 2.1) and SOURCE.txt (the source offer: what was built,
# from what, how). Point BAMBUBAR_FFMPEG at it to run the decoder tests against it. The build fails if a DLL lacks
# a function FFmpeg.cs binds, or imports a DLL Windows doesn't have.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
pins="$here/version.txt"
binding="$here/../src/BambuBar.Core/Camera/Decoding/FFmpeg.cs"

if [ $# -lt 2 ]; then
    sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//' >&2
    exit 2
fi
mkdir -p "$1" "$2"
work="$(cd "$1" && pwd)"
out="$(cd "$2" && pwd)"
shift 2
rids=("$@")
[ ${#rids[@]} -gt 0 ] || rids=(win-x64 win-arm64)

pin() {
    local value
    value="$(sed -n "s/^$1=//p" "$pins" | head -n 1)"
    [ -n "$value" ] || { echo "version.txt has no $1" >&2; exit 1; }
    printf '%s' "$value"
}

sha256() {
    if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d ' ' -f 1; else shasum -a 256 "$1" | cut -d ' ' -f 1; fi
}

jobs="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)"

# fetch <name> <url> <sha256>: downloads into <work>/download/<name>/ unless it's there already, checks the
# SHA-256 and prints the file's path.
fetch() {
    local dir="$work/download/$1" file
    file="$dir/$(basename "$2")"
    mkdir -p "$dir"
    if [ ! -f "$file" ] || [ "$(sha256 "$file")" != "$3" ]; then
        echo "Downloading $2" >&2
        curl -fsSL --retry 3 -o "$file.part" "$2"
        mv "$file.part" "$file"
    fi
    local actual
    actual="$(sha256 "$file")"
    if [ "$actual" != "$3" ]; then
        echo "$2 has SHA-256 $actual, expected $3" >&2
        exit 1
    fi
    printf '%s' "$file"
}

# unpack <archive> <dir>: a fresh copy of the archive's single top folder at <dir>.
unpack() {
    rm -rf "$2" "$2.tmp"
    mkdir -p "$2.tmp"
    tar -xf "$1" -C "$2.tmp"
    mv "$2.tmp"/* "$2"
    rmdir "$2.tmp"
}

# The toolchain for this host.
case "$(uname -s)-$(uname -m)" in
    Darwin-*) host=macos ;;
    Linux-x86_64) host=linux-x86_64 ;;
    *) echo "No llvm-mingw pinned for $(uname -s) $(uname -m)" >&2; exit 1 ;;
esac
toolchain="$work/llvm-mingw-$(pin llvm-mingw-version)"
if [ ! -x "$toolchain/bin/clang" ]; then
    unpack "$(fetch llvm-mingw "$(pin "llvm-mingw-url-$host")" "$(pin "llvm-mingw-sha256-$host")")" "$toolchain"
fi
export PATH="$toolchain/bin:$PATH"

# nasm, for x64 only (ARM64's assembly goes through clang), alone in its folder for PATH.
nasm="$work/nasm-$(pin nasm-version)/bin"
if [[ " ${rids[*]} " == *" win-x64 "* ]] && [ ! -x "$nasm/nasm" ]; then
    nasm_source="$work/nasm-$(pin nasm-version)/src"
    unpack "$(fetch nasm "$(pin nasm-url)" "$(pin nasm-sha256)")" "$nasm_source"
    (cd "$nasm_source" && ./configure && make -j"$jobs" nasm) >"$work/nasm.log" 2>&1 || { tail -n 40 "$work/nasm.log" >&2; exit 1; }
    mkdir -p "$nasm"
    cp "$nasm_source/nasm" "$nasm/"
fi

# FFmpeg's source, unmodified.
version="$(pin ffmpeg-version)"
source="$work/ffmpeg-$version"
unpack "$(fetch ffmpeg "$(pin ffmpeg-url)" "$(pin ffmpeg-sha256)")" "$source"

read -r -a flags <<<"$(pin configure)"
# The functions FFmpeg.cs binds, per library: "avcodec avcodec_open2", …
exports="$(sed -n 's/.*NativeLibrary\.GetExport(\([a-z]*\), nameof(\([A-Za-z0-9_]*\))).*/\1 \2/p' "$binding")"
[ -n "$exports" ] || { echo "Found no GetExport calls in $binding" >&2; exit 1; }
read -r -a dlls <<<"$(pin dlls)"

for rid in "${rids[@]}"; do
    case "$rid" in
        win-x64) arch=x86_64 ;;
        win-arm64) arch=aarch64 ;;
        *) echo "Unknown runtime $rid (win-x64 or win-arm64)" >&2; exit 1 ;;
    esac
    triple="$arch-w64-mingw32"
    build="$work/build-$rid"
    install="$work/install-$rid"
    rm -rf "$build" "$install"
    mkdir -p "$build"
    # Nothing of this machine in the DLLs: the source is ./src to the compiler (__FILE__ is in assert messages),
    # nasm is found on PATH, and the install goes through DESTDIR, so the configure line avutil_configuration()
    # returns is the same everywhere.
    ln -s "$source" "$build/src"
    configure=(--enable-cross-compile --target-os=mingw32 --arch="$arch" --cross-prefix="$triple-" --cc="$triple-clang"
        "${flags[@]}")
    echo "Configuring FFmpeg $version for $rid" >&2
    (cd "$build" && PATH="$nasm:$PATH" src/configure "${configure[@]}" >configure.log) ||
        { tail -n 40 "$build/ffbuild/config.log" >&2; exit 1; }
    echo "Building FFmpeg $version for $rid" >&2
    (PATH="$nasm:$PATH" make -C "$build" -j"$jobs" && make -C "$build" install DESTDIR="$install") \
        >"$build/make.log" 2>&1 || { tail -n 40 "$build/make.log" >&2; exit 1; }
    install="$install/usr/local"

    dest="$out/$rid"
    rm -rf "$dest"
    mkdir -p "$dest"
    for dll in "${dlls[@]}"; do cp "$install/bin/$dll" "$dest/"; done
    for built in "$install"/bin/*.dll; do
        [ -f "$dest/$(basename "$built")" ] || { echo "The build made $(basename "$built"), which version.txt's dlls lack" >&2; exit 1; }
    done

    # Every function FFmpeg.cs binds is exported by the library it looks in.
    missing=()
    while read -r library function; do
        file="$(printf '%s\n' "${dlls[@]}" | grep "^$library-" || true)"
        [ -n "$file" ] || { echo "version.txt's dlls lack $library" >&2; exit 1; }
        # The table is read whole first: `objdump | grep -q` would let grep exit at the first match, objdump die of
        # SIGPIPE on large tables (avutil) and pipefail report the function missing.
        table="$(llvm-objdump -p "$dest/$file" | awk '/^Export Table:/ { e = 1 } e && NF == 3 { print $3 }')"
        grep -x -F -- "$function" <<<"$table" >/dev/null || missing+=("$file:$function")
    done <<<"$exports"
    [ ${#missing[@]} -eq 0 ] || { echo "Missing exports: ${missing[*]}" >&2; exit 1; }

    # And they import nothing but Windows (the UCRT, kernel32, bcrypt…) and each other: no MinGW runtime DLLs.
    for dll in "${dlls[@]}"; do
        foreign="$(llvm-objdump -p "$dest/$dll" | sed -n 's/^ *DLL Name: //p' |
            grep -v -i -E '^(kernel32|user32|advapi32|bcrypt|ole32|shell32|api-ms-win-[a-z0-9-]+)\.dll$' |
            grep -v -x -F "$(printf '%s\n' "${dlls[@]}")" || true)"
        [ -z "$foreign" ] || { echo "$dll imports $foreign" >&2; exit 1; }
    done

    cp "$source/COPYING.LGPLv2.1" "$dest/LICENSE.txt"
    {
        echo "Bambu Bar decodes its camera with FFmpeg $version (https://ffmpeg.org), unmodified, under the GNU Lesser"
        echo "General Public License version 2.1 or later (LICENSE.txt). These libraries are loaded at run time; you may"
        echo "replace them with your own build of the same major versions (${dlls[*]})."
        echo
        echo "Source: $(pin ffmpeg-url)"
        echo "SHA-256: $(pin ffmpeg-sha256)"
        assembler=""
        [ "$rid" != win-x64 ] || assembler=", nasm $(pin nasm-version) (https://www.nasm.us)"
        echo "Toolchain: llvm-mingw $(pin llvm-mingw-version) (https://github.com/mstorsjo/llvm-mingw)$assembler"
        echo "Configured with: ${configure[*]}"
        echo "then built with make && make install, keeping only the DLLs above (Bambu Bar's windows/ffmpeg/build.sh)."
    } >"$dest/SOURCE.txt"

    echo "$rid:" >&2
    for dll in "${dlls[@]}"; do
        printf '  %-16s %9d bytes  %s\n' "$dll" "$(wc -c <"$dest/$dll" | tr -d ' ')" "$(sha256 "$dest/$dll")" >&2
    done
    printf '  total            %9d bytes\n' "$(cat "${dlls[@]/#/$dest/}" | wc -c | tr -d ' ')" >&2
done
