# Design: using the phone over Wi-Fi

Status: **agreed** (decisions in §9). **W0 spike passed** (§10). **W1 built and verified on a real phone** (§11). Next: W3 comfort items, when wanted. Builds on the USB design (`docs/THREAT_MODEL.md`) and on what PhoneBox got
right and wrong (study notes, Module 4).

## 1. Goal

Unplug the cable and the panel keeps working over the home Wi-Fi, without making the computer any easier to attack
than it is today.

What we want:
- **No new trust path for the first contact.** The phone already gets its key over adb. Wi-Fi should reuse that, not
  invent a second, weaker way in.
- **Each phone keeps its own identity,** and revoking a phone cuts it off on USB and Wi-Fi at once.
- **Off by default:** for the computer as a whole, and for each phone separately.
- **The settings page never works over Wi-Fi.** It stays on `127.0.0.1` only.

Not goals:
- Use over the internet or between networks. Tailscale might come later, as the same design on a different address.
- Phones that were never plugged in. QR pairing is phase W2 (§8), deferred.

## 2. What changes on the network

Over USB, traffic stays inside the cable (adb reverse to `127.0.0.1`). Over Wi-Fi it crosses a network that other
devices share. These are the new threats, and what stops each one:

| # | Threat | Protection |
|---|---|---|
| W-T1 | Someone on the Wi-Fi reads the traffic (stats, track names, device key) | TLS for everything; no plain HTTP listener on the network |
| W-T2 | A fake "agent" (ARP or DNS spoofing, or a rogue access point) in the middle | The phone **pins the agent's public key**, which it received over adb. It doesn't trust certificate authorities or names |
| W-T3 | Any device on the Wi-Fi connects to the agent | **Mutual TLS:** the phone must present a key held in its Android Keystore (it can't be exported), registered over adb. Without it the TLS handshake fails before any HTTP is read |
| W-T4 | A stolen copy of the phone's stored device key | Not enough over Wi-Fi: the Keystore client key is also required, and it can't be copied off the phone |
| W-T5 | A web page in a browser on the network (DNS rebinding, cross-site requests) | The browser has no client certificate, so the TLS handshake fails. Any `Origin` header is refused as a second lock |
| W-T6 | The settings page or other local-only routes reached over Wi-Fi | Routes are **refused over Wi-Fi unless marked for it**. Admin routes can never be marked; a test enforces this (§5.3) |
| W-T7 | Brute force or flooding of the Wi-Fi port | Mutual TLS first; a connection limit and a handshake timeout; only private addresses are accepted |
| W-T8 | The agent is reachable on café or hotel Wi-Fi | The listener only runs when at least one phone has Wi-Fi turned on. It refuses connections from public IP ranges. On Windows the firewall's Private/Public choice also applies. Approved networks only: W3 (§9, decision 3) |
| W-T9 | A lost or stolen phone | **Revoke** in the settings page works at once, also for live streams that are already open (§5.4). Turning off Wi-Fi access stops the listener |
| W-T10 | The agent's TLS private key is copied from the computer | Owner-only file (`0600`), plus Windows DPAPI (per-user encryption). It's no worse than `devices.json`, which already holds the proof keys |
| W-T11 | The phone connects to an old IP address that now belongs to another device | That device can't pass the key pin (W-T2), so the worst case is "not connected" |

Unchanged, and still out of scope: malware running as the same user on the computer, or on a rooted phone.

## 3. How it works

### 3.1 Turning it on (over USB, once per phone)

```
 Settings page (127.0.0.1 only)       Agent                                   Phone (USB, adb)
 ─ Phones tab: "Allow over Wi-Fi" ──► marks the device wifi=true
                                      starts the Wi-Fi listener (if not running)
                                      adb: content call getClientKey ───────► app makes an EC P-256 key pair in the
                                                                               Android Keystore (if none) and returns
                                      ◄──────────────────────────────────────── its public key (SubjectPublicKeyInfo)
                                      stores clientSpki = SHA-256(that key)
                                      adb: content call setWifi ────────────► {port, agentPin, addresses}
                                                                               stored next to the device key (KeyVault)
```

- Both adb calls go through the existing `EnrolProvider`. It only accepts the adb shell's uid (2000) holding `DUMP`,
  so no other app on the phone can make the phone register a key, or receive the pin.
- Nothing new crosses the network to set this up. **adb stays the root of trust.**
- Turning "Allow over Wi-Fi" off deletes `clientSpki`, and the phone's next handshake fails.

### 3.2 Connecting

```
Phone                                                   Agent, Wi-Fi listener (TLS, port 8766)
1. USB first: 127.0.0.1:8765 + proof (today's code). If that works, stop here.
2. Otherwise, for each saved address (last good one first):
   TLS handshake, sends client cert from Keystore ────►  checks client cert: its key hash = some device's clientSpki
                                                         with wifi on and not revoked. Otherwise the handshake fails
   checks the agent's key hash == agentPin ◄──────────  self-signed certificate (no machine name in it)
3. GET /api/v1/hello?d=…&n=…  (the same proof check as USB; costs little, keeps one code path)
4. Requests carry "Authorization: Device <id>.<key>" as today. The agent also checks that this device id is the
   same device the client certificate belongs to (a phone can't use another phone's key over its own TLS session).
5. Every 30 s while on Wi-Fi, try USB again. USB wins when the cable is back (faster, and it charges).
```

- The panel (WebView) doesn't change at all. It still talks only to `MainActivity`'s proxy and its allow-list
  (`ProxyRules`); only `AgentClient` chooses between USB and Wi-Fi.
- **Addresses:** the agent sends its current private addresses at setup and on every authenticated
  `GET /api/v1/wifi` (over either transport). The phone keeps the last 4.
  - If the computer's address changes while the phone is on Wi-Fi and no saved address works, the panel shows
    "Plug in once or check the computer". The INSTALL guide will suggest a DHCP reservation.
  - Automatic discovery (mDNS) is phase W3.

### 3.3 The agent's Wi-Fi key

- **The key:** ECDSA P-256, made on first use.
- **The certificate:** self-signed, valid for 1 year, `CN=DeskPanel`. No computer name, so a network scan doesn't reveal it.
- **What the phone pins:** `SHA-256(SubjectPublicKeyInfo)`, the **public key**, not the certificate. So the agent can renew the
  certificate every year with the same key, without touching phones.
- **Storage:** `wifi-key.pem` in the data folder, owner-only (`0600`); on Windows it's also DPAPI-encrypted for the user.
  Uninstall removes it (add it to `Installer.DataFiles`).
- **Rotation (W3):** "Rotate Wi-Fi key" makes a *next* key.
  - Phones learn its pin over the authenticated `GET /api/v1/wifi` (`nextPin`) and accept either pin.
  - Once every Wi-Fi phone has fetched it, or after 7 days, the agent switches over.
  - A phone that missed it re-learns the pin on its next USB connection.

## 4. Agent changes

| Area | Change |
|---|---|
| Kestrel | A second listener: any address, port `8766` (set in the settings page and stored in the owner-only data folder, **never a command-line switch**, so T18 still holds: a tampered login item can't open the agent to the network), **HTTPS only**, TLS 1.2 or 1.3. On macOS .NET's TLS server only does 1.2 (W0 found this), with ECDHE + AES-256-GCM, so 1.2 must stay allowed. Client certificate **required**. Max 16 connections, 5 s handshake timeout. The loopback listener on 8765 is unchanged |
| Connection filter | Before TLS: accept only remote addresses in 10/8, 172.16/12, 192.168/16, 169.254/16, fc00::/7 and fe80::/10. Everything else is closed at once |
| Client check | `ClientCertificateValidation`: the certificate's public-key hash must match `clientSpki` of an active device with `wifi=true`. Certificate chains and dates are ignored; the key match *is* the identity. The matched device id goes into `HttpContext.Items` |
| `TrustGate` | Requests on the Wi-Fi listener: (1) the route must carry `LanAllowed`, otherwise 404; (2) Device routes need the `Device` key **and** the same device as the TLS client certificate; (3) any `Origin` header gets 403 |
| `BrowserGuard` | Unchanged for loopback. Wi-Fi requests skip its Host check: there's no DNS name, and mutual TLS stops browsers already |
| Routes | `.WithTrust(TrustClass.Device).OverLan()` for: hello, state, stream, layout, media, volume, actions, and the new `GET /api/v1/wifi`. Everything else stays loopback-only |
| `EnrolledDevice` | New fields: `Wifi` (bool), `ClientSpki` (hex or null), `LastSeen` and `LastTransport` (for the Phones tab) |
| Stream | Re-check that the device is still active before each message (at least every 10 s). This also fixes today's gap: a revoked phone's open stream keeps running until it reconnects |
| Settings page | Phones tab: per-phone "Allow over Wi-Fi" switch, and "connected via USB / Wi-Fi (192.168.1.23), 2 min ago". A global note says when the Wi-Fi listener is running and on which addresses |
| Audit | `audit.jsonl` records Wi-Fi on/off per phone, and the first Wi-Fi connection per run (device and IP). Never keys |
| Docs | `THREAT_MODEL.md` gets W-T1–W-T11; `PRIVACY.md` notes the listener; `INSTALL.md` gets "Using Wi-Fi" |

## 5. Rules that tests enforce

1. **Every Admin route is loopback-only.** The route-classification test fails if an Admin route has `LanAllowed`, and
   lists every route that does have it (the list is reviewed in the pull request).
2. **The Wi-Fi listener refuses:**
   - a connection without a client certificate;
   - a certificate from an unknown key, a revoked device, or a device with Wi-Fi off;
   - a `Device` key belonging to another device than the certificate;
   - an `Origin` header;
   - a non-`LanAllowed` route.

   These are in-process tests with a real Kestrel on a random port and real TLS.
3. **The address filter** accepts the private ranges and refuses public IPv4 and IPv6 addresses (table test).
4. **Revocation ends an open stream** within 10 s.
5. **One pin computation, shared by C# and Kotlin.** As with the proof today, both sides test the same vector (a fixed
   key → a fixed `SHA-256(SPKI)`).
6. **Phone side** (Kotlin unit tests):
   - the TrustManager refuses any key other than the pin, including a valid CA-signed certificate;
   - transport choice prefers USB;
   - the address list is capped and ordered.
7. **`wifi-key.pem` is owner-only.** Same as the `devices.json` test.

## 6. Phone changes

- **`KeyVault`:** also holds `agentPin`, `port`, `addresses` and the Keystore alias of the client key (EC P-256,
  `PURPOSE_SIGN`, not exportable). Backups stay disabled.
- **`EnrolProvider`:** two new methods, `getClientKey` and `setWifi`, with the same uid 2000 + `DUMP` check.
  `setWifi` with `enabled=false` deletes the stored pin and addresses.
- **`AgentClient`:**
  - A transport is either `Usb(127.0.0.1:8765)` or `Wifi(address, port)`.
  - Wi-Fi uses an `SSLContext` with a `KeyManager` that offers the Keystore key, and a `TrustManager` that accepts
    only the pinned key. No hostname check is needed, because the pin is the identity.
  - The "send a control once, never retry" rule stays exactly as it is.
- **`network_security_config.xml`:** unchanged. Cleartext only to `127.0.0.1`; Wi-Fi is HTTPS through our own TrustManager.
- **UI:** a small "USB" or "Wi-Fi" mark in a corner of the panel, so the user knows which transport is in use.

## 7. Phases

| Phase | What | Rough size |
|---|---|---|
| **W0, spike (first!)** | Check the riskiest assumption: Kestrel on **macOS** with a required client certificate and TLS 1.2+1.3, reached from the real phone (Android version of the LAVA phone?) with a Keystore client key. If macOS can't do this cleanly, we rethink before building (fallback in §9, decision 4) | 1–2 days |
| **W1, Wi-Fi via USB setup** | Everything in §3–§6 except rotation and discovery | 1–2 weeks part-time |
| **W2, QR pairing** | For a phone that never gets plugged in (§8). **Deferred** (§9, decision 1) | ~1 week |
| **W3, comfort** | mDNS discovery (random service name, no computer name), key rotation, "only on these networks" | ~1 week |

## 8. Phase W2: QR pairing (outline)

For a phone that has the app but no USB debugging. The good parts of PhoneBox's design, without its weak points:

- **The QR code** is shown on the settings page (loopback only). It contains the agent's addresses, port and pin, plus a
  128-bit one-time code. The code works once, for 5 minutes, and locks after 5 wrong tries.
- **Scanned inside the app,** not by the camera app opening a link. Another app could register the same link scheme and
  capture the code. This needs the camera permission, and an open-source scanner with no Google Play services.
- **The claim goes over TLS pinned to the QR's pin.** The phone sends the code, its model, and its new Keystore public key.
- **The computer asks first,** like USB enrolment: "Allow Pixel 6 over Wi-Fi? Check code AB12-CD34". The same check
  code is shown on the phone. Only after **Allow** does the phone get its device id and key. A photographed QR code is
  therefore not enough on its own.

## 9. Decisions (19 Nov 2026)

1. **QR pairing (W2): not now.** Every phone is plugged in once for setup. Revisit only if someone needs a phone that
   can't use USB debugging.
2. **The phone's Android version** decides TLS 1.2 or 1.3 for the W0 spike. Read it with
   `adb shell getprop ro.build.version.release` the next time the phone is plugged in.
3. **Any private network for W1,** with all the locks in §2. "Only on networks I approve" stays in W3.
4. **Fallback (no mutual TLS) only if W0 proves mutual TLS can't work.** If it comes to that, the weaker protection
   for W-T4 and W-T5 must be written into the threat model before building.
5. **Windows firewall:** `INSTALL.md`'s "Using Wi-Fi" section explains the prompt in advance: choose **Private networks**
   only, and it needs administrator rights once.

## 10. W0 spike result (19 Nov 2026): passed

Setup:
- **Mac side:** a throwaway Kestrel server (`spike/w0/server`, on branch `spike/w0-wifi`, never merged), configured
  exactly as §4 describes. Self-signed P-256 certificate; client certificate required; TLS 1.2+1.3 allowed.
- **Phone side:** a throwaway app (`app.deskpanel.w0spike`, uninstalled afterwards) on the LAVA LXX504, Android 15.
  It used an EC P-256 key generated in the Android Keystore, with a TrustManager that accepts only the pinned server key.
- **Network:** both on the same home Wi-Fi (10.188.77.0/24). The phone's requests arrived from its Wi-Fi address, not over USB.

| Check | Result |
|---|---|
| Phone with its Keystore key + correct pin | ✅ Connected. The server identified the phone by the hash of its Keystore public key (the same value the phone computed) |
| The Keystore private key can't be exported | ✅ `getEncoded()` is null |
| Phone with a wrong pin | ✅ Refused by the phone during the handshake, before any request was sent |
| Phone without a client key | ✅ Refused by the server during the handshake (`CERTIFICATE_UNKNOWN`) |
| Server that only accepts one registered key, phone with another key | ✅ Refused during the handshake (the validation callback returned false); the registered key still worked |
| A client with no certificate (curl on the Mac) | ✅ Refused during the handshake |

Findings that change the build:
1. **macOS negotiates TLS 1.2 only,** even when the client offers 1.3: .NET's server-side TLS on macOS doesn't do 1.3.
   - The cipher is `TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384` (forward secrecy, authenticated encryption), which is acceptable.
   - Windows 11 can do 1.3. Keep both versions allowed; never allow anything below 1.2.
2. **The key must go through PKCS#12 once** (`Export` then `X509CertificateLoader.LoadPkcs12`) before Kestrel on macOS
   can use it. That fits storing it as an owner-only file (§3.3).
3. **The client certificate's chain errors are expected.** It's self-signed by the Keystore, so .NET reports
   `RemoteCertificateChainErrors`. Our check ignores chains and compares only the key hash, as designed.
4. **Kestrel logs harmless `fail` entries on macOS** when a client closes a TLS connection ("The encryption operation
   failed … Input/output error"). The Wi-Fi listener must filter these out, or `agent.log` fills with noise.
5. **Android needs no special setup.** No extra permission beyond `INTERNET`, and no network-security-config change
   (the custom TrustManager handles HTTPS). A plain `X509KeyManager` that returns the Keystore key works.

Not covered by W0, still to check in W1:
- Windows (Kestrel's client-certificate behaviour with SChannel);
- long-lived SSE streams over Wi-Fi (sleep, Wi-Fi roaming);
- reconnecting after the computer's IP address changes.

## 11. W1 real-world test (19 Nov 2026): passed

LAVA LXX504 (Android 15), the agent from `w1/phone-transport` on the Mac, both on the same home Wi-Fi.

| Case | Result |
|---|---|
| "Allow over Wi-Fi" with the phone plugged in | ✅ Keys swapped over adb. The phone stored the agent's pin (equal to an independent openssl computation), and the port opened only then |
| Unplug | ✅ Within seconds the panel stayed live and showed "Wi-Fi"; connections came from the phone's Wi-Fi address |
| A control over Wi-Fi | ✅ play/pause twice, about 205 ms each, through the normal gate and activity log |
| Plug back in | ✅ Back to "USB" within the 30 s re-check |
| Wi-Fi off while unplugged | ✅ The phone went offline, and the agent forgot its key. The port closed: it was the only Wi-Fi phone |
| Agent restarted while the phone was on Wi-Fi | ✅ The port reopened at start, and the phone was back 3 s later after a fresh proof |
| "Allow over Wi-Fi" off with the phone plugged in (step 2) | ✅ The phone deleted its details and its Keystore key (a new key was made on the next request) |

Found and fixed during the test:
- **An open stream over Wi-Fi only re-checked revocation.** Another phone could keep the port open, and then a phone
  whose Wi-Fi was turned off kept its stream until it reconnected.
- It now also re-checks that the phone is allowed on Wi-Fi with the same key.
- Test: `TurningWifiOffEndsThatPhonesOpenStreamEvenIfThePortStaysOpen`, which fails on the old code.

Still open:
- Windows hand test (the same checks pass in CI on Windows);
- the computer's address changing while the phone is unplugged (the phone then needs one USB connection; W3 adds discovery);
- an hours-long Wi-Fi session (sleep, roaming).

