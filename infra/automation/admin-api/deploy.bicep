@description('Location for all resources')
param location string = resourceGroup().location

@description('Base name for all resources')
param baseName string = 'matoolkit-admin-api'

@description('Fully qualified domain name of the SQL Server (e.g. sql-scheduler-dev.database.windows.net).')
param sqlServerFqdn string

@description('Name of the SQL database.')
param sqlDatabaseName string

@description('Entra ID tenant ID for authentication')
param entraIdTenantId string = ''

@description('Entra ID client ID (app registration) for authentication')
param entraIdClientId string = ''

@description('Entra ID audience URI for authentication')
param entraIdAudience string = ''

@description('Name of the existing Key Vault.')
param keyVaultName string

@description('Name of the existing Service Bus namespace (from shared deployment).')
param serviceBusNamespaceName string

@description('Subnet resource ID for VNet integration. Leave empty to skip VNet integration.')
param adminApiSubnetId string = ''

@description('Resource ID of the existing Log Analytics workspace (from shared deployment). Leave empty to skip workspace linkage.')
param logAnalyticsWorkspaceId string = ''

@description('Tags to apply to all resources')
param tags object = {
  component: 'admin-api'
  project: 'ma-toolkit'
}

var storageAccountName = replace('${baseName}st', '-', '')
var appInsightsName = '${baseName}-ai'
var functionAppName = '${baseName}-func'
var hostingPlanName = '${baseName}-plan'

// Built-in role definition IDs
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'   // Key Vault Secrets User
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'  // Storage Blob Data Owner
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aafa' // Storage Table Data Contributor
var serviceBusDataSenderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'       // Azure Service Bus Data Sender

// ---------------------------------------------------------------------------
// Existing resources
// ---------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

// ---------------------------------------------------------------------------
// Key Vault Secret — SQL connection string (managed identity auth, no password)
// ---------------------------------------------------------------------------

var sqlConnectionStringValue = 'Server=tcp:${sqlServerFqdn},1433;Initial Catalog=${sqlDatabaseName};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

resource sqlConnectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'admin-api-sql-connection-string'
  properties: {
    value: sqlConnectionStringValue
  }
}

// Storage Account for Azure Functions
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: take(storageAccountName, 24)
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    networkAcls: !empty(adminApiSubnetId) ? {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      virtualNetworkRules: [
        { id: adminApiSubnetId, action: 'Allow' }
      ]
    } : {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
    }
  }
}

// Application Insights
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    Request_Source: 'rest'
    WorkspaceResourceId: !empty(logAnalyticsWorkspaceId) ? logAnalyticsWorkspaceId : null
  }
}

// Flex Consumption Hosting Plan
resource hostingPlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: hostingPlanName
  location: location
  tags: tags
  sku: {
    tier: 'FlexConsumption'
    name: 'FC1'
  }
  kind: 'functionapp'
  properties: {
    reserved: true
  }
}

// Function App
resource functionApp 'Microsoft.Web/sites@2023-01-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: hostingPlan.id
    httpsOnly: true
    publicNetworkAccess: !empty(adminApiSubnetId) ? 'Disabled' : null
    virtualNetworkSubnetId: !empty(adminApiSubnetId) ? adminApiSubnetId : null
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      vnetRouteAllEnabled: !empty(adminApiSubnetId)
      appSettings: [
        {
          name: 'AzureWebJobsStorage__accountName'
          value: storageAccount.name
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
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'AdminApi__SqlConnectionString'
          value: '@Microsoft.KeyVault(SecretUri=${sqlConnectionStringSecret.properties.secretUri})'
        }
        {
          name: 'AdminApi__ServiceBusNamespace'
          value: '${serviceBusNamespace.name}.servicebus.windows.net'
        }
        {
          name: 'AzureAd__Instance'
          value: 'https://login.microsoftonline.com/'
        }
        {
          name: 'AzureAd__TenantId'
          value: entraIdTenantId
        }
        {
          name: 'AzureAd__ClientId'
          value: entraIdClientId
        }
        {
          name: 'AzureAd__Audience'
          value: entraIdAudience
        }
      ]
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
    }
  }
}

// ---------------------------------------------------------------------------
// Role Assignments
// ---------------------------------------------------------------------------

// Key Vault Secrets User on the vault
resource keyVaultSecretsUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, functionApp.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
  }
}

// Storage Blob Data Owner on the storage account
resource storageBlobOwnerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageBlobDataOwnerRoleId)
  scope: storageAccount
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
  }
}

// Storage Table Data Contributor on the storage account
resource storageTableContributorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageTableDataContributorRoleId)
  scope: storageAccount
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
  }
}

// Service Bus Data Sender on the namespace
resource serviceBusSenderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, functionApp.id, serviceBusDataSenderRoleId)
  scope: serviceBusNamespace
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataSenderRoleId)
  }
}

// Outputs
output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output functionAppPrincipalId string = functionApp.identity.principalId
output appInsightsInstrumentationKey string = appInsights.properties.InstrumentationKey
output appInsightsConnectionString string = appInsights.properties.ConnectionString
