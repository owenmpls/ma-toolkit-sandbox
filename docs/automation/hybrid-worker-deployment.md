# Hybrid Worker Deployment Guide

Step-by-step instructions to deploy the Azure infrastructure for a hybrid worker instance and install the worker as a Windows Service on an on-premises server.

## Prerequisites

- Azure CLI (`az`) installed and logged in
- An existing MA Toolkit shared infrastructure deployment (Service Bus namespace, Key Vault) -- see [Deployment Guide](../deployment-guide.md)
- Access to an on-premises Windows Server with:
  - PowerShell 7.4+ (`pwsh.exe`)
  - Windows PowerShell 5.1 (`powershell.exe`)
  - .NET 8 SDK (for building the service host)
  - WinRM enabled (for the PSSession pool)
  - Network access to Azure (Service Bus, Key Vault, Storage) and to on-prem services (AD domain controller, Exchange Server)
- Permissions to create app registrations and assign RBAC roles in the Azure subscription

## Overview

Deploying a hybrid worker instance involves three stages:

1. **Azure setup** -- Create a service principal, generate a certificate, and deploy the Bicep template (Service Bus subscription, RBAC, update storage)
2. **Key Vault secrets** -- Upload on-premises service account credentials and the target tenant certificate
3. **Windows installation** -- Install the worker on the target server, configure, and start the service

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

The Bicep template creates the Service Bus subscription (with SQL filter), RBAC role assignments, and optionally an update storage account. Deploy it once per hybrid worker instance.

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

Set `deployUpdateStorage` to `false` if you already have an update storage account from a previous hybrid worker deployment.

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
- **Update storage** (if `deployUpdateStorage = true`):
  - Storage account (`Standard_LRS`, no public blob access)
  - Blob container `hybrid-worker`
  - Storage Blob Data Reader role on the SP

## Step 4: Upload On-Premises Credentials to Key Vault

If the worker will connect to on-premises services (AD, Exchange Server), store the service account credentials in Key Vault as JSON secrets.

### Active Directory Credential

```bash
az keyvault secret set \
  --vault-name your-kv \
  --name "ad-service-account" \
  --value '{"username": "CORP\\svc-matoolkit", "password": "your-password"}'
```

### Exchange Server Credential

```bash
az keyvault secret set \
  --vault-name your-kv \
  --name "exchange-service-account" \
  --value '{"username": "CORP\\svc-matoolkit-exch", "password": "your-password"}'
```

The secret names must match the `credentialSecret` values in the worker configuration (Step 7).

## Step 5: Upload Target Tenant Certificate to Key Vault

If the worker will execute cloud functions (Entra ID, Exchange Online), upload the target tenant's PFX certificate to Key Vault. This is the same certificate used by the app registration in the target tenant.

```bash
# Combine key and cert if separate
cat target-key.pem target-cert.pem > target-combined.pem

# Import into Key Vault
az keyvault certificate import \
  --vault-name your-kv \
  --name "worker-app-cert" \
  --file target-combined.pem
```

The certificate name must match the `targetTenant.certificateName` in the worker configuration (default: `worker-app-cert`).

## Step 6: Install the Worker on Windows

All remaining steps run on the target Windows server. Clone or copy the repository to the server.

### 6a. Download .NET Dependencies

```powershell
cd src\automation\hybrid-worker
pwsh -File Download-Dependencies.ps1
```

This fetches the required NuGet packages (Azure.Messaging.ServiceBus, Azure.Identity, etc.) into `dotnet-libs/`.

### 6b. Prepare the Configuration File

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

### 6c. Run the Installer

Run the installer as Administrator. The installer builds the .NET service host, copies files to `C:\ProgramData\MaToolkit\HybridWorker\`, imports the certificate, registers the Windows Service, and configures service recovery.

**With a Group Managed Service Account (recommended for production):**

```powershell
.\Install-HybridWorker.ps1 `
    -ConfigPath .\config\worker-config.json `
    -CertificatePath .\hybrid-worker-01.pfx `
    -CertificatePassword (Read-Host -AsSecureString "PFX password") `
    -ServiceAccount 'CORP\svc-matoolkit-hw$'
```

**With a standard service account:**

```powershell
.\Install-HybridWorker.ps1 `
    -ConfigPath .\config\worker-config.json `
    -CertificatePath .\hybrid-worker-01.pfx `
    -CertificatePassword (Read-Host -AsSecureString "PFX password") `
    -ServiceAccount 'CORP\svc-matoolkit' `
    -ServiceAccountPassword (Read-Host -AsSecureString "Account password")
```

