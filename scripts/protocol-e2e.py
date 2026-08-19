#!/usr/bin/python3
"""Run the protocol-level Word MCP E2E against an already-running service.

The script intentionally uses only the Python standard library. It creates one
synthetic PNG in the configured LibreChat upload root and writes retrieved
previews/artifacts into a dedicated empty output directory. The directory is
created when absent, and existing files are never replaced.
"""

from __future__ import annotations

import argparse
import base64
import binascii
import http.client
import json
import math
import os
import re
import secrets
import ssl
import stat
import struct
import sys
import time
import zlib
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping, Sequence
from urllib.parse import parse_qs, urlsplit, urlunsplit


PROTOCOL_VERSION = "2026-07-28"
MAX_REQUEST_BYTES = 2 * 1024 * 1024
MAX_MCP_RESPONSE_BYTES = 64 * 1024 * 1024
MAX_ARTIFACT_BYTES = 64 * 1024 * 1024
MAX_PREVIEW_BYTES = 8 * 1024 * 1024
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
SAFE_IDENTIFIER = re.compile(r"\A[A-Za-z0-9_-]{1,128}\Z")
SAFE_FILE_NAME = re.compile(r"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\Z")
SAFE_ERROR_CODE = re.compile(r"\A[a-z0-9_]{1,80}\Z")

EXPECTED_TOOLS = frozenset(
    {
        "word_get_capabilities",
        "word_analyze",
        "word_get_analysis_chunk",
        "word_render_preview",
        "word_replace_text",
        "word_apply_edits",
        "word_populate_template",
        "word_start_document",
        "word_add_sections_to_draft",
        "word_finish_document",
        "word_insert_document_sections",
        "word_refine_document_section",
        "word_get_job",
        "word_wait_for_job",
        "word_get_preview_images",
        "word_cancel_job",
    }
)

TERMINAL_JOB_STATES = frozenset(
    {
        "succeeded",
        "failed",
        "canceled",
        "timed_out",
        "rejected_unsafe_document",
    }
)


class E2EFailure(RuntimeError):
    """A sanitized, user-displayable protocol E2E failure."""


class JsonRpcFailure(E2EFailure):
    def __init__(self, code: object):
        self.code = code
        safe_code = code if isinstance(code, int) and not isinstance(code, bool) else "unknown"
        super().__init__(f"JSON-RPC request failed with code {safe_code}")


class ToolCallFailure(E2EFailure):
    def __init__(self, tool_name: str, code: object):
        self.tool_name = tool_name
        self.code = code if isinstance(code, str) and SAFE_ERROR_CODE.fullmatch(code) else "unknown"
        super().__init__(f"{tool_name} returned tool error {self.code}")


class SafeArgumentParser(argparse.ArgumentParser):
    def error(self, message: str) -> None:
        del message
        raise E2EFailure("Invalid command-line arguments; use --help for the accepted options")


def require(condition: object, message: str) -> None:
    if not condition:
        raise E2EFailure(message)


def load_json(data: bytes | str, label: str) -> Any:
    def reject_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate property")
            result[key] = value
        return result

    def reject_constant(value: str) -> None:
        del value
        raise ValueError("non-finite JSON number")

    try:
        text = data.decode("utf-8", errors="strict") if isinstance(data, bytes) else data
        return json.loads(
            text,
            object_pairs_hook=reject_duplicates,
            parse_constant=reject_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError):
        raise E2EFailure(f"{label} was not valid unambiguous UTF-8 JSON") from None


def normalized_origin(url: str) -> tuple[str, str, int]:
    try:
        parsed = urlsplit(url)
        port = parsed.port
    except ValueError:
        raise E2EFailure("URL has an invalid port") from None
    require(parsed.scheme in {"http", "https"}, "URL scheme must be http or https")
    require(parsed.hostname is not None, "URL must contain a host")
    require(parsed.username is None and parsed.password is None, "URL credentials are not allowed")
    default_port = 443 if parsed.scheme == "https" else 80
    return parsed.scheme, parsed.hostname.lower(), port or default_port


def validate_request_url(url: str) -> None:
    require(len(url) <= 8_192, "URL exceeded the local length ceiling")
    parsed = urlsplit(url)
    normalized_origin(url)
    require(not parsed.fragment, "URL fragments are not allowed")
    require(not any(character in url for character in "\r\n\x00"), "URL contains a control character")


def normalize_base_url(value: str) -> str:
    parsed = urlsplit(value)
    normalized_origin(value)
    require(not parsed.query and not parsed.fragment, "Base URL must not contain a query or fragment")
    path = parsed.path.rstrip("/")
    return urlunsplit((parsed.scheme, parsed.netloc, path, "", ""))


def endpoint_url(base_url: str, endpoint: str) -> str:
    require(endpoint.startswith("/"), "Internal endpoint must start with a slash")
    parsed = urlsplit(base_url)
    return urlunsplit((parsed.scheme, parsed.netloc, f"{parsed.path}{endpoint}", "", ""))


@dataclass(frozen=True)
class HttpResponse:
    status: int
    headers: Mapping[str, tuple[str, ...]]
    body: bytes

    def values(self, name: str) -> tuple[str, ...]:
        return self.headers.get(name.lower(), ())

    def value(self, name: str) -> str | None:
        values = self.values(name)
        return ", ".join(values) if values else None


