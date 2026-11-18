# Checking a DeskPanel download

DeskPanel's agent can press keys and open programs on your computer, so check a download before you run it. The checks are,
from simplest to strongest:

1. **Checksums:** the file wasn't damaged or swapped in transit.
2. **minisign signature:** the checksum list was signed with the DeskPanel release key.
3. **Build provenance:** GitHub confirms the file was built by this repository's release workflow from a specific commit.

Download the files you want plus `SHA256SUMS` and `SHA256SUMS.minisig` into one folder, then work in that folder.

## 1. Signature over the checksums

Install minisign (`brew install minisign` on macOS, `winget install jedisct1.minisign` on Windows, or `apt install minisign`),
then check `SHA256SUMS` against the public key in this repository, `release/minisign.pub`. Get the key from the repository,
not from the release page:

```bash
minisign -V -p minisign.pub -m SHA256SUMS
```

It must say `Signature and comment signature verified`, and the trusted comment must name the version you downloaded.

## 2. Checksums

macOS and Linux:

```bash
shasum -a 256 -c SHA256SUMS --ignore-missing
```

On Windows (PowerShell), compare this output with the file's line in `SHA256SUMS`:

```powershell
(Get-FileHash .\deskpanel-agent-X.Y.Z-win-x64.exe -Algorithm SHA256).Hash.ToLower()
```

## 3. Build provenance (needs the GitHub CLI)

```bash
gh attestation verify deskpanel-agent-X.Y.Z-osx-arm64 --repo Arun949/deskpanel
```

## 4. The phone app

The APK is signed with the DeskPanel release key. Android itself refuses an update signed with a different key. To check a fresh
download, compare its certificate fingerprint with the one below (`apksigner` is in the Android SDK's build-tools):

```bash
apksigner verify --print-certs deskpanel-X.Y.Z.apk
```

Release certificate SHA-256: *(added with the first release)*

## Running the agent for the first time

- **macOS:** the agent is not yet notarized by Apple, so macOS blocks a downloaded copy. After the checks above pass, remove the
  download mark and make it executable:

  ```bash
  xattr -d com.apple.quarantine deskpanel-agent-X.Y.Z-osx-arm64
  ```

  ```bash
  chmod +x deskpanel-agent-X.Y.Z-osx-arm64
  ```

  Only do this for a file that passed the checks.
- **Windows:** SmartScreen may warn that the publisher is unknown, because the agent isn't code-signed yet. Choose
  **More info → Run anyway** only after the checks pass.
