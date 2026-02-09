# Admin CLI Usage Guide

## Overview

`matoolkit` is a cross-platform .NET CLI tool for managing the M&A Toolkit automation subsystem. It provides full coverage of the Admin API for runbook management, batch creation, automation control, and execution monitoring.

## Installation

### As a .NET Global Tool

```bash
# Build and install from source
cd src/automation/admin-cli/src/AdminCli
dotnet pack
dotnet tool install --global --add-source ./bin/Release MaToolkit.AdminCli

# Then use from anywhere
matoolkit --help
```

### As a Standalone Executable

```bash
# macOS (Apple Silicon)
dotnet publish src/automation/admin-cli/src/AdminCli/ -c Release -r osx-arm64 --self-contained

# macOS (Intel)
dotnet publish src/automation/admin-cli/src/AdminCli/ -c Release -r osx-x64 --self-contained

# Windows
dotnet publish src/automation/admin-cli/src/AdminCli/ -c Release -r win-x64 --self-contained

# Linux
dotnet publish src/automation/admin-cli/src/AdminCli/ -c Release -r linux-x64 --self-contained
```

## Configuration

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

### Commands

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

## Authentication

The CLI uses Entra ID device code flow for authentication. Tokens are cached persistently via MSAL -- you only need to sign in once.

```bash
# Configure auth settings
matoolkit config set tenant-id YOUR_TENANT_ID
matoolkit config set client-id YOUR_CLIENT_ID

# Sign in (opens browser for device code flow)
matoolkit auth login

# Check auth status
matoolkit auth status
```

For CI/CD, use environment variables:
```bash
export MATOOLKIT_TENANT_ID=your-tenant-id
export MATOOLKIT_CLIENT_ID=your-client-id
export MATOOLKIT_API_SCOPE=api://your-client-id/.default
```

## Command Reference

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

## Common Workflows

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

# 3. Create the batch
matoolkit batch create my-runbook members.csv

# 4. Advance through init steps
matoolkit batch advance 123

# 5. Wait for init to complete, then advance each phase
matoolkit batch get 123          # check status
matoolkit batch advance 123      # dispatch next phase
matoolkit batch advance 123      # dispatch next phase

# 6. Monitor progress
matoolkit batch phases 123
matoolkit batch steps 123
matoolkit batch steps 123 --status failed
```

### Automated Migration Setup

```bash
# 1. Publish the runbook
matoolkit runbook publish my-runbook.yaml

# 2. Preview what the query returns
matoolkit query preview my-runbook

# 3. Enable automation
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
```

### Investigate Failed Steps

```bash
matoolkit batch steps 123 --status failed
matoolkit batch get 123
matoolkit batch phases 123
```

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Error (details on stderr) |

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

- Token may have expired -- run `matoolkit auth login` again
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
