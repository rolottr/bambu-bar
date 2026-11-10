# Releasing Bambu Bar

Every version ships on two channels, built from the same commit on `main`. They differ only in what the
`APP_STORE` compilation condition leaves out.

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
2. **GitHub:**
   - `NOTARY_PROFILE=notary scripts/package.sh` writes `build/Bambu-Bar-<version>-macos.zip` and its `.sha256`.
   - `gh release create v<version>` with the zip and the checksum, then `UPLOAD=1 scripts/appcast.sh` to sign
     the zip for Sparkle and attach `appcast.xml`. Supporters get the update from there.
   - Tag the commit (`v<version>`, annotated) and push the tag.
3. **App Store:**
   - `scripts/appstore.sh` archives and exports, checking that the app is sandboxed and has no Sparkle; run
     `scripts/check-appstore-licensing.sh` on the archived app. `UPLOAD=1 scripts/appstore.sh` uploads to App
     Store Connect. The build number is the date and time.
   - In App Store Connect: wait for processing (TestFlight shows the build), add the version under macOS App,
     select the build, paste "What's New", and submit for review. The listing texts are kept locally in
     `appstore/listing.md` (not in git).
4. Add a line to the log below.

## Release log

| Version | Channel | Build | Commit | Date | Status |
| --- | --- | --- | --- | --- | --- |
| 2.1.0 | GitHub | 202611101251 | `v2.1.0` | 2026-11-10 | Published, latest release; zip SHA-256 `fb32096294681d48f1196fe54298515b3616021bf7e3ec354be7250cce120f0c` |
| 2.0.2 | GitHub | 202611090033 | `v2.0.2` | 2026-11-09 | Published; zip SHA-256 `fc64b2bc7b5c11019df3f13978604ad0c1f2b004df5124c3793a8caa42eafb03` |
| 2.0.1 | GitHub | 202611082312 | `v2.0.1` | 2026-11-08 | Published; zip SHA-256 `962b9b692b13c26da016a46b42741ddd9843aa606b67e494960a948f57284e8f` |
| 2.0.0 | GitHub | 202611011630 | 3eb45bb (`v2.0.0`) | 2026-11-01 | Published; zip SHA-256 `050b40c6ca849924c67852acd7f29a6280e046dbf1082182bd1a919677b55813` |
| 2.0.0 | App Store | 20261102.0103 | 3eb45bb | 2026-11-02 | Uploaded to App Store Connect; not yet submitted for review |
| 1.5.0 | GitHub | | 0749232 (`v1.5.0`) | 2026-10-26 | Published |
| 1.4.0 | App Store | | 05cb003 | 2026-10-24 | Uploaded; review status in App Store Connect |
| 1.3.1 | GitHub | | 2868f32 (`v1.3.1`) | 2026-10-24 | Published |
