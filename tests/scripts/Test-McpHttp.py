#!/usr/bin/env python3
"""Smoke-test the anonymous stateful HTTP MCP endpoint using only the standard library."""

import argparse
import base64
import json
import platform
import struct
import sys
import time
import urllib.error
import urllib.request


def field(value, name, default=None):
    """Read DTO properties regardless of their JSON naming policy."""
    if not isinstance(value, dict):
        return default
    return next((item for key, item in value.items() if key.casefold() == name.casefold()), default)


class McpHttpClient:
    def __init__(self, url, timeout):
        self.url = url
        self.timeout = timeout
        self.session_id = None
        self.protocol_version = "2025-11-25"
        self.request_id = 0
        self.requests = []
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def exchange(self, method, body=None):
        headers = {
            "Accept": "application/json, text/event-stream",
            "MCP-Protocol-Version": self.protocol_version,
        }
        if self.session_id:
            headers["Mcp-Session-Id"] = self.session_id
        data = None
        if body is not None:
            headers["Content-Type"] = "application/json"
            data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        record = {
            "httpMethod": method,
            "method": (body or {}).get("method"),
            "hasSessionId": self.session_id is not None,
            "hasAuthorization": False,
        }
        if (body or {}).get("method") == "tools/call":
            record["tool"] = body["params"]["name"]
        self.requests.append(record)
        request = urllib.request.Request(self.url, data=data, headers=headers, method=method)
        try:
            with self.opener.open(request, timeout=self.timeout) as response:
                record["status"] = response.status
                record["responseContentType"] = response.headers.get_content_type()
                record["contentLength"] = response.headers.get("Content-Length")
                record["transferEncoding"] = response.headers.get("Transfer-Encoding")
                returned_id = response.headers.get("Mcp-Session-Id")
                if returned_id:
                    if self.session_id and returned_id != self.session_id:
                        raise RuntimeError("The MCP session ID changed unexpectedly.")
                    self.session_id = returned_id
                record["stage"] = "reading-response-body"
                chunks = []
                record["responseBytesRead"] = 0
                while True:
                    chunk = response.read1(65536)
                    if not chunk:
                        break
                    chunks.append(chunk)
                    record["responseBytesRead"] += len(chunk)
                    if record["responseContentType"] == "text/event-stream" and (body or {}).get("id") is not None:
                        text = b"".join(chunks).decode("utf-8", errors="replace")
                        expected_id = body["id"]
                        complete_events = text.replace("\r\n", "\n").split("\n\n")[:-1]
                        matched = False
                        for event in complete_events:
                            data_text = "\n".join(
                                line[5:].lstrip() for line in event.splitlines() if line.startswith("data:")
                            )
                            if data_text and json.loads(data_text).get("id") == expected_id:
                                matched = True
                                break
                        if matched:
                            record["completedBy"] = "matching-sse-response"
                            break
                payload_bytes = b"".join(chunks)
                record["responseBytes"] = len(payload_bytes)
                record["stage"] = "response-complete"
                payload = payload_bytes.decode("utf-8")
                content_type = response.headers.get_content_type()
        except urllib.error.HTTPError as error:
            record["status"] = error.code
            detail = error.read().decode("utf-8", errors="replace")[:800]
            raise RuntimeError("HTTP {}: {}".format(error.code, detail)) from error
        if not payload.strip():
            return None
        if content_type == "text/event-stream":
            expected_id = (body or {}).get("id")
            for event in payload.replace("\r\n", "\n").split("\n\n"):
                text = "\n".join(line[5:].lstrip() for line in event.splitlines() if line.startswith("data:"))
                if text:
                    message = json.loads(text)
                    if message.get("id") == expected_id:
                        return message
            raise RuntimeError("The SSE response did not contain the matching JSON-RPC result.")
        return json.loads(payload)

    def rpc(self, method, parameters=None, notification=False):
        body = {"jsonrpc": "2.0", "method": method, "params": parameters or {}}
        if not notification:
            self.request_id += 1
            body["id"] = self.request_id
        response = self.exchange("POST", body)
        if notification:
            return None
        if response is None or response.get("id") != body["id"]:
            raise RuntimeError("The JSON-RPC response identifier did not match the request.")
        if "error" in response:
            raise RuntimeError("MCP error: " + json.dumps(response["error"], ensure_ascii=False))
        return response["result"]

    def initialize(self):
        result = self.rpc(
            "initialize",
            {
                "protocolVersion": self.protocol_version,
                "capabilities": {},
                "clientInfo": {"name": "avdt-python-smoke", "version": "1.0.0"},
            },
        )
        if not self.session_id:
            raise RuntimeError("The HTTP server did not establish a stateful MCP session.")
        self.protocol_version = result["protocolVersion"]
        self.rpc("notifications/initialized", notification=True)
        return result

    def tool(self, name, arguments=None):
        result = self.rpc("tools/call", {"name": name, "arguments": arguments or {}})
        if result.get("isError"):
            raise RuntimeError("Tool {} failed: {}".format(name, json.dumps(result, ensure_ascii=False)[:1500]))
        return result

    def close(self):
        if self.session_id:
            self.exchange("DELETE")


