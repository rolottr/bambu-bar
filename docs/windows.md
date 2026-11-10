# Bambu Bar for Windows: building

The Windows app lives in `windows/` and shares the Mac's printer data, texts and test fixtures in `shared/`.
It needs Windows 10 1903 or later, or Windows 11, on x64 or ARM64.

## Layout

| Path | What |
| --- | --- |
| `windows/src/BambuBar.Core` | The printer layer and app logic (`net10.0`, no Windows APIs: builds and tests on macOS and Linux) |
| `windows/src/BambuBar.Windows` | The WPF app (`BambuBar.exe`): tray icon, panel, window, widgets host |
| `windows/tests/BambuBar.Core.Tests` | Unit tests, against the fixtures in `shared/ModelReports` |
| `windows/tests/BambuBar.Integration.Tests` | Tests against the Docker simulators in `shared/scripts/integration`; they skip without them |
| `windows/ffmpeg` | `build.sh` builds the minimal FFmpeg the camera decodes with; `version.txt` pins its source, toolchain (SHA-256) and configure flags |
| `windows/installer` | `BambuBar.iss` (Inno Setup), `notices.ps1` (THIRD-PARTY-NOTICES.txt), `smoke.ps1` (CI install test), `licenses/`, `art/` (the wizard's pictures and `make-art.py`, which draws them) |
| `windows/tools` | `version.sh`/`version.ps1` (the shared version), `integration-test.sh`, `BambuBar.Cli` (Core's commands on any platform), `windows-strings.txt` (Windows texts waiting for translation) |
| `shared/` | Printer art and models, HMS texts, certificates, filament names, `Localizable.xcstrings`, fixtures, simulators |

## Build and test

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). On a Mac without admin rights,
`dotnet-install.sh --channel 10.0 --install-dir ~/.dotnet` works too. Run `dotnet` from `windows/`: its
`global.json` pins the SDK and selects Microsoft.Testing.Platform for `dotnet test`.

```sh
cd windows
dotnet build BambuBar.slnx                 # everything; the WPF app builds on macOS and Linux too
dotnet test --solution BambuBar.slnx       # unit tests (integration tests skip)
tools/integration-test.sh                  # starts the simulators in Docker, runs the integration tests
dotnet publish src/BambuBar.Windows -c Release -r win-x64     # or win-arm64: self-contained BambuBar.exe
```

`--probe <host> <serial> <access-code> [seconds] [--insecure-tls] [--raw-filament] [--raw-all]` connects like the
app and prints the Mac probe's summary, line for line: `BambuBar.exe --probe …` on Windows, or
`dotnet run --project tools/BambuBar.Cli -- --probe …` anywhere (`--insecure-tls` for the mock fleet). Core's
printer store also handles the Mac's `--demo [scenario] [--fleet]`, `--connect <host> <serial> <code>` and
`--emulator <BambuStudio.conf> [--add-all]` (none of them saves anything); the app passes them on once its shell
exists (P3.1).

`--probe-camera <host> <serial> <access-code> [seconds] [--jpeg|--rtsp] [--model <name>] [--insecure-tls] [--out <file.png>]`
opens the camera like the app, logs the handshake and the frames, decodes them with FFmpeg and saves the first picture
as a PNG (`bambubar-camera.png` in the temporary folder). Against the mock camera of `tools/integration-test.sh --keep`:
`dotnet run --project tools/BambuBar.Cli -- --probe-camera 127.0.0.1 01P00C000000001 12345678 5 --rtsp --insecure-tls`.

## Camera decoding (FFmpeg)

The camera is decoded with FFmpeg 9 (ledger D17: LGPL, dynamically linked, unmodified): libavcodec 63, libavutil 61
and libswscale 10, bound by hand in `Core/Camera/Decoding`. The app loads them from `ffmpeg\` beside `BambuBar.exe`.
That's a minimal build: only the h264, hevc and mjpeg decoders and swscale, no formats, network, filters, devices,
programs, encoders, hardware acceleration or external libraries, so it is plain LGPL 2.1 or later and its source is
FFmpeg's tarball alone. About 3.5 MB on x64 and 2.4 MB on ARM64. `windows/ffmpeg/build.sh` cross-compiles it with
llvm-mingw from the source, toolchain and nasm pinned (with their SHA-256) in `windows/ffmpeg/version.txt`, which
also holds the configure flags. It checks that the DLLs export every function `FFmpeg.cs` binds and import only
Windows' own DLLs, and writes them with `LICENSE.txt` and `SOURCE.txt` (the source offer) to `<out>/<rid>/`:

```sh
windows/ffmpeg/build.sh ~/tmp/ffmpeg-work ~/tmp/ffmpeg win-x64 win-arm64   # macOS or Linux x86_64, about 2 minutes
```

The work folder keeps the downloads and the toolchain; keep it outside the repository. The DLLs come out byte for
byte the same from any folder on the same kind of host; a Mac's and Linux's differ only in the last bit of one
floating-point constant (clang folds it with the host's maths library) and so in the build ID. CI builds them on Linux (cached until `version.txt`, `build.sh` or `FFmpeg.cs`
change), and the Windows jobs copy `<rid>/` into `ffmpeg\`. The app loads `swresample-7.dll` too if it's there, but
the minimal build doesn't have or need it.

Elsewhere the app uses Homebrew's FFmpeg on a Mac (`brew install ffmpeg`) or the system's on Linux;
`BAMBUBAR_FFMPEG=<folder>` points anywhere else (on Windows, at `<out>/win-x64`) and `BAMBUBAR_FFMPEG_LOG=1` lets
FFmpeg's warnings through. Without FFmpeg 9 the decoder tests skip and the camera says it needs FFmpeg.

The app only runs on Windows. CI (`.github/workflows/build.yml`) tests Core on Linux and builds and publishes
both architectures on Windows.

## Installer

`windows/installer/BambuBar.iss` (Inno Setup 6.3+) builds one installer per architecture,
`Bambu-Bar-<ver>-windows-x64.exe` and `…-arm64.exe`, from the install image CI stages in `build/windows/<rid>/`:

| In the image | From |
| --- | --- |
| `BambuBar.exe`, the .NET runtime, `Assets\`, `AppIcon.png`, `LICENSE.txt`, `Skins\BambuBar\`, `Skins\version.txt` | `dotnet publish` (the csproj adds the skins from `rainmeter/Skins/BambuBar`, their version file = app version + a hash of the skin files, and the license) |
| `ffmpeg\` | the `ffmpeg` job (above) |
| `Rainmeter\` | `rainmeter/ci/payload.ps1`: the installer pinned in `rainmeter/version.txt`, SHA-256 checked, installed portably, trimmed to `Rainmeter.exe`, `Rainmeter.dll` and `Languages\1033.rmlang` (the skins use only built-in measures). x64 only: the ARM64 installer carries it too (emulated) |
| `THIRD-PARTY-NOTICES.txt` | `windows/installer/notices.ps1`: Rainmeter (GPL-2.0 + where its source is), FFmpeg (`LICENSE.txt` + `SOURCE.txt`), Nunito (OFL), Fluent icons (MIT), and every package and runtime pack in `BambuBar.deps.json`, with the license files from the restored packages (`windows/installer/licenses/` for the ones that ship none) |

```powershell
pwsh rainmeter/ci/payload.ps1 -Out build/windows/win-x64/Rainmeter -SourceDir build/release   # + rainmeter-<ver>-source.zip
pwsh windows/installer/notices.ps1 -App build/windows/win-x64
ISCC /DAppVersion=$(pwsh windows/tools/version.ps1) /DArch=x64 /DSourceDir=$PWD\build\windows\win-x64 /DOutputDir=$PWD\build\installer windows\installer\BambuBar.iss
```

The wizard's pictures are committed BMPs, one per Windows scale: the welcome and finish pages show the printer at work on
a green glow with the name, the other pages the app icon. To redraw them (Pillow needed):
`dotnet run --project windows/tools/BambuBar.Cli -- --art /tmp/printer.png 588 696`, then
`python3 windows/installer/art/make-art.py /tmp/printer.png`.

What it does:

- **Per user, no admin**: `%LOCALAPPDATA%\Programs\Bambu Bar`, Windows 10 1903+; the x64 installer runs only on x64
  Windows, the ARM64 one only on ARM64. A Start menu shortcut carrying the AppUserModelID `dev.rolottr.BambuBar`
  (toasts), `bambubar://` and the AppUserModelID's name and icon under `HKCU\Software\Classes` (the app writes them
  too). An update replaces `Assets\`, `ffmpeg\`, `Rainmeter\` and `Skins\` wholesale.
- **Before copying**, it stops a `BambuBar.exe` and a `Rainmeter.exe` running from that folder (and no other: a
  Rainmeter the person runs themselves is left alone).
- **Silent update**: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`. If the app was running it is started again
  afterwards, with `--background` when it had been started with it (at sign-in). Interactive installs offer
  "Launch Bambu Bar" on the last page.
