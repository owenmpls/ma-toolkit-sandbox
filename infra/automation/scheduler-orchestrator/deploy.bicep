// ---------------------------------------------------------------------------
// Scheduler + Orchestrator — Azure Functions (Flex Consumption) + SQL + RBAC
// ---------------------------------------------------------------------------
// Deploys both the scheduler and orchestrator function apps, a shared SQL
// database, Service Bus subscriptions, and RBAC assignments. Requires the
// shared infrastructure (Service Bus, Key Vault) to be deployed first.
// ---------------------------------------------------------------------------

@description('Environment name used as a suffix for resource names (e.g. dev, staging, prod).')
param environmentName string

@description('Azure region for all new resources.')
param location string = resourceGroup().location

@description('Name of the existing Service Bus namespace (from shared deployment).')
param serviceBusNamespaceName string

@description('Name of the existing Key Vault (from shared deployment).')
param keyVaultName string

@description('Resource ID of the existing Log Analytics workspace (from shared deployment).')
param logAnalyticsWorkspaceId string

@description('Name of the existing Log Analytics workspace (from shared deployment).')
param logAnalyticsWorkspaceName string

@description('Object ID of the Entra ID group or user to set as SQL Server Entra administrator.')
param sqlEntraAdminObjectId string

@description('Display name for the SQL Server Entra administrator.')
param sqlEntraAdminName string = 'EntraAdmin'

@description('Subnet resource ID for scheduler VNet integration. Leave empty to skip.')
param schedulerSubnetId string = ''

@description('Subnet resource ID for orchestrator VNet integration. Leave empty to skip.')
param orchestratorSubnetId string = ''

@description('Subnet resource ID for private endpoints. Leave empty to skip private endpoint creation.')
param privateEndpointsSubnetId string = ''

@description('Resource ID of the SQL private DNS zone (from shared deployment). Required when privateEndpointsSubnetId is set.')
param sqlDnsZoneId string = ''

@description('Resource ID of the blob storage private DNS zone (from shared deployment). Required when privateEndpointsSubnetId is set.')
param stBlobDnsZoneId string = ''

@description('Resource ID of the queue storage private DNS zone (from shared deployment). Required when privateEndpointsSubnetId is set.')
param stQueueDnsZoneId string = ''

@description('Resource ID of the table storage private DNS zone (from shared deployment). Required when privateEndpointsSubnetId is set.')
param stTableDnsZoneId string = ''

@description('Tags to apply to all resources.')
param tags object = {
  project: 'ma-toolkit'
}

// ---------------------------------------------------------------------------
// Variables
// ---------------------------------------------------------------------------

var schedulerBaseName = 'scheduler-${environmentName}'
var orchestratorBaseName = 'orchestrator-${environmentName}'

var schedulerTags = union(tags, { component: 'scheduler' })
var orchestratorTags = union(tags, { component: 'orchestrator' })

// Built-in role definition IDs
var serviceBusDataSenderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'  // Azure Service Bus Data Sender
var serviceBusDataReceiverRoleId = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0' // Azure Service Bus Data Receiver
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'   // Key Vault Secrets User
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'  // Storage Blob Data Owner
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aafa' // Storage Table Data Contributor

var createPrivateEndpoints = !empty(privateEndpointsSubnetId)

// ---------------------------------------------------------------------------
// Existing resources (from shared deployment)
// ---------------------------------------------------------------------------

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource orchestratorEventsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' existing = {
  parent: serviceBusNamespace
  name: 'orchestrator-events'
}

resource workerJobsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' existing = {
  parent: serviceBusNamespace
  name: 'worker-jobs'
}

resource workerResultsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' existing = {
  parent: serviceBusNamespace
  name: 'worker-results'
}

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' existing = {
  name: logAnalyticsWorkspaceName
}

// ---------------------------------------------------------------------------
// Azure SQL Server + Database
// ---------------------------------------------------------------------------