class HttpTransport:
    def __init__(self, timeout_seconds: float):
        self.timeout_seconds = timeout_seconds
        self.ssl_context = ssl.create_default_context()

    def request(
        self,
        method: str,
        url: str,
        *,
        headers: Mapping[str, str] | None = None,
        body: bytes | None = None,
        max_response_bytes: int,
        timeout_seconds: float | None = None,
        label: str,
    ) -> HttpResponse:
        validate_request_url(url)
        parsed = urlsplit(url)
        timeout = timeout_seconds if timeout_seconds is not None else self.timeout_seconds
        port = parsed.port
        connection: http.client.HTTPConnection
        if parsed.scheme == "https":
            connection = http.client.HTTPSConnection(
                parsed.hostname,
                port,
                timeout=timeout,
                context=self.ssl_context,
            )
        else:
            connection = http.client.HTTPConnection(parsed.hostname, port, timeout=timeout)

        target = parsed.path or "/"
        if parsed.query:
            target = f"{target}?{parsed.query}"

        try:
            connection.request(method, target, body=body, headers=dict(headers or {}))
            raw_response = connection.getresponse()
            response_headers: dict[str, list[str]] = {}
            for name, value in raw_response.getheaders():
                response_headers.setdefault(name.lower(), []).append(value)
            content_lengths = response_headers.get("content-length", [])
            if content_lengths:
                require(len(content_lengths) == 1, f"{label} returned duplicate Content-Length headers")
                try:
                    declared_length = int(content_lengths[0], 10)
                except ValueError:
                    raise E2EFailure(f"{label} returned an invalid Content-Length") from None
                require(declared_length >= 0, f"{label} returned a negative Content-Length")
                require(
                    declared_length <= max_response_bytes,
                    f"{label} response exceeded the local byte ceiling",
                )

            chunks: list[bytes] = []
            received = 0
            while True:
                chunk = raw_response.read(min(64 * 1024, max_response_bytes - received + 1))
                if not chunk:
                    break
                received += len(chunk)
                require(received <= max_response_bytes, f"{label} response exceeded the local byte ceiling")
                chunks.append(chunk)
            return HttpResponse(
                raw_response.status,
                {name: tuple(values) for name, values in response_headers.items()},
                b"".join(chunks),
            )
        except E2EFailure:
            raise
        except (OSError, TimeoutError, ssl.SSLError, http.client.HTTPException):
            raise E2EFailure(f"{label} HTTP exchange failed") from None
        finally:
            connection.close()


@dataclass(frozen=True)
class Caller:
    user_id: str
    conversation_id: str


@dataclass(frozen=True)
class ToolResponse:
    structured: dict[str, Any]
    content: list[dict[str, Any]]


