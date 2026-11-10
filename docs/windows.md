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
| `windows/tools` | `version.sh`/`version.ps1` (the shared version), `integration-test.sh` |
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

The app only runs on Windows. CI (`.github/workflows/build.yml`) tests Core on Linux and builds and publishes
both architectures on Windows.

## Version

There is one version line for the Mac and Windows: `MARKETING_VERSION` in `project.yml`. `Directory.Build.props`
reads it at build time (file version `X.Y.Z.0`), and `tools/version.sh` or `tools/version.ps1` print it for
scripts. Never write the version anywhere else.
