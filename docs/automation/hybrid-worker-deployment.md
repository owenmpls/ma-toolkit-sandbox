# Hybrid Worker Deployment Guide

Step-by-step instructions to deploy the Azure infrastructure for a hybrid worker instance and install the worker as a Scheduled Task on an on-premises server.

## Prerequisites

- Azure CLI (`az`) installed and logged in
- An existing MA Toolkit shared infrastructure deployment (Service Bus namespace, Key Vault) -- see [Deployment Guide](../deployment-guide.md)
- Access to an on-premises Windows Server with:
  - PowerShell 7.4+ (`pwsh.exe`)
  - Windows PowerShell 5.1 (`powershell.exe`)
  - WinRM enabled (for the PSSession pool)
  - Network access to Azure (Service Bus, Key Vault, Storage) and to on-prem services (AD domain controller, Exchange Server)
- Permissions to create app registrations and assign RBAC roles in the Azure subscription

## Overview

Deploying a hybrid worker instance involves three stages:

1. **Azure setup** -- Create a service principal, generate a certificate, and deploy the Bicep template (Service Bus subscription, RBAC, update storage)
2. **Key Vault secrets** -- Upload on-premises service account credentials
3. **Windows installation** -- Install the worker on the target server, configure, and register the scheduled task

Each hybrid worker instance gets its own service principal, certificate, Service Bus subscription, and configuration. Multiple instances can share the same update storage account.

---

## Step 1: Create the Service Principal

Each hybrid worker needs its own Entra ID app registration and service principal for authenticating to Azure resources (Service Bus, Key Vault, Storage).

```bash
# Create the app registration
az ad app create --display-name "ma-toolkit-hybrid-worker-01"
```

Note the `appId` from the output -- this is the service principal's client ID.

```bash
# Create the service principal
az ad sp create --id <APP_ID>
```

Note the service principal's **object ID** (different from `appId`) -- you need this for the Bicep deployment:

```bash
az ad sp show --id <APP_ID> --query id -o tsv
```

## Step 2: Generate and Import a Certificate

The hybrid worker authenticates to Azure using a certificate instead of a client secret. Generate a self-signed certificate or use one from your PKI.

### Option A: Self-Signed Certificate

```bash
# Generate a self-signed certificate (valid for 1 year)
openssl req -x509 -newkey rsa:2048 -keyout hw-key.pem -out hw-cert.pem -days 365 -nodes \
  -subj "/CN=ma-toolkit-hybrid-worker-01"

# Combine into PFX for the Windows certificate store
openssl pkcs12 -export -out hybrid-worker-01.pfx -inkey hw-key.pem -in hw-cert.pem

# Upload the public certificate to the app registration
az ad app credential reset \
  --id <APP_ID> \
  --cert @hw-cert.pem \
  --append
```

### Option B: Enterprise PKI Certificate

Request a certificate from your organization's CA. Export it as a PFX file with the private key. Upload just the public certificate (`.cer` or `.pem`) to the app registration:

```bash
az ad app credential reset \
  --id <APP_ID> \
  --cert @your-cert.pem \
  --append
```

Keep the PFX file -- you will import it on the Windows server in Step 6.

## Step 3: Deploy Azure Infrastructure

The Bicep template creates the Service Bus subscription (with SQL filter) and RBAC role assignments. Deploy it once per hybrid worker instance.

> **Prerequisite:** The shared hybrid-worker infrastructure (update storage account) must be deployed first via `deploy-infra.yml` or manually with `infra/automation/hybrid-worker-shared/deploy.bicep`.

### 3a. Edit the Parameters File

Copy and edit the parameters file:

```bash
cp infra/automation/hybrid-worker/deploy.parameters.json \
   infra/automation/hybrid-worker/deploy.parameters.hybrid-worker-01.json
```

Update the values:

