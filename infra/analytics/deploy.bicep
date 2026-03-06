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

@description('Tags for all resources')
param tags object = {
  project: 'ma-toolkit'
  subsystem: 'analytics'
  environment: environment
}

var uniqueSuffix = uniqueString(resourceGroup().id, baseName)
var storageAccountName = 'stanalytics${take(uniqueSuffix, 12)}'

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

// --- Databricks Workspace ---
module databricksWorkspace 'modules/databricks-workspace/main.bicep' = {
  name: 'analytics-databricks-workspace'
  params: {
    workspaceName: '${baseName}-analytics-dbw-${environment}'
    location: location
    tags: tags
  }
}

// --- Databricks Access Connector ---
module databricksAccessConnector 'modules/databricks-access-connector/main.bicep' = {
  name: 'analytics-databricks-access-connector'
  params: {
    accessConnectorName: '${baseName}-analytics-dbac-${environment}'
    location: location
    tags: tags
  }
}

// --- Data Factory ---
module dataFactory 'modules/data-factory/main.bicep' = {
  name: 'analytics-data-factory'
  params: {
    factoryName: '${baseName}-analytics-adf-${environment}'
    location: location
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
    imagePath: 'analytics/graph-ingest:latest'
  }
  {
    name: '${baseName}-exo-ingest-${environment}'
    imagePath: 'analytics/exo-ingest:latest'
  }
  {
    name: '${baseName}-spo-ingest-${environment}'
    imagePath: 'analytics/spo-ingest:latest'
  }
]

var commonEnvVars = [
  { name: 'KEYVAULT_NAME', value: keyVaultName }
  { name: 'STORAGE_ACCOUNT_NAME', value: storageAccountName }
  { name: 'LANDING_CONTAINER', value: 'landing' }
]

module acaJobs 'modules/container-app-job/main.bicep' = [
  for config in jobConfigs: {
    name: 'job-${config.name}'
    params: {
      jobName: config.name
      location: location
      environmentId: acaEnvironment.id
      containerImage: '${acr.properties.loginServer}/${config.imagePath}'
      acrLoginServer: acr.properties.loginServer
      acrPullIdentityId: acrPullIdentity.id
      envVars: commonEnvVars
      tags: tags
      contributorPrincipalIds: [dataFactory.outputs.principalId]
    }
    dependsOn: [acrPullRoleAssignment, dataFactory]
  }
]

// --- Reference the analytics storage account for RBAC scoping ---
resource analyticsStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
  dependsOn: [storageAccount]
}

// --- RBAC for ACA Job system-assigned identities ---
// Unrolled per job to avoid for-loop limitations with runtime values

// Graph ingest — KV Secrets User
resource kvSecretsUser0 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, jobConfigs[0].name, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: acaJobs[0].outputs.systemAssignedPrincipalId
    principalType: 'ServicePrincipal'
  }
}
resource kvSecretsUser1 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, jobConfigs[1].name, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: acaJobs[1].outputs.systemAssignedPrincipalId
    principalType: 'ServicePrincipal'
  }
}
resource kvSecretsUser2 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, jobConfigs[2].name, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: acaJobs[2].outputs.systemAssignedPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// KV Certificate User
resource kvCertUser0 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, jobConfigs[0].name, 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba')
    principalId: acaJobs[0].outputs.systemAssignedPrincipalId
    principalType: 'ServicePrincipal'
  }
}
resource kvCertUser1 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, jobConfigs[1].name, 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba')
    principalId: acaJobs[1].outputs.systemAssignedPrincipalId
    principalType: 'ServicePrincipal'
  }
}
resource kvCertUser2 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, jobConfigs[2].name, 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba')
    principalId: acaJobs[2].outputs.systemAssignedPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Storage Blob Data Contributor
resource storageBlobRole0 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(analyticsStorage.id, jobConfigs[0].name, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: analyticsStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: acaJobs[0].outputs.systemAssignedPrincipalId
    principalType: 'ServicePrincipal'
  }
}
resource storageBlobRole1 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(analyticsStorage.id, jobConfigs[1].name, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: analyticsStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: acaJobs[1].outputs.systemAssignedPrincipalId
    principalType: 'ServicePrincipal'
  }
}
resource storageBlobRole2 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(analyticsStorage.id, jobConfigs[2].name, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: analyticsStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: acaJobs[2].outputs.systemAssignedPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Storage Blob Data Contributor for Databricks Access Connector (UC metastore root)
resource databricksStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(analyticsStorage.id, 'databricks-access-connector', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: analyticsStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: databricksAccessConnector.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

// Key Vault Secrets User for Data Factory (read tenant registry secrets)
resource adfKvSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, 'data-factory', '4633458b-17de-408a-b874-0445c86b69e6')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: dataFactory.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

// Contributor on ACA Jobs for Data Factory (start jobs via ARM) is granted
// inside the container-app-job module via the contributorPrincipalIds param.

// Contributor on Databricks Workspace for Data Factory (trigger DLT pipelines)
resource databricksWorkspaceRef 'Microsoft.Databricks/workspaces@2024-05-01' existing = {
  name: '${baseName}-analytics-dbw-${environment}'
  dependsOn: [databricksWorkspace]
}

resource adfDatabricksContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(databricksWorkspaceRef.id, 'data-factory', 'b24988ac-6180-42a0-ab88-20f7382dd24c')
  scope: databricksWorkspaceRef
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c')
    principalId: dataFactory.outputs.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- Outputs ---
output storageAccountName string = storageAccountName
output acaEnvironmentId string = acaEnvironment.id
output jobNames array = [acaJobs[0].outputs.jobName, acaJobs[1].outputs.jobName, acaJobs[2].outputs.jobName]
output jobPrincipalIds array = [acaJobs[0].outputs.systemAssignedPrincipalId, acaJobs[1].outputs.systemAssignedPrincipalId, acaJobs[2].outputs.systemAssignedPrincipalId]
output databricksWorkspaceUrl string = databricksWorkspace.outputs.workspaceUrl
output dataFactoryName string = dataFactory.outputs.factoryName
