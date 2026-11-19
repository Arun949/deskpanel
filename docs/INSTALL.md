# Installing DeskPanel

DeskPanel has two parts:

- **The agent** runs on your computer and shows its stats to the phone. It only listens on `127.0.0.1`, so nothing on
  your network can reach it.
- **The app** runs on an old Android phone, which is connected over USB.

Everything installs per user and needs no administrator rights, except installing Android's `adb` tool if you don't have it.

You need:
- **A computer:** macOS 13 or later (Apple silicon or Intel), or Windows 10/11 (x64). Linux runs the agent, but shows no stats yet.
- **A phone:** Android 7 or later, with a USB cable.
- **adb** (Android platform tools), installed by the steps below.

## 1. Download and check

From the [latest release](https://github.com/Arun949/deskpanel/releases/latest), download:
- the agent for your computer: `deskpanel-agent-X.Y.Z-osx-arm64` (Apple silicon), `-osx-x64` (Intel Mac) or `-win-x64.exe`;
- the phone app, `deskpanel-X.Y.Z.apk`;
- the checksum files, `SHA256SUMS` and `SHA256SUMS.minisig`.

**Check them before running anything:** follow [VERIFY.md](VERIFY.md). The agent can press keys and open programs, so
this is worth the two minutes.

## 2. Install the agent

### macOS

In Terminal, in the download folder. The first command removes the download mark; only do this after the checks pass,
because the agent isn't notarized by Apple yet.

```bash
xattr -d com.apple.quarantine deskpanel-agent-X.Y.Z-osx-arm64
```

```bash
chmod +x deskpanel-agent-X.Y.Z-osx-arm64
```

```bash
./deskpanel-agent-X.Y.Z-osx-arm64 install
```

`install` does three things:
- copies the agent to `~/Library/Application Support/DeskPanel/bin/deskpanel-agent`;
- adds a login item (`~/Library/LaunchAgents/app.deskpanel.agent.plist`);
- starts it, and prints what to do next.

You can delete the downloaded file afterwards. For the rest of this guide, `deskpanel-agent` means the installed copy:

```bash
alias deskpanel-agent="$HOME/Library/Application Support/DeskPanel/bin/deskpanel-agent"
```

Install adb if you don't have it:

```bash
brew install android-platform-tools
```

### Windows

In PowerShell, in the download folder. SmartScreen may warn that the publisher is unknown, because the agent isn't
code-signed yet. Choose **More info → Run anyway**, but only after the checks pass.

```powershell
.\deskpanel-agent-X.Y.Z-win-x64.exe install
```

`install` does three things:
- copies the agent to `%LOCALAPPDATA%\Programs\DeskPanel\deskpanel-agent.exe`;
- adds a login item (in `HKCU\…\Run`);
- starts it.

At each login a console window may flash briefly as it starts in the background. For the rest of this guide:

```powershell
Set-Alias deskpanel-agent "$env:LOCALAPPDATA\Programs\DeskPanel\deskpanel-agent.exe"
```

Install adb if you don't have it:

```powershell
winget install Google.PlatformTools
```

## 3. Set up the phone

1. **Turn on USB debugging:**
   - Settings → About phone → tap **Build number** seven times.
   - Then Settings → System → Developer options → turn on **USB debugging**.
2. **Plug the phone in** and tap **Allow** on its "Allow USB debugging?" prompt. Tick "Always allow from this computer".
3. **Install the app:**

   ```bash
   adb install deskpanel-X.Y.Z.apk
   ```

4. **Allow the phone on your computer.** Within a few seconds the computer asks: "A phone wants to show this computer's dashboard".
   Answer **Allow** only if it's the phone you just plugged in. The phone gets its own key, which you can revoke later.
5. **Open DeskPanel on the phone.** The dashboard appears.

## 4. Choose what the phone may control

Everything that changes something on your computer starts **off**. This includes media keys, volume, opening apps and
key presses. Open the settings page:

```bash
deskpanel-agent settings
```

The settings page has four tabs:
- **Phones:** the phones you've allowed, and **Revoke** for each.
- **Layout:** move, resize and configure the widgets.
- **Actions:** the apps and key combinations the phone's shortcut buttons may use.
- **Activity:** a log of every control used.

**macOS permissions.** The first time the agent does each of these, macOS asks for permission:
- **Key presses** need Accessibility: System Settings → Privacy & Security → Accessibility → add or enable `deskpanel-agent`.
- **Media control** needs Automation: macOS asks once for Music, Spotify and System Events.

## Using Wi-Fi (optional)

Once the phone works over USB, it can also use your home or office Wi-Fi, so the cable becomes optional:

1. Keep the phone plugged in, and in the settings page (Phones tab) tick **Allow over Wi-Fi** for it.
2. Unplug. Within a few seconds the panel's corner shows **Wi-Fi** instead of **USB**. Plug back in any time, and it
   switches back to USB (faster, and it charges).

How it works, in short:
- **The phone and computer swap keys over USB, never over the network.** Over Wi-Fi everything is encrypted. The phone
  only talks to *your* agent, and the agent only accepts phones you allowed.
- **The settings page never works over Wi-Fi.**
- **Nothing listens on the network until you allow a phone.** Untick the switch, or revoke the phone, and it's cut off at once.

Things to know:
- Both devices must be on the same private network (home or office, not a café hotspot with client isolation).
- If the computer's address changes (a new router, or DHCP), plug the phone in once to refresh it. A **DHCP reservation**
  for the computer in your router avoids this.
- **Windows:** the first time Wi-Fi is turned on, Windows Firewall asks whether to allow `deskpanel-agent`. Choose
  **Private networks** only. Allowing it needs administrator rights once.
- **macOS:** if the firewall is on, macOS asks once whether `deskpanel-agent` may accept incoming connections. Choose **Allow**.
- The port is 8766. To change it, put `{"port": 9000}` in `wifi.json` in the data folder, then restart the agent.

## Day to day

- **Check whether it's installed and running.** If `status` says it isn't running, the log usually says why.

  ```bash
  deskpanel-agent status
  ```

- **Update:** download and check the new release, then run its `install` again. Your phones, layout and settings are kept.
- **Logs:** `agent.log` in the data folder, which `status` prints. The log holds no keys or links.

## Uninstall

On the computer, either remove everything, or keep your phone keys and settings for a later reinstall (`--keep-data`):

```bash
deskpanel-agent uninstall
```

```bash
deskpanel-agent uninstall --keep-data
```

On the phone:

```bash
adb uninstall app.deskpanel
```

On macOS, also remove `deskpanel-agent` from System Settings → Privacy & Security → Accessibility if you added it.
You can also turn USB debugging off again.

## Building from source instead

You need the .NET 10 SDK, Node 22, and JDK 17 plus the Android SDK for the app. From a clone of the repository:

```bash
dotnet publish agent/DeskPanel.Agent -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out
```

```bash
./out/DeskPanelAgent install
```

Use `-r osx-x64` or `-r win-x64` for other computers (on Windows, run `.\out\DeskPanelAgent.exe install`).

To build the app:

```bash
cd panel && npm ci && npm run build && cd ../android && ./gradlew assembleDebug
```

The app is then at `android/app/build/outputs/apk/debug/app-debug.apk`.
