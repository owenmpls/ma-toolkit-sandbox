// Hybrid Worker infrastructure — Service Bus subscription, RBAC, and update storage
// Deploy per hybrid worker instance (pass unique workerId for each)

// Parameters
param baseName string
param location string = resourceGroup().location
param workerId string
param serviceBusNamespaceName string
param keyVaultName string
param servicePrincipalObjectId string  // Object ID of the hybrid worker's SP
param tags object = { component: 'hybrid-worker', project: 'ma-toolkit' }

// Optional: update storage (deploy once, shared by all hybrid workers)
param deployUpdateStorage bool = true
param updateStorageAccountName string = '${baseName}hwupdate'

// Existing resources
resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = { name: serviceBusNamespaceName }
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: keyVaultName }
resource jobsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' existing = {
  parent: serviceBus
  name: 'worker-jobs'
}

// Service Bus subscription with SQL filter (same pattern as cloud-worker)
resource workerSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: jobsTopic
  name: 'worker-${workerId}'
  properties: {
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
    defaultMessageTimeToLive: 'P7D'
  }
}

resource workerSubscriptionRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2022-10-01-preview' = {
  parent: workerSubscription
  name: 'WorkerIdFilter'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: { sqlExpression: 'WorkerId = \'${workerId}\'' }
  }
}

// RBAC: SP -> Service Bus Data Receiver
resource sbReceiverRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBus
  name: guid(serviceBus.id, servicePrincipalObjectId, '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
    principalId: servicePrincipalObjectId
    principalType: 'ServicePrincipal'
  }
}

// RBAC: SP -> Service Bus Data Sender
resource sbSenderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBus
  name: guid(serviceBus.id, servicePrincipalObjectId, '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
    principalId: servicePrincipalObjectId
    principalType: 'ServicePrincipal'
  }
}

// RBAC: SP -> Key Vault Secrets User
resource kvSecretsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, servicePrincipalObjectId, '4633458b-17de-408a-b874-0445c86b69e6')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: servicePrincipalObjectId
    principalType: 'ServicePrincipal'
  }
}

// Update storage account (shared, deploy once)
resource updateStorage 'Microsoft.Storage/storageAccounts@2023-01-01' = if (deployUpdateStorage) {
  name: updateStorageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: { allowBlobPublicAccess: false }
}

resource updateContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = if (deployUpdateStorage) {
  name: '${updateStorage.name}/default/hybrid-worker'
  properties: { publicAccess: 'None' }
}

// RBAC: SP -> Storage Blob Data Reader (for update downloads)
resource storageReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployUpdateStorage) {
  scope: updateStorage
  name: guid(updateStorage.id, servicePrincipalObjectId, '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
    principalId: servicePrincipalObjectId
    principalType: 'ServicePrincipal'
  }
}

// Outputs
output subscriptionName string = workerSubscription.name
output updateStorageAccountName string = deployUpdateStorage ? updateStorage.name : ''
