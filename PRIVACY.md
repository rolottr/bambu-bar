# Bambu Bar Privacy Policy

_Last updated: November 25, 2026_

Bambu Bar is a macOS and Windows app that shows the status of Bambu Lab 3D printers on your local network. It is made by an independent developer (NINA LABS, LLC) and is not affiliated with Bambu Lab.

## The short version

Bambu Bar has no app analytics, advertising or tracking. Printer data stays on your network. Direct purchases use our checkout and licensing provider for payment, licensing and customer accounts. The Mac App Store edition uses Apple’s distribution.

## What the app stores, and where

Printer data is stored locally on your computer:

- **Printers you add** (name, IP address, serial number, model) and your preferences: in the app's local settings.
- **Printer access codes:** in your macOS Keychain or Windows Credential Manager (with a local DPAPI-encrypted fallback on Windows if Credential Manager is full). A code is only ever sent to the printer it belongs to, over an encrypted connection. The status connection checks the printer's certificate; the camera connection, like Bambu Studio's, goes to the printer's address without checking it.
- **Your last few prints** (job name, times, result), used for finish times and notifications: in a file in the app's own container.
- **A snapshot of each printer's status** for the desktop widgets: in a container shared only with Bambu Bar's widget.
- **Bambu Studio or OrcaSlicer settings:** if you choose that app's folder or settings file during setup, Bambu Bar reads the printers' access codes and addresses from it. It never changes the file, and it remembers only your permission to read it again.

Removing a printer in Printers deletes its stored access code.

## Network connections

- **Your printers:** Bambu Bar connects directly to each printer on your local network (MQTT over TLS, port 8883) and listens for the announcements printers send on the network (UDP port 2021) so it can find them.
- **Optional supporter licenses:** Our licensing provider receives the license key, activation ID and a random installation ID for activation and periodic checks, plus network information such as IP address. The key and validation date are stored locally on this computer (in a non-synchronizing macOS Keychain item or Windows Credential Manager).
- **Updates:** all direct copies (macOS and Windows) check `https://bambubar.app/appcast.xml` anonymously once a day unless turned off in Settings, sending no license or printer data. With an active website supporter license, downloading an update sends supporter license headers (`X-BambuBar-*`) to `https://bambubar.app/api/updates/download` to verify download access, never on the feed check; the Windows app removes them before following the redirect to the download host, while on macOS the system's download service may forward them to that host. No printer data is sent.
- **Mac App Store:** purchases and updates are managed by Apple.

The app does not use Bambu Lab's cloud services and does not need your Bambu Lab account.

## Data collected by the developer

We receive no printer telemetry or app usage statistics. Our checkout and licensing provider processes purchase details, including email, billing information and license activations, and makes purchase and licensing records available to us. The app never receives payment card details. See [our checkout provider’s privacy policy](https://polar.sh/legal/privacy).

## Children

The app does not profile users or collect printer telemetry, including from children.

## Changes

If this policy changes, the new version will be published at this address with a new date.

## Contact

Questions about privacy: [contact support](https://github.com/rolottr/bambu-bar/issues).
