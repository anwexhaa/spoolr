targetScope = 'resourceGroup'

@description('Short name used as a prefix for every resource in this deployment.')
@minLength(3)
@maxLength(12)
param name string = 'spoolr'

@description('Location for all resources. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Environment name, used in resource names and tags.')
@allowed(['dev', 'test', 'prod'])
param environmentName string = 'dev'

@description('Container image to deploy, including registry and tag.')
param containerImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

@description('Object id of the Entra ID group or user that administers the SQL server.')
param sqlAdminObjectId string

@description('Display name of the SQL administrator principal.')
param sqlAdminLogin string

var suffix = uniqueString(resourceGroup().id, name, environmentName)
var tags = {
  application: 'spoolr'
  environment: environmentName
}

// Identity comes first: everything else grants access to it, and creating it up front
// avoids a second deployment pass to wire the role assignments.
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-${environmentName}-id'
  location: location
  tags: tags
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${name}-${environmentName}-logs'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${name}-${environmentName}-insights'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: '${name}-${environmentName}-sb-${suffix}'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    // The service authenticates with managed identity, so the shared access keys that
    // ship with a new namespace are of no use to anything except an attacker.
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
  }
}

resource jobEventsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBus
  name: 'job-events'
  properties: {
    // Lifecycle events are only useful while they are current. A consumer that has been
    // down for a day wants the current state, not yesterday's backlog.
    defaultMessageTimeToLive: 'P1D'
    enablePartitioning: false

    // The publisher sends a deterministic MessageId, so a retried publish is collapsed
    // here rather than delivered twice.
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-05-01-preview' = {
  name: '${name}-${environmentName}-sql-${suffix}'
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'

    // No SQL login and password anywhere. Access is Entra ID only, which means there is
    // no database credential to leak or rotate.
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent: sqlServer
  name: '${name}-db'
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 1
  }
  properties: {
    // Serverless, so a development environment costs nothing while nobody is printing.
    autoPauseDelay: 60
    minCapacity: json('0.5')
    zoneRedundant: false
  }
}

resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-05-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${name}-${environmentName}-env'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${name}-${environmentName}-app'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerEnvironment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
    }
    template: {
      containers: [
        {
          name: 'spoolr'
          image: containerImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'AZURE_CLIENT_ID'
              value: identity.properties.clientId
            }
            {
              name: 'Database__Provider'
              value: 'SqlServer'
            }
            {
              name: 'Database__ConnectionString'
              value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase.name};Authentication=Active Directory Managed Identity;User Id=${identity.properties.clientId};Encrypt=True;'
            }
            {
              name: 'ServiceBus__FullyQualifiedNamespace'
              value: '${serviceBus.name}.servicebus.windows.net'
            }
            {
              name: 'ServiceBus__TopicName'
              value: jobEventsTopic.name
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: insights.properties.ConnectionString
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              periodSeconds: 15
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              periodSeconds: 10
            }
          ]
        }
      ]
      scale: {
        // The stalled-job sweep is safe on every replica, so scaling out does not need a
        // designated leader.
        minReplicas: 1
        maxReplicas: 5
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
}

// Azure Service Bus Data Sender. The service publishes job events and never consumes them,
// so it is not granted receive.
var serviceBusDataSenderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'

resource serviceBusRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: jobEventsTopic
  name: guid(jobEventsTopic.id, identity.id, serviceBusDataSenderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      serviceBusDataSenderRoleId
    )
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output applicationUrl string = 'https://${containerApp.properties.configuration.ingress.fqdn}'
output identityClientId string = identity.properties.clientId
output sqlServerName string = sqlServer.properties.fullyQualifiedDomainName
output serviceBusNamespace string = '${serviceBus.name}.servicebus.windows.net'
