metadata description = 'Externally reachable API container app with explicit HTTP startup, readiness, and liveness probes.'

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

@description('Digest-pinned API image.')
param image string

@description('Container Apps secrets. Key Vault references only: no literal secret value is ever passed.')
param secrets array = []

@description('Private registry configuration. Empty for public GHCR images.')
param registries array = []

@description('Plain-text environment variables and secret references for the container.')
param environmentVariables array

@description('Lowest replica count the app scales to.')
@minValue(0)
@maxValue(30)
param minReplicas int = 1

@description('Highest replica count the app scales to.')
@minValue(1)
@maxValue(30)
param maxReplicas int = 2

@description('Concurrent requests per replica before the HTTP scaler adds a replica.')
param concurrentRequestsPerReplica int = 50

@description('Container port that serves HTTP traffic.')
param targetPort int = 8080

@description('Exact host the ingress serves. Probes send it as an explicit Host header because the platform otherwise probes the replica address, which host filtering rejects. It must be one of Security:Hosts:AllowedHosts.')
param ingressHost string

@description('Optional custom hostname bound to the ingress.')
param customDomainName string = ''

@description('Resource ID of the managed environment certificate for the custom hostname.')
param customDomainCertificateId string = ''

@description('vCPU allocated to the container.')
param cpu string = '0.5'

@description('Memory allocated to the container.')
param memory string = '1Gi'

var customDomains = empty(customDomainName)
  ? []
  : [
      {
        name: customDomainName
        certificateId: customDomainCertificateId
        bindingType: 'SniEnabled'
      }
    ]

// Container Apps probes the replica by address, so the request arrives with a
// Host header the API's host filtering does not allow and every probe would be
// answered with 400 before the health endpoint ran. Sending the ingress host
// explicitly keeps the probe on the same allow-listed host as real traffic.
var probeHostHeaders = [
  {
    name: 'Host'
    value: ingressHost
  }
]

resource api 'Microsoft.App/containerApps@2026-01-01' = {
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
      ingress: {
        external: true
        targetPort: targetPort
        transport: 'auto'
        allowInsecure: false
        clientCertificateMode: 'ignore'
        customDomains: customDomains
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
    }
    template: {
      containers: [
        {
          name: 'api'
          image: image
          env: environmentVariables
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          probes: [
            // Default Container Apps probes are TCP and report healthy while
            // the app returns 500s, so all three are declared explicitly as
            // HTTP. Thresholds match the platform's own documented defaults
            // for startup (long) and readiness (long) budgets.
            {
              type: 'Startup'
              httpGet: {
                path: '/health/startup'
                port: targetPort
                scheme: 'HTTP'
                httpHeaders: probeHostHeaders
              }
              initialDelaySeconds: 3
              periodSeconds: 3
              timeoutSeconds: 3
              failureThreshold: 60
              successThreshold: 1
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: targetPort
                scheme: 'HTTP'
                httpHeaders: probeHostHeaders
              }
              initialDelaySeconds: 3
              periodSeconds: 5
              timeoutSeconds: 5
              failureThreshold: 48
              successThreshold: 1
            }
            // Liveness answers ahead of rate limiting and authentication and
            // never touches PostgreSQL: a transient database blip must not
            // restart every replica.
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: targetPort
                scheme: 'HTTP'
                httpHeaders: probeHostHeaders
              }
              periodSeconds: 10
              timeoutSeconds: 1
              failureThreshold: 3
              successThreshold: 1
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-concurrency'
            http: {
              metadata: {
                concurrentRequests: string(concurrentRequestsPerReplica)
              }
            }
          }
        ]
      }
    }
  }
}

output resourceId string = api.id
output name string = api.name
output fqdn string = api.properties.configuration.ingress.fqdn
