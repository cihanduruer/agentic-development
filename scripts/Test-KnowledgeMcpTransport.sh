#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
temporary_directory="$(mktemp -d)"
server_pid=""
trap 'if [[ -n "$server_pid" ]]; then kill "$server_pid" 2>/dev/null || true; wait "$server_pid" 2>/dev/null || true; fi; rm -rf "$temporary_directory"' EXIT

endpoint="http://127.0.0.1:5087/mcp"
token="knowledge-mcp-transport-regression-token-0001"
revision="$(git -C "$repository_root" rev-parse HEAD)"

KnowledgeMcp__SearchEndpoint="https://example.search.windows.net" \
KnowledgeMcp__AccessToken="$token" \
ASPNETCORE_URLS="http://127.0.0.1:5087" \
dotnet "$repository_root/tools/KnowledgeMcp/bin/Debug/net10.0/KnowledgeMcp.dll" \
    >"$temporary_directory/server.log" 2>&1 &
server_pid=$!

for attempt in $(seq 1 60); do
    if curl --silent --show-error --fail \
        --oauth2-bearer "$token" \
        http://127.0.0.1:5087/health >/dev/null 2>&1; then
        break
    fi
    if ! kill -0 "$server_pid" 2>/dev/null; then
        cat "$temporary_directory/server.log"
        echo "Knowledge MCP process exited before becoming ready." >&2
        exit 1
    fi
    if [[ "$attempt" -eq 60 ]]; then
        cat "$temporary_directory/server.log"
        echo "Knowledge MCP did not become ready within 60 seconds." >&2
        exit 1
    fi
    sleep 1
done

python3 "$repository_root/scripts/Test-KnowledgeMcpRedirects.py"

mkdir -p "$repository_root/ValidationEvidence"
KNOWLEDGE_MCP_ACCESS_TOKEN="$token" \
python3 "$repository_root/scripts/Test-KnowledgeMcpEndpoint.py" \
    --url "$endpoint" \
    --revision "$revision" \
    --source-sha "$revision" \
    --run-id "${GITHUB_RUN_ID:-hosted-transport-regression}" \
    --run-attempt "${GITHUB_RUN_ATTEMPT:-1}" \
    --output "$repository_root/ValidationEvidence/mcp-transport-smoke.json" \
    --protocol-only