resource sqlServer 'Microsoft.Sql/servers@2023-05-01-preview' = {
  name: 'sql-${schedulerBaseName}'
  location: location
  tags: schedulerTags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      login: sqlEntraAdminName
      sid: sqlEntraAdminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent: sqlServer
  name: 'sqldb-${schedulerBaseName}'
  location: location
  tags: schedulerTags
  sku: {
    name: 'GP_S_Gen5_1'
    tier: 'GeneralPurpose'
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 34359738368 // 32 GB (serverless minimum)
    autoPauseDelay: 60 // minutes – serverless auto-pause
    minCapacity: json('0.5') // minimum vCores when active
  }
}

// ---------------------------------------------------------------------------
// SQL Diagnostic Settings
// ---------------------------------------------------------------------------

resource sqlDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: sqlDatabase
  name: 'sql-diagnostics'
  properties: {
    workspaceId: logAnalyticsWorkspace.id
    logs: [
      { category: 'SQLSecurityAuditEvents', enabled: true, retentionPolicy: { enabled: false, days: 0 } }
      { category: 'AutomaticTuning', enabled: true, retentionPolicy: { enabled: false, days: 0 } }
    ]
    metrics: [
      { category: 'Basic', enabled: true, retentionPolicy: { enabled: false, days: 0 } }
    ]
  }
}

// ---------------------------------------------------------------------------
// Key Vault Secrets — SQL connection strings (managed identity auth, no password)
// ---------------------------------------------------------------------------

var sqlConnectionStringValue = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${sqlDatabase.name};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

resource schedulerSqlSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'scheduler-sql-connection-string'
  properties: {
    value: sqlConnectionStringValue
  }
}

resource orchestratorSqlSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'orchestrator-sql-connection-string'
  properties: {
    value: sqlConnectionStringValue
  }
}

// ===========================================================================
// SCHEDULER FUNCTION APP
// ===========================================================================

resource schedulerStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take('${replace('st${schedulerBaseName}', '-', '')}${uniqueString(resourceGroup().id)}', 24)
  location: location
  tags: schedulerTags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    networkAcls: !empty(schedulerSubnetId) ? {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      virtualNetworkRules: [
        { id: schedulerSubnetId, action: 'Allow' }
      ]
    } : {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
    }
  }
}

resource schedulerAppInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${schedulerBaseName}'
  location: location
  tags: schedulerTags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspaceId
  }
}

resource schedulerAppServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'asp-${schedulerBaseName}'
  location: location
  tags: schedulerTags
  kind: 'functionapp'
  sku: {
    tier: 'FlexConsumption'
    name: 'FC1'
  }
  properties: {
    reserved: true // Linux
  }
}

resource schedulerFunctionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-${schedulerBaseName}'
  location: location
  tags: schedulerTags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: schedulerAppServicePlan.id
    httpsOnly: true
    publicNetworkAccess: !empty(schedulerSubnetId) ? 'Disabled' : null
    virtualNetworkSubnetId: !empty(schedulerSubnetId) ? schedulerSubnetId : null
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      vnetRouteAllEnabled: !empty(schedulerSubnetId)
      appSettings: [
        {
          name: 'AzureWebJobsStorage__accountName'
          value: schedulerStorage.name
        }
        {
          name: 'WEBSITE_USE_PLACEHOLDER_DOTNETISOLATED'
          value: '1'
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: schedulerAppInsights.properties.ConnectionString
        }
        {
          name: 'Scheduler__SqlConnectionString'
          value: '@Microsoft.KeyVault(SecretUri=${schedulerSqlSecret.properties.secretUri})'
        }
        {
          name: 'Scheduler__ServiceBusNamespace'
          value: '${serviceBusNamespace.name}.servicebus.windows.net'
        }
        {
          name: 'Scheduler__OrchestratorTopicName'
          value: 'orchestrator-events'
        }
      ]
    }
  }
}

// ===========================================================================
// ORCHESTRATOR FUNCTION APP
// ===========================================================================

resource orchestratorStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take('${replace('st${orchestratorBaseName}', '-', '')}${uniqueString(resourceGroup().id)}', 24)
  location: location
  tags: orchestratorTags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    networkAcls: !empty(orchestratorSubnetId) ? {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      virtualNetworkRules: [
        { id: orchestratorSubnetId, action: 'Allow' }
      ]
    } : {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
    }
  }
}