**With LocalSystem (development/testing only):**

```powershell
.\Install-HybridWorker.ps1 `
    -ConfigPath .\config\worker-config.json `
    -CertificatePath .\hybrid-worker-01.pfx `
    -CertificatePassword (Read-Host -AsSecureString "PFX password")
```

The installer performs these steps:

1. Verifies prerequisites (PS 7.4+, powershell.exe, dotnet, WinRM)
2. Creates `C:\ProgramData\MaToolkit\HybridWorker\{current,staging,previous,config,logs}`
3. Copies `src/`, `modules/`, `dotnet-libs/`, `version.txt` into `current\`
4. Builds and publishes the .NET service host (`dotnet publish -c Release -r win-x64 --self-contained`)
5. Copies configuration to `config\worker-config.json`
6. Imports the PFX into `Cert:\LocalMachine\My` and grants private key read access to the service account
7. Registers the Windows Service (`MaToolkitHybridWorker`, startup: Automatic)
8. Configures service recovery: restart at 10s, 30s, 60s; reset counter after 1 day
9. Locks down `config\` directory ACL (Administrators: FullControl, service account: Read)

## Step 7: Verify Configuration and Start

### 7a. Verify the Certificate

```powershell
# Confirm the certificate is in the store
Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq "YOUR_THUMBPRINT" }
```

### 7b. Start the Service

```powershell
Start-Service MaToolkitHybridWorker
```

### 7c. Check Startup Logs

```powershell
# Watch the worker log for boot sequence
Get-Content C:\ProgramData\MaToolkit\HybridWorker\logs\worker.log -Tail 50 -Wait
```

You should see all 12 boot phases completing successfully. If the worker connects to Service Bus and starts the dispatcher loop, the installation is complete.

### 7d. Check Health Endpoint

```powershell
Invoke-RestMethod http://localhost:8080/health
```

### 7e. Check Windows Event Log

```powershell
Get-WinEvent -ProviderName MaToolkitHybridWorker -MaxEvents 10
```

---

## Deploying Additional Instances

To deploy a second hybrid worker (e.g., `hybrid-worker-02`), repeat Steps 1-7 with:

- A new app registration and service principal
- A new certificate
- A new Bicep deployment with `workerId = hybrid-worker-02` and `deployUpdateStorage = false` (reuse the existing update storage account)
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
- [ ] **Service registered** -- `Get-Service MaToolkitHybridWorker` returns the service with `Automatic` startup type
- [ ] **Service running** -- `Get-Service MaToolkitHybridWorker` shows `Running` status
- [ ] **Boot sequence** -- Worker log shows all 12 phases completing without errors
- [ ] **Health endpoint** -- `Invoke-RestMethod http://localhost:8080/health` returns a healthy response
- [ ] **Service Bus connected** -- Health response shows `serviceBus.connected = true`
- [ ] **Test job** -- Send a test job to the `worker-jobs` topic with `WorkerId = 'hybrid-worker-01'` and verify a result appears on the `worker-results` topic

---

## Troubleshooting

### Installer fails at "Building .NET service host"

Verify the .NET 8 SDK is installed:

```powershell
dotnet --list-sdks
```

If missing, install from https://dot.net.

### Service starts but immediately stops

Check the Event Log for the exit reason:

```powershell
Get-WinEvent -ProviderName MaToolkitHybridWorker -MaxEvents 10
```

Common causes:
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
- Check that the service account can create local PSSessions: `Enter-PSSession -ComputerName localhost -ConfigurationName Microsoft.PowerShell`
- For AD functions: verify the domain controller is reachable and the credential in Key Vault is correct

### Self-update downloads but fails to apply

Check the worker log for update-related errors. The update mechanism:

1. Downloads `version.json` from blob storage
2. Downloads the zip and verifies SHA256
3. Extracts to `staging/`
4. Writes `update-pending.json`
5. On next boot: renames `current/ -> previous/`, `staging/ -> current/`

If the rename fails (e.g., file locks), the rollback restores `previous/ -> current/`. Check that no other process is locking files in the `current/` directory.

---

## Uninstalling

```powershell
# Stop and remove the service, prompt for file removal
.\Uninstall-HybridWorker.ps1

# Stop and remove everything including config and logs
.\Uninstall-HybridWorker.ps1 -RemoveFiles
```

After uninstalling, you may also want to:

- Remove the app registration: `az ad app delete --id <APP_ID>`
- Remove the Service Bus subscription (it will be cleaned up if you re-deploy, but stale subscriptions can accumulate dead-lettered messages)
- Remove on-prem credential secrets from Key Vault
