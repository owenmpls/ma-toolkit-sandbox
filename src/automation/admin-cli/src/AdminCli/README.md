# M&A Toolkit CLI (matoolkit)

Command-line interface for managing M&A Toolkit migration automation.

## Installation

### As a .NET Global Tool

```bash
# Install from local build
dotnet pack
dotnet tool install --global --add-source ./nupkg MaToolkit.AdminCli

# Or install from NuGet (when published)
dotnet tool install --global MaToolkit.AdminCli
```

### As a Standalone Executable

```bash
# Build for your platform
dotnet publish -c Release -r osx-arm64 --self-contained
dotnet publish -c Release -r osx-x64 --self-contained
dotnet publish -c Release -r win-x64 --self-contained
dotnet publish -c Release -r linux-x64 --self-contained
```

## Configuration

Set the Admin API URL before using the CLI:

```bash
# Option 1: Environment variable (recommended for CI/CD)
export MATOOLKIT_API_URL=https://your-admin-api.azurewebsites.net

# Option 2: Config file
matoolkit config set api-url https://your-admin-api.azurewebsites.net

# Option 3: Per-command flag
matoolkit --api-url https://your-admin-api.azurewebsites.net runbook list
```

View current configuration:

```bash
matoolkit config show
```

## Authentication

The CLI uses Entra ID device code flow for authentication. Configure and sign in:

```bash
# Set auth parameters
matoolkit config set tenant-id YOUR_ENTRA_TENANT_ID
matoolkit config set client-id YOUR_APP_CLIENT_ID
matoolkit config set api-scope api://YOUR_CLIENT_ID/.default  # optional

# Sign in (opens browser for device code flow)
matoolkit auth login

# Check auth status
matoolkit auth status
```

Tokens are cached persistently — you only need to sign in once. For CI/CD, use environment variables:

```bash
export MATOOLKIT_TENANT_ID=your-tenant-id
export MATOOLKIT_CLIENT_ID=your-client-id
export MATOOLKIT_API_SCOPE=api://your-client-id/.default
```

## Commands

### Runbook Management

```bash
# Publish a runbook from YAML file
matoolkit runbook publish migration.yaml
matoolkit runbook publish migration.yaml --name my-runbook

# List all active runbooks
matoolkit runbook list
matoolkit runbook ls

# Get a runbook definition
matoolkit runbook get my-runbook
matoolkit runbook get my-runbook --version 2
matoolkit runbook get my-runbook --output my-runbook.yaml

# List all versions of a runbook
matoolkit runbook versions my-runbook

# Deactivate a runbook version
matoolkit runbook delete my-runbook --version 2
matoolkit runbook delete my-runbook --version 2 --force
```

### Automation Control

```bash
# Check automation status
matoolkit automation status my-runbook

# Enable automated batch creation
matoolkit automation enable my-runbook

# Disable automated batch creation
matoolkit automation disable my-runbook
```

### Query Preview

```bash
# Preview query results without creating a batch
matoolkit query preview my-runbook
matoolkit query preview my-runbook --limit 20
matoolkit query preview my-runbook --json
```

### CSV Templates

```bash
# Download CSV template for manual batch creation
matoolkit template download my-runbook
matoolkit template download my-runbook --output members.csv
```

### Batch Management

```bash
# List batches
matoolkit batch list
matoolkit batch list --runbook my-runbook
matoolkit batch list --status active
matoolkit batch ls

# Get batch details
matoolkit batch get 123

# Create a manual batch from CSV
matoolkit batch create my-runbook members.csv

# Advance a manual batch to the next phase
matoolkit batch advance 123
matoolkit batch advance 123 --auto  # Advance through all phases

# Cancel a batch
matoolkit batch cancel 123
matoolkit batch cancel 123 --force
```

### Member Management

```bash
# List batch members
matoolkit batch members 123

# Add members to a batch
matoolkit batch add-members 123 new-members.csv

# Remove a member from a batch
matoolkit batch remove-member 123 456
matoolkit batch remove-member 123 456 --force
```

### Execution Tracking

```bash
# List phase executions
matoolkit batch phases 123

# List step executions
matoolkit batch steps 123
matoolkit batch steps 123 --phase migration
matoolkit batch steps 123 --status failed
matoolkit batch steps 123 --limit 100
```

## Typical Workflows

### Manual Batch Workflow

```bash
# 1. Download template
matoolkit template download my-runbook --output members.csv

# 2. Edit members.csv with your data
# ...

# 3. Create the batch
matoolkit batch create my-runbook members.csv

# 4. Advance through phases
matoolkit batch advance 123  # Dispatch init steps
# Wait for init to complete...
matoolkit batch advance 123  # Dispatch first phase
# Wait for phase to complete...
matoolkit batch advance 123  # Dispatch next phase
# ...

# 5. Monitor progress
matoolkit batch get 123
matoolkit batch phases 123
matoolkit batch steps 123
```

### Enable Automation

```bash
# 1. Publish runbook
matoolkit runbook publish my-runbook.yaml

# 2. Preview what the query returns
matoolkit query preview my-runbook

# 3. Enable automation
matoolkit automation enable my-runbook

# 4. Monitor batches
matoolkit batch list --runbook my-runbook
```

## Environment Variables

| Variable | Description |
|----------|-------------|
| `MATOOLKIT_API_URL` | Admin API base URL |
| `MATOOLKIT_TENANT_ID` | Entra ID tenant ID |
| `MATOOLKIT_CLIENT_ID` | App registration client ID |
| `MATOOLKIT_API_SCOPE` | API scope (optional, defaults to `api://{client-id}/.default`) |
| `MATOOLKIT_DEBUG` | Set to `1` for debug output |

## Exit Codes

| Code | Description |
|------|-------------|
| 0 | Success |
| 1 | Error (check stderr for details) |

## License

Proprietary - M&A Toolkit
