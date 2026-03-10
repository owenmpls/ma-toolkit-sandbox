@description('Name of the Network Security Perimeter')
param nspName string

@description('Location for resources')
param location string = resourceGroup().location

@description('Storage account resource ID to associate with the NSP')
param storageAccountId string

@description('Access mode for the storage association (Learning or Enforced)')
@allowed(['Learning', 'Enforced'])
param accessMode string = 'Learning'

@description('Tags for resources')
param tags object = {}

resource nsp 'Microsoft.Network/networkSecurityPerimeters@2023-08-01-preview' = {
  name: nspName
  location: location
  tags: tags
}

resource profile 'Microsoft.Network/networkSecurityPerimeters/profiles@2023-08-01-preview' = {
  parent: nsp
  name: 'default-profile'
  location: location
}

resource databricksServerlessRule 'Microsoft.Network/networkSecurityPerimeters/profiles/accessRules@2023-08-01-preview' = {
  parent: profile
  name: 'allow-databricks-serverless'
  properties: {
    direction: 'Inbound'
    serviceTags: ['AzureDatabricksServerless']
  }
}

resource storageAssociation 'Microsoft.Network/networkSecurityPerimeters/resourceAssociations@2023-08-01-preview' = {
  parent: nsp
  name: 'storage-association'
  properties: {
    privateLinkResource: {
      id: storageAccountId
    }
    profile: {
      id: profile.id
    }
    accessMode: accessMode
  }
}
