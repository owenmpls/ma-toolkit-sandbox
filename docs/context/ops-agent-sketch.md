# Ops Agent — Implementation Plan

## Context

The MA Toolkit needs an AI-powered operations assistant that can investigate issues, manage automation, dispatch ad-hoc jobs, and answer questions about batch/step status. The design splits into two components:

1. **Backend** (Azure App Service) — runs the agentic loop with Claude Opus 4.6 and executes server-side tools (admin API, SQL, Service Bus, Log Analytics, az CLI) via managed identity over the private VNet
2. **CLI** (local Python tool) — interactive REPL like Claude Code, connects to the backend via WebSocket over public HTTPS, authenticates via Entra ID device code flow (same as admin-cli), and provides client-side file system tools

## Architecture

```
Local Machine                         Azure (Private VNet)
┌──────────────────┐   WebSocket/TLS   ┌──────────────────────────────┐
│  ops-agent CLI   │ ◀──────────────▶  │  App Service (Python)        │
│  - Interactive   │   Entra ID JWT    │  - Agentic loop (Claude 4.6) │
│    REPL          │                   │  - Server tools               │
│  - File tools    │                   │  - Managed identity           │
│    (local fs)    │                   └──────────┬───────────────────┘
│  - Browser auth  │                              │ MI + VNet
│    (+ device     │                  ┌───────────┼────────────┐
│     code fb)     │                  ▼           ▼            ▼
└──────────────────┘            ┌─────────┐ ┌─────────┐ ┌──────────┐
                                │Admin API│ │ SQL DB  │ │Svc Bus   │
                                │(full)   │ │(SELECT) │ │Log Anlytx│
                                └─────────┘ └─────────┘ └──────────┘
                                                         ┌──────────┐
                                                         │GitHub API│
                                                         │(code)    │
                                                         └──────────┘
```

## WebSocket Protocol

The CLI and backend communicate over a single WebSocket connection with JSON messages.

**CLI → Backend:**
```jsonc
{"type": "auth", "token": "<jwt>"}                               // First message after connect
{"type": "message", "content": "List all failed batches"}        // User message
{"type": "client_tool_result", "id": "req-1", "result": "..."}  // File tool result
```

**Backend → CLI:**
```jsonc
{"type": "auth_ok"}                                              // Auth accepted
{"type": "auth_error", "message": "Invalid token"}               // Auth rejected
{"type": "text", "content": "Let me check..."}                   // Streaming text chunk
{"type": "tool_call", "name": "query_batches", "input": {...}}   // Server tool starting
{"type": "tool_result", "name": "query_batches", "summary": "..."} // Server tool done
{"type": "client_tool_request", "id": "req-1", "name": "read_file", "input": {"path": "members.csv"}} // File tool request
{"type": "done"}                                                 // Response complete
{"type": "error", "message": "..."}                              // Error
```

**Flow for client tools:** When the agent calls `read_file` or `write_file`, the backend sends a `client_tool_request`, then awaits the `client_tool_result` via an `asyncio.Event` before continuing the agentic loop.

## Implementation Steps

### 1. Add `snet-ops-agent` subnet to shared infrastructure

**File:** `infra/shared/deploy.bicep`

Append a new subnet at the end (index 6): `snet-ops-agent` at `10.0.6.0/24` with `Microsoft.Web/serverFarms` delegation (differs from existing Function App subnets which use `Microsoft.App/environments`).

Add output: `output opsAgentSubnetId string = vnet.properties.subnets[6].id`

Existing subnet index references (4=private-endpoints, 5=deployment-scripts) are unchanged.

### 2. Create project structure

**New directory:** `src/automation/ops-agent/`

