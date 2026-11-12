targetScope = 'subscription'

@description('Approved region for the Windows B1 pilot.')
@allowed(['eastasia'])
param location string = 'eastasia'

@description('New resource group; deployment must not modify an existing workload.')
param resourceGroupName string = 'rg-purview-function-test'

@description('The single approved workstation IPv4 /32 for Function, SCM and Vault access.')
@allowed(['112.118.75.90/32'])
param workstationCidr string

@description('Tenant containing the approved reader application and certificate.')
param tenantId string

@description('Existing certificate-authenticated SharePoint/Purview application client ID.')
param readerClientId string

@description('Fixed, approved Purview user context. This is not an Entra delegation restriction.')
param delegatedUserEmail string

@description('Approved SharePoint document-library drive ID.')
param sharePointDriveId string

@description('Exact approved SharePoint download hostname.')
param sharePointHostName string

@description('Deploying user object ID, granted certificate administration only on the new Vault.')
param deployerObjectId string

@description('Existing certificate lookup thumbprint only. Never provide a PFX or password.')
@minLength(40)
@maxLength(40)
param certificateThumbprint string

@description('Keep true for a complete deployment. False is bootstrap-only: the Function is not ready until its runtime roles are validated and deployed using its actual principal ID.')
param deployRuntimeRoles bool = true

var tags = {
  environment: 'test'
  workload: 'purview-reader'
  session: 'ee9a'
}

module group 'br/public:avm/res/resources/resource-group:0.4.4' = {
  name: 'purview-resource-group'
  params: {
    name: resourceGroupName
    location: location
    tags: tags
    enableTelemetry: false
  }
}

module pilot './modules/pilot.bicep' = {
  name: 'purview-pilot'
  scope: resourceGroup(resourceGroupName)
  params: {
    location: location
    tags: tags
    workstationCidr: workstationCidr
    tenantId: tenantId
    readerClientId: readerClientId
    delegatedUserEmail: delegatedUserEmail
    sharePointDriveId: sharePointDriveId
    sharePointHostName: sharePointHostName
    deployerObjectId: deployerObjectId
    certificateThumbprint: certificateThumbprint
  }
  dependsOn: [group]
}

// Runtime role GUIDs require the real principal, which exists only after the pilot.
module roles './modules/roles.bicep' = if (deployRuntimeRoles) {
  name: 'runtime-data-roles'
  scope: resourceGroup(resourceGroupName)
  params: {
    principalId: pilot.outputs.runtimePrincipalId
    hostStorageName: pilot.outputs.hostStorageName
    outputStorageName: pilot.outputs.outputStorageName
    outputContainerName: pilot.outputs.outputContainerName
    insightsName: pilot.outputs.insightsName
  }
}

output functionAppName string = pilot.outputs.functionAppName
output functionEndpoint string = pilot.outputs.functionEndpoint
output outputBlobServiceUri string = pilot.outputs.outputBlobServiceUri
output vaultName string = pilot.outputs.vaultName
