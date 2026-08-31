metadata description = 'Resource-group cost budget with actual and forecast alerts for the evaluation deployment.'

@description('Budget name.')
param name string

@description('Monthly budget amount in the billing currency.')
@minValue(1)
param amount int

@description('First day of the month the budget starts tracking, formatted yyyy-MM-01.')
param startDate string

@description('Email addresses that receive budget alerts. Resource-group owners are always notified.')
param contactEmails array = []

@description('Directory roles on the resource group that receive budget alerts.')
param contactRoles array = [
  'Owner'
]

var contacts = empty(contactEmails)
  ? {
      contactRoles: contactRoles
    }
  : {
      contactEmails: contactEmails
      contactRoles: contactRoles
    }

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: name
  properties: {
    category: 'Cost'
    amount: amount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: startDate
    }
    notifications: {
      ActualGreaterThan50Percent: union(contacts, {
        enabled: true
        operator: 'GreaterThan'
        threshold: 50
        thresholdType: 'Actual'
      })
      ActualGreaterThan90Percent: union(contacts, {
        enabled: true
        operator: 'GreaterThan'
        threshold: 90
        thresholdType: 'Actual'
      })
      ForecastedGreaterThan100Percent: union(contacts, {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Forecasted'
      })
    }
  }
}

output resourceId string = budget.id
output name string = budget.name
