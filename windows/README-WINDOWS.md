# BitChat for Windows — unsigned development port

Source baseline: `fd1437/bitchat_windows-macos` commit
`04995f68e470a18d1fc99f86fa326c2a87bab4f2`.

The SwiftUI/CoreBluetooth app cannot be directly compiled for Windows. This
port implements its wire protocols in C#/.NET 8, with WPF and Windows BLE.
The original Swift application remains unchanged.

## Install

Run **BitChat-Setup-unsigned.exe**. One offline installer contains x64 and ARM64
applications, .NET runtime, AAC codec and Tor. It chooses the native application,
installs for the current user, and creates shortcuts and an uninstaller. No
certificate/signing step or separate runtime installation is required.

Minimum OS: Windows 10 2004 on x64; Windows 11 on ARM64. The bundled AAC and
Tor helpers are x64 processes (Windows 11 ARM runs these through x64 emulation).
A BLE adapter is needed for offline mesh. VM Bluetooth support depends on a
usable Bluetooth adapter being exposed to Windows.

## Desktop lifecycle and connection recovery

On first normal launch, BitChat asks once whether to start at Windows sign-in.
The setting can be changed later in Settings. Automatic launch starts in the
notification area. Closing the window keeps networking active; use Open BitChat
or Quit BitChat in its tray menu. Reopening the executable activates the existing
instance. The uninstaller removes the current-user startup registration. The
custom title-bar controls remain clickable, and maximized windows use the active
monitor's work area so the taskbar remains accessible.

When an installation already exists, the installer offers repair/update, a
latest-release check, or uninstall. The app checks GitHub releases every ten
seconds with conditional requests, shows an update badge, downloads the unsigned
installer and verifies its published SHA-256 before closing, updating and
restarting. Old staged update files are cleaned up automatically.

BLE notifications respect the negotiated ATT payload (including 20-byte links).
Central sends use complete frames or prepare/execute long writes, preserving
the ATT offsets required by the Swift receiver. BLE sends
retire failed or timed-out central links so discovery can reconnect, and retry
stopped scans. File assemblies expire after inactivity rather than simply after
one minute of an ongoing transfer. A file accepted by no physical link reports
failure rather than success. Native diagnostics exercise hide/restore as well as
rendering. Real intermittent radio failures still require hardware verification.

## Modern interface, drafts and native notifications

The default interface uses a modern proportional-font layout with rounded message
cards and consistent controls. Settings > Hack mode restores the original
terminal colors, monospace font and compact layout. Both modes support light
and dark themes.

Selected files, pasted files/images, dropped files and recorded voice notes are
staged in the composer. Only Send transmits the draft. Failed attachments remain
in the draft for retry. Live hold-to-talk still transmits immediately. The draft
is in memory, with a maximum of 16 files / 8 MiB total and the protocol's existing
1 MiB per-file limit.

Windows notifications use the native Windows toast service, including an image
thumbnail, quick-reply input and Send action. Activations are handled while
BitChat is running or through a new background instance that forwards to the
existing instance. Custom alert windows are retained as a fallback. Windows
notification settings and Do Not Disturb apply. Native image thumbnails need a
local PNG; only bounded previews are written and these expire after one day.

The media viewer supports previous/next files, zoom, fit, copy image/file and
save. Ctrl+C copies, left/right navigate, and Ctrl+wheel zooms. The sidebar shows
the update download percentage and progress bar before checksum verification.

The old installer's "latest version" option could first install its bundled
version and then install a newer release. It now starts the existing app's update
check directly when the installed updater is at least 0.1.8. Older updater
builds are first repaired with the bundled version to fix their locked-file
rename bug. The finish page launches the application once. The x64 and ARM64 branches
only extract the matching executable; .NET may separately extract its embedded
runtime when the application starts.

## Composer and images

Enter sends a message; Shift+Enter inserts a line break. Incoming images show a
Tap to reveal placeholder, decode from DPAPI storage on demand, can be hidden
again, and open larger when clicked. Reveals persist only for the current app
session. Private image read receipts wait until reveal in the active window.
Canonical and legacy CONTENT length encodings match the Swift decoder. Native
UI diagnostics exercise Enter submission and reveal/hide with a real encrypted
PNG; portable tests deliver signed Swift-generated images through fragments.
The file picker supports several files in one selection. The conversation header
opens a multi-select file list for deleting local copies. Settings offer one-day,
seven-day, thirty-day or never-delete retention. New installations show message
text and image thumbnails in an on-screen notification with an inline reply box;
the existing hide-preview setting remains available. Nearby users' battery
percentages are shown only when they publish them, and sharing your own level is
off by default. The custom title bar follows the selected dark or light theme.
Notifications are emitted when the app is hidden or minimized, or another
conversation is selected; their position follows the app's monitor and avoids
the taskbar. A system-tray balloon is used if the rich in-app notification fails.
Typing indicators and emoji reaction synchronization are currently available to
other Windows port clients; iOS has no matching typing/reaction wire event.