- **Uninstall** stops both, removes the shortcut, the folder, `bambubar://`, the AppUserModelID key, the sign-in
  entry (`Run` and Task Manager's `StartupApproved\Run` value) and the `.rmskin` association our Rainmeter registers
  for itself when the person has no Rainmeter. Settings, history and widgets (`%APPDATA%\Bambu Bar`,
  `%LOCALAPPDATA%\Bambu Bar`) stay unless the person says yes to removing them, or `/PURGE=1` on a silent uninstall;
  Credential Manager entries always stay.
- **Signing** (ledger §9) isn't done yet. The hook: sign `BambuBar.exe` in the image, then
  `ISCC "/Sbambubar=signtool sign /fd SHA256 /f <pfx> /p <pw> /tr http://timestamp.digicert.com /td SHA256 $f" /DSignTool=bambubar …`
  signs the installer and its uninstaller.

CI builds both installers in the `windows` job, then on x64 runs `windows/installer/smoke.ps1`: silent install,
checks the files, registry keys and shortcut, runs the installed `BambuBar.exe --snapshot`, silent uninstall, and
checks that only the user's data is left. Artifacts: `Bambu-Bar-windows-installer-<rid>` (installer + `.sha256`),
`rainmeter-source` (for the release) and `Bambu-Bar-windows-install-smoke` (logs, snapshots).

Before that, `BambuBar.exe --a11y-check <dir>` (`A11yCheck.cs`) opens the panel, its menus, the stop confirmation
and every page of the window over the demo fleet, and fails on a control without a name, a glyph or meaningless
name, two same-named controls in one named group, an unnamed picture, a control Tab never reaches (or a Tab stop
without a name or focus rectangle), and Enter, Space or Esc not opening or closing what they should. It reads WPF's
automation peers and, per window, the UI Automation client's tree. Artifact: `Bambu-Bar-windows-a11y`
(`a11y-report.txt` with each scene's tree, names, roles, states and Tab order; `a11y-problems.txt`).

## Version

There is one version line for the Mac and Windows: `MARKETING_VERSION` in `project.yml`. `Directory.Build.props`
reads it at build time (file version `X.Y.Z.0`), and `tools/version.sh` or `tools/version.ps1` print it for
scripts. Never write the version anywhere else.
