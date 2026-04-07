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

@description('VNet ID for Databricks VNet injection (optional)')
param vnetId string = ''

@description('Databricks host subnet name for VNet injection (optional)')
param databricksHostSubnetName string = ''

@description('Databricks container subnet name for VNet injection (optional)')
param databricksContainerSubnetName string = ''

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
    vnetId: vnetId
    publicSubnetName: databricksHostSubnetName
    privateSubnetName: databricksContainerSubnetName
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

// --- Network Security Perimeter (serverless SQL warehouse → storage) ---
module networkSecurityPerimeter 'modules/network-security-perimeter/main.bicep' = {
  name: 'analytics-nsp'
  params: {
    nspName: '${baseName}-analytics-nsp'
    location: location
    storageAccountId: storageAccount.outputs.storageAccountId
    tags: tags
  }
}

// --- Ingestion Orchestrator Function App ---

@description('Subnet resource ID for orchestrator VNet integration. Leave empty to skip.')
param orchestratorSubnetId string = ''

var orchestratorBaseName = '${baseName}-ingest-orch'
var orchestratorStorageName = take(replace('${orchestratorBaseName}st', '-', ''), 24)
var orchestratorFuncName = '${orchestratorBaseName}-func-${take(uniqueSuffix, 8)}'
var orchestratorPlanName = '${orchestratorBaseName}-plan'
var orchestratorAiName = '${orchestratorBaseName}-ai'
var orchestratorTags = union(tags, { component: 'ingestion-orchestrator' })

resource orchestratorStorage 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: orchestratorStorageName
  location: location
  tags: orchestratorTags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    publicNetworkAccess: 'Enabled'   // Required for Flex Consumption zip deployment via Kudu
  }
}

resource orchestratorDeployContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: '${orchestratorStorage.name}/default/deployment'
}

resource orchestratorAppInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: orchestratorAiName
  location: location
  tags: orchestratorTags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: !empty(logAnalyticsWorkspaceId) ? logAnalyticsWorkspaceId : null
  }
}

resource orchestratorPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: orchestratorPlanName
  location: location
  tags: orchestratorTags
  kind: 'functionapp'
  sku: {
    tier: 'FlexConsumption'
    name: 'FC1'
  }
  properties: {
    reserved: true
  }
}

resource orchestratorFunc 'Microsoft.Web/sites@2023-12-01' = {
  name: orchestratorFuncName
  location: location
  tags: orchestratorTags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: orchestratorPlan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    virtualNetworkSubnetId: !empty(orchestratorSubnetId) ? orchestratorSubnetId : null
    siteConfig: {
      vnetRouteAllEnabled: !empty(orchestratorSubnetId)
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: orchestratorStorageName }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: orchestratorAppInsights.properties.ConnectionString }
        { name: 'Ingestion__KeyVaultName', value: keyVaultName }
        { name: 'Ingestion__SubscriptionId', value: subscription().subscriptionId }
        { name: 'Ingestion__ResourceGroupName', value: resourceGroup().name }
        { name: 'Ingestion__ConfigPath', value: 'Config' }
      ]
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${orchestratorStorage.properties.primaryEndpoints.blob}deployment'
          authentication: { type: 'SystemAssignedIdentity' }
        }
      }
      scaleAndConcurrency: {
        instanceMemoryMB: 2048
        maximumInstanceCount: 100
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '8.0'
      }
    }
  }
}

@description('Resource ID of the Log Analytics workspace for App Insights. Leave empty to skip workspace linkage.')
param logAnalyticsWorkspaceId string = ''

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

// ACA Jobs receive most env vars at start time from the orchestrator.
// Only KEYVAULT_NAME is static (needed for cert loading at container startup).
var commonEnvVars = [
  { name: 'KEYVAULT_NAME', value: keyVaultName }
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
    }
    dependsOn: [acrPullRoleAssignment]
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

// --- RBAC for Ingestion Orchestrator ---

// Storage Blob Data Contributor on analytics ADLS (write run/task JSONL)
resource orchestratorStorageBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(analyticsStorage.id, orchestratorFuncName, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: analyticsStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: orchestratorFunc.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Storage Blob Data Owner on orchestrator's own storage (AzureWebJobsStorage)
resource orchestratorOwnStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(orchestratorStorage.id, orchestratorFuncName, 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
  scope: orchestratorStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
    principalId: orchestratorFunc.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Contributor scoped to the resource group — gives the orchestrator access to
// start and poll ACA Job executions via ARM API. In production, scope this to
// individual ACA Job resources using existing resource references.
resource orchestratorRgContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, orchestratorFuncName, 'b24988ac-6180-42a0-ab88-20f7382dd24c')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c')
    principalId: orchestratorFunc.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- Outputs ---
output storageAccountName string = storageAccountName
output acaEnvironmentId string = acaEnvironment.id
output jobNames array = [acaJobs[0].outputs.jobName, acaJobs[1].outputs.jobName, acaJobs[2].outputs.jobName]
output jobPrincipalIds array = [acaJobs[0].outputs.systemAssignedPrincipalId, acaJobs[1].outputs.systemAssignedPrincipalId, acaJobs[2].outputs.systemAssignedPrincipalId]
output databricksWorkspaceUrl string = databricksWorkspace.outputs.workspaceUrl
output orchestratorFunctionAppName string = orchestratorFunc.name
output orchestratorPrincipalId string = orchestratorFunc.identity.principalId