resource orchestratorAppInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${orchestratorBaseName}'
  location: location
  tags: orchestratorTags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspaceId
  }
}

resource orchestratorAppServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'asp-${orchestratorBaseName}'
  location: location
  tags: orchestratorTags
  kind: 'functionapp'
  sku: {
    tier: 'FlexConsumption'
    name: 'FC1'
  }
  properties: {
    reserved: true // Linux
  }
}

resource orchestratorFunctionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-${orchestratorBaseName}'
  location: location
  tags: orchestratorTags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: orchestratorAppServicePlan.id
    httpsOnly: true
    publicNetworkAccess: !empty(orchestratorSubnetId) ? 'Disabled' : null
    virtualNetworkSubnetId: !empty(orchestratorSubnetId) ? orchestratorSubnetId : null
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      vnetRouteAllEnabled: !empty(orchestratorSubnetId)
      appSettings: [
        {
          name: 'AzureWebJobsStorage__accountName'
          value: orchestratorStorage.name
        }
        {
          name: 'WEBSITE_USE_PLACEHOLDER_DOTNETISOLATED'
          value: '1'
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: orchestratorAppInsights.properties.ConnectionString
        }
        {
          name: 'ServiceBusConnection__fullyQualifiedNamespace'
          value: '${serviceBusNamespace.name}.servicebus.windows.net'
        }
        {
          name: 'Orchestrator__SqlConnectionString'
          value: '@Microsoft.KeyVault(SecretUri=${orchestratorSqlSecret.properties.secretUri})'
        }
        {
          name: 'Orchestrator__ServiceBusNamespace'
          value: '${serviceBusNamespace.name}.servicebus.windows.net'
        }
        {
          name: 'Orchestrator__OrchestratorEventsTopicName'
          value: 'orchestrator-events'
        }
        {
          name: 'Orchestrator__OrchestratorSubscriptionName'
          value: 'orchestrator'
        }
        {
          name: 'Orchestrator__WorkerJobsTopicName'
          value: 'worker-jobs'
        }
        {
          name: 'Orchestrator__WorkerResultsTopicName'
          value: 'worker-results'
        }
        {
          name: 'Orchestrator__WorkerResultsSubscriptionName'
          value: 'orchestrator'
        }
      ]
    }
  }
}

// ---------------------------------------------------------------------------
// Service Bus Subscriptions
// ---------------------------------------------------------------------------

// Orchestrator subscription on orchestrator-events topic
resource orchestratorEventsSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: orchestratorEventsTopic
  name: 'orchestrator'
  tags: orchestratorTags
  properties: {
    maxDeliveryCount: 10
    lockDuration: 'PT1M'
    defaultMessageTimeToLive: 'P7D'
    deadLetteringOnMessageExpiration: true
  }
}

// Orchestrator subscription on worker-results topic
resource workerResultsSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: workerResultsTopic
  name: 'orchestrator'
  tags: orchestratorTags
  properties: {
    maxDeliveryCount: 10
    lockDuration: 'PT1M'
    defaultMessageTimeToLive: 'P7D'
    deadLetteringOnMessageExpiration: true
  }
}

// ---------------------------------------------------------------------------
// Role Assignments — Scheduler
// ---------------------------------------------------------------------------

// Scheduler → Service Bus Data Sender
resource schedulerSbSenderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, schedulerFunctionApp.id, serviceBusDataSenderRoleId)
  scope: serviceBusNamespace
  properties: {
    principalId: schedulerFunctionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataSenderRoleId)
  }
}

// Scheduler → Key Vault Secrets User
resource schedulerKvAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, schedulerFunctionApp.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    principalId: schedulerFunctionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
  }
}

// ---------------------------------------------------------------------------
// Role Assignments — Orchestrator
// ---------------------------------------------------------------------------

// Orchestrator → Service Bus Data Sender
resource orchestratorSbSenderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, orchestratorFunctionApp.id, serviceBusDataSenderRoleId)
  scope: serviceBusNamespace
  properties: {
    principalId: orchestratorFunctionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataSenderRoleId)
  }
}

