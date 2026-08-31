metadata description = 'Background worker container app with no ingress and therefore no HTTP probes.'

@description('Azure region for the container app.')
param location string

@description('Tags applied to the container app. Must include azd-service-name so azd deploy can find it.')
param tags object

@description('Container app name.')
@minLength(2)
@maxLength(32)
param name string

@description('Resource ID of the Container Apps managed environment.')
param managedEnvironmentResourceId string

@description('Resource ID of the user-assigned identity the app runs as.')
param userAssignedIdentityResourceId string

@description('Digest-pinned worker image.')
param image string

@description('Container Apps secrets. Key Vault references only: no literal secret value is ever passed.')
param secrets array = []

@description('Private registry configuration. Empty for public GHCR images.')
param registries array = []

@description('Plain-text environment variables and secret references for the container.')
param environmentVariables array

@description('Lowest replica count the worker scales to.')
@minValue(0)
@maxValue(30)
param minReplicas int = 1

@description('Highest replica count the worker scales to.')
@minValue(1)
@maxValue(30)
param maxReplicas int = 1

@description('vCPU allocated to the container.')
param cpu string = '0.5'

@description('Memory allocated to the container.')
param memory string = '1Gi'

resource worker 'Microsoft.App/containerApps@2026-01-01' = {
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
      activeRevisionsMode: 'Single'
      maxInactiveRevisions: 2
      secrets: secrets
      registries: registries
    }
    template: {
      containers: [
        {
          // The worker builds a plain generic host with no HTTP listener, so a
          // probe of any kind would fail against a port nothing is bound to.
          name: 'worker'
          image: image
          env: environmentVariables
          resources: {
            cpu: json(cpu)
            memory: memory
          }
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

output resourceId string = worker.id
output name string = worker.name