```
src/automation/ops-agent/
├── CLAUDE.md
├── backend/
│   ├── requirements.txt
│   ├── Dockerfile
│   ├── app/
│   │   ├── __init__.py
│   │   ├── main.py              # FastAPI app: WebSocket endpoint, health check
│   │   ├── config.py            # Pydantic Settings (OPSAGENT_* env vars)
│   │   ├── auth.py              # JWT validation (same as admin API pattern)
│   │   ├── agent/
│   │   │   ├── __init__.py
│   │   │   ├── loop.py          # Agentic tool-use loop
│   │   │   ├── tools.py         # Tool registry: definitions + dispatch
│   │   │   └── prompts.py       # System prompt with platform context
│   │   └── tools/
│   │       ├── __init__.py
│   │       ├── admin_api.py     # Full admin API access (httpx + MI token)
│   │       ├── sql_query.py     # Read-only SQL (pyodbc + MI)
│   │       ├── az_cli.py        # Allowlisted az CLI (subprocess)
│   │       ├── service_bus.py   # Service Bus sender (azure-servicebus)
│   │       ├── log_analytics.py # KQL queries (azure-monitor-query)
│   │       └── github.py        # GitHub API: search code, read files (PAT from KV)
│   └── tests/
│       ├── __init__.py
│       ├── conftest.py
│       └── test_tools.py        # Safety guard tests
├── cli/
│   ├── pyproject.toml           # Installable package: `ops-agent` command
│   ├── requirements.txt
│   ├── ops_agent_cli/
│   │   ├── __init__.py
│   │   ├── main.py              # Entry point, REPL loop
│   │   ├── client.py            # WebSocket client (websockets library)
│   │   ├── auth.py              # MSAL device code flow + token cache
│   │   ├── config.py            # Config storage (~/.opsagent/config.json)
│   │   ├── files.py             # Local file system tool handlers
│   │   └── renderer.py          # Terminal output (rich library)
│   └── tests/
│       ├── __init__.py
│       └── test_auth.py
└── infra/
    ├── deploy.bicep              # App Service + RBAC
    └── deploy.parameters.json
```

### 3. Implement backend — agentic loop

**File:** `backend/app/agent/loop.py`

Core loop (same pattern as Claude Code):
1. Send `messages` + `tools` + `system` to Claude via streaming
2. Collect text chunks → send to CLI as `{"type": "text"}` messages
3. Collect `tool_use` content blocks
4. If tool is a **server tool** (admin_api, sql_query, etc.): execute on backend, send `tool_call`/`tool_result` to CLI
5. If tool is a **client tool** (read_file, write_file, list_directory): send `client_tool_request` to CLI, await `client_tool_result`
6. Append tool results to messages, loop back to step 1
7. If no tool calls: send `{"type": "done"}`, return
8. Safety limit: `MAX_TOOL_ROUNDS = 25`

**Claude API client:** Use `anthropic` Python SDK with Azure AI Foundry. The SDK's Foundry-specific client accepts `azure_ad_token_provider` from `azure-identity`. Base URL: `https://<resource>.services.ai.azure.com/anthropic`.

### 4. Implement backend — tools

**15 tools total** (12 server-side, 3 client-side):

| Tool | Location | Description |
|------|----------|-------------|
| **Server tools** (execute on backend with MI) | | |
| `query_batches` | Admin API | List batches with filters (runbookId, status, manual, limit, offset) |
| `get_batch` | Admin API | Batch details + phases + init steps |
| `query_runbooks` | Admin API | List active runbooks |
| `get_runbook` | Admin API | Runbook YAML + metadata |
| `query_phases` | Admin API | Phase executions for a batch |
| `query_steps` | Admin API | Step executions for a batch (filter by phase, status) |
| `manage_batch` | Admin API | Create batch, advance, cancel, add/remove members |
| `manage_automation` | Admin API | Get/enable/disable automation |
| `sql_query` | Direct SQL | Read-only SELECT queries for diagnostics |
| `az_cli` | subprocess | Allowlisted az commands for infrastructure health |
| `send_service_bus_message` | azure-servicebus | Dispatch ad-hoc jobs |
| `query_logs` | azure-monitor-query | KQL against Log Analytics |
| `search_code` | GitHub API | Search code in the repo (regex/keyword, file type filter) |
| `read_github_file` | GitHub API | Read a specific file from the repo by path |
| **Client tools** (execute on CLI locally) | | |
| `read_file` | CLI local fs | Read file from operator's working directory |
| `write_file` | CLI local fs | Write file to operator's working directory |
| `list_directory` | CLI local fs | List files/dirs in a path |

**Admin API access:** Full access to all 21 endpoints. The backend's MI acquires a token for the admin API's Entra ID audience (`api://<client-id>/.default`). The MI must be assigned the `Admin` app role on the admin API's Enterprise Application.

**SQL access:** Read-only, enforced two ways:
- **RBAC (primary):** MI only has `db_datareader` role — SQL Server rejects mutations
- **Application-level (defense-in-depth):** Keyword blocklist for INSERT, UPDATE, DELETE, DROP, etc.
- Row limit: 500 rows, result truncation at 50KB

### 5. Implement backend — WebSocket endpoint

**File:** `backend/app/main.py`

```python
@app.websocket("/ws/agent")
async def agent_websocket(websocket: WebSocket):
    await websocket.accept()
    # 1. Wait for auth message, validate JWT
    # 2. Loop: receive user messages, run agent loop, send responses
    # 3. Handle client_tool_result messages mid-loop
```

