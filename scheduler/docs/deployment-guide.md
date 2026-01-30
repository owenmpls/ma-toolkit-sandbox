# Scheduler Deployment Guide

## Prerequisites

- **Azure subscription** with permissions to create resources (Function App, SQL, Service Bus topics, role assignments)
- **.NET 8 SDK** -- [Download](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Azure CLI** (`az`) -- v2.60+ -- [Install](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli)
- **Azure Functions Core Tools** -- v4.x -- [Install](https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-tools)
- **An existing Azure Service Bus namespace** (the Bicep template creates the topic and subscription but expects the namespace to exist)
- **An existing Azure Key Vault** (referenced for RBAC assignment)
- **An existing Log Analytics workspace** (for Application Insights)

## Infrastructure Deployment

The Bicep template at `scheduler/infrastructure/deploy.bicep` provisions:

- Azure Functions App (Flex Consumption plan, .NET 8 isolated, Linux, system-assigned managed identity)
- Storage Account (for Functions runtime)
- Application Insights (connected to Log Analytics)
- Azure SQL Server + Database (Standard S0, 2 GB, auto-pause at 60 min)
- Service Bus topic (`orchestrator-events`) and subscription (`orchestrator`)
- RBAC role assignments:
  - **Azure Service Bus Data Sender** on the Service Bus namespace
  - **Key Vault Secrets User** on the Key Vault

### Deploy Command

```bash
az deployment group create \
  --resource-group <your-resource-group> \
  --template-file scheduler/infrastructure/deploy.bicep \
  --parameters scheduler/infrastructure/deploy.parameters.json \
  --parameters \
    environmentName='dev' \
    serviceBusNamespaceName='matoolkit-sb' \
    sqlAdminLogin='schedulerAdmin' \
    sqlAdminPassword='<SECURE_PASSWORD>' \
    keyVaultName='matoolkit-kv' \
    logAnalyticsWorkspaceId='/subscriptions/<SUB_ID>/resourceGroups/<RG>/providers/Microsoft.OperationalInsights/workspaces/<WS_NAME>'
```

Update the parameter values or edit `deploy.parameters.json` before deploying. The `sqlAdminPassword` should be provided as a secure parameter -- do not commit it to source control.

### Deploy the SQL Schema

After the SQL Server and database are provisioned, run the schema script against the database. The schema is located at `scheduler/database/schema.sql` (if present) or must be created manually. The required tables are:

- `runbooks`
- `batches`
- `batch_members`
- `phase_executions`
- `step_executions`
- `init_executions`
- Per-runbook dynamic data tables (created automatically by the scheduler at runtime)

### Deploy the Function App Code

```bash
cd scheduler/src/Scheduler.Functions
dotnet publish -c Release -o ./publish
cd publish
func azure functionapp publish func-scheduler-<environmentName>
```

Or use the Azure CLI:

```bash
cd scheduler/src/Scheduler.Functions
dotnet publish -c Release -o ./publish
az functionapp deployment source config-zip \
  --resource-group <your-resource-group> \
  --name func-scheduler-<environmentName> \
  --src ./publish.zip
```

## App Registration for Data Source Access

If your runbooks query Dataverse or Databricks, the Function App's managed identity (or a dedicated app registration) needs access:

### Dataverse (TDS Endpoint)

1. The Dataverse TDS endpoint must be enabled for the environment.
2. The connection string uses `Authentication=Active Directory Default`, which means `DefaultAzureCredential` handles token acquisition.
3. Grant the Function App's managed identity (or the app registration) the **System Administrator** or appropriate Dataverse security role so it can query the TDS endpoint.
4. Set the environment variable `DATAVERSE_CONNECTION_STRING` to `Server=<org>.crm.dynamics.com,5558;Authentication=Active Directory Default`.

### Databricks (SQL Statements API)

1. The Function App uses `DefaultAzureCredential` to obtain an AAD token with scope `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default` (the Databricks resource ID).
2. Grant the Function App's managed identity (or service principal) access to the Databricks workspace. Add it as a user in the workspace and grant `CAN USE` on the SQL warehouse.
3. Set the environment variables:
   - `DATABRICKS_CONNECTION_STRING` = `https://<workspace>.azuredatabricks.net`
   - `DATABRICKS_WAREHOUSE_ID` = the warehouse ID from Databricks

## Connection Configuration

All configuration is read through the standard .NET `IConfiguration` system. In Azure, set these as Function App application settings. Locally, use `local.settings.json`.

| Setting | Purpose | Example |
|---|---|---|
| `Scheduler__SqlConnectionString` | SQL Server connection string | `Server=tcp:sql-scheduler-dev.database.windows.net,1433;Initial Catalog=sqldb-scheduler-dev;...` |
| `Scheduler__ServiceBusNamespace` | Fully qualified Service Bus namespace | `matoolkit-sb.servicebus.windows.net` |
| `Scheduler__OrchestratorTopicName` | Service Bus topic name (default: `orchestrator-events`) | `orchestrator-events` |
| `DATAVERSE_CONNECTION_STRING` | Dataverse TDS endpoint | `Server=yourorg.crm.dynamics.com,5558;Authentication=Active Directory Default` |
| `DATABRICKS_CONNECTION_STRING` | Databricks workspace URL | `https://your-workspace.azuredatabricks.net` |
| `DATABRICKS_WAREHOUSE_ID` | Databricks SQL warehouse ID | `abc123def456` |

The `Scheduler__` prefix maps to the `SchedulerSettings` options class via the `Scheduler` configuration section.

## Local Development

### 1. Install Prerequisites

```bash
dotnet --version   # Ensure 8.x
func --version     # Ensure 4.x
```

### 2. Configure local.settings.json

The file is at `scheduler/src/Scheduler.Functions/local.settings.json`. Update connection strings for your local or dev SQL instance and Service Bus namespace:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "Scheduler__SqlConnectionString": "Server=localhost;Database=matoolkit;Trusted_Connection=true;TrustServerCertificate=true;",
    "Scheduler__ServiceBusNamespace": "matoolkit-sb.servicebus.windows.net",
    "Scheduler__OrchestratorTopicName": "orchestrator-events",
    "DATAVERSE_CONNECTION_STRING": "Server=yourorg.crm.dynamics.com,5558;Authentication=Active Directory Default",
    "DATABRICKS_CONNECTION_STRING": "https://your-workspace.azuredatabricks.net",
    "DATABRICKS_WAREHOUSE_ID": "your-warehouse-id"
  }
}
```

For local storage emulation, install and start [Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite).

### 3. Run the Schema

Create the required SQL tables in your local database. If you are using SQL Server LocalDB or a Docker SQL instance, apply the schema script before starting the function.

### 4. Start the Function

```bash
cd scheduler/src/Scheduler.Functions
func start
```

The timer function will trigger every 5 minutes. To trigger it manually for testing, use the Azure Functions Core Tools admin endpoint:

```bash
curl -X POST http://localhost:7071/admin/functions/SchedulerTimer \
  -H "Content-Type: application/json" \
  -d '{}'
