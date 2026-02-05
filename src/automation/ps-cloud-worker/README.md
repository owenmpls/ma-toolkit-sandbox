# PowerShell Cloud Worker

Containerized PowerShell worker for Azure Container Apps that processes migration automation jobs via Azure Service Bus. Part of the Migration Automation Toolkit's automation subsystem.

## Overview

The worker receives job messages from a Service Bus topic, executes functions from a standard library (Microsoft Graph + Exchange Online) or custom modules, and reports results back via a results topic. It supports parallel job execution via a managed RunspacePool with per-runspace authenticated sessions.

## Architecture

See [docs/architecture.md](docs/architecture.md) for detailed architecture documentation.

## Quick Start

### Prerequisites

- Azure subscription with Contributor access
- Azure CLI (`az`) and Bicep CLI installed
- Docker installed (for building the container image)
- PowerShell 7.4+
- An Entra ID app registration in the target tenant with:
  - Microsoft Graph application permissions: `User.ReadWrite.All`, `Group.ReadWrite.All`, `Directory.ReadWrite.All`
  - Exchange Online application permission (via app role or admin consent)
  - A client secret stored in Azure Key Vault

### Deploy Infrastructure

```bash
az deployment group create \
  --resource-group your-rg \
  --template-file infra/deploy.bicep \
  --parameters infra/deploy.parameters.json
```

### Build and Push Container

```bash
az acr login --name matoolkitacr
docker build -t matoolkitacr.azurecr.io/ps-cloud-worker:latest .
docker push matoolkitacr.azurecr.io/ps-cloud-worker:latest
```

### Store the App Secret

```bash
az keyvault secret set \
  --vault-name matoolkit-kv \
  --name worker-app-secret \
  --value "<your-app-client-secret>"
```

### Test

```powershell
./tests/Submit-TestJob.ps1 `
  -CsvPath ./tests/sample-jobs.csv `
  -ServiceBusNamespace 'matoolkit-sb.servicebus.windows.net' `
  -WorkerId 'worker-01' `
  -FunctionName 'New-EntraUser'
```

See [docs/deployment-guide.md](docs/deployment-guide.md) for full deployment instructions and [docs/job-submission-guide.md](docs/job-submission-guide.md) for job message format.

## Project Structure

```
ps-cloud-worker/
├── src/                          # Worker runtime code
│   ├── worker.ps1                # Main entry point
│   ├── config.ps1                # Configuration loader
│   ├── auth.ps1                  # Azure/MgGraph/EXO authentication
│   ├── servicebus.ps1            # Service Bus client
│   ├── runspace-manager.ps1      # Parallel execution pool
│   ├── job-dispatcher.ps1        # Job processing loop
│   ├── logging.ps1               # Application Insights logging
│   └── throttle-handler.ps1      # Retry/backoff for throttling
├── modules/
│   ├── StandardFunctions/        # Built-in migration functions
│   └── CustomFunctions/          # Customer extension modules
├── tests/                        # Test scripts and sample data
├── infra/                        # Bicep templates
└── docs/                         # Documentation
```

## Standard Functions

| Function | Service | Returns |
|---|---|---|
| `New-EntraUser` | Graph | Object (user details) |
| `Set-EntraUserUPN` | Graph | Boolean |
| `Add-EntraGroupMember` | Graph | Boolean |
| `Remove-EntraGroupMember` | Graph | Boolean |
| `New-EntraB2BInvitation` | Graph | Object (invitation) |
| `Convert-EntraB2BToInternal` | Graph | Object (user + password) |
| `Add-ExchangeSecondaryEmail` | EXO | Boolean |
| `Set-ExchangePrimaryEmail` | EXO | Boolean |
| `Set-ExchangeExternalAddress` | EXO | Boolean |
| `Set-ExchangeMailUserGuids` | EXO | Boolean |
| `Test-EntraAttributeMatch` | Graph | Object (match result) |
| `Test-ExchangeAttributeMatch` | EXO | Object (match result) |
| `Test-EntraGroupMembership` | Graph | Object (membership) |
| `Test-ExchangeGroupMembership` | EXO | Object (membership) |

## Extending with Custom Functions

Place custom modules in `modules/CustomFunctions/`. See [modules/CustomFunctions/README.md](modules/CustomFunctions/README.md) for the function contract and examples.
