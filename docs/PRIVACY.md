# Privacy and network inventory

Every outbound connection the agent or the app makes is listed here. A change that adds one must update this file.

| Component | Destination | When | Data sent |
|---|---|---|---|
| Agent (M1) | **none** on the network. Local only: `adb` (USB), `osascript` (Spotify, Music, volume) | — | — |
| Panel (M1) | **none** (CSP `connect-src 'self'`) | — | — |
| Android app (M1) | `127.0.0.1:8765` only (the USB tunnel); no other host is reachable from the panel | — | device ID and key, only after the agent's proof checks out |

The agent listens on `127.0.0.1` only. There's no telemetry.
