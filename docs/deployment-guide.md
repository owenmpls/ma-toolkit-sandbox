# Deployment Guide

Step-by-step instructions to deploy the MA Toolkit sandbox to Azure using GitHub Actions CI/CD.

## Prerequisites

- Azure CLI (`az`) installed and logged in
- GitHub CLI (`gh`) installed and authenticated
- An existing resource group (`rg-ma-toolkit-sandbox`) in the target subscription
- Permissions to create app registrations in Entra ID and assign roles on the resource group

## Step 1: Create the GitHub Actions App Registration + Service Principal

This app registration is used by GitHub Actions to authenticate to Azure via OIDC (workload identity federation). No client secret is needed.

```bash
# Create the app registration
az ad app create --display-name "ma-toolkit-github-actions"
```

Note the `appId` from the output — this is `AZURE_CLIENT_ID`.

```bash
# Create a service principal for the app
az ad sp create --id <AZURE_CLIENT_ID>
```

## Step 2: Assign Azure Roles to the Service Principal

Both roles are scoped to the resource group only — the service principal cannot modify anything else in the subscription.

```bash
SUBSCRIPTION_ID="4293db49-78c9-4e47-bcd0-b44b4f7610a4"
RESOURCE_GROUP="rg-ma-toolkit-sandbox"
SP_APP_ID="<AZURE_CLIENT_ID from step 1>"
RG_SCOPE="/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP"

# Contributor (resource group scope only)
az role assignment create \
  --assignee "$SP_APP_ID" \
  --role Contributor \
  --scope "$RG_SCOPE"

# Role Based Access Control Administrator (resource group scope, constrained to specific roles)
az role assignment create \
  --assignee "$SP_APP_ID" \
  --role "Role Based Access Control Administrator" \
  --scope "$RG_SCOPE" \
  --condition-version 2.0 \
  --condition "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {acdd72a7-3385-48ef-bd42-f606fba81ae7, 4633458b-17de-408a-b874-0445c86b69e6, ba92f5b4-2d11-453d-a403-e96b0029c9fe, b7e6dc6d-f1e8-4753-8033-0f276bb0955b, 0a9a7e1f-b9d0-4cc4-a60d-0319b160aafa, 69a216fc-b8fb-44d8-bc22-1f3c2cd27a39, 4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0, 7f951dda-4ed3-4680-a7ca-43fe172d538d}))"
```

The condition restricts the SP to only assign these specific roles (within the resource group):
- `acdd72a7` — Reader
- `4633458b` — Key Vault Secrets User
- `ba92f5b4` — Storage Blob Data Contributor
- `b7e6dc6d` — Storage Blob Data Owner
- `0a9a7e1f` — Storage Table Data Contributor
- `69a216fc` — Service Bus Data Sender
- `4f6d3b9b` — Service Bus Data Receiver
- `7f951dda` — AcrPull

## Step 3: Create the Federated Credential for OIDC

This allows GitHub Actions running in the `dev` environment to authenticate as the service principal without a client secret.

```bash
# Get the app's object ID (different from appId)
APP_OBJECT_ID=$(az ad app show --id <AZURE_CLIENT_ID> --query id -o tsv)

az ad app federated-credential create \
  --id "$APP_OBJECT_ID" \
  --parameters '{
    "name": "github-actions-dev",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:owenmpls/ma-toolkit-sandbox:environment:dev",
    "audiences": ["api://AzureADTokenExchange"]
  }'
```

The `subject` must exactly match `repo:<owner>/<repo>:environment:<environment-name>`.

## Step 4: Create the Admin API App Registration

This is the Entra ID app registration that protects the Admin API with JWT bearer authentication. It is separate from the GitHub Actions app registration.

```bash
az ad app create \
  --display-name "ma-toolkit-admin-api" \
  --sign-in-audience AzureADMyOrg \
  --identifier-uris "api://<new-app-client-id>" \
  --app-roles '[
    {
      "allowedMemberTypes": ["User"],
      "displayName": "Admin",
      "description": "Full admin access to the migration API",
      "isEnabled": true,
      "value": "Admin",
      "id": "'$(uuidgen)'"
    }
  ]'
```

