metadata description = 'Log Analytics workspace that receives Container Apps console and system logs.'

@description('Azure region for the workspace.')
param location string

@description('Tags applied to the workspace.')
param tags object

@description('Name of the Log Analytics workspace.')
@minLength(4)
@maxLength(63)
param name string

@description('Number of days log data is retained.')
@minValue(30)
@maxValue(730)
param retentionInDays int = 30

@description('Daily ingestion cap in GB. Use -1 for no cap.')
param dailyQuotaGb int = 1

resource workspace 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    workspaceCapping: {
      dailyQuotaGb: dailyQuotaGb
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
      disableLocalAuth: false
    }
  }
}

output resourceId string = workspace.id
output name string = workspace.name
output customerId string = workspace.properties.customerId
