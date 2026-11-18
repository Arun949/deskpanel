# Android app

A full-screen WebView showing the panel, plus the code that talks to the agent. No JavaScript bridge.

| File | Role |
|---|---|
| `MainActivity.kt` | Loads the panel from `https://appassets.androidplatform.net/assets/panel/`. Forwards only `GET /agent/api/v1/{state,layout}` carrying the `X-DeskPanel-Panel` header. Blocks every other host and navigation |
| `AgentClient.kt` | Talks to `127.0.0.1:8765` (adb reverse). **Checks the agent's HMAC proof before sending the key**, and proves again after a 401 or a new run ID |
| `AgentStream.kt` | Keeps the SSE stream open and pushes events to `window.onAgentStream`. A generation counter means a stopped stream never delivers |
| `EnrolProvider.kt` | Accepts the key only from the adb shell: uid 2000 **and** holding DUMP, both checked in code because `call()` isn't covered by the manifest permission |
| `KeyVault.kt` | Encrypts the key with AES-GCM using an Android Keystore key. Backups are disabled |
| `Proof.kt` | Pure proof math. `ProofTest` checks the same test value as the agent's C# test |

## Build
Requires JDK 17, Android SDK platform 35 (`ANDROID_HOME`) and a built panel.

```bash
cd panel && npm ci && npm run build && cd ..
```

```bash
cd android && JAVA_HOME=$(/usr/libexec/java_home -v 17) ANDROID_HOME=/opt/homebrew/share/android-commandlinetools ./gradlew testDebugUnitTest assembleDebug
```

The APK is written to `app/build/outputs/apk/debug/app-debug.apk`.

## Try it with a phone
1. On the phone, turn on **Developer options → USB debugging**, plug it in, and allow this computer.
2. Install the app:
   ```bash
   adb install -r android/app/build/outputs/apk/debug/app-debug.apk
   ```
3. Start the agent. Within 3 seconds it shows a Mac dialog: **"A phone wants to show this Mac's dashboard"**. Click **Allow**.
4. The agent delivers the key, sets up `adb reverse` and opens the app. The first time it reads Spotify or Music, macOS asks for Automation permission.
