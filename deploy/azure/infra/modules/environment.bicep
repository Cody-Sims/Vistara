metadata description = 'Container Apps managed environment wired to the Log Analytics workspace.'

@description('Azure region for the managed environment.')
param location string

@description('Tags applied to the managed environment.')
param tags object

@description('Name of the Container Apps managed environment.')
param name string

@description('Name of the Log Analytics workspace that receives console and system logs.')
param logAnalyticsWorkspaceName string

@description('Deploy the environment across availability zones. Evaluation deployments stay single-zone.')
param zoneRedundant bool = false

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2025-02-01' existing = {
  name: logAnalyticsWorkspaceName
}

resource managedEnvironment 'Microsoft.App/managedEnvironments@2026-01-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    zoneRedundant: zoneRedundant
    publicNetworkAccess: 'Enabled'
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsWorkspace.properties.customerId
        // Read at deployment time so the workspace key is never a template
        // parameter, an azd environment value, or a deployment output.
        sharedKey: logAnalyticsWorkspace.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
}

output resourceId string = managedEnvironment.id
output name string = managedEnvironment.name
output defaultDomain string = managedEnvironment.properties.defaultDomain
output staticIp string = managedEnvironment.properties.staticIp