**Session state:** In-memory dict keyed by connection ID. Each session holds the conversation history (list of messages). Sessions are cleaned up on disconnect.

**JWT validation:** Validate the Entra ID JWT using `PyJWT` + JWKS from `https://login.microsoftonline.com/{tenant}/discovery/v2.0/keys`. Check audience matches the ops-agent's app registration. Same validation pattern as the admin API uses via Microsoft.Identity.Web, but implemented in Python.

**Note:** The ops-agent backend needs its **own** Entra ID app registration (separate from the admin API's). The CLI authenticates against this app registration. The backend validates tokens against this audience.

### 6. Implement CLI — REPL and client

**File:** `cli/ops_agent_cli/main.py`

Interactive REPL loop:
1. Show prompt (`> `)
2. Read user input (multi-line via `prompt_toolkit`)
3. Send message over WebSocket to backend
4. Receive and render streaming responses:
   - `text` → render with markdown formatting (via `rich`)
   - `tool_call` → show spinner/status: "Calling query_batches..."
   - `tool_result` → show brief summary
   - `client_tool_request` → execute locally (read_file, write_file, list_directory), send result back
   - `done` → show prompt again
5. Special commands: `/clear` (reset conversation), `/quit` (exit), `/status` (show connection info)

**File:** `cli/ops_agent_cli/client.py`

WebSocket client using `websockets` library:
- Connect to `wss://<backend-url>/ws/agent`
- Send auth message with JWT on connect
- Async generator for receiving messages
- Send method for user messages and tool results

**File:** `cli/ops_agent_cli/auth.py`

Replicates the admin-cli auth pattern (`AuthService.cs`): **interactive browser flow** by default, **device code flow** as fallback (via `--use-device-code` flag). Uses `azure-identity` Python SDK (same library family as the C# admin-cli):

```python
from azure.identity import InteractiveBrowserCredential, DeviceCodeCredential, AuthenticationRecord

# Default: interactive browser flow (opens browser automatically)
credential = InteractiveBrowserCredential(
    tenant_id=config.tenant_id,
    client_id=config.client_id,
    redirect_uri="http://localhost",
    cache_persistence_options=TokenCachePersistenceOptions(name="opsagent-cli"),
    authentication_record=existing_record,  # from ~/.opsagent/auth_record.json if exists
)
record = credential.authenticate(scopes=[scope])

# Fallback: device code flow (for headless/SSH sessions)
credential = DeviceCodeCredential(
    tenant_id=config.tenant_id,
    client_id=config.client_id,
    cache_persistence_options=TokenCachePersistenceOptions(name="opsagent-cli"),
    authentication_record=existing_record,
)
record = credential.authenticate(scopes=[scope])

# Silent token refresh (DisableAutomaticAuthentication equivalent)
credential = InteractiveBrowserCredential(
    ...,
    authentication_record=record,
    disable_automatic_authentication=True,
)
token = credential.get_token(scope)
```

Auth record persisted to `~/.opsagent/auth_record.json` (binary serialization, 600 permissions on Unix — same pattern as admin-cli's `~/.matoolkit/auth_record.json`).

**Config storage:** `~/.opsagent/config.json` with keys: `agent_url`, `tenant_id`, `client_id`
Default scope: `api://{client_id}/.default`

**CLI auth commands:**
- `ops-agent auth login` — interactive browser flow (default)
- `ops-agent auth login --use-device-code` — device code flow fallback
- `ops-agent auth status` — show current auth state
- `ops-agent auth logout` — delete auth record

**File:** `cli/ops_agent_cli/files.py`

Client-side file tools (restricted to working directory):
- `read_file(path)` → resolve relative to cwd, read contents, return as string
- `write_file(path, content)` → resolve relative to cwd, write content
- `list_directory(path)` → resolve relative to cwd, list entries with metadata (size, type)
- **Safety:** Paths are resolved relative to cwd and must not escape it (no `../` traversal above cwd)

**File:** `cli/ops_agent_cli/renderer.py`

Terminal rendering using `rich`:
- Streaming text with markdown formatting (headers, code blocks, tables, bold)
- Tool call status with spinners
- Tool result summaries in dimmed text
- Error messages in red
- Client tool execution notifications

### 7. Implement backend — GitHub code tools

**File:** `backend/app/tools/github.py`

Gives the agent on-demand access to the project's source code so it understands how the system should behave.

**Authentication:** Fine-grained GitHub PAT (read-only Contents permission on the repo) stored in Key Vault as secret `ops-agent-github-pat`. Backend reads it at startup via `azure-keyvault-secrets`.

**Tools:**
- `search_code(query, file_type=None, path=None)` — Uses [GitHub Code Search API](https://docs.github.com/en/rest/search/search#search-code) (`GET /search/code?q={query}+repo:{owner}/{repo}`). Returns matching file paths with line snippets. Optional file type filter (`extension:py`) and path filter (`path:src/automation/orchestrator`).
- `read_github_file(path)` — Uses [GitHub Contents API](https://docs.github.com/en/rest/repos/contents#get-repository-content) (`GET /repos/{owner}/{repo}/contents/{path}`). Returns decoded file content. Limited to files <1MB (API limit); truncates at 50KB for context management.

**Implementation:** Uses `httpx` with `Authorization: Bearer {pat}` header. Repo owner/name from config (`OPSAGENT_GITHUB_REPO`, e.g., `owner/ma-toolkit-sandbox`). Branch defaults to `main`.

### 8. Implement startup context loading

On the **first message** of each conversation, the backend automatically loads key architecture documents from the repo and injects them as an initial system-level context block in the conversation. This gives the agent foundational knowledge without consuming tool calls.

**Files to load at startup** (via GitHub Contents API):
- `CLAUDE.md` (root — architecture overview, project descriptions, commands)
- `src/automation/cloud-worker/CLAUDE.md` (worker architecture, job/result message format)
- `src/automation/orchestrator/CLAUDE.md` (orchestration patterns, event types)
- `src/automation/database/schema.sql` (all 6 SQL tables with columns)

**Implementation:** In `backend/app/agent/loop.py`, on first message (empty conversation history), fetch these files and prepend as a user message with role context:
```
[System context - loaded from repository]
--- CLAUDE.md ---
{content}
--- cloud-worker/CLAUDE.md ---
{content}
...
```

This adds ~15-20KB of context to the first turn. Cached in memory for the lifetime of the backend process to avoid repeated GitHub API calls.

### 9. Implement system prompt

**File:** `backend/app/agent/prompts.py`

The system prompt provides concise operational context. Detailed architecture knowledge comes from the startup context (step 8) and on-demand GitHub code tools (step 7).

System prompt includes:
- Platform overview (scheduler → orchestrator → workers pipeline)
- Status enums (batch, phase, step, member statuses)
- All 6 SQL table names with key columns for diagnostic queries
- Admin API endpoint catalog (so the agent knows what it can do)
- Service Bus topic/message format (for ad-hoc dispatch)
- Safety rules: confirm destructive operations, no secrets in output
- File tool guidance: use read_file/write_file for CSV exports, report generation
- GitHub tool guidance: use search_code/read_github_file to look up implementation details when investigating how the system should behave

### 10. Create infrastructure Bicep

**File:** `infra/automation/ops-agent/deploy.bicep`

Resources:
- **App Service Plan** (Linux, B1 for MVP)
- **App Service** (Python 3.12, gunicorn + uvicorn, WebSocket enabled)
- **Application Insights** (linked to shared Log Analytics)
- **Key Vault secret:** `ops-agent-sql-connection-string` (MI auth)
- **System-assigned managed identity** RBAC:
  - Key Vault Secrets User (read SQL connection string from KV reference)
  - Service Bus Data Sender + Receiver (ad-hoc job dispatch)
  - Log Analytics Reader (KQL queries)
  - Cognitive Services User on AI Foundry resource (Claude API)

App settings (via Bicep):
```
OPSAGENT_AI_FOUNDRY_BASE_URL       → https://<resource>.services.ai.azure.com/anthropic
OPSAGENT_AI_FOUNDRY_DEPLOYMENT     → claude-opus-4-6
OPSAGENT_ADMIN_API_BASE_URL        → https://matoolkit-admin-api-func-<suffix>.azurewebsites.net
OPSAGENT_ADMIN_API_AUDIENCE        → api://<admin-api-client-id>
OPSAGENT_SQL_CONNECTION_STRING     → @Microsoft.KeyVault(SecretUri=...)
OPSAGENT_SERVICE_BUS_NAMESPACE     → matoolkit-sbus.servicebus.windows.net
OPSAGENT_LOG_ANALYTICS_WORKSPACE_ID → <workspace guid>
OPSAGENT_GITHUB_PAT                → @Microsoft.KeyVault(SecretUri=...) (fine-grained PAT, read-only Contents)
OPSAGENT_GITHUB_REPO               → owner/ma-toolkit-sandbox
OPSAGENT_ENTRA_TENANT_ID           → <tenant-id>
OPSAGENT_ENTRA_CLIENT_ID           → <ops-agent app registration client-id>
OPSAGENT_ENTRA_AUDIENCE            → api://<ops-agent-client-id>
APPLICATIONINSIGHTS_CONNECTION_STRING → from App Insights
SCM_DO_BUILD_DURING_DEPLOYMENT     → true
```

**VNet integration** (matches existing pattern):
```bicep
virtualNetworkSubnetId: !empty(opsAgentSubnetId) ? opsAgentSubnetId : null
vnetRouteAllEnabled: !empty(opsAgentSubnetId)
```

**WebSocket:** `webSocketsEnabled: true` in siteConfig

### 11. Manual setup steps (documented, not automated)

1. **Entra ID app registration** for ops-agent (public client, `http://localhost` redirect URI)
   - Create `Admin` app role (or reuse existing admin API app roles scheme)
   - Grant API permission to self (`api://<ops-agent-client-id>/.default`)
2. **Azure AI Foundry**: Create resource in portal, deploy Claude Opus 4.6 from model catalog
3. **SQL user**: `CREATE USER [matoolkit-ops-agent-app-<suffix>] FROM EXTERNAL PROVIDER; ALTER ROLE db_datareader ADD MEMBER [...]`
4. **Admin API app role**: Assign `Admin` role to the ops-agent MI in the admin API's Enterprise Application
5. **Cognitive Services RBAC**: Assign `Cognitive Services User` to MI on AI Foundry resource
6. **GitHub PAT**: Create fine-grained PAT with read-only Contents permission on the repo, store in Key Vault as `ops-agent-github-pat`

### 12. Update deployment workflows

**`deploy-infra.yml`**: Add `ops-agent` to stage selector, add deployment step.

**`deploy-apps.yml`**: Add `ops-agent` path filter (`src/automation/ops-agent/backend/**`), add deploy job using `az webapp deploy --type zip`.

### 13. Write tests

**Backend tests** (`backend/tests/test_tools.py`):
- SQL keyword blocklist: verify SELECT passes, INSERT/DROP blocked
- az CLI allowlist: verify allowed commands pass, disallowed rejected
- Tool result truncation at 50KB

**CLI tests** (`cli/tests/test_auth.py`):
- Config file read/write
- File tool path traversal prevention

## Key Dependencies

**Backend** (`backend/requirements.txt`):
```
anthropic>=0.50.0
fastapi>=0.115.0
uvicorn[standard]>=0.34.0
gunicorn>=23.0.0
httpx>=0.28.0
azure-identity>=1.19.0
azure-servicebus>=7.13.0
azure-monitor-query>=1.4.0
pyodbc>=5.1.0
pydantic-settings>=2.7.0
PyJWT[crypto]>=2.9.0
```

Note: The GitHub PAT is read via Key Vault reference in app settings (`@Microsoft.KeyVault(SecretUri=...)`) — same pattern as the SQL connection string. No additional `azure-keyvault-secrets` SDK needed; the App Service runtime resolves KV references automatically.

**CLI** (`cli/requirements.txt`):
```
websockets>=13.0
azure-identity>=1.19.0
prompt-toolkit>=3.0.0
rich>=13.0.0
```

## Critical Files to Modify

| File | Change |
|------|--------|
| `infra/shared/deploy.bicep` | Add `snet-ops-agent` (10.0.6.0/24, `Microsoft.Web/serverFarms`) + output |
| `.github/workflows/deploy-infra.yml` | Add `ops-agent` stage option + deployment step |
| `.github/workflows/deploy-apps.yml` | Add `ops-agent` path filter + deploy job |

## Critical Files to Reference

| File | Purpose |
|------|---------|
| `infra/automation/admin-api/deploy.bicep` | Pattern for App Service Bicep, RBAC, KV refs |
| `src/automation/database/schema.sql` | All 6 SQL tables the agent can query |
| `src/automation/admin-api/src/AdminApi.Functions/Functions/` | API request/response shapes |
| `src/automation/admin-cli/src/AdminCli/Services/AuthService.cs` | Auth flow to replicate in Python |
| `src/automation/cloud-worker/CLAUDE.md` | Worker job/result message format for Service Bus dispatch |

## Verification

1. **Backend local dev**: `uvicorn app.main:app --reload` with mock Claude client, test WebSocket via `websocat`
2. **CLI local dev**: `python -m ops_agent_cli.main` connecting to local backend
3. **Tool safety tests**: `pytest backend/tests/test_tools.py`
4. **Build**: `pip install -r requirements.txt` for both backend and cli
5. **Infrastructure**: `az deployment group what-if` on Bicep template
6. **End-to-end**: Install CLI (`pip install cli/`), configure (`ops-agent config set ...`), authenticate (`ops-agent auth login`), connect to deployed backend, ask "List all batches"