## Implemented paths

- BLE central/peripheral discovery; v1/v2 packets; streaming notification assembly;
  bounded fragmentation/reassembly, relay TTL, ping and topology-based trace.
- Signed public chat; Noise XX private sessions, read/delivery receipts and
  encrypted attachments up to the upstream 1 MiB cap.
- Swift-compatible stable media IDs, DPAPI-protected durable receiver receipts and
  deletion tombstones; retries cannot recreate explicitly deleted media.
- Private-media capability proof tied to the current Noise generation, persistent
  downgrade pins, one-send legacy-clear consent and bounded reconnect retries
  (8 retained packets / 4 MiB / 120 seconds, two retries per file and per reconnect).
- IRC commands, blocking, mutual favorites and Nostr identity exchange.
- Signed Nostr location channels, presence, nearby notes and BitChat's proprietary
  authenticated private envelopes, including embedded message/receipt framing.
- Offline world map, manual location/precision selection and Windows location access.
- Creator-signed private groups, encrypted messages, invitations and epoch/key rotation.
- Signed mesh-board posts, expiry and author-only tombstones; GCS history sync.
- QR identity links/images, Noise challenge/response and verified-peer vouches.
- Noise X courier envelopes, one-time prekeys, persistent outbox, quota-bounded
  mutual-favorite courier storage and recipient handover.
- Optional Nostr gateway/mesh bridge with signed carriers and loop/rate limits.
- Kind-1401 private courier drops: throwaway signing keys, recipient-scoped durable
  dedup, relay-OK publication, bounded offline queue, UTC-day watches and direct
  handoff bound to the authenticated physical Noise connection.
- AAC voice notes/live push-to-talk using the iOS 16 kHz mono/16 kbit settings,
  with finalized M4A files and replacement of provisional live playback files.
- Optional Tor relay routing that fails closed when enabled.
- Windows account encryption (DPAPI) for keys, history, outbox, prekeys and media;
  foreground-only optional live voice autoplay and triple-click local identity/data wipe.

## Validation and remaining parity

**This is a development port, not a claim of complete iOS parity.** The portable
suite currently passes 193 interoperability/security checks: reference Noise
vectors; historical iOS/Android DM envelopes; signed packets, groups, board,
courier Noise X and GCS filters generated by the actual Swift implementation;
and simulated private-message/media exchanges over small links. Media coverage
includes process/session replacement, lost ACKs, replay suppression, storage
failures, corrupt receipt quarantine, deletion-journal recovery, capability
downgrades, per-contact ACK binding and retry quotas.

The Windows CI validates both architectures, launches the native application,
checks DPAPI/media-ledger persistence/AAC/M4A/Tor, and renders the WPF UI. Its diagnostics explicitly report
whether a Bluetooth adapter exists; a cloud runner cannot prove radio interop.
Real iOS ↔ Windows BLE/audio/reconnect tests and local-VM installation testing
remain required. Full iOS localization, native share-extension integration,
private-media in-flight cancellation/convergence, 100 MiB incoming-media storage
quota and seven-day retention, mesh-clear archive/watermark semantics, nearby-notes
permission/kill-switch behavior and several UI surfaces remain below full parity.
Bridge courier tests exercise simulated transports; physical relay-to-BLE
interoperability still requires device testing. English is the current Windows UI language.

## Build / native diagnostics

Install .NET SDK 8, CMake/Visual Studio C++ build tools, and NSIS 3.13, then run `./build.ps1` from PowerShell. Or:

    ./build-zlib.ps1 -Rid win-x64
    ./build-zlib.ps1 -Rid win-arm64
    dotnet run --project BitChat.InteropTests -c Release
    dotnet publish BitChat.Windows -c Release -r win-x64 -o publish/win-x64
    dotnet publish BitChat.Windows -c Release -r win-arm64 -o publish/win-arm64
    makensis -DPAYLOAD=/absolute/publish -DOUTPUT=/absolute/BitChat-Setup-unsigned.exe installer.nsi

On Windows, run the installed executable with:

    BitChat.exe --self-test C:\path\BitChat-diagnostics.json

This writes a native diagnostic report and a UI PNG, using disposable local test
state. It does not contact Nostr relays or establish Tor circuits. Codec source,
licenses and rebuild instructions accompany the installer. See `vendor/provenance.json`.


### Private-message recovery

Early ciphertext from the same in-progress Noise generation is bounded and held
until the exact session authenticates, rather than being marked as seen and
lost. A known peer sending ciphertext after the PC restarts triggers a fresh
handshake. Outbound messages wait while a replacement session negotiates.
Public messages arriving before a signed identity announcement are similarly
held with bounded size and lifetime; invalid signatures cannot poison dedup.
Bluetooth write requests are consumed serially per link. These cases have
regression tests; physical Mac/Windows radio reliability still needs device
validation.
