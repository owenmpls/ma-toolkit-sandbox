// Cloud Worker - Azure Infrastructure
// Deploys: Container App Environment, Container App, Application Insights
// Requires: shared infrastructure (Service Bus, Key Vault, Log Analytics, ACR) deployed first

@description('Base name for all resources')
param baseName string

@description('Azure region for deployment')
param location string = resourceGroup().location

@description('Target tenant ID for the worker to connect to')
param targetTenantId string

@description('App registration client ID for the worker')
param appId string

@description('Worker ID for this container instance')
param workerId string = 'worker-01'

@description('Maximum parallel runspaces per worker container')
@minValue(1)
@maxValue(20)
param maxParallelism int = 4

@description('Container image name (must be pushed to ACR before deployment)')
param containerImage string = '${baseName}acr.azurecr.io/cloud-worker:latest'

@description('Container CPU cores')
param containerCpu string = '1.0'

@description('Container memory in Gi')
param containerMemory string = '2.0Gi'

@description('Idle timeout in seconds before the worker shuts down (0 to disable). Works with scale-to-zero so ACA can terminate the instance after the worker exits.')
@minValue(0)
param idleTimeoutSeconds int = 300

@description('Grace period in seconds to wait for active jobs to complete during shutdown.')
@minValue(0)
param shutdownGraceSeconds int = 30

@description('Subnet resource ID for the Container App Environment. Leave empty to skip VNet integration.')
param cloudWorkerSubnetId string = ''

@description('Name of the existing Service Bus namespace (from shared deployment).')
param serviceBusNamespaceName string

@description('Name of the existing Key Vault (from shared deployment).')
param keyVaultName string

@description('Name of the existing Container Registry (from shared deployment).')
param containerRegistryName string

@description('Name of the existing Log Analytics workspace (from shared deployment).')
param logAnalyticsWorkspaceName string

@description('Tags to apply to all resources')
param tags object = {
  component: 'cloud-worker'
  project: 'ma-toolkit'
}

// Built-in role definition IDs
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d' // AcrPull

// ---------------------------------------------------------------------------
// Existing resources (from shared deployment)
// ---------------------------------------------------------------------------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' existing = {
  name: logAnalyticsWorkspaceName
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

resource jobsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' existing = {
  parent: serviceBus
  name: 'worker-jobs'
}

// --- Application Insights ---
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-ai'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// --- Container App Environment ---
resource containerAppEnv 'Microsoft.App/managedEnvironments@2023-05-01' = {
  name: '${baseName}-env'
  location: location
  tags: tags
  properties: {
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    vnetConfiguration: !empty(cloudWorkerSubnetId) ? {
      infrastructureSubnetId: cloudWorkerSubnetId
      internal: true
    } : null
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

// --- Worker Subscription with SQL Filter ---
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
    sqlFilter: {
      sqlExpression: 'WorkerId = \'${workerId}\''
    }
  }
}

// --- Service Bus connection string secret (used by KEDA scaler for auth) ---
// KEDA requires a connection string — this cannot use managed identity today.
// The auth rule is scoped to Listen only (KEDA only needs to read message count).
resource jobsTopicAuthRule 'Microsoft.ServiceBus/namespaces/topics/authorizationRules@2022-10-01-preview' = {
  parent: jobsTopic
  name: 'keda-monitor'
  properties: {
    rights: [
      'Listen'
    ]
  }
}

// Store SB connection string in Key Vault for the KEDA scaler
resource sbConnectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'keda-sb-connection-string'
  properties: {
    value: jobsTopicAuthRule.listKeys().primaryConnectionString
  }
}

// --- Container App (Worker) ---
resource workerApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: '${baseName}-worker-${workerId}'
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      registries: [
        {
          server: containerRegistry.properties.loginServer
          identity: 'system'
        }
      ]
      secrets: [
        {
          name: 'sb-connection-string'
          keyVaultUrl: sbConnectionStringSecret.properties.secretUri
          identity: 'system'
        }
      ]
      // No ingress needed - this is a background worker
    }
    workloadProfileName: 'Consumption'
    template: {
      containers: [
        {
          name: 'cloud-worker'
          image: containerImage
          resources: {
            cpu: json(containerCpu)
            memory: containerMemory
          }
          env: [
            { name: 'WORKER_ID', value: workerId }
            { name: 'MAX_PARALLELISM', value: string(maxParallelism) }
            { name: 'SERVICE_BUS_NAMESPACE', value: '${serviceBus.name}.servicebus.windows.net' }
            { name: 'JOBS_TOPIC_NAME', value: 'worker-jobs' }
            { name: 'RESULTS_TOPIC_NAME', value: 'worker-results' }
            { name: 'KEY_VAULT_NAME', value: keyVault.name }
            { name: 'TARGET_TENANT_ID', value: targetTenantId }
            { name: 'APP_ID', value: appId }
            { name: 'CERT_NAME', value: 'worker-app-cert' }
            { name: 'APPINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
            { name: 'IDLE_TIMEOUT_SECONDS', value: string(idleTimeoutSeconds) }
            { name: 'SHUTDOWN_GRACE_SECONDS', value: string(shutdownGraceSeconds) }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: [
          {
            name: 'service-bus-jobs'
            custom: {
              type: 'azure-servicebus'
              metadata: {
                topicName: 'worker-jobs'
                subscriptionName: 'worker-${workerId}'
                messageCount: '1'
              }
              auth: [
                {
                  secretRef: 'sb-connection-string'
                  triggerParameter: 'connection'
                }
              ]
            }
          }
        ]
      }
    }
  }
}

// --- Role Assignments ---

// Worker managed identity -> AcrPull on Container Registry
resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: containerRegistry
  name: guid(containerRegistry.id, workerApp.id, acrPullRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: workerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Worker managed identity -> Key Vault Secrets User
resource kvSecretsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, workerApp.id, '4633458b-17de-408a-b874-0445c86b69e6')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: workerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Worker managed identity -> Service Bus Data Receiver (worker-jobs topic)
resource sbDataReceiverRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBus
  name: guid(serviceBus.id, workerApp.id, '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
    principalId: workerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Worker managed identity -> Service Bus Data Sender (worker-results topic)
resource sbDataSenderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBus
  name: guid(serviceBus.id, workerApp.id, '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
    principalId: workerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- Outputs ---
output containerAppName string = workerApp.name
output containerAppFqdn string = workerApp.properties.configuration.?ingress.?fqdn ?? 'N/A (no ingress)'
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output workerPrincipalId string = workerApp.identity.principalId
