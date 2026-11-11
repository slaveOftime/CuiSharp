#!/usr/bin/env python3
"""Serve the Pages artifact and use headless Chrome to verify rendered pixels and text input.

Requires websocket-client (`python -m pip install websocket-client`). This is a CI-only test helper;
it is not copied to the published site.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request

import websocket


def free_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def chrome_executable() -> str:
    candidates = [
        os.environ.get("CHROME_PATH"),
        shutil.which("chrome"),
        shutil.which("chrome.exe"),
        str(Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Google/Chrome/Application/chrome.exe"),
        str(Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Google/Chrome/Application/chrome.exe"),
    ]
    for candidate in candidates:
        if candidate and Path(candidate).is_file():
            return candidate
    raise FileNotFoundError("Chrome was not found; install Chrome or set CHROME_PATH")


def get_json(url: str, timeout: float = 2.0):
    with urllib.request.urlopen(url, timeout=timeout) as response:
        return json.load(response)


def evaluate(ws, expression: str, command_id: int, deadline: float, errors: list[str]):
    command_id += 1
    ws.send(json.dumps({
        "id": command_id,
        "method": "Runtime.evaluate",
        "params": {"expression": expression, "returnByValue": True},
    }))

    while time.monotonic() < deadline:
        try:
            message = json.loads(ws.recv())
        except websocket.WebSocketTimeoutException:
            continue
        method = message.get("method")
        if method == "Runtime.exceptionThrown":
            details = message.get("params", {}).get("exceptionDetails", {})
            errors.append(details.get("text", "browser JavaScript exception"))
        elif method == "Runtime.consoleAPICalled":
            params = message.get("params", {})
            if params.get("type") == "error":
                args = params.get("args", [])
                errors.append(" ".join(arg.get("value", arg.get("description", "")) for arg in args))
        if message.get("id") == command_id:
            result = message.get("result", {}).get("result", {})
            if "exceptionDetails" in message.get("result", {}):
                details = message["result"]["exceptionDetails"]
                errors.append(details.get("text", "Runtime.evaluate failed"))
                return None, command_id
            return result.get("value"), command_id
    return None, command_id


def run(site: Path, timeout_seconds: float) -> None:
    site = site.resolve()
    if not (site / "index.html").is_file():
        raise FileNotFoundError(f"No index.html in published site: {site}")

    server_port = free_port()
    debug_port = free_port()
    server = subprocess.Popen(
        [sys.executable, "-m", "http.server", str(server_port), "--bind", "127.0.0.1", "--directory", str(site)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    browser = None
    ws = None
    profile = None
    errors: list[str] = []
    try:
        base_url = f"http://127.0.0.1:{server_port}/"
        server_deadline = time.monotonic() + 15
        while time.monotonic() < server_deadline:
            try:
                with urllib.request.urlopen(base_url, timeout=1) as response:
                    if response.status == 200:
                        break
            except OSError:
                time.sleep(0.1)
        else:
            raise RuntimeError("the local Pages server did not start")

        profile = tempfile.mkdtemp(prefix="cui-wasm-smoke-")
        browser = subprocess.Popen(
            [
                chrome_executable(),
                "--headless=new",
                "--disable-gpu",
                "--no-first-run",
                "--no-default-browser-check",
                f"--user-data-dir={profile}",
                f"--remote-debugging-port={debug_port}",
                "--remote-allow-origins=*",
                "--window-size=1280,900",
                "about:blank",
            ],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )

        debug_url = f"http://127.0.0.1:{debug_port}"
        browser_deadline = time.monotonic() + 30
        while time.monotonic() < browser_deadline:
            if browser.poll() is not None:
                raise RuntimeError(f"Chrome exited during startup: {browser.returncode}")
            try:
                get_json(f"{debug_url}/json/version")
                break
            except OSError:
                time.sleep(0.1)
        else:
            raise RuntimeError("Chrome's DevTools endpoint did not start")

        pages = get_json(f"{debug_url}/json/list")
        page = next(item for item in pages if item.get("type") == "page")
        ws = websocket.create_connection(page["webSocketDebuggerUrl"], timeout=2, suppress_origin=True)
        next_id = 0

        def send(method: str, params: dict | None = None) -> None:
            nonlocal next_id
            next_id += 1
            ws.send(json.dumps({"id": next_id, "method": method, "params": params or {}}))

        send("Runtime.enable")
        send("Page.enable")
        send("Page.navigate", {"url": base_url + "?page=2&smoke=1"})

        expression = """(() => {
          const canvas = document.querySelector('#surface');
          const status = document.querySelector('#status')?.textContent ?? '';
          const kind = document.documentElement.dataset.renderer;
          if (!kind || !canvas || !canvas.width || !canvas.height) return JSON.stringify({status, nonBackground: 0});
          let data;
          if (kind === '2d') data = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
          else {
            const gl = canvas.getContext('webgl2');
            if (!gl) return JSON.stringify({status, nonBackground: -1, error: 'no webgl2 context'});
            data = new Uint8Array(canvas.width * canvas.height * 4);
            gl.readPixels(0, 0, canvas.width, canvas.height, gl.RGBA, gl.UNSIGNED_BYTE, data);
          }
          let nonBackground = 0;
          for (let i = 0; i < data.length; i += 32) {
            if (data[i] !== 248 || data[i + 1] !== 248 || data[i + 2] !== 248) nonBackground++;
          }
          return JSON.stringify({status, width: canvas.width, height: canvas.height, nonBackground, renderer: kind});
        })()"""

        deadline = time.monotonic() + timeout_seconds
        latest = None
        while time.monotonic() < deadline:
            latest, next_id = evaluate(ws, expression, next_id, min(deadline, time.monotonic() + 3), errors)
            if latest:
                try:
                    result = json.loads(latest)
                except json.JSONDecodeError:
                    result = {}
                status = result.get("status", "")
                if "BROWSER-SMOKE-FAIL" in status or "startup failed" in status:
                    raise RuntimeError(status)
                if "BROWSER-SMOKE-OK" in status:
                    if result.get("nonBackground", 0) < 1000:
                        raise RuntimeError(f"smoke marker reported but canvas was blank: {result}")
                    print(
                        "BROWSER-WASM-SMOKE PASS "
                        f"{result.get('width')}x{result.get('height')} "
                        f"nonBackgroundSamples={result.get('nonBackground')}"
                    )
                    break
            if errors:
                raise RuntimeError("browser error: " + " | ".join(errors[-5:]))
            time.sleep(0.15)
        else:
            raise TimeoutError(f"browser smoke did not complete; last DOM result: {latest!r}")
    finally:
        if ws is not None:
            try:
                ws.send(json.dumps({"id": 999999, "method": "Browser.close", "params": {}}))
                ws.close()
            except Exception:
                pass
        if browser is not None:
            try:
                browser.wait(timeout=8)
            except subprocess.TimeoutExpired:
                browser.terminate()
                try:
                    browser.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    browser.kill()
        server.terminate()
        try:
            server.wait(timeout=5)
        except subprocess.TimeoutExpired:
            server.kill()
        if profile is not None:
            shutil.rmtree(profile, ignore_errors=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("site", type=Path, help="the published wwwroot directory")
    parser.add_argument("--timeout", type=float, default=45)
    args = parser.parse_args()
    try:
        run(args.site, args.timeout)
        return 0
    except Exception as error:
        print(f"BROWSER-WASM-SMOKE FAIL: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
