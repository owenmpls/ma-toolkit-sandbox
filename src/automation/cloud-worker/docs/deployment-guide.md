# Deployment Guide

## Prerequisites

- Azure subscription with Contributor access to a resource group
- Azure CLI (`az`) with Bicep support
- Docker
- PowerShell 7.4+
- An Entra ID app registration in the **target** tenant

## Step 1: Create the App Registration

In the **target** tenant (the tenant the worker will manage), create an app registration:

1. Go to **Azure Portal > Entra ID > App registrations > New registration**
2. Name: `migration-worker` (or similar)
3. Supported account types: **Single tenant**
4. No redirect URI needed

### Assign API Permissions

**Microsoft Graph** (Application permissions):
- `User.ReadWrite.All`
- `Group.ReadWrite.All`
- `Directory.ReadWrite.All`

**Exchange Online** (for Exchange cmdlets to work with app-only auth):
- Under **APIs my organization uses**, search for `Office 365 Exchange Online`
- Add Application permission: `Exchange.ManageAsApp`

Grant admin consent for all permissions.

### Create a Certificate

1. Go to **Certificates & secrets > Certificates > Upload certificate**
2. Upload the public key portion of a certificate (`.cer` or `.pem`)
3. Note the thumbprint — you'll import the full PFX (with private key) into Key Vault in step 3

Alternatively, generate a self-signed certificate:

```bash
# Generate a self-signed certificate (valid for 1 year)
openssl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem -days 365 -nodes -subj '/CN=migration-worker'
openssl pkcs12 -export -out worker-cert.pfx -inkey key.pem -in cert.pem

# Upload the public key to the app registration
# Then import the PFX to Key Vault (step 3)
```

### Assign Exchange Admin Role

For Exchange Online app-only access, the app registration's service principal must be assigned the **Exchange Administrator** role:

```powershell
# Connect to the target tenant
Connect-MgGraph -TenantId <target-tenant-id> -Scopes "RoleManagement.ReadWrite.Directory"

# Get the service principal
$sp = Get-MgServicePrincipal -Filter "appId eq '<your-app-id>'"

# Get the Exchange Administrator role
$role = Get-MgDirectoryRole -Filter "displayName eq 'Exchange Administrator'"
if (-not $role) {
    $roleTemplate = Get-MgDirectoryRoleTemplate -Filter "displayName eq 'Exchange Administrator'"
    $role = New-MgDirectoryRole -RoleTemplateId $roleTemplate.Id
}

# Assign the role
New-MgDirectoryRoleMember -DirectoryRoleId $role.Id -BodyParameter @{
    "@odata.id" = "https://graph.microsoft.com/v1.0/directoryObjects/$($sp.Id)"
}
```

## Step 2: Deploy Azure Infrastructure

Edit `infra/automation/cloud-worker/deploy.parameters.json` (relative to the repo root) with your values:

```json
{
  "parameters": {
    "baseName": { "value": "matoolkit" },
    "location": { "value": "eastus" },
    "targetTenantId": { "value": "<target-tenant-guid>" },
    "appId": { "value": "<app-registration-client-id>" },
    "workerId": { "value": "worker-01" },
    "maxParallelism": { "value": 4 },
    "idleTimeoutSeconds": { "value": 300 }
  }
}
```

Deploy:

```bash
az group create --name matoolkit-rg --location eastus

az deployment group create \
  --resource-group matoolkit-rg \
  --template-file infra/automation/cloud-worker/deploy.bicep \
  --parameters infra/automation/cloud-worker/deploy.parameters.json
```

This creates:
- Container App Environment + Container App (min 0, max 1 replicas — scales to zero when idle)
- KEDA Service Bus scaler that starts the container when messages arrive for the worker
- Service Bus namespace with `worker-jobs` and `worker-results` topics
- Worker subscription with SQL filter on `worker-jobs` topic
- Shared access policy on `worker-jobs` topic for KEDA monitoring
- Orchestrator subscription on `worker-results` topic
- Key Vault with RBAC
- Application Insights + Log Analytics workspace
- Container Registry
- Role assignments for managed identity

## Step 3: Import the Certificate into Key Vault

```bash
az keyvault certificate import \
  --vault-name matoolkit-kv \
  --name worker-app-cert \
  --file worker-cert.pfx
```

Key Vault stores the PFX as a certificate object. The worker retrieves it via the associated secret (same name), which returns the base64-encoded PFX with the private key.

## Step 4: Build and Push the Container Image

