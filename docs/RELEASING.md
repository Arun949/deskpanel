# Releasing DeskPanel

The release workflow (`.github/workflows/release.yml`) builds everything on GitHub's machines. Your computer never
builds a release, and the signing keys are used only inside the workflow.

| Run | How | What it does |
|---|---|---|
| Dry run | Actions → release → **Run workflow** | Tests, builds the four agents and the APK (unsigned), smoke-tests the agents on macOS, Windows and Linux, writes `SHA256SUMS`. Signs and publishes nothing. Download the `deskpanel-X.Y.Z` artifact to look at the files. |
| Real run | push a tag `vX.Y.Z` | All of the above, plus: signs the APK, signs `SHA256SUMS` with minisign, records build provenance, creates a **draft** release. |

What a release contains:

- `deskpanel-agent-X.Y.Z-osx-arm64`, `-osx-x64`, `-win-x64.exe`, `-linux-x64`: each agent is one self-contained executable, so the user doesn't need .NET installed.
- `deskpanel-X.Y.Z.apk`: the phone app, signed with your release key.
- `deskpanel-panel-X.Y.Z-sbom.cdx.json` and `deskpanel-agent-X.Y.Z-dependencies.json`: what each part is built from.
- `SHA256SUMS` and `SHA256SUMS.minisig`: checksums of every file, plus your signature over them.
- Build provenance (a GitHub attestation) for every file.

## One-time setup (you do this; the keys never go into the repo)

Run these in your own terminal, from the repo folder. Keep the key files **outside** the repo, for example in `~/DeskPanelKeys`,
and back both up somewhere offline (a password manager or an encrypted USB stick):

- If you lose the APK key, phones can't update the app and must uninstall it first.
- If you lose the minisign key, you have to publish a new public key, and people can't check the change against an old release.

```bash
mkdir -p ~/DeskPanelKeys && chmod 700 ~/DeskPanelKeys
```

### 1. minisign key (signs the checksum file)

```bash
brew install minisign
```

```bash
minisign -G -W -p release/minisign.pub -s ~/DeskPanelKeys/minisign.key
```

`-W` creates the key without a password, because the workflow can't type one. The GitHub secret store keeps it safe instead.
Commit `release/minisign.pub` (it's public). The workflow refuses to publish if the signature doesn't check out against it.

```bash
gh secret set MINISIGN_SECRET_KEY --repo Arun949/deskpanel < ~/DeskPanelKeys/minisign.key
```

### 2. Android release key (signs the APK)

`keytool` asks for a password and your name. Use a long random password and save it in your password manager.

```bash
keytool -genkeypair -v -keystore ~/DeskPanelKeys/deskpanel-release.jks -alias deskpanel -keyalg RSA -keysize 4096 -validity 10000
```

```bash
base64 -i ~/DeskPanelKeys/deskpanel-release.jks | gh secret set ANDROID_KEYSTORE_BASE64 --repo Arun949/deskpanel
```

```bash
gh secret set ANDROID_KEY_ALIAS --repo Arun949/deskpanel --body deskpanel
```

Each of the next two commands asks you to paste the password. With a keystore made by `keytool` the two are normally the same:

```bash
gh secret set ANDROID_KEYSTORE_PASSWORD --repo Arun949/deskpanel
```

```bash
gh secret set ANDROID_KEY_PASSWORD --repo Arun949/deskpanel
```

Write down the certificate fingerprint and put it in `docs/VERIFY.md` (step 4 there), so people can check that an APK is yours:

```bash
keytool -list -v -keystore ~/DeskPanelKeys/deskpanel-release.jks -alias deskpanel | grep SHA256
```

## Each release

1. Set the same version in `Directory.Build.props` (`<Version>`) and `android/app/build.gradle.kts` (`versionName`). Also
   increase `versionCode` by 1, or phones won't install it as an update. `python3 tools/check_version.py` checks that the two versions agree.
2. Merge to `main`, wait for CI to pass, and run a dry run if anything about packaging changed.
3. Tag and push. A tag that doesn't match the code's version stops the workflow at the first job.

   ```bash
   git tag -s v0.1.0 -m "DeskPanel 0.1.0"
   ```

   ```bash
   git push origin v0.1.0
   ```

   (`-s` signs the tag if you have git signing set up. Otherwise use `-a`.)
4. When the run is green, open the draft under **Releases**, read the notes, and press **Publish**.

## If something goes wrong

- **The workflow failed before publishing:** delete the tag (`git push --delete origin vX.Y.Z` and `git tag -d vX.Y.Z`), fix the problem, and tag again.
- **You found a problem in a draft:** delete the draft and the tag, then start again.
- **A key leaked:**
  - Rotate it straight away: make a new key, update the secret, and commit the new `release/minisign.pub`.
  - Say so in the next release notes.
  - An APK key can't be swapped for installed apps. Users will have to uninstall and reinstall.