Note the `appId` — this is `ENTRA_ID_CLIENT_ID`. Update the identifier URI after creation:

```bash
ADMIN_API_CLIENT_ID="<appId from above>"

az ad app update \
  --id "$ADMIN_API_CLIENT_ID" \
  --identifier-uris "api://$ADMIN_API_CLIENT_ID"
```

The values you'll need for GitHub secrets:
- `ENTRA_ID_TENANT_ID` — your Azure AD tenant ID (`az account show --query tenantId -o tsv`)
- `ENTRA_ID_CLIENT_ID` — the `appId` from above
- `ENTRA_ID_AUDIENCE` — `api://<ADMIN_API_CLIENT_ID>`

## Step 5: Get the SQL Entra Admin Object ID

This is the object ID of the Entra ID group or user that will be set as the SQL Server Entra administrator. This user/group can then create contained database users for the managed identities.

```bash
# If using a group:
az ad group show --group "Your SQL Admin Group Name" --query id -o tsv

# If using your own user:
az ad signed-in-user show --query id -o tsv
```

Note this value — it becomes the `SQL_ENTRA_ADMIN_OBJECT_ID` secret.

## Step 6: Generate a Throwaway SQL Admin Password

The ARM `Microsoft.Sql/servers` resource type requires `administratorLogin` + `administratorLoginPassword` at creation time, even though we immediately enable Entra-only authentication (which makes the password permanently inert). Generate any strong password:

```bash
openssl rand -base64 32
```

Note this value — it becomes the `SQL_ADMIN_PASSWORD` secret.

## Step 7: Create the Cloud Worker App Registrations

Each cloud-worker instance connects to a different target tenant. You need an app registration **in each target tenant** with certificates for Graph + Exchange Online.

For each target tenant (madev1, madev2):

1. Switch to the target tenant (or have an admin in that tenant do this)
2. Create an app registration with the required API permissions (Microsoft Graph + Exchange Online)
3. Upload a certificate (the worker uses certificate-based auth via Key Vault)
4. Note the **tenant ID** and **app (client) ID**

These become:
- `MADEV1_TENANT_ID` / `MADEV1_APP_ID`
- `MADEV2_TENANT_ID` / `MADEV2_APP_ID`

## Step 8: Create the GitHub Environment and Configure Secrets/Variables

### 8a. Create the `dev` environment

Go to: **GitHub repo** > **Settings** > **Environments** > **New environment**

Name: `dev`

No protection rules are required for initial setup. You can add required reviewers later.

### 8b. Add environment secrets

Navigate to: **Settings** > **Environments** > **dev** > **Environment secrets** > **Add secret**

| Secret | Value | Source |
|---|---|---|
| `AZURE_CLIENT_ID` | App ID of `ma-toolkit-github-actions` | Step 1 |
| `AZURE_TENANT_ID` | Your Azure AD tenant ID | `az account show --query tenantId -o tsv` |
| `SQL_ADMIN_PASSWORD` | Throwaway password | Step 6 |
| `SQL_ENTRA_ADMIN_OBJECT_ID` | Object ID of SQL admin group/user | Step 5 |
| `ENTRA_ID_TENANT_ID` | Your Azure AD tenant ID | Same as `AZURE_TENANT_ID` |
| `ENTRA_ID_CLIENT_ID` | App ID of `ma-toolkit-admin-api` | Step 4 |
| `ENTRA_ID_AUDIENCE` | `api://<ENTRA_ID_CLIENT_ID>` | Step 4 |
| `MADEV1_TENANT_ID` | Target tenant ID for madev1 | Step 7 |
| `MADEV1_APP_ID` | App registration client ID in madev1 tenant | Step 7 |
| `MADEV2_TENANT_ID` | Target tenant ID for madev2 | Step 7 |
| `MADEV2_APP_ID` | App registration client ID in madev2 tenant | Step 7 |

