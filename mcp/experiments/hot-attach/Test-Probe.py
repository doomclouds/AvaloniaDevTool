"""Owned Windows fixture only: source MCP, native hot attach, local and loopback HTTP."""
import argparse
import base64
import ctypes
import json
import os
import pathlib
import queue
import socket
import struct
import subprocess
import threading
import time
from ctypes import wintypes


def wait_until(function, timeout=25):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = function()
        if value:
            return value
        time.sleep(0.1)
    raise TimeoutError("Condition did not become true")


class Mcp:
    def __init__(self, command, environment, output):
        self.log = open(output, "w", encoding="utf-8")
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=self.log, text=True, encoding="utf-8", env=environment,
                                        creationflags=subprocess.CREATE_NO_WINDOW)
        self.lines = queue.Queue()
        self.sequence = 0
        threading.Thread(target=lambda: [self.lines.put(line) for line in self.process.stdout], daemon=True).start()
        try:
            self.info = self.request("initialize", {
                "protocolVersion": "2025-11-25", "capabilities": {},
                "clientInfo": {"name": "avalonia-attach-probe", "version": "0.1.0"}}, timeout=120)
        except Exception:
            self.close()
            raise
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def send(self, value):
        self.process.stdin.write(json.dumps(value) + "\n")
        self.process.stdin.flush()

    def request(self, method, params, timeout=40):
        self.sequence += 1
        self.send({"jsonrpc": "2.0", "id": self.sequence, "method": method, "params": params})
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            line = self.lines.get(timeout=max(0.1, deadline - time.monotonic()))
            message = json.loads(line)  # Any non-protocol stdout is a test failure.
            if message.get("id") == self.sequence:
                if "error" in message:
                    raise AssertionError(message["error"])
                return message["result"]
        raise TimeoutError(method)

    def tool(self, name, **arguments):
        result = self.request("tools/call", {"name": name, "arguments": arguments})
        if result.get("isError"):
            raise AssertionError({"tool": name, "result": result})
        return result

    def close(self):
        if self.process.poll() is None:
            self.process.stdin.close()
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                subprocess.run(["taskkill", "/PID", str(self.process.pid), "/T", "/F"], capture_output=True)
        self.log.close()


def data(result):
    if result.get("structuredContent") is not None:
        return result["structuredContent"]
    text = [block["text"] for block in result.get("content", []) if block.get("type") == "text"]
    values = []
    for part in text:
        try:
            values.append(json.loads(part))
        except json.JSONDecodeError:
            values.append(part)
    return values[0] if len(values) == 1 else values


def dictionaries(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from dictionaries(child)
    elif isinstance(value, list):
        for child in value:
            yield from dictionaries(child)


def fixture_ticks(process):
    class FileTime(ctypes.Structure):
        _fields_ = [("low", wintypes.DWORD), ("high", wintypes.DWORD)]
    get_times = ctypes.windll.kernel32.GetProcessTimes
    get_times.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(FileTime)] * 4
    times = [FileTime() for _ in range(4)]
    if not get_times(int(process._handle), *(ctypes.byref(value) for value in times)):
        raise ctypes.WinError()
    return ((times[0].high << 32) | times[0].low) + 504911232000000000


