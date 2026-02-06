# Admin CLI Architecture

## Overview

The Admin CLI (`matoolkit`) is a cross-platform .NET CLI tool that provides full coverage of the Admin API. It is built with `System.CommandLine` for parsing and `Spectre.Console` for rich terminal output, with Entra ID device code flow authentication via `Azure.Identity`.

## Component Diagram

```
+-------------------+
|   matoolkit CLI    |
+-------------------+
| Program.cs        |  Entry point, builds config, creates services, registers commands
|   |               |
|   +-- AuthService |  Device code flow, persistent MSAL token cache
|   |               |
|   +-- AdminApiClient  HTTP client, auto-injects bearer tokens
|       |           |
|       +-- Commands/   One class per command group (runbook, batch, auth, etc.)
+-------------------+
        |
        | HTTPS + Bearer token
        v
+-------------------+
|   Admin API       |
| (Azure Functions) |
+-------------------+
```

## Key Components

### Program.cs — Entry Point

Orchestrates startup:

1. `BuildConfiguration()` — loads config from env vars (`MATOOLKIT_*`) and `~/.matoolkit/config.json`
2. Creates `AuthService` with the configuration
3. Creates `AdminApiClient` with configuration + auth service
4. Registers all command groups on the root command
5. Builds the `System.CommandLine` parser and invokes it

### AuthService — Authentication

Manages Entra ID device code flow authentication:

```
AuthService
  ├── IsConfigured()          Check if tenant-id + client-id are set
  ├── GetAccessTokenAsync()   Get/refresh token via DeviceCodeCredential
  ├── TenantId                From config or MATOOLKIT_TENANT_ID env var
  ├── ClientId                From config or MATOOLKIT_CLIENT_ID env var
  └── ApiScope                From config or MATOOLKIT_API_SCOPE env var
```

**Token caching**: Uses `TokenCachePersistenceOptions` with name `"matoolkit-cli"`. MSAL persists tokens to the OS credential store (Keychain on macOS, DPAPI on Windows, libsecret on Linux). Users only need to complete the device code flow once; subsequent calls use the cached token until it expires.

**Device code flow**: When a fresh token is needed, `DeviceCodeCredential` displays a message like "To sign in, use a web browser to open https://microsoft.com/devicelogin and enter the code ABCD1234". The CLI shows this via `Spectre.Console` markup.

### AdminApiClient — HTTP Client

Wraps all Admin API calls with automatic authentication:

```
AdminApiClient
  ├── GetApiUrl()                   Resolve API URL from override/config/env
  ├── GetConfiguredClientAsync()    Configure HttpClient + inject bearer token
  │
  ├── Runbook Operations
  │   ├── PublishRunbookAsync()
  │   ├── ListRunbooksAsync()
  │   ├── GetRunbookAsync()
  │   ├── ListRunbookVersionsAsync()
  │   └── DeleteRunbookVersionAsync()
  │
  ├── Automation Operations
  │   ├── GetAutomationStatusAsync()
  │   └── SetAutomationStatusAsync()
  │
  ├── Query/Template Operations
  │   ├── PreviewQueryAsync()
  │   └── DownloadTemplateAsync()
  │
  ├── Batch Operations
  │   ├── ListBatchesAsync()
  │   ├── GetBatchAsync()
  │   ├── CreateBatchAsync()
  │   ├── AdvanceBatchAsync()
  │   └── CancelBatchAsync()
  │
  ├── Member Operations
  │   ├── ListMembersAsync()
  │   ├── AddMembersAsync()
  │   └── RemoveMemberAsync()
  │
  └── Execution Tracking
      ├── ListPhasesAsync()
      └── ListStepsAsync()
```

**Auth injection**: `GetConfiguredClientAsync()` checks if `AuthService.IsConfigured()` is true. If so, it calls `GetAccessTokenAsync()` and sets the `Authorization: Bearer` header. If auth is not configured, requests are sent without auth headers (for local development or function-key environments).

**Error handling**: `EnsureSuccessAsync()` reads the response body on non-2xx status and throws `HttpRequestException` with the status code and body.

### Command Classes — CLI Interface

Each command group is a static class with a `Create()` factory method that returns a `System.CommandLine.Command`:

