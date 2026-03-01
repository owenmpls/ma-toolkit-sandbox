@description('Name of the Databricks access connector')
param accessConnectorName string

@description('Location for resources')
param location string = resourceGroup().location

@description('Tags for resources')
param tags object = {}

resource accessConnector 'Microsoft.Databricks/accessConnectors@2024-05-01' = {
  name: accessConnectorName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {}
}

output accessConnectorId string = accessConnector.id
output principalId string = accessConnector.identity.principalId
