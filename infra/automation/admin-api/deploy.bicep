@description('Location for all resources')
param location string = resourceGroup().location

@description('Base name for all resources')
param baseName string = 'matoolkit-admin-api'

@description('SQL Server connection string')
@secure()
param sqlConnectionString string

@description('Entra ID tenant ID for authentication')
param entraIdTenantId string = ''

@description('Entra ID client ID (app registration) for authentication')
param entraIdClientId string = ''

@description('Entra ID audience URI for authentication')
param entraIdAudience string = ''

@description('Name of the existing Key Vault.')
param keyVaultName string

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

// ---------------------------------------------------------------------------
// Existing resources
// ---------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

// ---------------------------------------------------------------------------
// Key Vault Secret — SQL connection string
// ---------------------------------------------------------------------------

resource sqlConnectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'admin-api-sql-connection-string'
  properties: {
    value: sqlConnectionString
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
    } : null
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
    RetentionInDays: 30
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
    virtualNetworkSubnetId: !empty(adminApiSubnetId) ? adminApiSubnetId : null
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|8.0'
      vnetRouteAllEnabled: !empty(adminApiSubnetId)
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
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

// Outputs
output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output functionAppPrincipalId string = functionApp.identity.principalId
output appInsightsInstrumentationKey string = appInsights.properties.InstrumentationKey
output appInsightsConnectionString string = appInsights.properties.ConnectionString
