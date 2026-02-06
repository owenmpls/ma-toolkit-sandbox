# Admin CLI Usage Guide

## Installation

### As a .NET Global Tool

```bash
cd src/automation/admin-cli/src/AdminCli
dotnet pack
dotnet tool install --global --add-source ./bin/Release MaToolkit.AdminCli
```

### As a Standalone Executable

```bash
# macOS (Apple Silicon)
dotnet publish src/automation/admin-cli/src/AdminCli/ -c Release -r osx-arm64 --self-contained -o publish/osx-arm64

# Windows
dotnet publish src/automation/admin-cli/src/AdminCli/ -c Release -r win-x64 --self-contained -o publish/win-x64

# Linux
dotnet publish src/automation/admin-cli/src/AdminCli/ -c Release -r linux-x64 --self-contained -o publish/linux-x64
```

## Initial Setup

### 1. Configure the API URL

```bash
matoolkit config set api-url https://your-admin-api.azurewebsites.net
```

### 2. Configure Authentication

```bash
matoolkit config set tenant-id YOUR_ENTRA_TENANT_ID
matoolkit config set client-id YOUR_APP_CLIENT_ID

# Optional: override the default scope
matoolkit config set api-scope api://YOUR_CLIENT_ID/.default
```

### 3. Sign In

```bash
matoolkit auth login
```

This triggers the device code flow — open the URL shown in the terminal and enter the code. After signing in, your token is cached and you won't need to sign in again until it expires.

### 4. Verify Setup

```bash
matoolkit config show     # Check all settings
matoolkit auth status     # Check auth configuration
matoolkit runbook list    # Test API connectivity
```

## Authentication

### Device Code Flow

The CLI uses Entra ID device code flow for interactive authentication. When you run `matoolkit auth login`:

1. The CLI displays a URL and a code
2. Open the URL in a browser and enter the code
3. Sign in with your Entra ID account
4. The CLI receives and caches your token

Tokens are cached persistently on the OS credential store. You only need to sign in once per session (tokens are refreshed automatically).

### Environment Variables

For CI/CD or scripting, you can set auth via environment variables:

```bash
export MATOOLKIT_API_URL=https://your-api.azurewebsites.net
export MATOOLKIT_TENANT_ID=your-tenant-id
export MATOOLKIT_CLIENT_ID=your-client-id
export MATOOLKIT_API_SCOPE=api://your-client-id/.default
```

### Checking Auth Status

```bash
matoolkit auth status
```

Shows tenant ID, client ID, API scope, and whether auth is configured.

## Command Reference

### Runbook Management

```bash
# Publish a runbook from YAML file
matoolkit runbook publish migration.yaml
matoolkit runbook publish migration.yaml --name my-runbook

# List all active runbooks
matoolkit runbook list
matoolkit runbook ls                              # alias

# Get a runbook definition
matoolkit runbook get my-runbook
matoolkit runbook get my-runbook --version 2      # specific version
matoolkit runbook get my-runbook --output out.yaml # save to file

# List all versions of a runbook
matoolkit runbook versions my-runbook

# Deactivate a runbook version
matoolkit runbook delete my-runbook --version 2
matoolkit runbook delete my-runbook --version 2 --force  # skip confirmation
```

### Automation Control

```bash
# Check automation status
matoolkit automation status my-runbook

# Enable automated batch creation (scheduler will query data sources)
matoolkit automation enable my-runbook

# Disable automated batch creation (existing batches continue processing)
matoolkit automation disable my-runbook
```

### Query Preview

```bash
# Preview query results without creating a batch
matoolkit query preview my-runbook
matoolkit query preview my-runbook --limit 20     # limit sample rows
matoolkit query preview my-runbook --json         # JSON output
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
matoolkit batch ls                                # alias

# Get batch details (includes phases and init steps)
matoolkit batch get 123

# Create a manual batch from CSV
matoolkit batch create my-runbook members.csv

# Advance a manual batch to the next phase
matoolkit batch advance 123
matoolkit batch advance 123 --auto                # advance through all phases

# Cancel a batch
matoolkit batch cancel 123
matoolkit batch cancel 123 --force                # skip confirmation
```

### Member Management

```bash
# List batch members
matoolkit batch members 123

# Add members to a manual batch
matoolkit batch add-members 123 new-members.csv

# Remove a member
matoolkit batch remove-member 123 456
matoolkit batch remove-member 123 456 --force     # skip confirmation
```

### Execution Tracking