class McpClient:
    def __init__(self, transport: HttpTransport, base_url: str, secret: str, caller: Caller):
        self.transport = transport
        self.mcp_url = endpoint_url(base_url, "/mcp")
        self.secret = secret
        self.caller = caller
        self.request_id = 0

    @staticmethod
    def request_metadata() -> dict[str, Any]:
        return {
            "io.modelcontextprotocol/protocolVersion": PROTOCOL_VERSION,
            "io.modelcontextprotocol/clientInfo": {
                "name": "word-mcp-protocol-e2e",
                "version": "1.0.0",
            },
            "io.modelcontextprotocol/clientCapabilities": {},
        }

    def headers(
        self,
        caller: Caller,
        *,
        mcp_method: str,
        tool_name: str | None,
        protocol_version: str | None,
        authenticated: bool = True,
    ) -> dict[str, str]:
        headers = {
            "Accept": "application/json, text/event-stream",
            "Content-Type": "application/json; charset=utf-8",
            "Mcp-Method": mcp_method,
            "X-LibreChat-User-ID": caller.user_id,
            "X-LibreChat-Conversation-ID": caller.conversation_id,
        }
        if authenticated:
            headers["Authorization"] = f"Bearer {self.secret}"
        if protocol_version is not None:
            headers["MCP-Protocol-Version"] = protocol_version
        if tool_name is not None:
            headers["Mcp-Name"] = tool_name
        return headers

    def raw_request(
        self,
        method: str,
        params: Mapping[str, Any],
        *,
        caller: Caller | None = None,
        include_metadata: bool = True,
        protocol_version: str | None = PROTOCOL_VERSION,
        tool_name: str | None = None,
        timeout_seconds: float | None = None,
    ) -> tuple[int, HttpResponse]:
        self.request_id += 1
        request_id = self.request_id
        request_params = dict(params)
        if include_metadata:
            require("_meta" not in request_params, "Internal MCP metadata was supplied twice")
            request_params["_meta"] = self.request_metadata()
        payload = json.dumps(
            {
                "jsonrpc": "2.0",
                "id": request_id,
                "method": method,
                "params": request_params,
            },
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
        ).encode("utf-8")
        require(len(payload) <= MAX_REQUEST_BYTES, "MCP request exceeded the local byte ceiling")
        response = self.transport.request(
            "POST",
            self.mcp_url,
            headers=self.headers(
                caller or self.caller,
                mcp_method=method,
                tool_name=tool_name,
                protocol_version=protocol_version,
            ),
            body=payload,
            max_response_bytes=MAX_MCP_RESPONSE_BYTES,
            timeout_seconds=timeout_seconds,
            label="MCP",
        )
        return request_id, response

    def rpc(
        self,
        method: str,
        params: Mapping[str, Any],
        *,
        caller: Caller | None = None,
        include_metadata: bool = True,
        protocol_version: str | None = PROTOCOL_VERSION,
        tool_name: str | None = None,
        timeout_seconds: float | None = None,
    ) -> Any:
        request_id, response = self.raw_request(
            method,
            params,
            caller=caller,
            include_metadata=include_metadata,
            protocol_version=protocol_version,
            tool_name=tool_name,
            timeout_seconds=timeout_seconds,
        )
        require(response.status == 200, f"MCP request returned HTTP {response.status}")
        envelope = parse_mcp_envelope(response, request_id)
        error = envelope.get("error")
        if error is not None:
            code = error.get("code") if isinstance(error, dict) else None
            raise JsonRpcFailure(code)
        require("result" in envelope, "MCP response did not contain result or error")
        return envelope["result"]

    def verify_supported_handshake(self) -> None:
        result = self.rpc("server/discover", {})
        require(isinstance(result, dict), "server/discover result was not an object")
        versions = result.get("supportedVersions")
        require(
            isinstance(versions, list)
            and all(isinstance(version, str) for version in versions)
            and PROTOCOL_VERSION in versions,
            "server/discover did not advertise the requested protocol version",
        )
        require(result.get("resultType") == "complete", "server/discover did not return a complete result")
        require(isinstance(result.get("capabilities"), dict), "server/discover omitted capabilities")
        metadata = result.get("_meta")
        server_info = (
            metadata.get("io.modelcontextprotocol/serverInfo")
            if isinstance(metadata, dict)
            else None
        )
        require(isinstance(server_info, dict), "server/discover omitted server identity metadata")
        require(
            isinstance(server_info.get("name"), str)
            and bool(server_info["name"])
            and isinstance(server_info.get("version"), str)
            and bool(server_info["version"]),
            "server/discover returned malformed server identity metadata",
        )

    def list_tools(self) -> None:
        result = self.rpc("tools/list", {})
        require(isinstance(result, dict), "tools/list result was not an object")
        tools = result.get("tools")
        require(isinstance(tools, list), "tools/list did not return a tools array")
        names: list[str] = []
        for tool in tools:
            require(isinstance(tool, dict), "tools/list contained a non-object tool")
            name = tool.get("name")
            require(isinstance(name, str), "tools/list contained a tool without a name")
            require(isinstance(tool.get("inputSchema"), dict), f"{name} omitted inputSchema")
            names.append(name)
        require(len(names) == len(set(names)), "tools/list contained duplicate tool names")
        require(
            len(names) == 16 and set(names) == EXPECTED_TOOLS,
            "tools/list did not expose the exact expected 16-tool contract",
        )

    def call_tool(
        self,
        name: str,
        arguments: Mapping[str, Any],
        *,
        caller: Caller | None = None,
        timeout_seconds: float | None = None,
    ) -> ToolResponse:
        result = self.rpc(
            "tools/call",
            {"name": name, "arguments": dict(arguments)},
            caller=caller,
            tool_name=name,
            timeout_seconds=timeout_seconds,
        )
        require(isinstance(result, dict), f"{name} result was not an object")
        content = result.get("content")
        structured = result.get("structuredContent")
        require(
            isinstance(content, list) and all(isinstance(block, dict) for block in content),
            f"{name} omitted MCP content blocks",
        )
        require(isinstance(structured, dict), f"{name} omitted structuredContent")
        is_error = result.get("isError", False)
        require(isinstance(is_error, bool), f"{name} returned a non-boolean isError")
        if is_error:
            validate_structured_tool_error(name, structured, content)
            raise ToolCallFailure(name, structured.get("code"))
        if name != "word_get_preview_images":
            validate_structured_text_mirror(name, structured, content)
        return ToolResponse(structured, content)

    def expect_tool_error(
        self,
        name: str,
        arguments: Mapping[str, Any],
        caller: Caller,
        expected_code: str,
    ) -> None:
        try:
            self.call_tool(name, arguments, caller=caller)
        except ToolCallFailure as error:
            require(error.code == expected_code, f"{name} returned an unexpected denial code")
            return
        raise E2EFailure(f"{name} unexpectedly exposed a cross-scope job")

    def verify_unauthenticated_rejection(self) -> None:
        self.request_id += 1
        request_id = self.request_id
        payload = json.dumps(
            {
                "jsonrpc": "2.0",
                "id": request_id,
                "method": "tools/list",
                "params": {"_meta": self.request_metadata()},
            },
            separators=(",", ":"),
        ).encode("utf-8")
        response = self.transport.request(
            "POST",
            self.mcp_url,
            headers=self.headers(
                self.caller,
                mcp_method="tools/list",
                tool_name=None,
                protocol_version=PROTOCOL_VERSION,
                authenticated=False,
            ),
            body=payload,
            max_response_bytes=64 * 1024,
            label="unauthenticated MCP",
        )
        require(response.status == 401, "unauthenticated MCP request was not rejected with HTTP 401")


def parse_mcp_envelope(response: HttpResponse, request_id: int) -> dict[str, Any]:
    require(response.body, "MCP response body was empty")
    content_type = (response.value("Content-Type") or "").lower()
    envelopes: list[Any] = []
    if "text/event-stream" in content_type or response.body.lstrip().startswith((b"data:", b"event:")):
        try:
            text = response.body.decode("utf-8")
        except UnicodeDecodeError:
            raise E2EFailure("MCP SSE response was not UTF-8") from None
        data_lines: list[str] = []
        for line in text.splitlines() + [""]:
            if not line:
                if data_lines:
                    data = "\n".join(data_lines)
                    if data != "[DONE]":
                        envelopes.append(load_json(data, "MCP SSE data"))
                    data_lines = []
                continue
            if line.startswith("data:"):
                value = line[len("data:") :]
                data_lines.append(value[1:] if value.startswith(" ") else value)
    else:
        envelopes.append(load_json(response.body, "MCP response"))

    matches = []
    for envelope in envelopes:
        if not isinstance(envelope, dict):
            continue
        response_id = envelope.get("id")
        if isinstance(response_id, int) and not isinstance(response_id, bool) and response_id == request_id:
            matches.append(envelope)
    require(len(matches) == 1, "MCP response did not contain exactly one matching request ID")
    envelope = matches[0]
    require(envelope.get("jsonrpc") == "2.0", "MCP response used an unexpected JSON-RPC version")
    return envelope