| Parameter | Value |
|-----------|-------|
| `baseName` | Your base name prefix (e.g., `matoolkit`) |
| `workerId` | Unique worker ID (e.g., `hybrid-worker-01`) |
| `serviceBusNamespaceName` | Your Service Bus namespace name |
| `keyVaultName` | Your Key Vault name |
| `servicePrincipalObjectId` | Object ID of the SP from Step 1 |
| `updateStorageAccountName` | Name of the shared update storage account (from hybrid-worker-shared deployment) |

### 3b. Deploy

```bash
az deployment group create \
  --name hybrid-worker-01-infra \
  --resource-group your-rg \
  --template-file infra/automation/hybrid-worker/deploy.bicep \
  --parameters infra/automation/hybrid-worker/deploy.parameters.hybrid-worker-01.json
```

This creates:

- **Service Bus subscription** `worker-hybrid-worker-01` on the `worker-jobs` topic with SQL filter `WorkerId = 'hybrid-worker-01'`
- **RBAC roles** on the service principal:
  - Service Bus Data Receiver (namespace scope)
  - Service Bus Data Sender (namespace scope)
  - Key Vault Secrets User (Key Vault scope)
  - Storage Blob Data Reader (update storage account scope)

## Step 4: Upload Credentials to Key Vault

Store service account credentials in Key Vault as JSON secrets for each enabled service.

### Active Directory Credentials (One Per Forest)

Each forest in the `serviceConnections.activeDirectory.forests` array has its own `credentialSecret`:

```bash
# Forest: corp.contoso.com
az keyvault secret set \
  --vault-name your-kv \
  --name "ad-cred-contoso" \
  --value '{"username": "CORP\\svc-matoolkit", "password": "your-password"}'

# Forest: emea.contoso.com
az keyvault secret set \
  --vault-name your-kv \
  --name "ad-cred-emea" \
  --value '{"username": "EMEA\\svc-matoolkit", "password": "your-password"}'
```

### Exchange Server Credential

```bash
az keyvault secret set \
  --vault-name your-kv \
  --name "exchange-service-account" \
  --value '{"username": "CORP\\svc-matoolkit-exch", "password": "your-password"}'
```

### SharePoint Online / Teams Credentials (If Enabled)

```bash
az keyvault secret set \
  --vault-name your-kv \
  --name "spo-service-account" \
  --value '{"username": "svc-matoolkit-spo@contoso.com", "password": "your-password"}'

az keyvault secret set \
  --vault-name your-kv \
  --name "teams-service-account" \
  --value '{"username": "svc-matoolkit-teams@contoso.com", "password": "your-password"}'
```

The secret names must match the `credentialSecret` values in the worker configuration (Step 6).

## Step 5: Install the Worker on Windows

All remaining steps run on the target Windows server. Clone or copy the repository to the server.

### 5a. Download .NET Dependencies

```powershell
cd src\automation\hybrid-worker
pwsh -File Download-Dependencies.ps1
```

This fetches the required NuGet packages (Azure.Messaging.ServiceBus, Azure.Identity, etc.) into `dotnet-libs/`.

### 5b. Prepare the Configuration File

Copy and edit the example configuration:

```powershell
Copy-Item config\worker-config.example.json config\worker-config.json
notepad config\worker-config.json
```