// Orchestrator → Service Bus Data Receiver
resource orchestratorSbReceiverAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, orchestratorFunctionApp.id, serviceBusDataReceiverRoleId)
  scope: serviceBusNamespace
  properties: {
    principalId: orchestratorFunctionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataReceiverRoleId)
  }
}

// Orchestrator → Key Vault Secrets User
resource orchestratorKvAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, orchestratorFunctionApp.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    principalId: orchestratorFunctionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
  }
}

// ---------------------------------------------------------------------------
// Role Assignments — Storage (identity-based AzureWebJobsStorage)
// ---------------------------------------------------------------------------

// Scheduler → Storage Blob Data Owner on scheduler storage
resource schedulerStorageBlobOwnerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(schedulerStorage.id, schedulerFunctionApp.id, storageBlobDataOwnerRoleId)
  scope: schedulerStorage
  properties: {
    principalId: schedulerFunctionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
  }
}

// Scheduler → Storage Table Data Contributor on scheduler storage
resource schedulerStorageTableContributorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(schedulerStorage.id, schedulerFunctionApp.id, storageTableDataContributorRoleId)
  scope: schedulerStorage
  properties: {
    principalId: schedulerFunctionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
  }
}

// Orchestrator → Storage Blob Data Owner on orchestrator storage
resource orchestratorStorageBlobOwnerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(orchestratorStorage.id, orchestratorFunctionApp.id, storageBlobDataOwnerRoleId)
  scope: orchestratorStorage
  properties: {
    principalId: orchestratorFunctionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
  }
}

// Orchestrator → Storage Table Data Contributor on orchestrator storage
resource orchestratorStorageTableContributorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(orchestratorStorage.id, orchestratorFunctionApp.id, storageTableDataContributorRoleId)
  scope: orchestratorStorage
  properties: {
    principalId: orchestratorFunctionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
  }
}

// ===========================================================================
// PRIVATE ENDPOINTS (conditional — only when privateEndpointsSubnetId is set)
// ===========================================================================

// --- SQL Server Private Endpoint ---
resource sqlPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = if (createPrivateEndpoints) {
  name: '${schedulerBaseName}-pe-sql'
  location: location
  tags: schedulerTags
  properties: {
    subnet: {
      id: privateEndpointsSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${schedulerBaseName}-plsc-sql'
        properties: {
          privateLinkServiceId: sqlServer.id
          groupIds: ['sqlServer']
        }
      }
    ]
  }
}

resource sqlDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (createPrivateEndpoints) {
  parent: sqlPrivateEndpoint
  name: 'sql-dns-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'sql-config'
        properties: {
          privateDnsZoneId: sqlDnsZoneId
        }
      }
    ]
  }
}

// --- Scheduler Storage Private Endpoints (blob, queue, table) ---
resource schedulerStBlobPe 'Microsoft.Network/privateEndpoints@2023-11-01' = if (createPrivateEndpoints) {
  name: '${schedulerBaseName}-pe-st-blob'
  location: location
  tags: schedulerTags
  properties: {
    subnet: {
      id: privateEndpointsSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${schedulerBaseName}-plsc-st-blob'
        properties: {
          privateLinkServiceId: schedulerStorage.id
          groupIds: ['blob']
        }
      }
    ]
  }
}

resource schedulerStBlobDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (createPrivateEndpoints) {
  parent: schedulerStBlobPe
  name: 'st-blob-dns-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'st-blob-config'
        properties: {
          privateDnsZoneId: stBlobDnsZoneId
        }
      }
    ]
  }
}

resource schedulerStQueuePe 'Microsoft.Network/privateEndpoints@2023-11-01' = if (createPrivateEndpoints) {
  name: '${schedulerBaseName}-pe-st-queue'
  location: location
  tags: schedulerTags
  properties: {
    subnet: {
      id: privateEndpointsSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${schedulerBaseName}-plsc-st-queue'
        properties: {
          privateLinkServiceId: schedulerStorage.id
          groupIds: ['queue']
        }
      }
    ]
  }
}

