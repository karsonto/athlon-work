#!/usr/bin/env python3
"""Minimal Chromium CDP helper for Athlon room sandbox (navigate / page info)."""
from __future__ import annotations

import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

CDP = os.environ.get("ATHLON_CDP", "http://127.0.0.1:9222")
USER_DATA = os.environ.get("ATHLON_CHROME_PROFILE", "/tmp/athlon-chrome")
CHROME_BIN = os.environ.get("ATHLON_CHROME_BIN", "chromium")


def _get(path: str, timeout: float = 3.0):
    with urllib.request.urlopen(CDP + path, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def _put(path: str, timeout: float = 30.0):
    req = urllib.request.Request(CDP + path, method="PUT")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def ensure() -> None:
    try:
        _get("/json/version", timeout=1.0)
        return
    except Exception:
        pass

    os.makedirs(USER_DATA, exist_ok=True)
    log_path = os.path.join(USER_DATA, "chrome.log")
    with open(log_path, "ab") as log:
        subprocess.Popen(
            [
                CHROME_BIN,
                "--headless=new",
                "--remote-debugging-port=9222",
                "--remote-allow-origins=*",
                "--no-sandbox",
                "--disable-gpu",
                "--disable-dev-shm-usage",
                "--disable-software-rasterizer",
                f"--user-data-dir={USER_DATA}",
                "about:blank",
            ],
            stdout=log,
            stderr=subprocess.STDOUT,
            start_new_session=True,
        )

    deadline = time.time() + 20
    while time.time() < deadline:
        try:
            _get("/json/version", timeout=1.0)
            return
        except Exception:
            time.sleep(0.25)
    raise SystemExit("failed to start chromium CDP on :9222")


def list_pages():
    pages = _get("/json/list")
    return [p for p in pages if p.get("type") == "page"]


def active_page():
    pages = list_pages()
    if not pages:
        return None
    return pages[0]


def navigate(url: str) -> dict:
    ensure()
    quoted = urllib.parse.quote(url, safe=":/?#[]@!$&'()*+,;=%")
    try:
        page = _put(f"/json/new?{quoted}", timeout=60.0)
    except urllib.error.HTTPError:
        # Older Chromium: GET
        page = _get(f"/json/new?{quoted}", timeout=60.0)

    # Give the document a moment to settle title
    time.sleep(0.6)
    refreshed = active_page() or page
    return {
        "ok": True,
        "action": "navigate",
        "url": refreshed.get("url") or url,
        "title": refreshed.get("title") or "",
        "id": refreshed.get("id"),
    }


def _cdp_ws(method: str, params: dict | None = None) -> dict:
    try:
        import websocket  # type: ignore
    except ImportError as exc:
        raise SystemExit("websocket-client is required for back/forward/reload") from exc

    page = active_page()
    if page is None:
        raise SystemExit("no active page")
    ws_url = page.get("webSocketDebuggerUrl")
    if not ws_url:
        raise SystemExit("page has no webSocketDebuggerUrl")

    ws = websocket.create_connection(ws_url, timeout=20)
    try:
        msg_id = 1
        ws.send(json.dumps({"id": msg_id, "method": method, "params": params or {}}))
        while True:
            raw = ws.recv()
            data = json.loads(raw)
            if data.get("id") == msg_id:
                if "error" in data:
                    raise SystemExit(json.dumps(data["error"]))
                return data.get("result") or {}
    finally:
        ws.close()


def go_back() -> dict:
    ensure()
    _cdp_ws("Page.goBack")
    time.sleep(0.4)
    page = active_page() or {}
    return {"ok": True, "action": "back", "url": page.get("url", ""), "title": page.get("title", "")}


def go_forward() -> dict:
    ensure()
    _cdp_ws("Page.goForward")
    time.sleep(0.4)
    page = active_page() or {}
    return {"ok": True, "action": "forward", "url": page.get("url", ""), "title": page.get("title", "")}


def reload() -> dict:
    ensure()
    _cdp_ws("Page.reload", {"ignoreCache": False})
    time.sleep(0.5)
    page = active_page() or {}
    return {"ok": True, "action": "reload", "url": page.get("url", ""), "title": page.get("title", "")}


def info() -> dict:
    ensure()
    page = active_page() or {}
    return {"ok": True, "url": page.get("url", ""), "title": page.get("title", "")}


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print("usage: browser_ctl.py <ensure|info|navigate|back|forward|reload> [url]", file=sys.stderr)
        return 2
    cmd = argv[1].lower()
    try:
        if cmd == "ensure":
            ensure()
            result = {"ok": True}
        elif cmd == "info":
            result = info()
        elif cmd == "navigate":
            if len(argv) < 3:
                raise SystemExit("navigate requires url")
            result = navigate(argv[2])
        elif cmd == "back":
            result = go_back()
        elif cmd == "forward":
            result = go_forward()
        elif cmd in ("reload", "refresh"):
            result = reload()
        else:
            raise SystemExit(f"unknown command: {cmd}")
        print(json.dumps(result, ensure_ascii=False))
        return 0
    except SystemExit as exc:
        print(json.dumps({"ok": False, "error": str(exc)}, ensure_ascii=False))
        return 1
    except Exception as exc:  # noqa: BLE001
        print(json.dumps({"ok": False, "error": str(exc)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