```
Commands/
  ├── RunbookCommands.cs      runbook publish|list|get|versions|delete
  ├── AutomationCommands.cs   automation status|enable|disable
  ├── QueryCommands.cs        query preview
  ├── TemplateCommands.cs     template download
  ├── BatchCommands.cs        batch list|get|create|advance|cancel|members|...
  ├── AuthCommands.cs         auth login|status
  └── ConfigCommands.cs       config show|set|path
```

Commands receive either `AdminApiClient` or `AuthService` via their `Create()` method (not DI — `System.CommandLine` uses handler binding). Each command handler:

1. Calls the appropriate `AdminApiClient` method
2. Formats the result using `Spectre.Console` (tables, panels, markup)
3. Handles errors with colored output

## Configuration System

### Hierarchy (highest priority first)

1. **Command-line options** — `--api-url` global option
2. **Environment variables** — `MATOOLKIT_API_URL`, `MATOOLKIT_TENANT_ID`, etc.
3. **Config file** — `~/.matoolkit/config.json`

### Config File

```json
{
  "API_URL": "https://your-api.azurewebsites.net",
  "TENANT_ID": "your-tenant-id",
  "CLIENT_ID": "your-client-id",
  "API_SCOPE": "api://your-client-id/.default"
}
```

The `config set` command maps user-friendly keys to internal keys:

| User Key | Config Key |
|----------|------------|
| `api-url` | `API_URL` |
| `tenant-id` | `TENANT_ID` |
| `client-id` | `CLIENT_ID` |
| `api-scope` | `API_SCOPE` |

## Authentication Flow

```
matoolkit auth login
    |
    v
AuthService.IsConfigured()?
    |
    ├── No  → Error: "Set tenant-id and client-id first"
    |
    └── Yes → DeviceCodeCredential.GetTokenAsync()
                |
                ├── Cached token valid? → Return immediately
                |
                └── No cached token → Device code flow
                      |
                      v
                    Display: "To sign in, open https://microsoft.com/devicelogin
                              and enter code ABCD1234"
                      |
                      v
                    User completes browser flow
                      |
                      v
                    Token cached to OS credential store
                      |
                      v
                    "Successfully authenticated!"
```

Subsequent API calls:

```
matoolkit runbook list
    |
    v
AdminApiClient.ListRunbooksAsync()
    |
    v
GetConfiguredClientAsync()
    |
    v
AuthService.GetAccessTokenAsync()
    |
    ├── Cached token valid? → Use cached token
    └── Expired?           → MSAL refreshes automatically
    |
    v
HTTP GET /api/runbooks
  Authorization: Bearer eyJ0eXAi...
```

## Testing Strategy

Tests are organized into three areas:

### AdminApiClientTests (28 tests)
- Uses `RichardSzalay.MockHttp` to mock HTTP responses
- Injects mock `HttpClient` via reflection (replaces private `_httpClient` field)
- Tests all 17 API methods plus error handling

### CommandParsingTests (18 tests)
- Verifies command tree structure via `System.CommandLine` introspection
- Checks subcommands, arguments, options, aliases, and descriptions

### Auth Tests (16 tests)
- `AuthServiceTests` (9) — `IsConfigured()` logic, property accessors, error when unconfigured
- `AuthCommandParsingTests` (4) — `auth login` and `auth status` command structure
- `AdminApiClientAuthTests` (3) — Constructor accepts optional `AuthService`, works with null

## Packaging & Distribution

### Global Tool

```bash
cd src/automation/admin-cli/src/AdminCli
dotnet pack
dotnet tool install --global --add-source ./bin/Release MaToolkit.AdminCli
```

Configured via `AdminCli.csproj`:
- `PackAsTool=true`, `ToolCommandName=matoolkit`
- Framework-dependent (requires .NET runtime)

### Self-Contained Executables

```bash
dotnet publish -c Release -r osx-arm64 --self-contained -o publish/osx-arm64
dotnet publish -c Release -r win-x64 --self-contained -o publish/win-x64
dotnet publish -c Release -r linux-x64 --self-contained -o publish/linux-x64
```

Produces standalone executables with no .NET runtime dependency.