```bash
# List phase executions
matoolkit batch phases 123

# List step executions
matoolkit batch steps 123
matoolkit batch steps 123 --phase migration       # filter by phase
matoolkit batch steps 123 --status failed          # filter by status
matoolkit batch steps 123 --limit 100             # limit results
```

### Configuration

```bash
# Show all settings and their sources
matoolkit config show

# Set a configuration value
matoolkit config set api-url https://your-api.azurewebsites.net
matoolkit config set tenant-id YOUR_TENANT_ID
matoolkit config set client-id YOUR_CLIENT_ID
matoolkit config set api-scope api://YOUR_CLIENT_ID/.default

# Show config file path
matoolkit config path
```

## Workflows

### First-Time Setup

```bash
# 1. Install
cd src/automation/admin-cli/src/AdminCli && dotnet pack
dotnet tool install --global --add-source ./bin/Release MaToolkit.AdminCli

# 2. Configure
matoolkit config set api-url https://your-api.azurewebsites.net
matoolkit config set tenant-id YOUR_TENANT_ID
matoolkit config set client-id YOUR_CLIENT_ID

# 3. Authenticate
matoolkit auth login

# 4. Verify
matoolkit runbook list
```

### Manual Migration Batch

```bash
# 1. Download the CSV template
matoolkit template download my-runbook --output members.csv

# 2. Edit members.csv with your migration members
#    (fill in primary key + required columns)

# 3. Create the batch
matoolkit batch create my-runbook members.csv
# Output: batchId=123, status=pending_init, memberCount=25

# 4. Advance through init steps
matoolkit batch advance 123
# Output: action=init_dispatched

# 5. Wait for init to complete, then advance each phase
matoolkit batch get 123          # check status
matoolkit batch advance 123      # dispatch next phase
matoolkit batch advance 123      # dispatch next phase
# ... repeat until all phases complete

# 6. Monitor progress
matoolkit batch phases 123       # see phase status
matoolkit batch steps 123        # see individual step results
matoolkit batch steps 123 --status failed  # check failures
```

### Automated Migration Setup

```bash
# 1. Publish the runbook
matoolkit runbook publish my-runbook.yaml

# 2. Preview what the query returns
matoolkit query preview my-runbook

# 3. Enable automation (scheduler creates batches every 5 min)
matoolkit automation enable my-runbook

# 4. Monitor
matoolkit batch list --runbook my-runbook
matoolkit automation status my-runbook

# 5. Disable when done
matoolkit automation disable my-runbook
```

### Add Members to an Existing Batch

```bash
# 1. Download template
matoolkit template download my-runbook --output new-members.csv

# 2. Fill in the new members

# 3. Add to existing batch
matoolkit batch add-members 123 new-members.csv
# Output: addedCount=10, skippedCount=2 (duplicates)
```

### Investigate Failed Steps

```bash
# List failed steps for a batch
matoolkit batch steps 123 --status failed

# Get full batch details
matoolkit batch get 123

# Check phase status
matoolkit batch phases 123
```

## Configuration Reference

### Config File Location

```
~/.matoolkit/config.json
```

### Available Keys

| Key | Environment Variable | Description |
|-----|---------------------|-------------|
| `api-url` | `MATOOLKIT_API_URL` | Admin API base URL |
| `tenant-id` | `MATOOLKIT_TENANT_ID` | Entra ID tenant ID |
| `client-id` | `MATOOLKIT_CLIENT_ID` | App registration client ID |
| `api-scope` | `MATOOLKIT_API_SCOPE` | API scope (default: `api://{client-id}/.default`) |

### Priority Order

1. Command-line option (`--api-url`)
2. Environment variable (`MATOOLKIT_*`)
3. Config file (`~/.matoolkit/config.json`)

## Troubleshooting

### "API URL not configured"

```bash
matoolkit config set api-url https://your-api.azurewebsites.net
# or
export MATOOLKIT_API_URL=https://your-api.azurewebsites.net
```

### "Authentication not configured"

```bash
matoolkit config set tenant-id YOUR_TENANT_ID
matoolkit config set client-id YOUR_CLIENT_ID
matoolkit auth login
```

### 401 Unauthorized

- Token may have expired — run `matoolkit auth login` again
- Verify tenant-id and client-id match the API's app registration
- Check `matoolkit auth status` for current configuration

### 403 Forbidden

- Your account doesn't have the required app role
- Admin operations require the `Admin` role in the Entra ID Enterprise Application
- Contact your admin to assign the role

### Debug Output

```bash
export MATOOLKIT_DEBUG=1
matoolkit runbook list          # will show stack traces on errors
```

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Error (details on stderr) |