### 8c. Add environment variables

Navigate to: **Settings** > **Environments** > **dev** > **Environment variables** > **Add variable**

| Variable | Value |
|---|---|
| `AZURE_SUBSCRIPTION_ID` | `4293db49-78c9-4e47-bcd0-b44b4f7610a4` |
| `RESOURCE_GROUP_NAME` | `rg-ma-toolkit-sandbox` |
| `AZURE_LOCATION` | `eastus2` |

## Step 9: Deploy Shared Infrastructure

Go to: **GitHub repo** > **Actions** > **Deploy Infrastructure** > **Run workflow**

Select stage: **`shared`**

This creates:
- Log Analytics workspace
- Service Bus namespace + topics
- Key Vault (with firewall)
- Container Registry
- VNet with 5 subnets
- 6 private DNS zones + VNet links
- Key Vault + Service Bus private endpoints

Wait for the workflow to complete successfully before proceeding.

## Step 10: Push a Seed Container Image

The cloud-worker ACA deployment references a container image that must exist in ACR. Now that shared infrastructure is deployed (ACR exists), push a seed image:

```bash
az acr build \
  --registry matoolkitacr \
  --image cloud-worker:latest \
  --file src/automation/cloud-worker/Dockerfile \
  src/automation/cloud-worker/
```

This is a one-time bootstrap step — after the first `deploy-apps` run, the real image (tagged with git SHA) will replace it.

## Step 11: Deploy Components

Go to: **Actions** > **Deploy Infrastructure** > **Run workflow**

Select stage: **`all`**

> The `shared` stage is idempotent — it will re-run harmlessly. All component stages will execute.

Alternatively, deploy components individually in order:
1. `scheduler-orchestrator` (first — admin-api depends on its SQL outputs)
2. `admin-api` (needs scheduler-orchestrator to exist)
3. `cloud-worker` (independent — can run anytime after shared)

This creates:
- SQL Server (Entra-only auth) + database
- 3 Function Apps (scheduler, orchestrator, admin-api) with VNet integration
- 2 ACA container apps (cloud-worker-madev1, cloud-worker-madev2)
- All storage accounts + private endpoints
- RBAC role assignments for managed identities

## Step 12: Create SQL Contained Database Users

After infrastructure is deployed, the Function App managed identities need database access. The SQL server has `publicNetworkAccess: Disabled` by default, so you'll need to temporarily open access:

```bash
# Temporarily enable public access and add your IP
az sql server update --name sql-scheduler-dev --resource-group rg-ma-toolkit-sandbox \
  --set publicNetworkAccess=Enabled
az sql server firewall-rule create --server sql-scheduler-dev \
  --resource-group rg-ma-toolkit-sandbox --name temp-admin \
  --start-ip-address <your-ip> --end-ip-address <your-ip>
```

Function App names include a `uniqueString` suffix for global uniqueness. Look up the actual names first — the managed identity name matches the Function App name:

```bash
az functionapp list --resource-group rg-ma-toolkit-sandbox --query "[].name" -o tsv
```

Then connect and create the contained database users:

```bash
sqlcmd -S sql-scheduler-dev.database.windows.net -d sqldb-scheduler-dev \
  --authentication-method ActiveDirectoryInteractive
```

```sql
-- Replace <suffix> with the uniqueString suffix from the Function App names above

CREATE USER [func-scheduler-dev-<suffix>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [func-scheduler-dev-<suffix>];
ALTER ROLE db_datawriter ADD MEMBER [func-scheduler-dev-<suffix>];

CREATE USER [func-orchestrator-dev-<suffix>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [func-orchestrator-dev-<suffix>];
ALTER ROLE db_datawriter ADD MEMBER [func-orchestrator-dev-<suffix>];

CREATE USER [matoolkit-admin-api-func-<suffix>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [matoolkit-admin-api-func-<suffix>];
ALTER ROLE db_datawriter ADD MEMBER [matoolkit-admin-api-func-<suffix>];
GO
```

