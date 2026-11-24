# Privacy and network inventory

Every outbound connection the agent or the app makes is listed here. A change that adds one must update this file.

| Component | Destination | When | Data sent |
|---|---|---|---|
| Agent (M1) | **none** on the network. Local only: `adb` (USB), `osascript` (Spotify, Music, volume); macOS CoreAudio/CoreMediaIO and the Windows camera/mic usage record (yes/no only, for the On Air light) | — | — |
| Panel (M1) | **none** (CSP `connect-src 'self'`) | — | — |
| Android app (M1) | `127.0.0.1:8765` only (the USB tunnel); no other host is reachable from the panel | — | device ID and key, only after the agent's proof checks out |

The agent listens on `127.0.0.1` only, **unless** a phone is allowed on Wi-Fi in the settings page. Only then does it also
listen on port 8766 (changeable in `wifi.json`) on the local network. That port is HTTPS only, accepts only phones whose
Keystore key was registered over USB, and only from private addresses (docs/design/WIFI.md). It makes no outbound
connections. It closes again when no phone is allowed on Wi-Fi. There's no telemetry.
