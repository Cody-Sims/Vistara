metadata description = 'Manually triggered Container Apps job that applies EF Core migrations before the API and worker start serving.'

@description('Azure region for the job.')
param location string

@description('Tags applied to the job.')
param tags object

@description('Container Apps job name.')
@minLength(2)
@maxLength(32)
param name string

@description('Resource ID of the Container Apps managed environment.')
param managedEnvironmentResourceId string

@description('Resource ID of the user-assigned identity the job runs as.')
param userAssignedIdentityResourceId string

@description('Digest-pinned migration image.')
param image string

@description('Container Apps secrets. Key Vault references only: no literal secret value is ever passed.')
param secrets array = []

@description('Private registry configuration. Empty for public GHCR images.')
param registries array = []

@description('Plain-text environment variables for the migration container.')
param environmentVariables array

@description('Seconds a single replica may run before the platform stops it.')
@minValue(60)
@maxValue(3600)
param replicaTimeout int = 900

@description('vCPU allocated to the container.')
param cpu string = '0.5'

@description('Memory allocated to the container.')
param memory string = '1Gi'

resource migrationJob 'Microsoft.App/jobs@2026-01-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${userAssignedIdentityResourceId}': {}
    }
  }
  properties: {
    environmentId: managedEnvironmentResourceId
    workloadProfileName: 'Consumption'
    configuration: {
      // azd has no containerappjob service target, so the digest arrives as a
      // template parameter and the job is started by the postprovision hook.
      triggerType: 'Manual'
      replicaTimeout: replicaTimeout
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      secrets: secrets
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: image
          env: environmentVariables
          resources: {
            cpu: json(cpu)
            memory: memory
          }
        }
      ]
    }
  }
}

output resourceId string = migrationJob.id
output name string = migrationJob.name
