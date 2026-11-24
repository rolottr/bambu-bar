# Releasing Bambu Bar

Every version ships from the same commit on `main`: the Mac on two channels, which differ only in what the
`APP_STORE` compilation condition leaves out, and Windows (x64 and ARM64) on GitHub beside the Mac zip. One version
line (`MARKETING_VERSION` in `project.yml`), one GitHub release, one `appcast.xml` for both platforms.

The release marked **latest** must always have the Mac zip and an `appcast.xml` with a Mac item for its version:
every installed Mac copy updates from `releases/latest`. `scripts/check-release.sh` checks that (and, since every
release ships both platforms, both Windows installers and their items) before a release becomes latest.

Windows: see [windows.md](windows.md) for the build. CI builds both installers on every push; the release takes them
from the run of the tagged commit. Updates on Windows come from the same `appcast.xml` (one item per architecture,
`sparkle:os="windows-x64"` / `"windows-arm64"` on the enclosure, which Sparkle on the Mac skips), with the same
supporter-license rule and the same signing key.

| | GitHub (direct download) | Mac App Store |
| --- | --- | --- |
| Target / scheme | `BambuBar` / BambuBar | `BambuBarAppStore` / BambuBar App Store |
| Built by | `scripts/build.sh` + `scripts/package.sh` | `scripts/appstore.sh` |
| Signing | Developer ID, Hardened Runtime, notarized by Apple | Apple Distribution (Xcode automatic signing), sandboxed |
| Polar supporter license | Included: "Support this project", Manage License…, Hero Supporter badge | Compiled out (checked by `scripts/check-appstore-licensing.sh`) |
| Updates | Sparkle, **only with an active supporter license**. Without one the app never checks; people download the next zip from GitHub themselves | The Mac App Store |
| Settings, access codes | `~/Library/Preferences/dev.rolottr.BambuBar.plist`, login keychain | `~/Library/Containers/dev.rolottr.BambuBar`, data protection keychain (the two don't share codes) |
| Bambu Studio's codes | Read directly | Read after the person picks its folder |

Printer monitoring, controls, widgets and the camera are the same on both and never need a license. The license
itself (Polar checkout, activation, offline grace) is described in the README's Privacy section.

## Cutting a release

1. Raise `MARKETING_VERSION` in `project.yml`. Run `scripts/test.sh` and `scripts/integration-test.sh` (Docker).
   Run `scripts/check-translations.sh`: command-line builds don't update `shared/Localizable.xcstrings`, so new
   text would otherwise ship in English. It fails on strings missing from the catalog, unused, or not in every
   language. Text built as a plain `String` doesn't reach it; give it `String(localized:)`.
2. **GitHub:**
   - `NOTARY_PROFILE=notary scripts/package.sh` writes `build/Bambu-Bar-<version>-macos.zip` and its `.sha256`.
   - Push the commit and wait for CI to pass on it. `scripts/windows-artifacts.sh` downloads that run's Windows
     installers (`build/Bambu-Bar-<version>-windows-x64.exe`, `-arm64.exe`, their `.sha256`) and the Rainmeter
     source zip; it refuses a failed run or another version. When code signing exists (ledger §9), sign both
     installers with Authenticode here, and write their `.sha256` again.
   - `gh release create v<version> --latest=false` with the Mac zip, both installers, the three checksums and
     `rainmeter-<ver>-source.zip` (the GPL source offer for the Rainmeter the Windows app bundles), then
     `UPLOAD=1 scripts/appcast.sh` to sign the zip and the installers for the updaters and attach `appcast.xml`
     (it adds the Windows items when both installers are in `build/`).
   - `scripts/check-release.sh v<version> && gh release edit v<version> --latest`. Supporters on both platforms
     get the update from there.
   - Tag the commit (`v<version>`, annotated) and push the tag.
3. **App Store:**
   - `scripts/appstore.sh` retains the archive in `build/AppStore/Archives.noindex/` and exports the installer
     to `build/AppStore/Export/`, checking that the app is sandboxed and has no Sparkle. Run
     `scripts/check-appstore-licensing.sh` on the archived app. `UPLOAD=1 scripts/appstore.sh` uploads to App
     Store Connect. The build number is the date and time.
   - In App Store Connect: wait for processing (TestFlight shows the build), add the version under macOS App,
     select the build, paste "What's New", and submit for review. The listing texts are kept locally in
     `appstore/listing.md` (not in git).
4. Add a line to the log below.

## Linux release assets

The native Linux downloads have their own versioned preview tag. Keep the existing macOS and Windows release marked latest for their updaters. See [Linux installation](../LINUX.md) for desktop-specific downloads.

### Native GitHub downloads

Run `linux/scripts/build.sh` from the release source on x86_64 Linux, then `linux/scripts/package-native.sh gnome` or `linux/scripts/package-native.sh plasma` on Ubuntu 26.04 (`dpkg-deb`, `unzip`). On Omarchy run `linux/scripts/package-native.sh omarchy` as an ordinary user with `base-devel` installed. Collect the outputs under `build/linux/native-release/<version>/`.

The Omarchy build uses the generated AUR recipe to produce the pacman package. Publish the matching source payload (`.tar.zst`) alongside the installable `.pkg.tar.zst` and both Ubuntu packages. The generated `aur/PKGBUILD` and `aur/.SRCINFO` reference release tag `linux-v<version>-<pkgrel>`. Include both in the recipe download. Compute checksums over the final artifacts. Rebuild with a new package revision when replacing a published payload. Test each installed package in its target desktop before publishing.

### Flatpak and standalone companions

On a Linux x86_64 builder with .NET 10, the dependencies in [linux.md](linux.md#build-locally-on-linux), and GNOME Platform/SDK 50:

```sh
BAMBUBAR_COMMIT=$(git rev-parse HEAD) linux/scripts/build.sh
cd build/linux/release/linux-x64
sha256sum -c SHA256SUMS-linux.txt
```

The build generates the Flatpak, GNOME extension, Plasma widget, native KDE/Omarchy UI package and standalone Hyprland helper archive on every builder, without requiring GNOME or Plasma packaging tools. `SOURCE-linux.txt` records the full source commit, version and runtime. The Flatpak contains FFmpeg/layer-shell licenses and source notices. Upload the complete contents of this release directory when publishing: `Bambu-Bar-<version>-linux-x64.flatpak`, `bambu-bar@rolottr.dev.shell-extension.zip`, `Bambu-Bar.plasmoid`, `Bambu-Bar-native-desktop.tar.gz`, `Bambu-Bar-hyprland.tar.gz`, `SHA256SUMS-linux.txt` and `SOURCE-linux.txt`.

Before publication, build the tagged source on the GNOME, KDE and Omarchy hosts; run portable-core, tray, GNOME widget and relevant helper/theme checks; verify version identity and install/run the resulting packages. Desktop companion versions must match the app's release. Build and packaging scripts only write local artifacts: they never push, create a GitHub release or upload files. The existing CI workflow does not yet produce these Linux assets; use the validated Linux builders. ARM64 is supported by the build recipe but must not be advertised or attached as tested until built and run on ARM64 hardware.

## Release log

| Version | Channel | Build | Commit | Date | Status |
| --- | --- | --- | --- | --- | --- |
| 3.0.0 | GitHub (Mac) | 202611232356 | `v3.0.0` | 2026-11-24 | Published, latest release; includes the unreleased 2.4.0 (Appearance); zip SHA-256 `cd0fecbef09607f321b54ca7e818022dfa58fc89026abd04711cc6f9539496c4` |
| 3.0.0 | GitHub (Windows) | Local build on the Windows VM | `v3.0.0` | 2026-11-24 | Published, unsigned; x64 SHA-256 `3de678ba591c8327fa66733e4123369007b86467021adbee77eda15894866ade`, ARM64 SHA-256 `3843ceb7ec0376471f8dab8d89f506907e9120d792a8a7b14dd25739789f81ee`; with `rainmeter-5.0.0.5293-source.zip` |
| 3.0.0 | GitHub (Linux) | Local builds on the GNOME, KDE and Omarchy VMs | `linux-v3.0.0-1` | 2026-11-24 | Published, pre-release; GNOME .deb, Plasma .deb, Omarchy pacman package and AUR recipe; digests in its `SHA256SUMS` |
| 2.3.2 | GitHub (Mac) | 202611201347 | `v2.3.2` | 2026-11-20 | Published; zip SHA-256 `eb9f967121363e45b8cb2f9c046f8b37ea3ecb5b036249b8fa7ba5284d9a6c56` |
| 2.3.2 | GitHub (Windows) | CI run 44738757152 | `v2.3.2` | 2026-11-20 | Published, unsigned; x64 SHA-256 `2913e83b5f14a2143523eb1c58600c432851d08d7729b9a51ae2ba5b91648702`, ARM64 SHA-256 `99561b819a7ec4366fd23b62fe26d7f321e8bdbc3f7e8b2bfaccf394bdd11c36`; with `rainmeter-5.0.0.5293-source.zip` |
| 2.3.1 | GitHub (Mac) | 202611171153 | `v2.3.1` | 2026-11-17 | Published; zip SHA-256 `fbd858c13f6f165c8d44a205b782cdc0df59e58e7cc0c02cb5d96c39a583fe29` |
| 2.3.1 | GitHub (Windows) | CI run 44068579378 | `v2.3.1` | 2026-11-17 | Published, unsigned; x64 SHA-256 `11370366deaa4a7cb6e5183f69b53432a8a1184ab28464819f1da1a4e527e506`, ARM64 SHA-256 `1fe69ac7c6886c30c8d4e55a537ce70e7d460f5a438092b3481f73395ab82eb4`; with `rainmeter-5.0.0.5293-source.zip` |
| 2.3.0 | GitHub (Mac) | 202611161712 | `v2.3.0` | 2026-11-16 | Published; includes the unreleased 2.2.4; zip SHA-256 `095fb2dbae59f2263266c7dd242a8a0bd55f90c4244f358d0b64589c22e55857` |
| 2.3.0 | GitHub (Windows) | CI run 43904888989 | `v2.3.0` | 2026-11-16 | Published, unsigned; x64 SHA-256 `b741d6267f2708d2605cc37c2e635ddfb920320ece257000895007d8b0f3122c`, ARM64 SHA-256 `d7801fb5f1f861925833c208980faa1be8c0aa51a19cf89f4d3bc7b131b1713c`; with `rainmeter-5.0.0.5293-source.zip` |
| 2.2.3 | GitHub (Mac) | 202611151845 | `v2.2.3` | 2026-11-15 | Published; zip SHA-256 `25669f51df3da70a4a4e532bc05fd27f3a447f4e3232e336dab2ee61011f30a2` |
| 2.2.3 | GitHub (Windows) | CI run 43743999830 | `v2.2.3` | 2026-11-15 | Published, unsigned; x64 SHA-256 `e05550bd6ee979b97806f68b302c5520b3d0ef043c2d9f6947db7a46ff27e32e`, ARM64 SHA-256 `7a48fa8e0d528653634f48e2d382c8c8e76e6fea6c4c0d0d14ab7e7f2ea6048b`; with `rainmeter-5.0.0.5293-source.zip` |
| 2.2.2 | GitHub (Mac) | 202611140056 | `v2.2.2` | 2026-11-14 | Published; zip SHA-256 `477299d94e19c291b9fa9b59a4b3934b7a19133605213c72ab57fa0b695f4d9d` |
| 2.2.2 | GitHub (Windows) | CI run 43530745494 | `v2.2.2` | 2026-11-14 | Published, unsigned; x64 SHA-256 `22753ca0b58174b076341770ae88031e218074380a1f46f19106f47f5cf17a00`, ARM64 SHA-256 `478ea0a6a047296ef62def502af6b6eec75fda077bd69a98188ebd054a5c0203`; with `rainmeter-5.0.0.5293-source.zip` |
| 2.2.1 | GitHub (Mac) | 202611132005 | `v2.2.1` | 2026-11-13 | Published; zip SHA-256 `6974a02624f75cf46aa4bcd78abb7768e4ea16b4c9289536b5191b9140d982e3` |
| 2.2.1 | GitHub (Windows) | CI run 43488821108 | `v2.2.1` | 2026-11-13 | Published, unsigned; x64 SHA-256 `49fdc0dd59ec67b83c2ea11f6ef1347654155bb3afb46bd32b987333fa4e03b9`, ARM64 SHA-256 `dee8df092b4ab8c33a28cb327ab0d4d47a428b5139bbd7609bfe28eca4b16634`; with `rainmeter-5.0.0.5293-source.zip` |
| 2.2.0 | GitHub (Mac) | 202611121937 | `v2.2.0` | 2026-11-12 | Published; zip SHA-256 `dcc593cf8ee012f5f605f05c0838dad53f804b5198097391daba6ec19a6c836b` |
| 2.2.0 | GitHub (Windows) | CI run 43281811571 | `v2.2.0` | 2026-11-12 | Published, first Windows release, unsigned; x64 SHA-256 `b2580093caff03694afc0322e4d93ab28c883df8e44926b8bb45cc02981cd985`, ARM64 SHA-256 `5b2fed1ffa3d9457a023b6bcfd3267189bcc4a47008c54edb479a1d807f6145a`; with `rainmeter-5.0.0.5293-source.zip` |
| 2.1.1 | GitHub | 202611101352 | `v2.1.1` | 2026-11-10 | Published; zip SHA-256 `dbce0d7e11fc7b2b84ab47757621e856a8edf54b53bcba15fafc9a851790515d` |
| 2.1.0 | GitHub | 202611101251 | `v2.1.0` | 2026-11-10 | Published; zip SHA-256 `fb32096294681d48f1196fe54298515b3616021bf7e3ec354be7250cce120f0c` |
| 2.0.2 | GitHub | 202611090033 | `v2.0.2` | 2026-11-09 | Published; zip SHA-256 `fc64b2bc7b5c11019df3f13978604ad0c1f2b004df5124c3793a8caa42eafb03` |
| 2.0.1 | GitHub | 202611082312 | `v2.0.1` | 2026-11-08 | Published; zip SHA-256 `962b9b692b13c26da016a46b42741ddd9843aa606b67e494960a948f57284e8f` |
| 2.0.0 | GitHub | 202611011630 | 3eb45bb (`v2.0.0`) | 2026-11-01 | Published; zip SHA-256 `050b40c6ca849924c67852acd7f29a6280e046dbf1082182bd1a919677b55813` |
| 2.0.0 | App Store | 20261102.0103 | 3eb45bb | 2026-11-02 | Uploaded to App Store Connect; not yet submitted for review |
| 1.5.0 | GitHub | | 0749232 (`v1.5.0`) | 2026-10-26 | Published |
| 1.4.0 | App Store | | 05cb003 | 2026-10-24 | Uploaded; review status in App Store Connect |
| 1.3.1 | GitHub | | 2868f32 (`v1.3.1`) | 2026-10-24 | Published |
