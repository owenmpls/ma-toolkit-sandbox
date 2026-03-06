@description('Name of the Databricks workspace')
param workspaceName string

@description('Location for resources')
param location string = resourceGroup().location

@description('VNet ID for VNet injection (optional)')
param vnetId string = ''

@description('Public subnet name for VNet injection (optional)')
param publicSubnetName string = ''

@description('Private subnet name for VNet injection (optional)')
param privateSubnetName string = ''

@description('Tags for resources')
param tags object = {}

resource workspace 'Microsoft.Databricks/workspaces@2024-05-01' = {
  name: workspaceName
  location: location
  tags: tags
  sku: {
    name: 'premium'  // Required for Unity Catalog
  }
  properties: {
    managedResourceGroupId: subscriptionResourceId('Microsoft.Resources/resourceGroups', 'databricks-rg-${workspaceName}')
    parameters: !empty(vnetId) && !empty(publicSubnetName) && !empty(privateSubnetName)
      ? {
          customVirtualNetworkId: { value: vnetId }
          customPublicSubnetName: { value: publicSubnetName }
          customPrivateSubnetName: { value: privateSubnetName }
          enableNoPublicIp: { value: true }
        }
      : {}
  }
}

output workspaceId string = workspace.id
output workspaceUrl string = workspace.properties.workspaceUrl
output workspaceName string = workspace.name