Update the values to match your environment. See the [Configuration Reference](hybrid-worker.md#configuration-reference) for all fields. At minimum, set:

- `workerId` -- Must match the `workerId` used in the Bicep deployment
- `serviceBus.namespace` -- Your Service Bus FQDN
- `auth.tenantId`, `auth.appId`, `auth.certificateThumbprint` -- SP details from Steps 1-2
- `auth.keyVaultName` -- Your Key Vault name
- `serviceConnections.*` -- Enable the services this worker will use
- `idleTimeoutSeconds` -- Recommended: `300` (worker exits after 5 min idle, next tick picks up new work)

### 5c. Run the Installer

Run the installer as Administrator. The installer copies files to `C:\ProgramData\MaToolkit\HybridWorker\`, imports the certificate, registers the Scheduled Task, and sets directory ACLs.

**With a Group Managed Service Account (recommended for production):**

```powershell
.\Install-HybridWorkerTask.ps1 `
    -ConfigPath .\config\worker-config.json `
    -CertificatePath .\hybrid-worker-01.pfx `
    -CertificatePassword (Read-Host -AsSecureString "PFX password") `
    -ServiceAccount 'CORP\svc-matoolkit-hw$'
```

**With a standard service account:**

```powershell
.\Install-HybridWorkerTask.ps1 `
    -ConfigPath .\config\worker-config.json `
    -CertificatePath .\hybrid-worker-01.pfx `
    -CertificatePassword (Read-Host -AsSecureString "PFX password") `
    -ServiceAccount 'CORP\svc-matoolkit' `
    -ServiceAccountPassword (Read-Host -AsSecureString "Account password")
```

**With SYSTEM (development/testing only):**

```powershell
.\Install-HybridWorkerTask.ps1 `
    -ConfigPath .\config\worker-config.json `
    -CertificatePath .\hybrid-worker-01.pfx `
    -CertificatePassword (Read-Host -AsSecureString "PFX password")
```

**With a custom interval (default is 5 minutes):**

```powershell
.\Install-HybridWorkerTask.ps1 `
    -ConfigPath .\config\worker-config.json `
    -CertificatePath .\hybrid-worker-01.pfx `
    -CertificatePassword (Read-Host -AsSecureString "PFX password") `
    -IntervalMinutes 10
```

The installer performs these steps:

1. Verifies prerequisites (PS 7.4+, powershell.exe, pwsh.exe, WinRM)
2. Creates `C:\ProgramData\MaToolkit\HybridWorker\{current,staging,previous,config,logs}`
3. Copies `src/`, `modules/`, `dotnet-libs/`, `version.txt`, `Start-HybridWorker.ps1` into `current\`
4. Copies configuration to `config\worker-config.json`
5. Imports the PFX into `Cert:\LocalMachine\My` and grants private key read access to the task account
6. Registers the Scheduled Task (`MaToolkitHybridWorker`) with:
   - Trigger: every N minutes (configurable, default 5)
   - `MultipleInstances = IgnoreNew` (overlap prevention)
   - `ExecutionTimeLimit = 2 hours` (safety net)
   - `RestartCount = 3` (retry on failure)
   - `StartWhenAvailable` (catch up missed triggers)
7. Locks down `config\` directory ACL (Administrators: FullControl, task account: Read)

## Step 6: Verify and Enable

### 6a. Verify the Certificate

```powershell
# Confirm the certificate is in the store
Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq "YOUR_THUMBPRINT" }
```

### 6b. Enable and Start the Task

```powershell
# Enable the scheduled task
Enable-ScheduledTask -TaskName MaToolkitHybridWorker

# Run a tick manually to verify
Start-ScheduledTask -TaskName MaToolkitHybridWorker
```

### 6c. Check Task History

```powershell
# View recent task runs
Get-ScheduledTask -TaskName MaToolkitHybridWorker | Get-ScheduledTaskInfo
```

### 6d. Verify in App Insights

Query for `LauncherTick` events in App Insights to confirm the worker is alive:

```kusto
customEvents
| where name == "LauncherTick"
| where customDimensions.WorkerId == "hybrid-worker-01"
| project timestamp, customDimensions.Version, customDimensions.MessagesFound, customDimensions.Action
| order by timestamp desc
| take 10
```

---

## Deploying Additional Instances

To deploy a second hybrid worker (e.g., `hybrid-worker-02`), repeat Steps 1-6 with:

- A new app registration and service principal
- A new certificate
- A new Bicep deployment with `workerId = hybrid-worker-02` (the shared update storage account is already deployed)
- A new configuration file with the updated `workerId` and SP details

---

## Key Vault Firewall Configuration

The hybrid worker connects to Key Vault from an on-premises network. If your Key Vault has a firewall configured (recommended), you need to allow access from the worker's public IP:

```bash
az keyvault network-rule add \
  --name your-kv \
  --ip-address <WORKER_PUBLIC_IP>/32