```bash
# Login to ACR
az acr login --name matoolkitacr

# Build the image
docker build -t matoolkitacr.azurecr.io/cloud-worker:latest .

# Push to ACR
docker push matoolkitacr.azurecr.io/cloud-worker:latest
```

After pushing, restart the container app to pick up the new image:

```bash
az containerapp revision restart \
  --name matoolkit-worker-worker-01 \
  --resource-group matoolkit-rg
```

## Step 5: Verify

Check container logs:

```bash
az containerapp logs show \
  --name matoolkit-worker-worker-01 \
  --resource-group matoolkit-rg \
  --follow
```

You should see the startup banner and "Worker 'worker-01' is READY and listening for jobs."

### Scale-to-Zero Behavior

The Container App is configured with **min replicas = 0** and **max replicas = 1**. A KEDA `azure-servicebus` scaler monitors the worker's subscription on the `worker-jobs` topic. When one or more messages are pending, KEDA scales the container from 0 to 1. The worker starts, authenticates, processes all available jobs, and then monitors the subscription for new messages. Once the idle timeout is reached (`IDLE_TIMEOUT_SECONDS`, default 300s), the worker shuts down gracefully and the process exits, allowing ACA to scale the container back to zero.

**Lifecycle:**
1. Orchestrator enqueues job(s) on the `worker-jobs` topic with `WorkerId` property
2. KEDA detects messages on the worker's subscription and scales the Container App from 0 → 1
3. Worker starts, authenticates to MgGraph and Exchange Online, begins processing
4. Worker processes all available messages, polling for new ones
5. After `IDLE_TIMEOUT_SECONDS` with no messages and no active jobs, the worker shuts down
6. ACA scales the Container App from 1 → 0

To disable idle timeout and keep the worker running indefinitely, set `IDLE_TIMEOUT_SECONDS=0`.

## Step 6: Test with a Sample Job

```powershell
./tests/Submit-TestJob.ps1 `
  -CsvPath ./tests/sample-jobs.csv `
  -ServiceBusNamespace 'matoolkit-sb.servicebus.windows.net' `
  -WorkerId 'worker-01' `
  -FunctionName 'New-EntraUser'
```

## Adding More Workers

To add additional workers for different environments (e.g., source vs target):

1. Deploy another subscription on the `worker-jobs` topic with a new worker ID filter
2. Deploy another Container App with a different `WORKER_ID` environment variable

You can do this by running the Bicep template again with a different `workerId` parameter, or by manually creating the resources.

## Deploying Custom Functions

### Volume Mount (recommended for development)

Mount custom modules from Azure Files:

```bash
# Create a storage account and file share
az storage share create --name custom-modules --account-name matoolkitsa

# Upload your module
az storage file upload-batch \
  --destination custom-modules \
  --source ./modules/CustomFunctions \
  --account-name matoolkitsa

# Mount the share in the container app
az containerapp update \
  --name matoolkit-worker-worker-01 \
  --resource-group matoolkit-rg \
  --set-env-vars CUSTOM_MODULES_PATH=/mnt/custom-modules
```

### Baked into Image (recommended for production)

Add custom modules to the `modules/CustomFunctions/` directory and rebuild the container image.

## Environment Variables Reference

| Variable | Required | Default | Description |
|---|---|---|---|
| `WORKER_ID` | Yes | - | Unique identifier for this worker instance |
| `MAX_PARALLELISM` | No | `2` | Max concurrent runspaces (1-20) |
| `SERVICE_BUS_NAMESPACE` | Yes | - | Service Bus FQDN |
| `JOBS_TOPIC_NAME` | No | `worker-jobs` | Jobs topic name |
| `RESULTS_TOPIC_NAME` | No | `worker-results` | Results topic name |
| `KEY_VAULT_NAME` | Yes | - | Key Vault name |
| `TARGET_TENANT_ID` | Yes | - | Target M365 tenant ID |
| `APP_ID` | Yes | - | App registration client ID |
| `CERT_NAME` | No | `worker-app-cert` | Key Vault certificate name |
| `APPINSIGHTS_CONNECTION_STRING` | No | - | App Insights connection string |
| `MAX_RETRY_COUNT` | No | `5` | Max throttle retries |
| `BASE_RETRY_DELAY_SECONDS` | No | `2` | Initial backoff delay |
| `MAX_RETRY_DELAY_SECONDS` | No | `120` | Max backoff delay |
| `IDLE_TIMEOUT_SECONDS` | No | `300` | Seconds with no activity before the worker shuts down (0 to disable) |
