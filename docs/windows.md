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

## Version

There is one version line for the Mac and Windows: `MARKETING_VERSION` in `project.yml`. `Directory.Build.props`
reads it at build time (file version `X.Y.Z.0`), and `tools/version.sh` or `tools/version.ps1` print it for
scripts. Never write the version anywhere else.
