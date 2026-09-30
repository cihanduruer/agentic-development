#!/usr/bin/env python3
import argparse
import json
import os
import re
import time
import urllib.error
import urllib.request
from pathlib import Path
from urllib.parse import urlsplit, urlunsplit


PROTOCOL_VERSION = "2025-11-25"
NONEXISTENT_REVISION = "0" * 40
SEARCH_QUERY = "managed identity Azure Search security requirements"


class SmokeFailure(Exception):
    pass


def request(url, payload=None, token=None, timeout=15, headers=None):
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    request_headers = {"Accept": "application/json, text/event-stream"}
    if data is not None:
        request_headers["Content-Type"] = "application/json"
    if token is not None:
        request_headers["Authorization"] = "Bearer " + token
    if headers is not None:
        request_headers.update(headers)
    message = urllib.request.Request(url, data=data, headers=request_headers, method="POST" if data else "GET")
    try:
        with urllib.request.urlopen(message, timeout=timeout) as response:
            return (
                response.status,
                decode_response(response.read().decode("utf-8")),
                {name.lower(): value for name, value in response.headers.items()},
            )
    except urllib.error.HTTPError as error:
        return error.code, None, {name.lower(): value for name, value in error.headers.items()}
    except (urllib.error.URLError, TimeoutError, OSError):
        return None, None, {}


def decode_response(body):
    if not body.strip():
        return None
    if body.lstrip().startswith("{"):
        return json.loads(body)
    for line in body.splitlines():
        if line.startswith("data:"):
            return json.loads(line[5:].strip())
    raise SmokeFailure("MCP response did not contain a JSON-RPC message.")


def protocol_request(endpoint, request_id, method, params, token, protocol_version=None, response_headers=None):
    headers = {"MCP-Protocol-Version": protocol_version} if protocol_version is not None else None
    status, response, returned_headers = request(
        endpoint,
        {
            "jsonrpc": "2.0",
            "id": request_id,
            "method": method,
            "params": params,
        },
        token,
        headers=headers,
    )
    if status != 200 or not isinstance(response, dict) or "error" in response:
        raise SmokeFailure(f"Authenticated MCP {method} request failed.")
    if response_headers is not None:
        response_headers.update(returned_headers)
    return response.get("result")


def call_tool(endpoint, request_id, arguments, token, protocol_version=PROTOCOL_VERSION):
    result = protocol_request(
        endpoint,
        request_id,
        "tools/call",
        {"name": "search_knowledge", "arguments": arguments},
        token,
        protocol_version,
    )
    if not isinstance(result, dict) or result.get("isError", False):
        raise SmokeFailure("MCP search_knowledge returned an error.")
    content = result.get("content")
    if not isinstance(content, list) or not content or content[0].get("type") != "text":
        raise SmokeFailure("MCP search_knowledge returned no text result.")
    try:
        return json.loads(content[0]["text"])
    except (KeyError, TypeError, json.JSONDecodeError) as error:
        raise SmokeFailure("MCP search_knowledge returned malformed JSON.") from error


def call_tool_with_retry(endpoint, request_id, arguments, token, deadline, protocol_version):
    while True:
        try:
            return call_tool(endpoint, request_id, arguments, token, protocol_version)
        except SmokeFailure:
            if time.monotonic() >= deadline:
                raise SmokeFailure("Search readiness did not succeed within ten minutes.") from None
            time.sleep(5)


def write_evidence(path, evidence):
    Path(path).write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")


