// Hybrid Worker shared infrastructure — update storage account
// Deploy once; shared by all hybrid worker instances.

param baseName string
param location string = resourceGroup().location
param cicdSpObjectId string = ''
param deploymentScriptsSubnetId string = ''
param tags object = { component: 'hybrid-worker', project: 'ma-toolkit' }

// Well-known role definition IDs
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var storageFileDataPrivContributorRoleId = '69566ab7-960f-475b-8e7c-b3118f30c6bd'

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
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      virtualNetworkRules: !empty(deploymentScriptsSubnetId) ? [
        {
          id: deploymentScriptsSubnetId
          action: 'Allow'
        }
      ] : []
    }
  }
}

resource updateContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: '${updateStorage.name}/default/hybrid-worker'
  properties: { publicAccess: 'None' }
}

// RBAC: CI/CD SP -> Storage Blob Data Contributor (for uploading update packages)
resource storageCicdRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(cicdSpObjectId)) {
  scope: updateStorage
  name: guid(updateStorage.id, cicdSpObjectId, storageBlobDataContributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: cicdSpObjectId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------------------
// Deployment Script Infrastructure — managed identity + script storage
// Used by upload.bicep (deployed via deploy-apps.yml) to upload packages
// from inside the VNet via ACI.
// ---------------------------------------------------------------------------

// Managed identity for the deployment script
resource uploadManagedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (!empty(deploymentScriptsSubnetId)) {
  name: 'id-hw-upload-${baseName}'
  location: location
  tags: tags
}

// Script storage account — dedicated runtime storage for the deployment script.
// Must use Deny + VNet rule for the ACI subnet, allowSharedKeyAccess, and
// Storage File Data Privileged Contributor RBAC for the managed identity.
// See: https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/deployment-script-template#access-private-virtual-network
resource scriptStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = if (!empty(deploymentScriptsSubnetId)) {
  name: take('sthwds${replace(baseName, '-', '')}${uniqueString(resourceGroup().id)}', 24)
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      virtualNetworkRules: [
        {
          id: deploymentScriptsSubnetId
          action: 'Allow'
        }
      ]
      ipRules: []
    }
  }
}

// Storage File Data Privileged Contributor on script storage for the deployment script identity
resource scriptStorageFileContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deploymentScriptsSubnetId)) {
  scope: scriptStorage
  name: guid(scriptStorage.id, uploadManagedIdentity.id, storageFileDataPrivContributorRoleId)
  properties: {
    principalId: uploadManagedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageFileDataPrivContributorRoleId)
  }
}

// Storage Blob Data Contributor on update storage for the deployment script identity
resource uploadStorageBlobContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deploymentScriptsSubnetId)) {
  scope: updateStorage
  name: guid(updateStorage.id, uploadManagedIdentity.id, storageBlobDataContributorRoleId)
  properties: {
    principalId: uploadManagedIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
  }
}

// Outputs
output updateStorageAccountName string = updateStorage.name
output uploadManagedIdentityId string = !empty(deploymentScriptsSubnetId) ? uploadManagedIdentity.id : ''
output uploadManagedIdentityClientId string = !empty(deploymentScriptsSubnetId) ? uploadManagedIdentity.properties.clientId : ''
output uploadScriptStorageAccountName string = !empty(deploymentScriptsSubnetId) ? scriptStorage.name : ''