def tool_value(result):
    value = result.get("structuredContent")
    if value is None:
        text = "\n".join(item.get("text", "") for item in result.get("content", []) if item.get("type") == "text")
        value = json.loads(text)
    if isinstance(value, dict) and "result" in value:
        return value["result"]
    return value


def smoke(client, pipe, inspect_native):
    initialization = client.initialize()
    catalog = client.rpc("tools/list")["tools"]
    if len(catalog) != 44:
        raise RuntimeError("Expected 44 agent tools; received {}.".format(len(catalog)))
    required = {"attach-to-app", "tree", "search", "props", "ui-screenshot", "native-windows", "capture-start"}
    if not required.issubset({tool["name"] for tool in catalog}):
        raise RuntimeError("The default agent catalog is missing required tools.")

    discovered = tool_value(client.tool("attach-to-app"))
    candidates = [app for app in field(discovered, "applications", []) if field(app, "pipeName") == pipe]
    if len(candidates) != 1:
        raise RuntimeError(
            "Expected one discovered application with pipe {}; received {}.".format(pipe, len(candidates))
        )
    attached = tool_value(client.tool("attach-to-app", {"id": field(candidates[0], "id")}))
    if field(attached, "attached") is not True:
        raise RuntimeError("The selected application did not attach.")
    tree = tool_value(client.tool("tree", {"depth": 2}))
    entries = field(tree, "entries", [])
    if not entries:
        raise RuntimeError("The attached application returned an empty tree.")
    found = tool_value(client.tool("search", {"query": "CounterButton", "limit": 10}))
    matches = [item for item in field(found, "matches", []) if field(item, "name") == "CounterButton"]
    if len(matches) != 1:
        raise RuntimeError("The AutomationSample CounterButton was not found uniquely.")
    node_id = field(matches[0], "id")
    properties = tool_value(client.tool("props", {"nodeId": node_id}))
    if not properties:
        raise RuntimeError("The target node returned no properties.")
    screenshot = client.tool("ui-screenshot", {"nodeId": node_id})
    images = [item for item in screenshot.get("content", []) if item.get("type") == "image"]
    if len(images) != 1 or images[0].get("mimeType") != "image/png":
        raise RuntimeError("ui-screenshot did not return exactly one PNG image.")
    png = base64.b64decode(images[0]["data"], validate=True)
    if len(png) < 33 or png[:8] != b"\x89PNG\r\n\x1a\n" or png[12:16] != b"IHDR":
        raise RuntimeError("The screenshot bytes did not have a PNG signature and IHDR.")
    width, height = struct.unpack("!II", png[16:24])
    if width <= 0 or height <= 0:
        raise RuntimeError("The PNG dimensions were not positive.")
    result = {
        "serverInfo": initialization.get("serverInfo"),
        "protocolVersion": client.protocol_version,
        "toolCount": len(catalog),
        "application": {"pipeName": pipe, "processId": field(field(attached, "session", {}), "processId")},
        "treeEntryCount": len(entries),
        "searchedNode": "CounterButton",
        "propertyCount": len(properties) if isinstance(properties, (list, dict)) else None,
        "png": {"signatureValid": True, "width": width, "height": height, "encodedBytes": len(png)},
    }
    if inspect_native:
        windows = tool_value(client.tool("native-windows"))
        if not isinstance(windows, list) or not windows:
            raise RuntimeError("native-windows did not return a real target window.")
        result["nativeWindows"] = {
            "count": len(windows),
            "titles": [field(window, "title") for window in windows],
            "processIds": sorted(
                {field(window, "processId") for window in windows if field(window, "processId") is not None}
            ),
        }
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True, help="The HTTP MCP endpoint, including /mcp.")
    parser.add_argument("--pipe", required=True, help="The exact pipe name of a dedicated AutomationSample.")
    parser.add_argument("--timeout", type=float, default=20, help="Per-request timeout in seconds.")
    parser.add_argument(
        "--native-windows", action="store_true", help="Also enumerate the target's Windows UIA windows."
    )
    args = parser.parse_args()
    client = McpHttpClient(args.url, args.timeout)
    evidence = {"clientPlatform": platform.platform(), "url": args.url, "authentication": "none"}
    started = time.monotonic()
    try:
        evidence["result"] = smoke(client, args.pipe, args.native_windows)
        evidence["passed"] = True
    except Exception as error:
        evidence["passed"] = False
        evidence["error"] = {"type": type(error).__name__, "message": str(error)}
    finally:
        try:
            client.close()
            evidence["sessionDeleteCompleted"] = client.session_id is not None
        except Exception as error:
            evidence["sessionDeleteCompleted"] = False
            evidence["closeError"] = str(error)
            evidence["passed"] = False
        evidence["requests"] = client.requests
        evidence["elapsedSeconds"] = round(time.monotonic() - started, 3)
    print(json.dumps(evidence, ensure_ascii=False, indent=2))
    return 0 if evidence["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
