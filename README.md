# DeskPanel

An old Android phone as a desk panel and safe control surface for your computer, connected over USB.
It's an independent design informed by a study of PhoneBox; see `docs/THREAT_MODEL.md` for the security model. **Install: [`docs/INSTALL.md`](docs/INSTALL.md)** (download, check, `deskpanel-agent install`). Checking a download: `docs/VERIFY.md`. Making a release: `docs/RELEASING.md`.

**Status: 0.1.0 released (M4: Windows adapters, signed releases, install/uninstall).** Verified on a real phone. Phones are enrolled over adb after a yes in a Mac dialog. Each phone has its own key, and the app checks the agent's proof before sending it. The dashboard shows a clock, Pomodoro timer, CPU/memory/network and now playing (Spotify/Music). Phones can control media, volume, approved apps and key combinations, each only after you grant it in the settings page (`deskpanel-agent settings`, or `tools/admin.sh` in development). Every control is rate-limited, deduplicated and logged. Layouts are edited in the settings page (Layout tab): drag, resize, set options; the agent checks every save against the widget catalogue (`agent/DeskPanel.Agent/Layout/widgets.json`) and the phone shows it within seconds.

## Layout
```
agent/DeskPanel.Agent         .NET 10 agent (loopback only)
agent/DeskPanel.Agent.Tests   xUnit: route classification, request gate, options
panel/                        TypeScript + Vite phone dashboard (widget registry, 12×6 grid)
android/                      Kotlin WebView app: agent proof check, adb enrolment receiver, Keystore key vault
docs/                         THREAT_MODEL.md, PRIVACY.md
```

## Develop
Requires a .NET SDK 10 or newer, and Node 22. The agent targets `net10.0`. With only a newer runtime installed (for example Homebrew's .NET 11), run tests and the agent with `DOTNET_ROLL_FORWARD=Major`. CI uses .NET 10.

```bash
dotnet test DeskPanel.slnx
```

```bash
dotnet run --project agent/DeskPanel.Agent
```

```bash
curl -s http://127.0.0.1:8765/api/v1/hello
```

```bash
cd panel && npm ci && npm test && npm run dev
```

## Rules for every change
1. Every route declares a `TrustClass` (`.WithTrust(...)`). CI fails otherwise.
2. The phone sends IDs, never commands, paths or URLs.
3. New outbound connection → `docs/PRIVACY.md`. New route, file read, secret or action → `docs/THREAT_MODEL.md` plus a test.
