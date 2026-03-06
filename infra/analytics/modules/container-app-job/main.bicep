@description('Name of the container app job')
param jobName string

@description('Location for resources')
param location string = resourceGroup().location

@description('Container App Environment ID')
param environmentId string

@description('Container image to run')
param containerImage string

@description('ACR login server')
param acrLoginServer string

@description('User-assigned managed identity ID for ACR pull')
param acrPullIdentityId string

@description('CPU cores for the job container')
param cpu string = '1.0'

@description('Memory for the job container')
param memory string = '2Gi'

@description('Environment variables for the container')
param envVars array = []

@description('Tags for resources')
param tags object = {}

resource job 'Microsoft.App/jobs@2025-01-01' = {
  name: jobName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned,UserAssigned'
    userAssignedIdentities: {
      '${acrPullIdentityId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 28800  // 8 hours max
      replicaRetryLimit: 0
      registries: [
        {
          server: acrLoginServer
          identity: acrPullIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'ingest'
          image: containerImage
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: envVars
        }
      ]
    }
  }
}

output jobId string = job.id
output jobName string = job.name
output systemAssignedPrincipalId string = job.identity.principalId
