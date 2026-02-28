@description('Base name for resources')
param baseName string = 'matoolkit'

@description('Location for all resources')
param location string = resourceGroup().location

@description('Environment suffix')
param environment string = 'dev'

@description('Subnet ID for analytics ACA environment')
param analyticsSubnetId string = ''

@description('Subnet ID for private endpoints')
param privateEndpointSubnetId string = ''

@description('Private DNS zone ID for blob storage')
param blobPrivateDnsZoneId string = ''

@description('Private DNS zone ID for DFS (ADLS Gen2)')
param dfsPrivateDnsZoneId string = ''

@description('Key Vault name (existing shared resource)')
param keyVaultName string = '${baseName}-kv'

@description('ACR name (existing shared resource)')
param acrName string = '${baseName}acr'

@description('Log Analytics workspace name (existing shared resource)')
param logAnalyticsName string = '${baseName}-logs'

@description('Databricks access connector principal ID (for UC storage access)')
param databricksAccessConnectorPrincipalId string = ''

@description('Tags for all resources')
param tags object = {
  project: 'ma-toolkit'
  subsystem: 'analytics'
  environment: environment
}

var uniqueSuffix = uniqueString(resourceGroup().id, baseName)
var storageAccountName = 'st${baseName}analytics${take(uniqueSuffix, 6)}'

// --- Existing shared resources ---
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' existing = {
  name: acrName
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: logAnalyticsName
}

// --- User-assigned managed identity for ACR pull ---
resource acrPullIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${baseName}-analytics-acrpull'
  location: location
  tags: tags
}

resource acrPullRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, acrPullIdentity.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '7f951dda-4ed3-4680-a7ca-43fe172d538d'  // AcrPull
    )
    principalId: acrPullIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- ADLS Gen2 Storage Account ---
module storageAccount 'modules/storage-account/main.bicep' = {
  name: 'analytics-storage'
  params: {
    storageAccountName: storageAccountName
    location: location
    containerNames: ['landing', 'unity-catalog']
    privateEndpointSubnetId: privateEndpointSubnetId
    blobPrivateDnsZoneId: blobPrivateDnsZoneId
    dfsPrivateDnsZoneId: dfsPrivateDnsZoneId
    tags: tags
  }
}

// --- ACA Environment ---
resource acaEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${baseName}-analytics-env-${environment}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    vnetConfiguration: !empty(analyticsSubnetId)
      ? {
          infrastructureSubnetId: analyticsSubnetId
          internal: true
        }
      : null
  }
}

// --- ACA Jobs ---
var jobConfigs = [
  {
    name: '${baseName}-graph-ingest-${environment}'
    image: '${acr.properties.loginServer}/analytics/graph-ingest:latest'
  }
  {
    name: '${baseName}-exo-ingest-${environment}'
    image: '${acr.properties.loginServer}/analytics/exo-ingest:latest'
  }
  {
    name: '${baseName}-spo-ingest-${environment}'
    image: '${acr.properties.loginServer}/analytics/spo-ingest:latest'
  }
]

var commonEnvVars = [
  { name: 'KEYVAULT_NAME'; value: keyVaultName }
  { name: 'STORAGE_ACCOUNT_NAME'; value: storageAccountName }
  { name: 'LANDING_CONTAINER'; value: 'landing' }
]

module acaJobs 'modules/container-app-job/main.bicep' = [
  for config in jobConfigs: {
    name: 'job-${config.name}'
    params: {
      jobName: config.name
      location: location
      environmentId: acaEnvironment.id
      containerImage: config.image
      acrLoginServer: acr.properties.loginServer
      acrPullIdentityId: acrPullIdentity.id
      envVars: commonEnvVars
      tags: tags
    }
    dependsOn: [acrPullRoleAssignment]
  }
]

// --- RBAC for ACA Job system-assigned identities ---

// Key Vault Secrets User (4633458b-17de-408a-b874-0445c86b69e6)
resource kvSecretsUserRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (config, i) in jobConfigs: {
    name: guid(keyVault.id, acaJobs[i].outputs.systemAssignedPrincipalId, '4633458b-17de-408a-b874-0445c86b69e6')
    scope: keyVault
    properties: {
      roleDefinitionId: subscriptionResourceId(
        'Microsoft.Authorization/roleDefinitions',
        '4633458b-17de-408a-b874-0445c86b69e6'
      )
      principalId: acaJobs[i].outputs.systemAssignedPrincipalId
      principalType: 'ServicePrincipal'
    }
  }
]

// Key Vault Certificate User (db79e9a7-68ee-4b58-9aeb-b90e7c24fcba)
resource kvCertUserRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (config, i) in jobConfigs: {
    name: guid(keyVault.id, acaJobs[i].outputs.systemAssignedPrincipalId, 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba')
    scope: keyVault
    properties: {
      roleDefinitionId: subscriptionResourceId(
        'Microsoft.Authorization/roleDefinitions',
        'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba'
      )
      principalId: acaJobs[i].outputs.systemAssignedPrincipalId
      principalType: 'ServicePrincipal'
    }
  }
]

// Storage Blob Data Contributor on the storage account (ba92f5b4-2d11-453d-a403-e96b0029c9fe)
resource storageBlobRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (config, i) in jobConfigs: {
    name: guid(storageAccount.outputs.storageAccountId, acaJobs[i].outputs.systemAssignedPrincipalId, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    scope: resourceId('Microsoft.Storage/storageAccounts', storageAccountName)
    properties: {
      roleDefinitionId: subscriptionResourceId(
        'Microsoft.Authorization/roleDefinitions',
        'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
      )
      principalId: acaJobs[i].outputs.systemAssignedPrincipalId
      principalType: 'ServicePrincipal'
    }
  }
]

// Storage Blob Data Contributor for Databricks Access Connector (UC metastore root)
resource databricksStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(databricksAccessConnectorPrincipalId)) {
  name: guid(storageAccount.outputs.storageAccountId, databricksAccessConnectorPrincipalId, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: resourceId('Microsoft.Storage/storageAccounts', storageAccountName)
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
    )
    principalId: databricksAccessConnectorPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// --- Outputs ---
output storageAccountName string = storageAccountName
output acaEnvironmentId string = acaEnvironment.id
output jobNames array = [for (config, i) in jobConfigs: acaJobs[i].outputs.jobName]
output jobPrincipalIds array = [for (config, i) in jobConfigs: acaJobs[i].outputs.systemAssignedPrincipalId]