def validate_structured_text_mirror(
    tool_name: str,
    structured: Mapping[str, Any],
    content: Sequence[Mapping[str, Any]],
) -> None:
    require(content, f"{tool_name} returned empty content")
    first = content[0]
    require(
        first.get("type") == "text" and isinstance(first.get("text"), str),
        f"{tool_name} did not return the structured JSON text block first",
    )
    mirrored = load_json(first["text"], f"{tool_name} text block")
    require(mirrored == structured, f"{tool_name} text and structuredContent differed")


def validate_structured_tool_error(
    tool_name: str,
    structured: Mapping[str, Any],
    content: Sequence[Mapping[str, Any]],
) -> None:
    require(
        set(structured) == {"status", "code", "field_path", "message", "correction"},
        f"{tool_name} returned a malformed structured tool error",
    )
    require(
        all(isinstance(structured[key], str) and structured[key] for key in structured),
        f"{tool_name} returned an incomplete structured tool error",
    )
    validate_structured_text_mirror(tool_name, structured, content)


class ExclusiveOutputDirectory:
    def __init__(self, path: Path, descriptor: int):
        self.path = path
        self.descriptor = descriptor

    @classmethod
    def create(cls, requested_path: str) -> "ExclusiveOutputDirectory":
        require(os.path.isabs(requested_path), "Output directory must be an absolute path")
        normalized = Path(os.path.abspath(requested_path))
        require(normalized.name not in {"", ".", ".."}, "Output directory target is invalid")
        parent_flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
        try:
            parent_descriptor = os.open(normalized.parent, parent_flags)
        except OSError:
            raise E2EFailure("Output directory parent is unavailable or unsafe") from None
        try:
            created_directory = False
            try:
                os.mkdir(normalized.name, mode=0o700, dir_fd=parent_descriptor)
                created_directory = True
            except FileExistsError:
                pass
            except OSError:
                raise E2EFailure("Output directory could not be created") from None
            directory_flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
            try:
                descriptor = os.open(normalized.name, directory_flags, dir_fd=parent_descriptor)
            except OSError:
                raise E2EFailure("Output directory could not be opened safely") from None
        finally:
            os.close(parent_descriptor)
        if created_directory:
            os.fchmod(descriptor, 0o700)
        try:
            entries = os.listdir(descriptor)
        except OSError:
            os.close(descriptor)
            raise E2EFailure("Output directory could not be inspected safely") from None
        if entries:
            os.close(descriptor)
            raise E2EFailure("Output directory must be empty")
        return cls(normalized, descriptor)

    def write(self, file_name: str, data: bytes) -> None:
        require(SAFE_FILE_NAME.fullmatch(file_name) is not None, "Output file name was unsafe")
        flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
        try:
            descriptor = os.open(file_name, flags, 0o600, dir_fd=self.descriptor)
        except FileExistsError:
            raise E2EFailure("An output file already exists; refusing to overwrite it") from None
        except OSError:
            raise E2EFailure("An output file could not be created safely") from None
        try:
            with os.fdopen(descriptor, "wb", closefd=True) as stream:
                descriptor = -1
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
        except OSError:
            raise E2EFailure("An output file could not be written completely") from None
        finally:
            if descriptor >= 0:
                os.close(descriptor)

    def close(self) -> None:
        if self.descriptor >= 0:
            os.close(self.descriptor)
            self.descriptor = -1


def png_chunk(chunk_type: bytes, data: bytes) -> bytes:
    checksum = binascii.crc32(chunk_type)
    checksum = binascii.crc32(data, checksum) & 0xFFFFFFFF
    return struct.pack(">I", len(data)) + chunk_type + data + struct.pack(">I", checksum)


