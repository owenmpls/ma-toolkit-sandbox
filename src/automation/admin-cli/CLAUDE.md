# CLAUDE.md -- Admin CLI

## Project Overview

Cross-platform .NET CLI tool for managing M&A Toolkit migration automation. Provides full coverage of the Admin API for runbook management, batch creation, automation control, and execution monitoring.

## Build and Run

```bash
# Build
dotnet build src/automation/admin-cli/src/AdminCli/

# Run directly
dotnet run --project src/automation/admin-cli/src/AdminCli/ -- runbook list

# Install as global tool (from project directory)
cd src/automation/admin-cli/src/AdminCli
dotnet pack
dotnet tool install --global --add-source ./bin/Release MaToolkit.AdminCli

# Then use from anywhere
matoolkit runbook list
```

## Tests

```bash
# Run tests
dotnet test src/automation/admin-cli/tests/AdminCli.Tests/

# Run with verbose output
dotnet test src/automation/admin-cli/tests/AdminCli.Tests/ --verbosity normal
```

## Cross-Platform Publishing

```bash
# macOS (Apple Silicon)
dotnet publish src/automation/admin-cli/src/AdminCli/ -c Release -r osx-arm64 --self-contained -o publish/osx-arm64

# macOS (Intel)
dotnet publish src/automation/admin-cli/src/AdminCli/ -c Release -r osx-x64 --self-contained -o publish/osx-x64

# Windows
dotnet publish src/automation/admin-cli/src/AdminCli/ -c Release -r win-x64 --self-contained -o publish/win-x64

# Linux
dotnet publish src/automation/admin-cli/src/AdminCli/ -c Release -r linux-x64 --self-contained -o publish/linux-x64
```

## Directory Structure

```
admin-cli/
  AdminCli.sln
  CLAUDE.md
  src/
    AdminCli/
      AdminCli.csproj
      Program.cs                     # Entry point, command registration
      Services/
        AdminApiClient.cs            # HTTP client for Admin API (async auth token injection)
        AuthService.cs               # Device code flow auth via Azure.Identity
      Models/
        ApiModels.cs                 # API request/response DTOs
      Commands/
        RunbookCommands.cs           # runbook publish/list/get/versions/delete
        AutomationCommands.cs        # automation status/enable/disable
        QueryCommands.cs             # query preview
        TemplateCommands.cs          # template download
        BatchCommands.cs             # batch list/get/create/advance/cancel/members/phases/steps
        ConfigCommands.cs            # config show/set/path (includes auth key mappings)
        AuthCommands.cs              # auth login/status
  tests/
    AdminCli.Tests/
      AdminCli.Tests.csproj
      Services/
        AdminApiClientTests.cs       # API client tests with mocked HTTP
        AdminApiClientAuthTests.cs   # Auth constructor and token injection tests
        AuthServiceTests.cs          # AuthService config and IsConfigured tests
      Commands/
        CommandParsingTests.cs       # Command structure verification
        AuthCommandParsingTests.cs   # Auth command structure verification
```

## Commands

| Command | Description |
|---------|-------------|
| `runbook publish <file>` | Publish runbook from YAML file |
| `runbook list` | List all active runbooks |
| `runbook get <name>` | Get runbook definition |
| `runbook versions <name>` | List all versions |
| `runbook delete <name> <version>` | Deactivate version |
| `automation status <runbook>` | Get automation status |
| `automation enable <runbook>` | Enable automation |
| `automation disable <runbook>` | Disable automation |
| `query preview <runbook>` | Preview query results |
| `template download <runbook>` | Download CSV template |
| `batch list` | List batches |
| `batch get <id>` | Get batch details |
| `batch create <runbook> <file>` | Create batch from CSV |
| `batch advance <id>` | Advance manual batch |
| `batch cancel <id>` | Cancel batch |
| `batch members <id>` | List members |
| `batch add-members <id> <file>` | Add members from CSV |
| `batch remove-member <batch-id> <member-id>` | Remove member |
| `batch phases <id>` | List phase executions |
| `batch steps <id>` | List step executions |
| `auth login` | Sign in using device code flow |
| `auth status` | Show authentication status |
| `config show` | Show configuration |
| `config set <key> <value>` | Set configuration |
| `config path` | Show config file path |