resource schedulerStQueueDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (createPrivateEndpoints) {
  parent: schedulerStQueuePe
  name: 'st-queue-dns-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'st-queue-config'
        properties: {
          privateDnsZoneId: stQueueDnsZoneId
        }
      }
    ]
  }
}

resource schedulerStTablePe 'Microsoft.Network/privateEndpoints@2023-11-01' = if (createPrivateEndpoints) {
  name: '${schedulerBaseName}-pe-st-table'
  location: location
  tags: schedulerTags
  properties: {
    subnet: {
      id: privateEndpointsSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${schedulerBaseName}-plsc-st-table'
        properties: {
          privateLinkServiceId: schedulerStorage.id
          groupIds: ['table']
        }
      }
    ]
  }
}

resource schedulerStTableDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (createPrivateEndpoints) {
  parent: schedulerStTablePe
  name: 'st-table-dns-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'st-table-config'
        properties: {
          privateDnsZoneId: stTableDnsZoneId
        }
      }
    ]
  }
}

// --- Orchestrator Storage Private Endpoints (blob, queue, table) ---
resource orchestratorStBlobPe 'Microsoft.Network/privateEndpoints@2023-11-01' = if (createPrivateEndpoints) {
  name: '${orchestratorBaseName}-pe-st-blob'
  location: location
  tags: orchestratorTags
  properties: {
    subnet: {
      id: privateEndpointsSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${orchestratorBaseName}-plsc-st-blob'
        properties: {
          privateLinkServiceId: orchestratorStorage.id
          groupIds: ['blob']
        }
      }
    ]
  }
}

resource orchestratorStBlobDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (createPrivateEndpoints) {
  parent: orchestratorStBlobPe
  name: 'st-blob-dns-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'st-blob-config'
        properties: {
          privateDnsZoneId: stBlobDnsZoneId
        }
      }
    ]
  }
}

resource orchestratorStQueuePe 'Microsoft.Network/privateEndpoints@2023-11-01' = if (createPrivateEndpoints) {
  name: '${orchestratorBaseName}-pe-st-queue'
  location: location
  tags: orchestratorTags
  properties: {
    subnet: {
      id: privateEndpointsSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${orchestratorBaseName}-plsc-st-queue'
        properties: {
          privateLinkServiceId: orchestratorStorage.id
          groupIds: ['queue']
        }
      }
    ]
  }
}

resource orchestratorStQueueDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (createPrivateEndpoints) {
  parent: orchestratorStQueuePe
  name: 'st-queue-dns-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'st-queue-config'
        properties: {
          privateDnsZoneId: stQueueDnsZoneId
        }
      }
    ]
  }
}

resource orchestratorStTablePe 'Microsoft.Network/privateEndpoints@2023-11-01' = if (createPrivateEndpoints) {
  name: '${orchestratorBaseName}-pe-st-table'
  location: location
  tags: orchestratorTags
  properties: {
    subnet: {
      id: privateEndpointsSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${orchestratorBaseName}-plsc-st-table'
        properties: {
          privateLinkServiceId: orchestratorStorage.id
          groupIds: ['table']
        }
      }
    ]
  }
}

resource orchestratorStTableDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (createPrivateEndpoints) {
  parent: orchestratorStTablePe
  name: 'st-table-dns-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'st-table-config'
        properties: {
          privateDnsZoneId: stTableDnsZoneId
        }
      }
    ]
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output schedulerFunctionAppName string = schedulerFunctionApp.name
output schedulerFunctionAppDefaultHostName string = schedulerFunctionApp.properties.defaultHostName
output schedulerFunctionAppPrincipalId string = schedulerFunctionApp.identity.principalId
output schedulerAppInsightsConnectionString string = schedulerAppInsights.properties.ConnectionString

output orchestratorFunctionAppName string = orchestratorFunctionApp.name
output orchestratorFunctionAppDefaultHostName string = orchestratorFunctionApp.properties.defaultHostName
output orchestratorFunctionAppPrincipalId string = orchestratorFunctionApp.identity.principalId
output orchestratorAppInsightsConnectionString string = orchestratorAppInsights.properties.ConnectionString

output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDatabase.name
output sqlManagedIdentityConnectionString string = sqlConnectionStringValue
