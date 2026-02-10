// ---------------------------------------------------------------------------
// Shared Infrastructure — Log Analytics, Service Bus, Key Vault, Container
// Registry, VNet, Subnets, Private DNS Zones, KV/SB Private Endpoints
// ---------------------------------------------------------------------------
// Deploy this template first. Other templates reference these resources via
// existing resource lookups using the output names.
// ---------------------------------------------------------------------------

@description('Base name for all resources (e.g. "matoolkit").')
@minLength(3)
param baseName string

@description('Azure region for deployment.')
param location string = resourceGroup().location

@description('Enable Key Vault firewall (deny-by-default + trusted Azure service bypass for Arc hybrid workers). VNet resources use the private endpoint created below.')
param enableKeyVaultFirewall bool = true

@description('Disable public network access on Service Bus. Only effective when serviceBusSku is Premium.')
param disableServiceBusPublicAccess bool = false

@description('Service Bus SKU. Use Premium for private endpoint support.')
@allowed(['Standard', 'Premium'])
param serviceBusSku string = 'Standard'

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
  name: '${baseName}-sbus'
  location: location
  tags: tags
  sku: {
    name: serviceBusSku
    tier: serviceBusSku
  }
  properties: {
    publicNetworkAccess: disableServiceBusPublicAccess ? 'Disabled' : null
  }
}

resource orchestratorEventsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBus
  name: 'orchestrator-events'
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
// - VNet resources connect via private endpoint (below)
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

// ===========================================================================
// VIRTUAL NETWORK + SUBNETS
// ===========================================================================

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: '${baseName}-vnet'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.0.0.0/16'
      ]
    }
    subnets: [
      {
        name: 'snet-scheduler'
        properties: {
          addressPrefix: '10.0.1.0/24'
          serviceEndpoints: [
            { service: 'Microsoft.Storage' }
          ]
          delegations: [
            {
              name: 'delegation-web'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'snet-orchestrator'
        properties: {
          addressPrefix: '10.0.2.0/24'
          serviceEndpoints: [
            { service: 'Microsoft.Storage' }
          ]
          delegations: [
            {
              name: 'delegation-web'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'snet-admin-api'
        properties: {
          addressPrefix: '10.0.3.0/24'
          serviceEndpoints: [
            { service: 'Microsoft.Storage' }
          ]
          delegations: [
            {
              name: 'delegation-web'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'snet-cloud-worker'
        properties: {
          addressPrefix: '10.0.4.0/23'
          delegations: [
            {
              name: 'delegation-aca'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'snet-private-endpoints'
        properties: {
          addressPrefix: '10.0.10.0/24'
        }
      }
      {
        name: 'snet-deployment-scripts'
        properties: {
          addressPrefix: '10.0.11.0/24'
          delegations: [
            {
              name: 'delegation-aci'
              properties: {
                serviceName: 'Microsoft.ContainerInstance/containerGroups'
              }
            }
          ]
        }
      }
    ]
  }
}

// ===========================================================================
// PRIVATE DNS ZONES
// ===========================================================================

resource sqlDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink${environment().suffixes.sqlServerHostname}'
  location: 'global'
  tags: tags
}

resource kvDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.vaultcore.azure.net'
  location: 'global'
  tags: tags
}

resource sbDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.servicebus.windows.net'
  location: 'global'
  tags: tags
}

resource stBlobDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.blob.${environment().suffixes.storage}'
  location: 'global'
  tags: tags
}

resource stQueueDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.queue.${environment().suffixes.storage}'
  location: 'global'
  tags: tags
}

resource stTableDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.table.${environment().suffixes.storage}'
  location: 'global'
  tags: tags
}

// ===========================================================================
// VNET LINKS FOR DNS ZONES
// ===========================================================================

resource sqlDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: sqlDnsZone
  name: '${baseName}-sql-link'
  location: 'global'
  tags: tags
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    registrationEnabled: false
  }
}

resource kvDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: kvDnsZone
  name: '${baseName}-kv-link'
  location: 'global'
  tags: tags
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    registrationEnabled: false
  }
}

resource sbDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: sbDnsZone
  name: '${baseName}-sb-link'
  location: 'global'
  tags: tags
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    registrationEnabled: false
  }
}

resource stBlobDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: stBlobDnsZone
  name: '${baseName}-st-blob-link'
  location: 'global'
  tags: tags
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    registrationEnabled: false
  }
}

resource stQueueDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: stQueueDnsZone
  name: '${baseName}-st-queue-link'
  location: 'global'
  tags: tags
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    registrationEnabled: false
  }
}

resource stTableDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: stTableDnsZone
  name: '${baseName}-st-table-link'
  location: 'global'
  tags: tags
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    registrationEnabled: false
  }
}

// ===========================================================================
// KEY VAULT + SERVICE BUS PRIVATE ENDPOINTS
// ===========================================================================

resource kvPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: '${baseName}-pe-kv'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: vnet.properties.subnets[4].id // snet-private-endpoints
    }
    privateLinkServiceConnections: [
      {
        name: '${baseName}-plsc-kv'
        properties: {
          privateLinkServiceId: keyVault.id
          groupIds: [
            'vault'
          ]
        }
      }
    ]
  }
}

resource kvDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: kvPrivateEndpoint
  name: 'kv-dns-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'kv-config'
        properties: {
          privateDnsZoneId: kvDnsZone.id
        }
      }
    ]
  }
}

resource sbPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = if (serviceBusSku == 'Premium') {
  name: '${baseName}-pe-sb'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: vnet.properties.subnets[4].id // snet-private-endpoints
    }
    privateLinkServiceConnections: [
      {
        name: '${baseName}-plsc-sb'
        properties: {
          privateLinkServiceId: serviceBus.id
          groupIds: [
            'namespace'
          ]
        }
      }
    ]
  }
}

resource sbDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (serviceBusSku == 'Premium') {
  parent: sbPrivateEndpoint
  name: 'sb-dns-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'sb-config'
        properties: {
          privateDnsZoneId: sbDnsZone.id
        }
      }
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

// VNet + Subnet IDs
output vnetId string = vnet.id
output schedulerSubnetId string = vnet.properties.subnets[0].id
output orchestratorSubnetId string = vnet.properties.subnets[1].id
output adminApiSubnetId string = vnet.properties.subnets[2].id
output cloudWorkerSubnetId string = vnet.properties.subnets[3].id
output privateEndpointsSubnetId string = vnet.properties.subnets[4].id
output deploymentScriptsSubnetId string = vnet.properties.subnets[5].id

// Private DNS Zone IDs (consumed by component templates for their own PEs)
output sqlDnsZoneId string = sqlDnsZone.id
output kvDnsZoneId string = kvDnsZone.id
output sbDnsZoneId string = sbDnsZone.id
output stBlobDnsZoneId string = stBlobDnsZone.id
output stQueueDnsZoneId string = stQueueDnsZone.id
output stTableDnsZoneId string = stTableDnsZone.id
