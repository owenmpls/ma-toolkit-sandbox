// ---------------------------------------------------------------------------
// Shared Network — VNet, Subnets, Private Endpoints, Private DNS Zones
// ---------------------------------------------------------------------------
// Creates a virtual network with dedicated subnets for each automation
// component, plus private endpoints and DNS zones for SQL, Key Vault,
// and Service Bus.
// ---------------------------------------------------------------------------

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Base name prefix for resource naming.')
param baseName string = 'matoolkit'

@description('Name of the existing SQL Server.')
param sqlServerName string

@description('Name of the existing Key Vault.')
param keyVaultName string

@description('Name of the existing Service Bus namespace.')
param serviceBusNamespaceName string

@description('Names of the storage accounts to create private endpoints for.')
param storageAccountNames array = []

// ---------------------------------------------------------------------------
// Existing resource references
// ---------------------------------------------------------------------------

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' existing = {
  name: sqlServerName
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

// ---------------------------------------------------------------------------
// Virtual Network
// ---------------------------------------------------------------------------

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: '${baseName}-vnet'
  location: location
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
          delegations: [
            {
              name: 'delegation-web'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
        }
      }
      {
        name: 'snet-orchestrator'
        properties: {
          addressPrefix: '10.0.2.0/24'
          delegations: [
            {
              name: 'delegation-web'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
        }
      }
      {
        name: 'snet-admin-api'
        properties: {
          addressPrefix: '10.0.3.0/24'
          delegations: [
            {
              name: 'delegation-web'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
        }
      }
      {
        name: 'snet-cloud-worker'
        properties: {
          addressPrefix: '10.0.4.0/23'
        }
      }
      {
        name: 'snet-private-endpoints'
        properties: {
          addressPrefix: '10.0.10.0/24'
        }
      }
    ]
  }
}

// ---------------------------------------------------------------------------
// Private DNS Zones
// ---------------------------------------------------------------------------

resource sqlDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.database.windows.net'
  location: 'global'
}

resource kvDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.vaultcore.azure.net'
  location: 'global'
}

resource sbDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.servicebus.windows.net'
  location: 'global'
}

resource stDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.blob.${environment().suffixes.storage}'
  location: 'global'
}

// ---------------------------------------------------------------------------
// VNet links for DNS zones
// ---------------------------------------------------------------------------

resource sqlDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: sqlDnsZone
  name: '${baseName}-sql-link'
  location: 'global'
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
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    registrationEnabled: false
  }
}

resource stDnsZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: stDnsZone
  name: '${baseName}-st-link'
  location: 'global'
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    registrationEnabled: false
  }
}

// ---------------------------------------------------------------------------
// Private Endpoints
// ---------------------------------------------------------------------------

resource sqlPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: '${baseName}-pe-sql'
  location: location
  properties: {
    subnet: {
      id: vnet.properties.subnets[4].id // snet-private-endpoints
    }
    privateLinkServiceConnections: [
      {
        name: '${baseName}-plsc-sql'
        properties: {
          privateLinkServiceId: sqlServer.id
          groupIds: [
            'sqlServer'
          ]
        }
      }
    ]
  }
}

resource kvPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: '${baseName}-pe-kv'
  location: location
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

resource sbPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: '${baseName}-pe-sb'
  location: location
  properties: {
    subnet: {
      id: vnet.properties.subnets[4].id // snet-private-endpoints
    }
    privateLinkServiceConnections: [
      {
        name: '${baseName}-plsc-sb'
        properties: {
          privateLinkServiceId: serviceBusNamespace.id
          groupIds: [
            'namespace'
          ]
        }
      }
    ]
  }
}

// ---------------------------------------------------------------------------
// Storage Account Private Endpoints
// ---------------------------------------------------------------------------

resource stPrivateEndpoints 'Microsoft.Network/privateEndpoints@2023-11-01' = [for (name, i) in storageAccountNames: {
  name: '${baseName}-pe-st-${i}'
  location: location
  properties: {
    subnet: {
      id: vnet.properties.subnets[4].id // snet-private-endpoints
    }
    privateLinkServiceConnections: [
      {
        name: '${baseName}-plsc-st-${i}'
        properties: {
          privateLinkServiceId: resourceId('Microsoft.Storage/storageAccounts', name)
          groupIds: [
            'blob'
          ]
        }
      }
    ]
  }
}]

// ---------------------------------------------------------------------------
// DNS Zone Groups (auto-register A records)
// ---------------------------------------------------------------------------

resource sqlDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: sqlPrivateEndpoint
  name: 'sql-dns-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'sql-config'
        properties: {
          privateDnsZoneId: sqlDnsZone.id
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

resource sbDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
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

resource stDnsGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = [for (name, i) in storageAccountNames: {
  parent: stPrivateEndpoints[i]
  name: 'st-dns-group-${i}'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'st-config-${i}'
        properties: {
          privateDnsZoneId: stDnsZone.id
        }
      }
    ]
  }
}]

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

output vnetId string = vnet.id
output schedulerSubnetId string = vnet.properties.subnets[0].id
output orchestratorSubnetId string = vnet.properties.subnets[1].id
output adminApiSubnetId string = vnet.properties.subnets[2].id
output cloudWorkerSubnetId string = vnet.properties.subnets[3].id
output privateEndpointsSubnetId string = vnet.properties.subnets[4].id