def has_window(pid):
    found = []
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    @callback_type
    def visit(window, _):
        owner = wintypes.DWORD()
        ctypes.windll.user32.GetWindowThreadProcessId(window, ctypes.byref(owner))
        if owner.value == pid and ctypes.windll.user32.IsWindowVisible(window):
            found.append(window)
        return True
    ctypes.windll.user32.EnumWindows(visit, 0)
    return bool(found)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", required=True)
    parser.add_argument("--prepared", required=True)
    parser.add_argument("--probe-root", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    repo, probe, output = map(pathlib.Path, (args.repo, args.probe_root, args.output))
    output.mkdir(parents=True, exist_ok=True)
    prepared = json.loads(pathlib.Path(args.prepared).read_text(encoding="utf-8-sig"))
    environment = os.environ.copy()
    environment["AVDT_RUNTIME_ROOT"] = prepared["RuntimeRoot"]
    with socket.socket() as port_socket:
        port_socket.bind(("127.0.0.1", 0))
        port = port_socket.getsockname()[1]
    environment["AVDT_EXECUTOR_PORT"] = str(port)
    fixture_dll = probe / "artifacts/bin/UninstrumentedApp/release/UninstrumentedApp.dll"
    managed_dll = probe / "artifacts/bin/ManagedBootstrap/release/AvaloniaDevTool.AttachProbe.Bootstrap.dll"
    injector_dll = probe / "artifacts/bin/release/Injector.dll"
    deps = fixture_dll.with_suffix(".deps.json").read_text(encoding="utf-8")
    assert "AvaloniaDevTool.Agent" not in deps and "DiagnosticsSupport" not in deps
    fixture_environment = os.environ.copy()
    for key in ("DOTNET_STARTUP_HOOKS", "CORECLR_ENABLE_PROFILING", "DOTNET_ENABLE_PROFILING"):
        fixture_environment.pop(key, None)
    fixture_log = open(output / "fixture.log", "w", encoding="utf-8")
    fixture = subprocess.Popen(["dotnet", str(fixture_dll)], env=fixture_environment,
                               stdout=fixture_log, stderr=fixture_log, creationflags=subprocess.CREATE_NO_WINDOW)
    local = remote = server = None
    report = {"fixturePid": fixture.pid, "fixtureHasAgentDependency": False, "steps": []}

    def record(step, **details):
        report["steps"].append({"name": step, **details})
        print(step, flush=True)

    def inject(action):
        request_path = output / (action + ".request.json")
        status_path = output / (action + ".status.json")
        request_path.write_text(json.dumps({"Action": action, "StatusPath": str(status_path)}), encoding="utf-8")
        run = subprocess.run(["dotnet", str(injector_dll), str(fixture.pid), str(ticks),
                              str(probe / "NativeBootstrap.dll"), str(managed_dll), str(request_path), "dotnet"],
                             capture_output=True, text=True, encoding="utf-8", timeout=45)
        (output / (action + ".injector.log")).write_text(run.stdout + run.stderr, encoding="utf-8")
        assert run.returncode == 0, run.stdout + run.stderr
        def status():
            try:
                return json.loads(status_path.read_text(encoding="utf-8"))
            except (FileNotFoundError, json.JSONDecodeError):
                return None
        result = wait_until(status)
        assert result["state"] == ("started" if action == "start" else "stopped"), result
        return result

    def attach(client):
        def discover():
            applications = data(client.tool("attach-to-app"))
            return next((item for item in dictionaries(applications)
                         if item.get("processId") == fixture.pid and "id" in item), None)
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline:
            target = wait_until(discover)
            try:
                result = data(client.tool("attach-to-app", id=target["id"]))
            except AssertionError as error:
                # The endpoint can be recreated between listing and selecting it after detach.
                # app_not_found is an explicit pre-attach rejection, so rediscovery is safe.
                if "app_not_found:" not in str(error):
                    raise
                continue
            assert any(item.get("attached") is True for item in dictionaries(result)), result
            return
        raise TimeoutError("The application endpoint kept changing during rediscovery")

    def find(client, name):
        result = data(client.tool("search", query=name))
        candidates = [item for item in dictionaries(result) if item.get("name") == name and ("nodeId" in item or "id" in item)]
        assert candidates, result
        return candidates[0].get("nodeId", candidates[0].get("id"))

    def check_text(client, node, expected):
        result = data(client.tool("props", nodeId=node, propertyNames=["Text"]))
        assert expected in json.dumps(result, ensure_ascii=False), result

    try:
        wait_until(lambda: has_window(fixture.pid))
        ticks = fixture_ticks(fixture)
        local = Mcp(["pwsh", "-NoProfile", "-NonInteractive", "-File", str(repo / "mcp/Start-Mcp.ps1")],
                    environment, output / "mcp.stderr.log")
        tools = local.request("tools/list", {})["tools"]
        assert local.info["serverInfo"]["icons"][0]["src"].startswith("data:image/png;base64,")
        record("file_based_mcp", toolCount=len(tools), server=local.info["serverInfo"]["name"], icon=True)
        before = data(local.tool("attach-to-app"))
        assert not any(item.get("processId") == fixture.pid for item in dictionaries(before)), before
        start = inject("start")
        assert start["agentWasLoaded"] is False and start["avaloniaAssemblyCopies"] == 1, start
        record("native_hot_attach", **start)
        attach(local)
        tree = data(local.tool("tree", depth=4))
        (output / "tree.json").write_text(json.dumps(tree, ensure_ascii=False, indent=2), encoding="utf-8")
        status_node = find(local, "StatusText")
        counter_node = find(local, "CounterText")
        button_node = find(local, "IncrementButton")
        check_text(local, status_node, "No Agent reference or startup hook")
        check_text(local, counter_node, "Clicks: 0")
        local.tool("ui-click", nodeId=button_node)
        check_text(local, counter_node, "Clicks: 1")
        local.tool("set-prop", nodeId=status_node, propertyName="Text", value="Externally attached")
        check_text(local, status_node, "Externally attached")
        local.tool("set-prop", nodeId=status_node, propertyName="Text", value="unset")
        check_text(local, status_node, "No Agent reference or startup hook")
        combo_node = find(local, "ModeSelector")
        local.tool("set-prop", nodeId=combo_node, propertyName="IsDropDownOpen", value="true")
        def popup_tree():
            snapshot = data(local.tool("tree", depth=2))
            return snapshot if "PopupRoot" in json.dumps(snapshot) else None
        wait_until(popup_tree)
        local.tool("set-prop", nodeId=combo_node, propertyName="IsDropDownOpen", value="unset")
        record("popup_discovery", observed=True)
        root = next(item for item in dictionaries(tree) if "Window" in str(item.get("type", item.get("typeName", ""))) and ("nodeId" in item or "id" in item))
        image_result = local.tool("screenshot", nodeId=root.get("nodeId", root.get("id")))
        png = base64.b64decode(next(item["data"] for item in image_result["content"] if item["type"] == "image"))
        (output / "attached.png").write_bytes(png)
        record("local_ui", clickCount=1, overrideRestored=True, pngDimensions=struct.unpack(">II", png[16:24]))
        local.tool("detach")
        assert fixture.poll() is None
        attach(local)
        check_text(local, find(local, "StatusText"), "No Agent reference or startup hook")
        local.tool("detach")
        record("reattach", targetStillRunning=True)
        server_log = open(output / "serve.log", "w", encoding="utf-8")
        server = subprocess.Popen(["dotnet", prepared["EntryAssembly"], "serve", "--listen", "127.0.0.1", "--port", str(port)],
                                  env=environment, stdout=server_log, stderr=server_log, creationflags=subprocess.CREATE_NO_WINDOW)
        def listening():
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=0.2):
                    return True
            except OSError:
                return False
        wait_until(listening)
        selection = data(local.tool("select-machine", ip="127.0.0.1"))
        attach(local)
        check_text(local, find(local, "CounterText"), "Clicks: 1")
        local.tool("detach")
        local.tool("select-machine")
        record("loopback_http", port=port, selection=selection)
        stop = inject("stop")
        record("agent_stopped", targetStillRunning=fixture.poll() is None, **stop)
        report["success"] = True
    finally:
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        if local:
            local.close()
        if server:
            server.terminate()
            server.wait(timeout=10)
            server_log.close()
        fixture.terminate()
        fixture.wait(timeout=10)
        fixture_log.close()
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
