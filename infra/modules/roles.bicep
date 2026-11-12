@description('Function system-assigned principal, resolved before this module starts.')
param principalId string

@description('Provisioned host/Durable account name.')
param hostStorageName string

@description('Provisioned private output account name.')
param outputStorageName string

@description('Provisioned private output container name.')
param outputContainerName string

@description('Provisioned Application Insights component name.')
param insightsName string

resource hostStorage 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: hostStorageName
}

resource outputStorage 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: outputStorageName
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' existing = {
  parent: outputStorage
  name: 'default'
}

resource outputContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' existing = {
  parent: blobService
  name: outputContainerName
}

resource insights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: insightsName
}

var hostRoleIds = [
  'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
]

resource hostRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for roleId in hostRoleIds: {
  name: guid(hostStorage.id, principalId, roleId)
  scope: hostStorage
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
  }
}]

resource outputRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(outputContainer.id, principalId, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: outputContainer
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}

resource telemetryRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(insights.id, principalId, '3913510d-42f4-4e42-8a64-420c390055eb')
  scope: insights
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
  }
}
