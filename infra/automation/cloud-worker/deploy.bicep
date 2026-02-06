// Cloud Worker - Azure Infrastructure
// Deploys: Container App Environment, Container App, Service Bus, Key Vault, Application Insights, Container Registry

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

// --- Log Analytics Workspace ---
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${baseName}-logs'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

// --- Application Insights ---
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-ai'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// --- Service Bus Namespace ---
resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: '${baseName}-sb'
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
}

// --- Service Bus Topics ---
resource jobsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBus
  name: 'worker-jobs'
  properties: {
    maxSizeInMegabytes: 1024
    defaultMessageTimeToLive: 'P1D'
  }
}

resource resultsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBus
  name: 'worker-results'
  properties: {
    maxSizeInMegabytes: 1024
    defaultMessageTimeToLive: 'P1D'
  }
}

// --- Worker Subscription with SQL Filter ---
resource workerSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: jobsTopic
  name: 'worker-${workerId}'
  properties: {
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
    defaultMessageTimeToLive: 'P1D'
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

// --- Orchestrator Subscription (all results) ---
resource orchestratorSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: resultsTopic
  name: 'orchestrator'
  properties: {
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
    defaultMessageTimeToLive: 'P1D'
  }
}

// --- Key Vault ---
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${baseName}-kv'
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

// --- Container Registry ---
resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: '${baseName}acr'
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: true
  }
}

// --- Container App Environment ---
resource containerAppEnv 'Microsoft.App/managedEnvironments@2023-05-01' = {
  name: '${baseName}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

// --- Service Bus connection string secret (used by KEDA scaler for auth) ---
// The scaler needs a connection string to check subscription message count.
// We use a shared access policy with Manage rights on the worker-jobs topic.
resource jobsTopicAuthRule 'Microsoft.ServiceBus/namespaces/topics/authorizationRules@2022-10-01-preview' = {
  parent: jobsTopic
  name: 'keda-monitor'
  properties: {
    rights: [
      'Manage'
      'Listen'
      'Send'
    ]
  }
}

// --- Container App (Worker) ---
resource workerApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: '${baseName}-worker-${workerId}'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      registries: [
        {
          server: containerRegistry.properties.loginServer
          username: containerRegistry.listCredentials().username
          passwordSecretRef: 'acr-password'
        }
      ]
      secrets: [
        {
          name: 'acr-password'
          value: containerRegistry.listCredentials().passwords[0].value
        }
        {
          name: 'sb-connection-string'
          value: jobsTopicAuthRule.listKeys().primaryConnectionString
        }
      ]
      // No ingress needed - this is a background worker
    }
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
            { name: 'APP_SECRET_NAME', value: 'worker-app-secret' }
            { name: 'APPINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
            { name: 'IDLE_TIMEOUT_SECONDS', value: string(idleTimeoutSeconds) }
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
output serviceBusNamespace string = '${serviceBus.name}.servicebus.windows.net'
output keyVaultName string = keyVault.name
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output containerRegistryLoginServer string = containerRegistry.properties.loginServer
output workerPrincipalId string = workerApp.identity.principalId