def synthetic_png() -> bytes:
    width = 640
    height = 360
    rows = bytearray()
    colors = ((31, 78, 121), (91, 155, 213), (165, 165, 165), (237, 125, 49))
    band_width = width // len(colors)
    for y in range(height):
        rows.append(0)
        for x in range(width):
            red, green, blue = colors[min(x // band_width, len(colors) - 1)]
            if (x // 32 + y // 32) % 2:
                red = min(255, red + 12)
                green = min(255, green + 12)
                blue = min(255, blue + 12)
            rows.extend((red, green, blue))
    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return b"".join(
        (
            PNG_SIGNATURE,
            png_chunk(b"IHDR", header),
            png_chunk(b"IDAT", zlib.compress(bytes(rows), level=9)),
            png_chunk(b"IEND", b""),
        )
    )


def create_synthetic_upload(uploads_root: str, user_id: str) -> str:
    require(os.path.isabs(uploads_root), "Uploads root must be an absolute path")
    uploads_root = os.path.abspath(uploads_root)
    require(
        os.path.dirname(uploads_root) != uploads_root,
        "Uploads root must not be the filesystem root",
    )
    require(SAFE_IDENTIFIER.fullmatch(user_id) is not None, "User ID must be a safe opaque identifier")
    root_flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        root_descriptor = os.open(uploads_root, root_flags)
    except OSError:
        raise E2EFailure("Uploads root is unavailable or unsafe") from None
    user_descriptor = -1
    try:
        created_user_directory = False
        try:
            os.mkdir(user_id, mode=0o755, dir_fd=root_descriptor)
            created_user_directory = True
        except FileExistsError:
            pass
        except OSError:
            raise E2EFailure("Synthetic upload user directory could not be created") from None

        user_flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
        try:
            user_descriptor = os.open(user_id, user_flags, dir_fd=root_descriptor)
        except OSError:
            raise E2EFailure("Synthetic upload user directory is unavailable or unsafe") from None
        if created_user_directory:
            os.fchmod(user_descriptor, 0o755)
        require(
            stat.S_ISDIR(os.fstat(user_descriptor).st_mode),
            "Synthetic upload user target was not a directory",
        )

        data = synthetic_png()
        for _ in range(8):
            image_file_id = f"e2eimg_{secrets.token_hex(12)}"
            file_name = f"{image_file_id}__protocol-e2e-synthetic.png"
            flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
            try:
                descriptor = os.open(file_name, flags, 0o644, dir_fd=user_descriptor)
            except FileExistsError:
                continue
            except OSError:
                raise E2EFailure("Synthetic upload could not be created safely") from None
            try:
                os.fchmod(descriptor, 0o644)
                with os.fdopen(descriptor, "wb", closefd=True) as stream:
                    descriptor = -1
                    stream.write(data)
                    stream.flush()
                    os.fsync(stream.fileno())
            except OSError:
                raise E2EFailure("Synthetic upload could not be written completely") from None
            finally:
                if descriptor >= 0:
                    os.close(descriptor)
            return image_file_id
        raise E2EFailure("Could not allocate a unique synthetic upload ID")
    finally:
        if user_descriptor >= 0:
            os.close(user_descriptor)
        os.close(root_descriptor)


def representative_definition() -> dict[str, Any]:
    return {
        "title": "架空サービス運用改善レポート",
        "purpose": "合成データだけを用いて Word MCP の文書生成経路を検証する。",
        "audience": "プロトコル E2E の確認担当者",
        "subject": "実在する組織・人物・案件を含まない合成業務報告",
        "locale": "ja-JP",
        "expected_section_count": 2,
        "template_source": "none",
        "layout": {
            "page_size": "a4",
            "orientation": "portrait",
            "margin_top_mm": 20,
            "margin_right_mm": 20,
            "margin_bottom_mm": 20,
            "margin_left_mm": 20,
            "columns": 1,
        },
        "theme": {
            "preset": "report",
            "accent": "1F4E79",
            "heading_font": "Noto Sans CJK JP",
            "body_font": "Noto Serif CJK JP",
            "code_font": "Noto Sans Mono CJK JP",
        },
        "design": {
            "density": "balanced",
            "cover": True,
            "table_of_contents": True,
        },
        "header_footer": {
            "header_text": "架空サービス運用改善レポート",
            "footer_text": "プロトコル E2E・合成データ",
            "page_numbers": True,
            "different_first_page": True,
            "different_even_odd": True,
        },
        "sections": [],
    }


def representative_sections(image_file_id: str) -> list[dict[str, Any]]:
    return [
        {
            "section_key": "summary",
            "title": "概要と観測結果",
            "blocks": [
                {"kind": "heading", "text": "検証の概要", "level": 2},
                {
                    "kind": "paragraph",
                    "runs": [
                        {"text": "本レポートは ", "bold": False},
                        {"text": "合成データのみ", "bold": True},
                        {"text": "で段階的な文書生成を確認します。", "bold": False},
                    ],
                },
                {
                    "kind": "callout",
                    "text": "ここに記載する名称、数値、画像はすべて自動生成された検証用情報です。",
                },
                {
                    "kind": "unordered_list",
                    "items": [
                        {"runs": [{"text": "初期化とツール公開契約を確認"}], "level": 0},
                        {"runs": [{"text": "段階 draft と非同期ジョブを確認"}], "level": 0},
                        {"runs": [{"text": "全ページの PNG プレビューを確認"}], "level": 1},
                    ],
                },
                {
                    "kind": "table",
                    "table": {
                        "columns": ["検証項目", "合成値", "状態"],
                        "rows": [
                            ["処理件数", "12", "正常"],
                            ["平均待機時間", "3.4 秒", "目標内"],
                            ["再試行件数", "1", "確認済み"],
                        ],
                        "caption": "表 1 合成メトリクス",
                        "description": "実在する運用値を含まない三列の検証表",
                        "allow_row_split": False,
                    },
                },
                {
                    "kind": "image",
                    "image_file_id": image_file_id,
                    "alt_text": "青、灰、橙の色帯で構成した合成検証画像",
                    "caption": "図 1 標準ライブラリで生成した合成画像",
                },
                {"kind": "page_break"},
                {
                    "kind": "paragraph",
                    "text": "明示改ページ後も本文、ヘッダー、フッター、ページ番号が継続することを確認します。",
                },
            ],
        },
        {
            "section_key": "actions",
            "title": "改善計画と次の確認",
            "blocks": [
                {"kind": "heading", "text": "改善計画", "level": 2},
                {
                    "kind": "key_value",
                    "key_values": [
                        {"key": "対象期間", "value": "架空年度 第1四半期"},
                        {"key": "優先度", "value": "中"},
                        {"key": "責任主体", "value": "合成運用チーム"},
                    ],
                },
                {
                    "kind": "ordered_list",
                    "items": [
                        {"runs": [{"text": "レイアウトを全ページで確認する"}], "level": 0},
                        {"runs": [{"text": "成果物の配信ヘッダーを確認する"}], "level": 0},
                        {"runs": [{"text": "所有権境界の拒否を確認する"}], "level": 0},
                    ],
                },
                {
                    "kind": "quote",
                    "text": "検証結果は再現可能な契約と合成 fixture に基づいて判断します。",
                },
                {"kind": "section_break", "section_break_kind": "next_page"},
                {"kind": "heading", "text": "次の確認", "level": 3},
                {
                    "kind": "paragraph",
                    "runs": [
                        {"text": "preview", "code": True},
                        {"text": " と artifact を照合し、すべてのページを取得します。"},
                    ],
                },
            ],
        },
    ]


def require_identifier(value: object, prefix: str, label: str) -> str:
    require(isinstance(value, str), f"{label} was missing")
    require(value.startswith(prefix) and SAFE_IDENTIFIER.fullmatch(value), f"{label} was malformed")
    return value


def wait_for_successful_job(
    client: McpClient,
    job_id: str,
    job_timeout_seconds: int,
) -> dict[str, Any]:
    deadline = time.monotonic() + job_timeout_seconds
    while True:
        remaining = deadline - time.monotonic()
        require(remaining >= 1, "Job did not reach a terminal state before the E2E deadline")
        wait_seconds = min(45, max(1, int(remaining)))
        response = client.call_tool(
            "word_wait_for_job",
            {"job_id": job_id, "wait_seconds": wait_seconds},
            timeout_seconds=wait_seconds + 15,
        ).structured
        require(response.get("job_id") == job_id, "word_wait_for_job returned a different job ID")
        status_value = response.get("status")
        require(isinstance(status_value, str), "word_wait_for_job omitted job status")
        if status_value not in TERMINAL_JOB_STATES:
            require(status_value in {"queued", "running"}, "word_wait_for_job returned an unknown state")
            continue
        if status_value != "succeeded":
            error = response.get("error")
            code = error.get("code") if isinstance(error, dict) else None
            safe_code = code if isinstance(code, str) and SAFE_ERROR_CODE.fullmatch(code) else "unknown"
            raise E2EFailure(f"Document job ended in {status_value} with code {safe_code}")
        return response


def validate_png(data: bytes, label: str) -> tuple[int, int]:
    require(len(data) <= MAX_PREVIEW_BYTES, f"{label} exceeded the PNG byte ceiling")
    require(data.startswith(PNG_SIGNATURE), f"{label} did not contain a PNG signature")
    offset = len(PNG_SIGNATURE)
    width = 0
    height = 0
    saw_header = False
    saw_image_data = False
    saw_end = False
    while offset < len(data):
        require(offset + 12 <= len(data), f"{label} contained a truncated PNG chunk")
        chunk_length = struct.unpack(">I", data[offset : offset + 4])[0]
        chunk_type = data[offset + 4 : offset + 8]
        chunk_end = offset + 12 + chunk_length
        require(chunk_end <= len(data), f"{label} contained an out-of-bounds PNG chunk")
        chunk_data = data[offset + 8 : offset + 8 + chunk_length]
        supplied_crc = struct.unpack(">I", data[offset + 8 + chunk_length : chunk_end])[0]
        calculated_crc = binascii.crc32(chunk_type)
        calculated_crc = binascii.crc32(chunk_data, calculated_crc) & 0xFFFFFFFF
        require(supplied_crc == calculated_crc, f"{label} contained a bad PNG checksum")
        if chunk_type == b"IHDR":
            require(not saw_header and offset == 8 and chunk_length == 13, f"{label} had an invalid IHDR")
            width, height = struct.unpack(">II", chunk_data[:8])
            require(width > 0 and height > 0, f"{label} had invalid dimensions")
            saw_header = True
        elif chunk_type == b"IDAT":
            require(saw_header and chunk_length > 0, f"{label} had invalid image data ordering")
            saw_image_data = True
        elif chunk_type == b"IEND":
            require(
                saw_header and saw_image_data and chunk_length == 0,
                f"{label} had an invalid IEND",
            )
            saw_end = True
            offset = chunk_end
            break
        offset = chunk_end
    require(
        saw_header and saw_image_data and saw_end and offset == len(data),
        f"{label} was not a complete PNG",
    )
    return width, height


def retrieve_all_previews(
    client: McpClient,
    output: ExclusiveOutputDirectory,
    job_id: str,
    page_count: int,
) -> None:
    for batch_start in range(1, page_count + 1, 4):
        page_numbers = list(range(batch_start, min(page_count, batch_start + 3) + 1))
        response = client.call_tool(
            "word_get_preview_images",
            {"job_id": job_id, "page_numbers": page_numbers},
        )
        require(
            response.structured.get("job_id") == job_id
            and response.structured.get("reviewed_page_numbers") == page_numbers,
            "word_get_preview_images returned mismatched review metadata",
        )
        require(
            len(response.content) == len(page_numbers) * 2 + 1,
            "word_get_preview_images returned an unexpected content block count",
        )
        for index, page_number in enumerate(page_numbers):
            text_block = response.content[index * 2]
            image_block = response.content[index * 2 + 1]
            require(
                text_block.get("type") == "text" and isinstance(text_block.get("text"), str),
                "Preview page label block was malformed",
            )
            require(
                text_block["text"].startswith(f"Page {page_number} "),
                "Preview page label did not match the requested page order",
            )
            require(image_block.get("type") == "image", "Preview image block was missing")
            media_type = image_block.get("mimeType", image_block.get("mime_type"))
            require(media_type == "image/png", "Preview image block had an unexpected media type")
            encoded = image_block.get("data")
            require(isinstance(encoded, str), "Preview image block omitted base64 data")
            require(
                len(encoded) <= ((MAX_PREVIEW_BYTES + 2) // 3) * 4 + 4,
                "Preview base64 exceeded the local byte ceiling",
            )
            try:
                image_data = base64.b64decode(encoded, validate=True)
            except (ValueError, binascii.Error):
                raise E2EFailure("Preview image block contained invalid base64") from None
            validate_png(image_data, f"preview page {page_number}")
            output.write(f"preview-page-{page_number:04d}.png", image_data)
        final_block = response.content[-1]
        require(
            final_block.get("type") == "text" and isinstance(final_block.get("text"), str),
            "Preview review-instructions block was missing",
        )


def require_security_headers(response: HttpResponse, file_name: str, method: str) -> None:
    cache_control = ",".join(response.values("Cache-Control")).lower()
    cache_tokens = {token.strip() for token in cache_control.split(",")}
    require("no-store" in cache_tokens, f"Artifact {method} omitted Cache-Control: no-store")
    content_type_options = ",".join(response.values("X-Content-Type-Options")).lower()
    require(
        "nosniff" in {token.strip() for token in content_type_options.split(",")},
        f"Artifact {method} omitted X-Content-Type-Options: nosniff",
    )
    disposition = response.value("Content-Disposition")
    require(disposition is not None, f"Artifact {method} omitted Content-Disposition")
    require("\r" not in disposition and "\n" not in disposition, "Artifact disposition contained a control character")
    require(
        disposition.startswith("attachment;") and f'filename="{file_name}"' in disposition,
        f"Artifact {method} returned an unsafe Content-Disposition",
    )


def parse_content_length(response: HttpResponse, label: str) -> int:
    values = response.values("Content-Length")
    require(len(values) == 1, f"{label} did not return exactly one Content-Length")
    try:
        length = int(values[0], 10)
    except ValueError:
        raise E2EFailure(f"{label} returned an invalid Content-Length") from None
    require(length > 0, f"{label} returned an empty artifact")
    return length


def verify_and_download_artifacts(
    transport: HttpTransport,
    base_url: str,
    output: ExclusiveOutputDirectory,
    job_id: str,
    artifact_links: object,
) -> int:
    require(isinstance(artifact_links, list), "Successful job omitted artifact links")
    links_by_kind: dict[str, dict[str, Any]] = {}
    for link in artifact_links:
        require(isinstance(link, dict), "Artifact link was not an object")
        kind = link.get("kind")
        require(kind in {"document", "pdf"}, "Job exposed an unexpected downloadable artifact kind")
        require(kind not in links_by_kind, "Job exposed duplicate downloadable artifact kinds")
        links_by_kind[kind] = link
    require(set(links_by_kind) == {"document", "pdf"}, "Job did not expose document and PDF artifacts")

    expected_media_types = {
        "document": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "pdf": "application/pdf",
    }
    # Download the document last because its first GET starts the shorter
    # post-download retention window for the whole job.
    for kind in ("pdf", "document"):
        link = links_by_kind[kind]
        file_name = link.get("file_name")
        artifact_id = require_identifier(link.get("artifact_id"), "art_", "artifact ID")
        artifact_url = link.get("url")
        require(
            isinstance(file_name, str) and SAFE_FILE_NAME.fullmatch(file_name),
            "Artifact link contained an unsafe file name",
        )
        require(isinstance(artifact_url, str), "Artifact link omitted its URL")
        validate_request_url(artifact_url)
        require(
            normalized_origin(artifact_url) == normalized_origin(base_url),
            "Artifact URL used an unexpected origin",
        )
        parsed = urlsplit(artifact_url)
        path_segments = parsed.path.split("/")
        require(
            len(path_segments) >= 5
            and path_segments[-4:] == ["artifacts", job_id, artifact_id, file_name],
            "Artifact URL used an unexpected path",
        )
        try:
            query = parse_qs(parsed.query, keep_blank_values=True, strict_parsing=True)
        except ValueError:
            raise E2EFailure("Artifact URL query was malformed") from None
        require(
            set(query) == {"disposition", "token"}
            and query["disposition"] == ["attachment"]
            and len(query["token"]) == 1
            and bool(query["token"][0]),
            "Artifact URL did not contain a single attachment-scoped token",
        )

        head = transport.request(
            "HEAD",
            artifact_url,
            headers={"Accept": expected_media_types[kind]},
            max_response_bytes=MAX_ARTIFACT_BYTES,
            label="artifact HEAD",
        )
        require(head.status == 200, f"Artifact HEAD returned HTTP {head.status}")
        require(not head.body, "Artifact HEAD returned a response body")
        require_security_headers(head, file_name, "HEAD")
        head_length = parse_content_length(head, "Artifact HEAD")
        head_media_type = (head.value("Content-Type") or "").split(";", 1)[0].strip().lower()
        require(head_media_type == expected_media_types[kind], "Artifact HEAD returned a wrong media type")

        get = transport.request(
            "GET",
            artifact_url,
            headers={"Accept": expected_media_types[kind]},
            max_response_bytes=MAX_ARTIFACT_BYTES,
            label="artifact GET",
        )
        require(get.status == 200, f"Artifact GET returned HTTP {get.status}")
        require_security_headers(get, file_name, "GET")
        require(len(get.body) == head_length, "Artifact GET length differed from HEAD")
        get_media_type = (get.value("Content-Type") or "").split(";", 1)[0].strip().lower()
        require(get_media_type == expected_media_types[kind], "Artifact GET returned a wrong media type")
        if kind == "document":
            require(get.body.startswith(b"PK\x03\x04"), "Downloaded document was not an OPC ZIP package")
        else:
            require(get.body.startswith(b"%PDF-"), "Downloaded preview was not a PDF")
        output.write(file_name, get.body)
    return len(links_by_kind)


def distinct_identifier(value: str) -> str:
    suffix = "-other"
    return f"{value[: 128 - len(suffix)]}{suffix}"


def wait_for_readiness(transport: HttpTransport, base_url: str, timeout_seconds: int) -> None:
    deadline = time.monotonic() + timeout_seconds
    readiness_url = endpoint_url(base_url, "/health/ready")
    while True:
        try:
            response = transport.request(
                "GET",
                readiness_url,
                headers={"Accept": "application/json"},
                max_response_bytes=64 * 1024,
                timeout_seconds=min(5, transport.timeout_seconds),
                label="readiness",
            )
        except E2EFailure:
            response = None
        if response is not None and response.status == 200:
            body = load_json(response.body, "readiness response")
            require(
                isinstance(body, dict) and body.get("status") == "ready",
                "Readiness endpoint returned an unexpected success body",
            )
            return
        remaining = deadline - time.monotonic()
        require(remaining > 0, "Service did not become ready before the E2E deadline")
        time.sleep(min(1.0, remaining))


def validate_arguments(arguments: argparse.Namespace) -> None:
    require(len(arguments.secret) >= 24, "Shared secret is shorter than the server minimum")
    require(
        not any(character.isspace() or not character.isprintable() for character in arguments.secret),
        "Shared secret is not HTTP-safe",
    )
    try:
        arguments.secret.encode("latin-1")
    except UnicodeEncodeError:
        raise E2EFailure("Shared secret is not HTTP-header encodable") from None
    require(SAFE_IDENTIFIER.fullmatch(arguments.user_id) is not None, "User ID must be a safe opaque identifier")
    require(
        SAFE_IDENTIFIER.fullmatch(arguments.conversation_id) is not None,
        "Conversation ID must be a safe opaque identifier",
    )
    require(
        math.isfinite(arguments.http_timeout) and arguments.http_timeout > 0,
        "HTTP timeout must be a positive finite number",
    )
    require(arguments.readiness_timeout > 0, "Readiness timeout must be positive")
    require(arguments.job_timeout >= 60, "Job timeout must be at least 60 seconds")


def run(arguments: argparse.Namespace) -> None:
    validate_arguments(arguments)
    base_url = normalize_base_url(arguments.base_url)
    transport = HttpTransport(arguments.http_timeout)
    caller = Caller(arguments.user_id, arguments.conversation_id)
    client = McpClient(transport, base_url, arguments.secret, caller)

    wait_for_readiness(transport, base_url, arguments.readiness_timeout)
    print("PASS readiness")
    client.verify_unauthenticated_rejection()
    print("PASS unauthenticated MCP rejection")
    client.verify_supported_handshake()
    print("PASS supported MCP handshake")
    client.list_tools()
    print("PASS exact 16-tool contract")

    output = ExclusiveOutputDirectory.create(arguments.output_dir)
    try:
        image_file_id = create_synthetic_upload(arguments.uploads_root, caller.user_id)
        start = client.call_tool(
            "word_start_document",
            {
                "definition": representative_definition(),
                "user_requested_new_workflow": True,
            },
        ).structured
        draft_id = require_identifier(start.get("draft_id"), "draft_", "draft ID")
        require(
            start.get("next_section_index") == 1 and start.get("remaining_section_count") == 2,
            "word_start_document returned unexpected draft counters",
        )

        added = client.call_tool(
            "word_add_sections_to_draft",
            {
                "draft_id": draft_id,
                "sections": representative_sections(image_file_id),
            },
        ).structured
        require(added.get("draft_id") == draft_id, "word_add_sections_to_draft changed the draft ID")
        require(
            added.get("next_section_index") == 3 and added.get("remaining_section_count") == 0,
            "word_add_sections_to_draft returned unexpected draft counters",
        )

        receipt = client.call_tool("word_finish_document", {"draft_id": draft_id}).structured
        job_id = require_identifier(receipt.get("job_id"), "job_", "job ID")
        require(receipt.get("status") in {"queued", "running"}, "word_finish_document returned an unexpected status")
        job = wait_for_successful_job(client, job_id, arguments.job_timeout)
        result = job.get("result")
        require(isinstance(result, dict), "Successful job omitted its result")
        page_count = result.get("page_count")
        require(
            isinstance(page_count, int) and not isinstance(page_count, bool) and 1 <= page_count <= 50,
            "Successful job returned an invalid page count",
        )
        print("PASS staged Japanese document workflow")

        retrieve_all_previews(client, output, job_id, page_count)
        print(f"PASS all preview image blocks ({page_count} pages)")
        refreshed_job = client.call_tool("word_get_job", {"job_id": job_id}).structured
        require(
            refreshed_job.get("job_id") == job_id and refreshed_job.get("status") == "succeeded",
            "word_get_job did not return the completed job before artifact download",
        )
        artifact_count = verify_and_download_artifacts(
            transport,
            base_url,
            output,
            job_id,
            refreshed_job.get("artifact_links"),
        )
        print(f"PASS artifact HEAD/GET and security headers ({artifact_count} artifacts)")

        other_user = Caller(distinct_identifier(caller.user_id), caller.conversation_id)
        other_conversation = Caller(caller.user_id, distinct_identifier(caller.conversation_id))
        client.expect_tool_error("word_get_job", {"job_id": job_id}, other_user, "job_not_found")
        client.expect_tool_error(
            "word_get_job",
            {"job_id": job_id},
            other_conversation,
            "job_not_found",
        )
        print("PASS cross-user and cross-conversation job denial")
    finally:
        output.close()


def parse_arguments() -> argparse.Namespace:
    parser = SafeArgumentParser(
        description="Run Word MCP protocol E2E against an already-running compose endpoint.",
    )
    parser.add_argument("--base-url", required=True, help="Service origin, for example http://127.0.0.1:18081")
    parser.add_argument("--secret", required=True, help="Configured Word MCP bearer shared secret")
    parser.add_argument("--uploads-root", required=True, help="Absolute host path mounted as LibreChat uploads")
    parser.add_argument("--output-dir", required=True, help="Absolute path for a new E2E output directory")
    parser.add_argument("--user-id", default="local-codex-user", help="Trusted synthetic caller user ID")
    parser.add_argument(
        "--conversation-id",
        default="local-codex-conversation",
        help="Trusted synthetic caller conversation ID",
    )
    parser.add_argument("--http-timeout", type=float, default=60.0, help="Per-request timeout in seconds")
    parser.add_argument("--readiness-timeout", type=int, default=60, help="Overall readiness deadline in seconds")
    parser.add_argument("--job-timeout", type=int, default=900, help="Overall asynchronous job deadline in seconds")
    return parser.parse_args()


def main() -> int:
    try:
        run(parse_arguments())
    except E2EFailure as error:
        print(f"FAIL {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("FAIL interrupted", file=sys.stderr)
        return 130
    except Exception as error:
        print(f"FAIL unexpected {type(error).__name__}", file=sys.stderr)
        return 1
    print("PASS protocol E2E completed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
