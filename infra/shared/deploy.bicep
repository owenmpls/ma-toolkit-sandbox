// ---------------------------------------------------------------------------
// Shared Infrastructure — Log Analytics, Service Bus, Key Vault, Container Registry
// ---------------------------------------------------------------------------
// Deploy this template first. Other templates reference these resources via
// existing resource lookups using the output names.
// ---------------------------------------------------------------------------

@description('Base name for all resources (e.g. "matoolkit").')
param baseName string

@description('Azure region for deployment.')
param location string = resourceGroup().location

@description('Enable Key Vault firewall (deny-by-default + trusted Azure service bypass for Arc hybrid workers). VNet resources use the private endpoint from network.bicep.')
param enableKeyVaultFirewall bool = true

@description('Disable public network access on Service Bus. Must remain false on Standard SKU (no private endpoint support).')
param disableServiceBusPublicAccess bool = false

@description('Log Analytics workspace data retention in days. Workspace-based App Insights inherit this value.')
param logAnalyticsRetentionDays int = 30

@description('Tags to apply to all resources.')
param tags object = {
  component: 'shared'
  project: 'ma-toolkit'
}

// ---------------------------------------------------------------------------
// Log Analytics Workspace
// ---------------------------------------------------------------------------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${baseName}-logs'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: logAnalyticsRetentionDays
  }
}

// ---------------------------------------------------------------------------
// Service Bus Namespace + Topics
// ---------------------------------------------------------------------------

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: '${baseName}-sb'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    publicNetworkAccess: disableServiceBusPublicAccess ? 'Disabled' : null
  }
}

resource orchestratorEventsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBus
  name: 'orchestrator-events'
  tags: tags
  properties: {
    maxSizeInMegabytes: 1024
    defaultMessageTimeToLive: 'P7D'
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    enablePartitioning: false
  }
}

resource workerJobsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBus
  name: 'worker-jobs'
  tags: tags
  properties: {
    maxSizeInMegabytes: 1024
    defaultMessageTimeToLive: 'P7D'
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    enablePartitioning: false
  }
}

resource workerResultsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBus
  name: 'worker-results'
  tags: tags
  properties: {
    maxSizeInMegabytes: 1024
    defaultMessageTimeToLive: 'P7D'
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    enablePartitioning: false
  }
}

// ---------------------------------------------------------------------------
// Key Vault — hybrid access model:
// - VNet resources connect via private endpoint (network.bicep)
// - Azure Arc hybrid workers connect via public endpoint (trusted service bypass)
// - All other public traffic denied when firewall is enabled
// ---------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${baseName}-kv'
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
    networkAcls: enableKeyVaultFirewall ? {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
    } : null
  }
}

// ---------------------------------------------------------------------------
// Container Registry
// ---------------------------------------------------------------------------

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: '${baseName}acr'
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

// ---------------------------------------------------------------------------
// Diagnostic Settings
// ---------------------------------------------------------------------------

resource kvDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: keyVault
  name: 'kv-diagnostics'
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      { categoryGroup: 'audit', enabled: true, retentionPolicy: { enabled: false, days: 0 } }
    ]
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output logAnalyticsWorkspaceId string = logAnalytics.id
output logAnalyticsWorkspaceName string = logAnalytics.name
output serviceBusNamespaceName string = serviceBus.name
output keyVaultName string = keyVault.name
output containerRegistryName string = containerRegistry.name
output containerRegistryLoginServer string = containerRegistry.properties.loginServer