## NuGet Packages

| Package | Version | Purpose |
|---------|---------|---------|
| System.CommandLine | 2.0.0-beta4 | CLI parsing framework |
| System.CommandLine.NamingConventionBinder | 2.0.0-beta4 | Handler binding |
| Microsoft.Extensions.Http | 9.0.0 | HttpClient factory |
| Microsoft.Extensions.Configuration | 9.0.0 | Configuration binding |
| Microsoft.Extensions.Configuration.Json | 9.0.0 | JSON config files |
| Microsoft.Extensions.Configuration.EnvironmentVariables | 9.0.0 | Environment variables |
| Azure.Identity | 1.13.1 | Entra ID device code flow authentication |
| Microsoft.Identity.Client.Extensions.Msal | 4.67.2 | Persistent token cache |
| Spectre.Console | 0.49.1 | Rich console output |

## Authentication

The CLI supports Entra ID authentication via device code flow. When configured, bearer tokens are automatically attached to all API requests.

### Setup
```bash
# Configure auth settings
matoolkit config set tenant-id YOUR_TENANT_ID
matoolkit config set client-id YOUR_CLIENT_ID
matoolkit config set api-scope api://YOUR_CLIENT_ID/.default  # optional, defaults to api://{client-id}/.default

# Sign in
matoolkit auth login

# Check status
matoolkit auth status
```

### How it works
- `AuthService` uses `DeviceCodeCredential` from `Azure.Identity`
- Tokens are cached persistently via MSAL (`TokenCachePersistenceOptions` named `"matoolkit-cli"`)
- `AdminApiClient.GetConfiguredClientAsync()` attaches `Authorization: Bearer <token>` when auth is configured
- If auth is not configured (no tenant-id/client-id), requests are sent without auth headers

### Environment variables
Auth settings can also be set via environment variables:
- `MATOOLKIT_TENANT_ID` – Entra ID tenant ID
- `MATOOLKIT_CLIENT_ID` – App registration client ID
- `MATOOLKIT_API_SCOPE` – API scope (optional)

## Configuration

The CLI reads configuration from:
1. Command-line options (`--api-url`)
2. Environment variables (`MATOOLKIT_*`)
3. Config file (`~/.matoolkit/config.json`)

Available config keys: `api-url`, `tenant-id`, `client-id`, `api-scope`

Set configuration:
```bash
# Via environment variable
export MATOOLKIT_API_URL=https://your-api.azurewebsites.net

# Via config file
matoolkit config set api-url https://your-api.azurewebsites.net

# Via command-line (per command)
matoolkit --api-url https://your-api.azurewebsites.net runbook list
```

## Key Architecture Decisions

- **System.CommandLine**: Modern .NET CLI framework with automatic help generation
- **Spectre.Console**: Rich terminal output with tables, panels, colors
- **Configuration hierarchy**: Command-line > Environment > Config file
- **Device code flow auth**: `Azure.Identity` with persistent MSAL token cache for SSO across sessions
- **Optional auth**: `AuthService` is passed as optional parameter — CLI works without auth configured
- **Global tool packaging**: Installable via `dotnet tool install`
- **Self-contained publishing**: Standalone executables for each platform
- **Async/await throughout**: All API calls are async (`GetConfiguredClientAsync` injects bearer tokens)

## Error Handling

- All API errors are caught and displayed with status code
- Set `MATOOLKIT_DEBUG=1` for stack traces
- Exit code 0 for success, 1 for errors

## Testing

**Test coverage (62 tests):**
- **AdminApiClientTests** (28 tests) – HTTP client tests with mocked responses (RichardSzalay.MockHttp)
- **CommandParsingTests** (18 tests) – Verify command structure, arguments, options, descriptions
- **AuthServiceTests** (9 tests) – `IsConfigured()` logic, property accessors, unconfigured error handling
- **AuthCommandParsingTests** (4 tests) – Auth command structure (login/status subcommands, descriptions)
- **AdminApiClientAuthTests** (3 tests) – Constructor with/without AuthService, optional parameter default