```

To publish a runbook locally:

```bash
curl -X POST http://localhost:7071/api/PublishRunbook \
  -H "Content-Type: application/json" \
  -d '{
    "name": "my-runbook",
    "yaml_content": "<YAML string>",
    "overdue_behavior": "rerun",
    "rerun_init": false
  }'
```

### 5. Debugging

Open the `scheduler/` folder (or the solution file `Scheduler.sln`) in Visual Studio or VS Code. The `.csproj` targets `net8.0` with the Azure Functions isolated worker model. Use the standard F5 debug experience.

## Post-Deployment Verification

After deploying, verify the following:

1. **Function App is running**: Check the Azure portal under Function App > Functions. You should see `SchedulerTimer` and `PublishRunbook`.

2. **Timer is firing**: Check Application Insights logs for `Scheduler timer triggered at` entries every 5 minutes.

3. **SQL connectivity**: If the timer logs `Error processing runbook`, check the SQL connection string and firewall rules. The Bicep template opens the firewall to Azure services (`0.0.0.0`).

4. **Service Bus connectivity**: Publish a test runbook and verify messages appear on the `orchestrator-events` topic. Check the Service Bus namespace in the portal under Topics > orchestrator-events > Subscriptions > orchestrator for message counts.

5. **RBAC assignments**: The Function App's managed identity needs:
   - **Azure Service Bus Data Sender** on the Service Bus namespace (assigned by Bicep)
   - **Key Vault Secrets User** on the Key Vault (assigned by Bicep)
   - SQL login (configured via connection string in app settings)
   - Dataverse/Databricks access (manual setup, see above)

6. **Publish a test runbook**:

```bash
curl -X POST https://func-scheduler-dev.azurewebsites.net/api/PublishRunbook?code=<FUNCTION_KEY> \
  -H "Content-Type: application/json" \
  -d '{
    "name": "test-runbook",
    "yaml_content": "name: test-runbook\ndescription: test\ndata_source:\n  type: dataverse\n  connection: DATAVERSE_CONNECTION_STRING\n  query: \"SELECT email, migration_date FROM contacts WHERE active = 1\"\n  primary_key: email\n  batch_time_column: migration_date\nphases:\n  - name: pre-migration\n    offset: T-1d\n    steps:\n      - name: notify-user\n        worker_id: worker-01\n        function: Send-Notification\n        params:\n          Email: \"{{email}}\"\n"
  }'
```

Verify the response includes a `runbookId` and `version`.