def run(args):
    endpoint_parts = urlsplit(args.url)
    local_protocol_test = args.protocol_only and endpoint_parts.hostname in ("127.0.0.1", "::1")
    if (
        (endpoint_parts.scheme != "https" and not (local_protocol_test and endpoint_parts.scheme == "http"))
        or not endpoint_parts.netloc
        or endpoint_parts.query
        or endpoint_parts.fragment
    ):
        raise SmokeFailure("MCP endpoint must be HTTPS, except for the protocol-only loopback test.")
    if not re.fullmatch(r"[0-9a-f]{40}", args.revision):
        raise SmokeFailure("knowledge revision must be a full lowercase 40-character SHA.")
    if not re.fullmatch(r"[0-9a-f]{40}", args.source_sha):
        raise SmokeFailure("source SHA must be a full lowercase 40-character SHA.")
    token = os.environ.get("KNOWLEDGE_MCP_ACCESS_TOKEN", "")
    if not re.fullmatch(r"[A-Za-z0-9._~-]{32,128}", token):
        raise SmokeFailure("The MCP token is missing or invalid.")

    evidence = {
        "status": "running",
        "runId": args.run_id,
        "runAttempt": args.run_attempt,
        "sourceSha": args.source_sha,
        "endpointHost": endpoint_parts.hostname,
        "checks": {},
    }
    if not args.protocol_only:
        evidence["knowledgeRevision"] = args.revision
    write_evidence(args.output, evidence)
    try:
        base_url = urlunsplit((endpoint_parts.scheme, endpoint_parts.netloc, "", "", ""))
        health_url = base_url + "/health"
        deadline = time.monotonic() + 600
        while True:
            status, health, _ = request(health_url, token=token, timeout=10)
            if status == 200 and isinstance(health, dict) and health.get("status") == "healthy":
                break
            if time.monotonic() >= deadline:
                raise SmokeFailure("MCP readiness did not succeed within ten minutes.")
            time.sleep(5)
        evidence["checks"]["health"] = "healthy"

        status, _, _ = request(endpoint_parts.geturl(), {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {},
                "clientInfo": {"name": "knowledge-mcp-smoke", "version": "1.0"},
            },
        })
        if status != 401:
            raise SmokeFailure("Unauthenticated MCP request was not rejected with HTTP 401.")
        evidence["checks"]["unauthenticatedHttpStatus"] = status

        initialize_headers = {}
        initialized = protocol_request(
            endpoint_parts.geturl(),
            2,
            "initialize",
            {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {},
                "clientInfo": {"name": "knowledge-mcp-smoke", "version": "1.0"},
            },
            token,
            response_headers=initialize_headers,
        )
        if not isinstance(initialized, dict) or not initialized.get("protocolVersion"):
            raise SmokeFailure("MCP initialize did not negotiate a protocol version.")
        if "mcp-session-id" in initialize_headers:
            raise SmokeFailure("Stateless MCP initialize unexpectedly returned a session ID.")
        protocol_version = initialized["protocolVersion"]
        status, _, _ = request(
            endpoint_parts.geturl(),
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            token,
            headers={"MCP-Protocol-Version": protocol_version},
        )
        if status not in (200, 202):
            raise SmokeFailure("MCP initialized notification failed.")
        evidence["checks"]["initialize"] = initialized["protocolVersion"]

        listed = protocol_request(
            endpoint_parts.geturl(), 3, "tools/list", {}, token, protocol_version
        )
        tools = listed.get("tools") if isinstance(listed, dict) else None
        if not isinstance(tools, list) or len(tools) != 1:
            raise SmokeFailure("MCP tools/list did not return exactly one tool.")
        tool = tools[0]
        annotations = tool.get("annotations", {})
        if tool.get("name") != "search_knowledge" or annotations.get("readOnlyHint") is not True:
            raise SmokeFailure("MCP tools/list returned an unexpected or non-read-only tool.")
        evidence["checks"]["toolsList"] = {
            "tools": [tool["name"]],
            "readOnlyHint": annotations["readOnlyHint"],
        }

        if args.protocol_only:
            missing_revision = call_tool(
                endpoint_parts.geturl(),
                5,
                {"query": SEARCH_QUERY, "revision": ""},
                token,
                protocol_version,
            )
            if missing_revision.get("Status") != "revision_required" or missing_revision.get("HasEvidence") is not False:
                raise SmokeFailure("Empty revision did not return revision_required.")
            evidence["checks"]["emptyRevision"] = "revision_required"
            evidence["status"] = "passed"
            write_evidence(args.output, evidence)
            return

        search_deadline = time.monotonic() + 600
        retrieval = call_tool_with_retry(
            endpoint_parts.geturl(),
            4,
            {"query": SEARCH_QUERY, "revision": args.revision},
            token,
            search_deadline,
            protocol_version,
        )
        if retrieval.get("HasEvidence") is not True or retrieval.get("Revision") != args.revision:
            raise SmokeFailure("Live Search did not return evidence for the requested revision.")
        passages = retrieval.get("Passages")
        if not isinstance(passages, list) or not passages:
            raise SmokeFailure("Live Search returned no cited passages.")
        for passage in passages:
            source = passage.get("SourceLink", "")
            if (
                not passage.get("Path", "").startswith("docs/knowledge/")
                or not passage.get("Path", "").endswith(".md")
                or not source.startswith(
                    f"https://github.com/cihanduruer/agentic-development/blob/{args.revision}/docs/knowledge/"
                )
                or not passage.get("ContentHash")
            ):
                raise SmokeFailure("Live Search evidence did not include a canonical citation and metadata.")
        evidence["checks"]["liveSearch"] = "evidence_found"
        evidence["retrieval"] = retrieval

        missing_revision = call_tool(
            endpoint_parts.geturl(),
            5,
            {"query": SEARCH_QUERY, "revision": ""},
            token,
            protocol_version,
        )
        if missing_revision.get("Status") != "revision_required" or missing_revision.get("HasEvidence") is not False:
            raise SmokeFailure("Empty revision did not return revision_required.")
        evidence["checks"]["emptyRevision"] = "revision_required"

        no_hit = call_tool(
            endpoint_parts.geturl(),
            6,
            {"query": SEARCH_QUERY, "revision": NONEXISTENT_REVISION},
            token,
            protocol_version,
        )
        if no_hit.get("Status") != "no_evidence" or no_hit.get("HasEvidence") is not False:
            raise SmokeFailure("Known nonexistent revision did not return no_evidence.")
        evidence["checks"]["nonexistentRevision"] = "no_evidence"
        evidence["status"] = "passed"
        write_evidence(args.output, evidence)
        return
    except Exception:
        evidence["status"] = "failed"
        evidence["failure"] = "MCP readiness or protocol smoke did not pass; see the workflow step result."
        write_evidence(args.output, evidence)
        raise


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--run-attempt", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--protocol-only", action="store_true")
    args = parser.parse_args()
    try:
        run(args)
    except SmokeFailure as error:
        print(str(error))
        raise SystemExit(1) from None
    except Exception:
        print("Knowledge MCP endpoint smoke failed.")
        raise SystemExit(1) from None
    print("Knowledge MCP endpoint smoke passed; sanitized evidence was written.")


if __name__ == "__main__":
    main()