```

Alternatively, configure a VPN or ExpressRoute connection and use a private endpoint.

---

## Verification Checklist

After completing all steps, verify:

- [ ] **Service Bus subscription** -- In Azure Portal, navigate to Service Bus > Topics > worker-jobs > Subscriptions; verify `worker-hybrid-worker-01` exists with the SQL filter
- [ ] **RBAC assignments** -- Verify the SP has Service Bus Data Receiver, Service Bus Data Sender, and Key Vault Secrets User roles
- [ ] **Certificate** -- `Get-ChildItem Cert:\LocalMachine\My` shows the imported certificate with the correct thumbprint
- [ ] **Task registered** -- `Get-ScheduledTask -TaskName MaToolkitHybridWorker` returns the task
- [ ] **Task enabled** -- Task state shows `Ready`
- [ ] **Manual tick** -- `Start-ScheduledTask -TaskName MaToolkitHybridWorker` completes without error
- [ ] **App Insights** -- `LauncherTick` event appears with correct WorkerId, Version, and `Action = NoWork`
- [ ] **Test job** -- Send a test job to the `worker-jobs` topic with `WorkerId = 'hybrid-worker-01'` and verify a result appears on the `worker-results` topic (LauncherTick shows `MessagesFound = 1, Action = StartingWorker`)

---

## Troubleshooting

### Task fires but fails immediately

Check Task Scheduler history and exit code:

```powershell
Get-ScheduledTask -TaskName MaToolkitHybridWorker | Get-ScheduledTaskInfo
```

Common causes:
- PowerShell 7.4+ not installed or `pwsh.exe` not in PATH
- Configuration file not found at `C:\ProgramData\MaToolkit\HybridWorker\config\worker-config.json`
- Invalid JSON in the configuration file
- Certificate thumbprint does not match any certificate in `Cert:\LocalMachine\My`
- `dotnet-libs/` directory is empty (run `Download-Dependencies.ps1` first)

### Service Bus authentication fails with 401

- Verify the SP's object ID in the Bicep parameters matches the actual SP object ID (`az ad sp show --id <APP_ID> --query id`)
- Verify the certificate uploaded to the app registration matches the one in `Cert:\LocalMachine\My`
- Check that the Service Bus namespace has `disableLocalAuth: true` -- the worker uses RBAC, not SAS keys

### Key Vault returns 403 Forbidden

- Verify the SP has `Key Vault Secrets User` role: `az role assignment list --assignee <SP_OBJECT_ID> --scope /subscriptions/.../resourceGroups/.../providers/Microsoft.KeyVault/vaults/your-kv`
- If Key Vault firewall is enabled, add the worker's public IP or configure a private network path

### PSSession pool fails with "Access denied"

- Verify WinRM is running on the worker server: `Get-Service WinRM`
- Verify PSRemoting is enabled: `Test-WSMan localhost`
- Check that the task account can create local PSSessions: `Enter-PSSession -ComputerName localhost -ConfigurationName Microsoft.PowerShell`
- For AD functions: verify the domain controller is reachable and the credential in Key Vault is correct

### Self-update downloads but fails to apply

Check the worker log for update-related errors. The update mechanism:

1. Launcher checks `version.json` from blob storage each tick
2. Downloads the zip and verifies SHA256
3. Extracts to `staging/`
4. Writes `update-pending.json`
5. On next tick: renames `current/ -> previous/`, `staging/ -> current/`

If the rename fails (e.g., file locks from a running worker), the rollback restores `previous/ -> current/`. Verify the worker isn't still running from a previous tick (Task Scheduler's IgnoreNew should prevent this).

---

## Uninstalling

```powershell
# Stop and remove the task, prompt for file removal
.\Uninstall-HybridWorkerTask.ps1

# Stop and remove everything including config and logs
.\Uninstall-HybridWorkerTask.ps1 -RemoveFiles
```

After uninstalling, you may also want to:

- Remove the app registration: `az ad app delete --id <APP_ID>`
- Remove the Service Bus subscription (it will be cleaned up if you re-deploy, but stale subscriptions can accumulate dead-lettered messages)
- Remove on-prem credential secrets from Key Vault
