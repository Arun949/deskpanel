#!/usr/bin/env python3
"""Smoke test of the built agent on the OS it runs on (CI and locally).

Starts the agent with a throw-away data folder and a test device key, then checks:
  - it listens and answers /hello;
  - requests without a key, or with a foreign Host header, are refused;
  - with the key, /state and /layout answer, and on Windows and macOS CPU and memory are real numbers.
No phone, adb or network access needed. Exit code 0 = pass.
"""
import base64, glob, hashlib, json, os, platform, secrets, subprocess, sys, tempfile, time, urllib.error, urllib.request

PORT = 8799
BASE = f"http://127.0.0.1:{PORT}"


def agent_command():
    """`--exe PATH` tests a packaged (self-contained) agent; otherwise the Release build's DLL runs under `dotnet`."""
    if "--exe" in sys.argv:
        exe = os.path.abspath(sys.argv[sys.argv.index("--exe") + 1])
        if not os.path.isfile(exe):
            sys.exit(f"{exe} not found")
        return [exe]
    hits = sorted(glob.glob("agent/DeskPanel.Agent/bin/Release/*/DeskPanelAgent.dll"))
    if not hits:
        sys.exit("DeskPanelAgent.dll not found: build first (dotnet build -c Release)")
    return ["dotnet", hits[-1]]


def make_device(home):
    key = base64.urlsafe_b64encode(secrets.token_bytes(32)).rstrip(b"=").decode()
    device_id = secrets.token_hex(8)
    derive = lambda purpose: hashlib.sha256(f"deskpanel|{purpose}|{key}".encode()).hexdigest()
    with open(os.path.join(home, "devices.json"), "w", encoding="utf-8") as f:
        json.dump([{"id": device_id, "serial": "SMOKE", "model": "Smoke test", "authHash": derive("auth"),
                    "proofKey": derive("proof"), "createdAt": "2026-01-01T00:00:00Z", "revoked": False}], f)
    return f"Device {device_id}.{key}"


def get(path, headers=None):
    req = urllib.request.Request(BASE + path, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            return r.status, json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        return e.code, None


def main():
    command = agent_command()
    home = tempfile.mkdtemp(prefix="deskpanel-smoke-")
    auth = make_device(home)
    env = dict(os.environ, DESKPANEL_HOME=home, DOTNET_CLI_TELEMETRY_OPTOUT="1")
    agent = subprocess.Popen([*command, "--port", str(PORT)], env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    failures = []
    try:
        for _ in range(60):
            try:
                if get("/api/v1/hello")[0] == 200:
                    break
            except OSError:
                pass
            time.sleep(0.5)
        else:
            failures.append("agent did not start")
            return failures

        check = lambda cond, what: None if cond else failures.append(what)
        check(get("/api/v1/state")[0] == 401, "keyless /state must be 401")
        check(get("/api/v1/hello", {"Host": f"evil.example:{PORT}"})[0] == 403, "foreign Host must be 403")
        check(get("/api/v1/hello", {"Origin": "https://evil.example"})[0] == 403, "foreign Origin must be 403")

        status, _ = get("/api/v1/state", {"Authorization": auth})  # starts sampling
        check(status == 200, f"/state with key: {status}")
        time.sleep(4)
        status, state = get("/api/v1/state", {"Authorization": auth})
        check(status == 200 and state is not None, "/state after sampling")
        layout_status, _ = get("/api/v1/layout", {"Authorization": auth})
        check(layout_status == 200, f"/layout: {layout_status}")

        metrics = (state or {}).get("metrics") or {}
        print(f"{platform.system()}: metrics={metrics} volume={(state or {}).get('volume')} media={(state or {}).get('media')}")
        if platform.system() in ("Windows", "Darwin"):
            check(isinstance(metrics.get("cpu"), (int, float)), "CPU must be a number on this OS")
            check((metrics.get("ramTotalGb") or 0) > 0, "total memory must be > 0 on this OS")
            check((metrics.get("ramUsedGb") or 0) > 0, "used memory must be > 0 on this OS")
        return failures
    finally:
        agent.terminate()
        try:
            out, _ = agent.communicate(timeout=10)
        except subprocess.TimeoutExpired:
            agent.kill()
            out, _ = agent.communicate()
        if failures:
            print("---- agent output ----\n" + (out or ""))


if __name__ == "__main__":
    problems = main()
    for p in problems:
        print("FAIL:", p)
    print("smoke test:", "FAILED" if problems else "passed")
    sys.exit(1 if problems else 0)
