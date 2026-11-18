# DeskPanel threat model

Status: **M3** (layout editor) on branch `m3/layout-editor`: verified on a LAVA LXX504. Update this file in the same pull request as any change that adds a route, a file read, an outbound connection, a stored secret, or a control action.

## Assets
| Asset | Where |
|---|---|
| Ability to act on the PC (launch apps, send keys, media, volume) | Agent action registry (from M2) |
| What the PC is doing (metrics, now playing, later sessions) | Agent snapshots → phone |
| Device keys | PC: hashed in `devices.json`. Phone: Android Keystore-wrapped |
| Admin session | `HttpOnly` cookie in the PC browser |
| Integration secrets (later) | OS keychain only, never in JSON files |

## Trust boundaries
1. **Loopback port 127.0.0.1:8765.** Reachable by every local process on the PC **and every app on the phone** (through `adb reverse`). Being on loopback grants **nothing**.
2. **USB / adb.** Used as the root of trust for enrolment: whoever approved USB debugging on the phone and enrolment on the PC.
3. **Browser on the PC.** Any web page can send requests to 127.0.0.1. Stopped by `BrowserGuard`.
4. **Phone WebView.** Panel code only; no third-party content. Strict CSP.

## Threats and controls

| # | Threat (PhoneBox study module) | Control | Test | Status |
|---|---|---|---|---|
| T1 | Another app on the phone calls the agent through adb reverse (M1, M3) | Every route has a `TrustClass`; Device routes need a per-device key | `RouteClassificationTests`, `ApiSecurityTests.ADeviceRouteRefuses…` | **M0 done** (all Device calls refused until enrolment exists) |
| T2 | A route is added without access control | Unclassified routes answer 404 at run time; CI test fails at build time | `EveryRouteDeclaresATrustClass` | **M0 done** |
| T3 | Web page → DNS rebinding / CSRF against 127.0.0.1 | `BrowserGuard`: Host allow-list, Origin same-origin, `Sec-Fetch-Site` | `ApiSecurityTests` (host, origin, sec-fetch) | **M0 done** |
| T4 | Public route leaks data or changes state | Public routes are GET-only and return identity only | `PublicRoutesAreReadOnly`, `HelloIsPublic…` | **M0 done** |
| T5 | Another phone app listens on 127.0.0.1:8765 and impersonates the agent (M3) | App checks HMAC(deviceKey, nonce, runId) from `/hello` before any Device request | `HelloProvesTheAgentKnowsTheDeviceKey`, `TheProofMatchesTheSharedVector` (C#), `ProofTest` (Kotlin, same vector) | **M1 done** (on-device check pending) |
| T6 | Key stolen from the phone or reused by another device (M4) | Per-device key, Keystore-wrapped; hashed on the PC; revocable | `DeviceAuthTests`, `AdbEnrollerTests`, `DevicesFileIsOwnerOnly` | **M1 done**: PC asks in a Mac dialog; key stored on PC only as derived hashes (owner-only); phone keeps it Keystore-wrapped, backups off; only adb shell (uid 2000 + DUMP) can deliver it. Revocation UI: M2 |
| T7 | Admin routes reached by the phone, or the phone's key used on admin routes (M1, M11) | Separate `Admin` class with a session cookie from a one-time link; Device keys never valid on `/admin` | `AdminTests.ADeviceKeyNeverOpensAdminRoutes`, `ControlTests.ControlsNeedADeviceKey`, one-time link tests | **M2 done**: admin = one-time link from an owner-only file → HttpOnly SameSite=Strict cookie (Path=/admin) + `X-DeskPanel-Admin` header + CSP |
| T8 | Arbitrary command execution (M7) | No route accepts a command, path or arguments from the phone; actions run by ID from `actions.json`; launch targets are chosen in a file dialog | `ControlTests`, `KeySpecAndActionTests`, Android `ProxyRulesTest` | **M2 done**: actions are `launch` (an existing `.app` picked in a macOS dialog, opened with no arguments) or `keys` (fixed key table); unknown → 404, not on layout → 403, no grant → 403 |
| T9 | Secrets leak to the phone or logs (M2) | Separate files; secrets in the OS keychain; secret canary test | `TheDeviceKeyNeverLeaves` | **M1 done** for device keys |
| T10 | Abuse by flooding actions (M7) | Token bucket per device; audit log | `KeyPressesAreRateLimited`, audit assertions in `ControlTests` | **M2 done**: token bucket per device and permission (keys: 3 burst, 1/s); `audit.jsonl` owner-only, no targets or text |
| T11 | Malicious update (M12) | Manual updates in the MVP. Releases are built only by the release workflow (pinned actions, least-privilege jobs), checksums signed with minisign and checked against the committed `release/minisign.pub`, build provenance attested, APK signed, published as a draft for review. Users check downloads per `docs/VERIFY.md`. Later: in-app check against a built-in key, no downgrade | Workflow verifies its own signature before publishing | **M4: signed releases**; automatic updates not built |
| T12 | Media artwork used to read files (M8) | Artwork bytes only from the media API, size cap, image-signature check | — | **M1: not exposed** (no artwork route yet; track text is length-capped) |
| T13 | Injected markup in the panel (M10) | `h()` builds text nodes only; CSP `script-src 'self'` | `h() never turns text into markup` | **M0 done** |

| T14 | A hiccup on the phone revokes a working enrolment (found on the real phone in M2) | Only an explicit `enrolled=false` from the app triggers re-enrolment; no answer = try again | `AnAppThatDoesNotAnswerKeepsItsEnrolment`, `StatusAnswersAreReadStrictly` | **M2 done** |
| T15 | A tap retried after a network error runs twice | `Idempotency-Key` per tap (agent remembers 2 min); app never retries a POST | `ARetriedRequestWithTheSameIdempotencyKeyRunsOnce`, panel `controls.test.ts` | **M2 done** |

| T16 | A layout carries unexpected data to the phone (scripts, URLs, wrong types) | Every saved option is checked against the catalogue schema (type, range, choices, id lists); unknown options and widget types refused with exact paths; hand edits are cleaned on load | `CatalogTests`, `LayoutEditorApiTests`, panel `catalog.test.ts` | **M3 done** |
| T17 | Two editors overwrite each other's layout | Save carries a content-hash version; a stale version gets 409 and nothing is written | `ASaveOverSomeoneElsesChangeIsAConflict` | **M3 done** |

## Explicit non-goals (MVP)
- Defending against malware already running as the same user on the PC (it can read the agent's files anyway).
- Defending a rooted phone.
- Wi-Fi access (comes after the MVP, with its own section here).
