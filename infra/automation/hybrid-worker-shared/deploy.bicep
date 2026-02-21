// Hybrid Worker shared infrastructure — update storage account
// Deploy once; shared by all hybrid worker instances.

param baseName string
param location string = resourceGroup().location
param cicdSpObjectId string = ''
param tags object = { component: 'hybrid-worker', project: 'ma-toolkit' }

// Update storage account (shared by all hybrid workers)
resource updateStorage 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: '${baseName}hwupdate'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowBlobPublicAccess: false
    publicNetworkAccess: 'Enabled'
    allowSharedKeyAccess: false
  }
}

resource updateContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: '${updateStorage.name}/default/hybrid-worker'
  properties: { publicAccess: 'None' }
}

// RBAC: CI/CD SP -> Storage Blob Data Contributor (for uploading update packages)
resource storageCicdRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(cicdSpObjectId)) {
  scope: updateStorage
  name: guid(updateStorage.id, cicdSpObjectId, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: cicdSpObjectId
    principalType: 'ServicePrincipal'
  }
}

// Outputs
output updateStorageAccountName string = updateStorage.name