After creating the users, lock down the SQL server again:

```bash
az sql server firewall-rule delete --server sql-scheduler-dev \
  --resource-group rg-ma-toolkit-sandbox --name temp-admin
az sql server update --name sql-scheduler-dev --resource-group rg-ma-toolkit-sandbox \
  --set publicNetworkAccess=Disabled
```

## Step 13: Deploy Application Code

Go to: **Actions** > **Deploy Applications** > **Run workflow**

Select component: **`all`**

This:
- Builds and tests all .NET projects
- Publishes and zip-deploys the 3 Function Apps
- Builds the cloud-worker container image in ACR (tagged with git SHA)
- Updates both ACA container apps to use the new image

After this, code deployments happen **automatically** on every push to `main` that changes files under `src/automation/`. Only changed components are redeployed.

## Step 14: Upload Cloud Worker Certificates to Key Vault

The cloud worker needs PFX certificates in Key Vault for authenticating to Graph and Exchange Online in the target tenants.

```bash
# Upload certificate for each target tenant
az keyvault certificate import \
  --vault-name matoolkit-kv \
  --name worker-app-cert \
  --file /path/to/certificate.pfx \
  --password "<pfx-password-if-any>"
```

The certificate name `worker-app-cert` must match the `CERT_NAME` environment variable configured in the cloud-worker Bicep template.

---

## Verification Checklist

After completing all steps, verify:

- [ ] **GitHub Actions OIDC** — Run `deploy-infra.yml` with `shared` stage; the "Azure Login" step should succeed without any client secret
- [ ] **Shared resources** — Check resource group in Azure Portal: Log Analytics, Service Bus, Key Vault, ACR, VNet all exist
- [ ] **Component resources** — SQL Server, Function Apps, ACA environments + apps all exist
- [ ] **Private endpoints** — In Azure Portal, navigate to the VNet > snet-private-endpoints; verify private endpoints for SQL, KV, SB, and all storage accounts
- [ ] **Function Apps running** — Check each Function App's "Functions" blade; functions should be listed
- [ ] **Cloud worker scaling** — Send a test message to the `worker-jobs` Service Bus topic; the ACA container app should scale from 0 to 1
- [ ] **SQL connectivity** — Check Function App logs for SQL connection errors; managed identity auth should work after step 12
- [ ] **Auto-deploy** — Push a change to `src/automation/` on `main`; the deploy-apps workflow should trigger and deploy only the changed component

## Troubleshooting

### "Infrastructure has not been deployed" error in deploy-apps

The app deployment workflow checks for a `shared-infra` deployment in the resource group. Run `deploy-infra.yml` with stage `all` or `shared` first.

### OIDC login fails with "AADSTS700016"

The federated credential subject claim doesn't match. Verify:
- The GitHub environment is named exactly `dev`
- The federated credential subject is `repo:owenmpls/ma-toolkit-sandbox:environment:dev`
- The workflow job has `environment: dev`

### Role assignment fails with "does not have authorization"

The service principal needs the "Role Based Access Control Administrator" role on the resource group. Re-run step 2. If the condition is too restrictive, check which role GUID the Bicep template is trying to assign and add it to the condition.

### SQL connection fails with "Login failed"

- Verify step 12 was completed (contained database users created)
- Verify the Function App managed identity name matches the `CREATE USER` statement
- Check that Entra-only auth is enabled on the SQL server (the `sqlEntraOnlyAuth` resource in the template handles this)

### Cloud worker stays at 0 replicas

- Verify the KEDA scaler connection string secret exists in Key Vault (`keda-sb-connection-string`)
- Verify the ACA managed identity has Key Vault Secrets User role
- Check ACA system logs: `az containerapp logs show --name matoolkit-worker-worker-madev1 --resource-group rg-ma-toolkit-sandbox --type system`
