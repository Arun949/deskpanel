# Roadmap: more widgets and controls

Agreed 19 Nov 2026: build all of these, in batches. Each feature is finished before the next starts:
- design notes;
- a permission if it changes anything (off by default);
- tests;
- a test on the real phone;
- a merge.

## Rules every feature follows

- **The phone only shows data and sends IDs.** It never sends commands, paths, URLs or text to type. The PC does all the work.
- **Anything that changes something on the computer gets its own permission,** off until granted in the settings page.
  It goes through the control gate: permission, rate limit, deduplication, activity log.
- **The phone never talks to the internet.** If a widget needs outside data (weather, GitHub), the **agent** fetches it
  from one fixed source, and that source is listed in `docs/PRIVACY.md`.
- **Read-only signals are sampled only while a phone is watching,** like CPU today.
- **Nothing reads screen contents, the clipboard or keystrokes.**

## Batch 1: the top 5

| # | Feature | Kind | Permission | Notes |
|---|---|---|---|---|
| 1 | **On Air light**: the phone glows red while the camera or mic is in use | read-only | none | macOS: CoreMediaIO / CoreAudio "running somewhere"; Windows: the system's camera/mic usage record |
| 2 | **Mic mute** button with visible state | control | `mic.control` | macOS: the input device's mute (or input volume 0 as a fallback); Windows: Core Audio capture endpoint mute |
| 3 | **Scenes** ("Start work"): one tap runs a saved list of allowed actions | control | each step needs its own permission | Steps are existing action IDs; no new kinds of power |
| 4 | **Audio output switcher** | control | `audio.output` | Shows output devices by name; the phone sends a device ID from a list the agent gave it |
| 5 | **Ambient mode** (live graphs already exist: the System stat widget's 60 s sparkline) | panel | none | Dim drifting clock after 10 min idle |

## Batch 2: sound, focus and system

| # | Feature | Kind | Permission |
|---|---|---|---|
| 6 | Album art (blurred cover background) | read-only | none (size cap, image-signature check: T12) |
| 7 | Per-app volume (Windows first) | control | `volume.control` |
| 8 | Focus / Do Not Disturb switch | control | `focus.control` |
| 9 | Button pages (swipe between shortcut pages) | panel | none |
| 10 | Quick system actions: lock, display sleep, screenshot to file, dark mode, keep awake | control | `system.control` |
| 11 | Battery and disk | read-only | none |
| 12 | Top app by CPU | read-only | none (process name only) |

## Batch 3: time, developer and fun

| # | Feature | Kind | Notes |
|---|---|---|---|
| 13 | Next meeting countdown | read-only | Calendar read on the PC (macOS Calendar access, or an iCal URL fetched by the agent with an SSRF-safe client) |
| 14 | GitHub glance (PRs, reviews, CI) | read-only | Through `gh` on the PC; the token never leaves the PC |
| 15 | Claude Code sessions | read-only first | Approving prompts from the phone is a separate design, later |
| 16 | Run tests / build buttons | control | A fixed command set up in the settings page (like actions); shows only pass/fail |
| 17 | Weather | read-only | One fixed service, fetched by the agent; listed in PRIVACY.md |
| 18 | Break and water reminders, screen time today | panel / read-only | Screen time from the agent's own idle tracking, not app spying |
| 19 | Commit streak | read-only | From local git history of folders you choose |
| 20 | Photo frame (part of ambient mode) | read-only | Images from one folder you choose, resized by the agent |

## Status

| # | Feature | Status |
|---|---|---|
| 1 | On Air light | **done** (verified on the LAVA phone, 24 Nov 2026) |
| 2–20 | | planned |

### #1 On Air: test notes

- **Camera:** Photo Booth on the Mac turned it ON AIR with the red screen glow within about a second; quitting turned it off.
- **Mic, found in the test:** macOS's "Hey Siri" wake-word service (`corespeechd`) keeps the mic open all the time, so
  the light stayed on. That service is now ignored, as macOS's own orange mic dot does; calls still count (test
  `TheWakeWordListenerDoesNotCountAsACall`).
- **Only yes/no per device is sent,** never app names (test `OnlyYesOrNoIsSentNeverAppNames`).
- **Windows** uses the system's own camera/mic usage record. It's covered by unit tests and compiles; a hand test on a
  Windows PC is still to do.

### Found along the way: wireless adb looks like a new phone

- **What happened:** with Android's wireless debugging, adb names the phone `ip:port` instead of its USB serial. The
  agent keyed enrolment on that name, so it asked to enrol the same phone again, and created a second record.
- **Effect:** nothing is exposed. The old record's key no longer works, because the phone holds only the new one. But
  the old record stays "active" until revoked.
- **Fixed (T21):** phones are now identified by their hardware serial (`ro.serialno`), so USB and wireless adb are
  the same phone. On the LAVA phone, the record made over wireless adb was moved to its hardware serial on the next
  start, with no prompt.
