#!/usr/bin/env python3
"""Prints the release version, and fails if the versions in the code disagree or don't match the tag.

Usage: check_version.py [vX.Y.Z]
The agent version (Directory.Build.props <Version>) and the app version (android versionName) must be equal, and a tag,
when given, must be "v" + that version. A release can't go out with a stale or mismatched number.
"""
import re, sys
from pathlib import Path

root = Path(__file__).resolve().parent.parent
agent = re.search(r"<Version>([^<]+)</Version>", (root / "Directory.Build.props").read_text(encoding="utf-8"))
app = re.search(r'versionName\s*=\s*"([^"]+)"', (root / "android/app/build.gradle.kts").read_text(encoding="utf-8"))
if not agent or not app:
    sys.exit("version not found in Directory.Build.props or android/app/build.gradle.kts")
version = agent.group(1)
if app.group(1) != version:
    sys.exit(f"agent version {version} != app versionName {app.group(1)}")
if not re.fullmatch(r"\d+\.\d+\.\d+", version):
    sys.exit(f"version {version!r} is not X.Y.Z")
if len(sys.argv) > 1 and sys.argv[1] and sys.argv[1] != f"v{version}":
    sys.exit(f"tag {sys.argv[1]} does not match the code's version v{version}")
print(version)
