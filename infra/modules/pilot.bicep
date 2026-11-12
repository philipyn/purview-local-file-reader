@description('Approved deployment region.')
param location string

@description('Common pilot tags for all taggable resources.')
param tags object

@description('Single approved public IPv4 /32.')
param workstationCidr string

@description('Approved reader tenant ID.')
param tenantId string

@description('Existing reader client ID.')
param readerClientId string

@description('Fixed approved Purview user.')
param delegatedUserEmail string

@description('Approved SharePoint drive.')
param sharePointDriveId string

@description('Exact approved SharePoint download hostname.')
param sharePointHostName string

@description('Deploying user object ID.')
param deployerObjectId string

@description('Approved certificate lookup thumbprint, not a credential.')
param certificateThumbprint string

var hostStorageName = 'stpurviewhostdevee9a'
var outputStorageName = 'stpurviewoutdevee9a'
var outputContainerName = 'decrypted-output'
var functionAppName = 'func-purview-dev-ee9a'
var vaultName = 'kv-purview-dev-ee9a'
var insightsName = 'appi-purview-dev-ee9a'
var networkName = 'vnet-purview-dev-ee9a'
var nsgName = 'nsg-purview-dev-ee9a'
var planName = 'asp-purview-dev-ee9a'
var dnsZoneName = 'privatelink.blob.${environment().suffixes.storage}'
var outputBlobServiceUri = 'https://${outputStorageName}.blob.${environment().suffixes.storage}'
var integrationSubnetId = resourceId('Microsoft.Network/virtualNetworks/subnets', networkName, 'snet-functions')

module nsg 'br/public:avm/res/network/network-security-group:0.5.4' = {
  name: 'integration-nsg'
  params: {
    name: nsgName
    location: location
    tags: tags
    enableTelemetry: false
    securityRules: [
      {
        name: 'allow-runtime-output-https'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: '10.88.0.0/26'
          sourcePortRange: '*'
          destinationAddressPrefix: '10.88.0.64/27'
          destinationPortRange: '443'
        }
      }
      {
        name: 'deny-other-vnet-inbound'
        properties: {
          priority: 2000
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourceAddressPrefix: 'VirtualNetwork'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
      {
        name: 'deny-vnet-remote-admin'
        properties: {
          priority: 2000
          direction: 'Outbound'
          access: 'Deny'
          protocol: 'Tcp'
          sourceAddressPrefix: '*'
          sourcePortRange: '*'
          destinationAddressPrefix: 'VirtualNetwork'
          destinationPortRanges: ['22', '3389']
        }
      }
    ]
  }
}

module network 'br/public:avm/res/network/virtual-network:0.10.2' = {
  name: 'pilot-network'
  params: {
    name: networkName
    location: location
    tags: tags
    enableTelemetry: false
    addressPrefixes: ['10.88.0.0/24']
    subnets: [
      {
        name: 'snet-functions'
        addressPrefix: '10.88.0.0/26'
        delegation: 'Microsoft.Web/serverFarms'
        networkSecurityGroupResourceId: resourceId('Microsoft.Network/networkSecurityGroups', nsgName)
        serviceEndpoints: [
          'Microsoft.Storage'
          'Microsoft.KeyVault'
        ]
      }
      {
        name: 'snet-private-endpoints'
        addressPrefix: '10.88.0.64/27'
        networkSecurityGroupResourceId: resourceId('Microsoft.Network/networkSecurityGroups', nsgName)
        privateEndpointNetworkPolicies: 'NetworkSecurityGroupEnabled'
      }
    ]
  }
  dependsOn: [nsg]
}

module hostStorage 'br/public:avm/res/storage/storage-account:0.33.1' = {
  name: 'host-storage'
  params: {
    name: hostStorageName
    location: location
    tags: tags
    enableTelemetry: false
    kind: 'StorageV2'
    skuName: 'Standard_LRS'
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
      ipRules: []
      virtualNetworkRules: []
    }
    blobServices: {
      deleteRetentionPolicyEnabled: true
      deleteRetentionPolicyDays: 7
      containerDeleteRetentionPolicyEnabled: true
      containerDeleteRetentionPolicyDays: 7
    }
  }
  dependsOn: [network]
}

module outputStorage 'br/public:avm/res/storage/storage-account:0.33.1' = {
  name: 'output-storage'
  params: {
    name: outputStorageName
    location: location
    tags: tags
    enableTelemetry: false
    kind: 'StorageV2'
    skuName: 'Standard_LRS'
    accessTier: 'Hot'
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
      ipRules: []
      virtualNetworkRules: []
    }
    blobServices: {
      deleteRetentionPolicyEnabled: false
      containerDeleteRetentionPolicyEnabled: false
      isVersioningEnabled: false
      containers: [
        {
          name: outputContainerName
          publicAccess: 'None'
        }
      ]
    }
  }
}

module dns 'br/public:avm/res/network/private-dns-zone:0.8.1' = {
  name: 'output-dns'
  params: {
    name: dnsZoneName
    tags: tags
    enableTelemetry: false
    virtualNetworkLinks: [
      {
        name: 'link-purview-dev-ee9a'
        virtualNetworkResourceId: resourceId('Microsoft.Network/virtualNetworks', networkName)
        registrationEnabled: false
      }
    ]
  }
  dependsOn: [network]
}

module privateEndpoint 'br/public:avm/res/network/private-endpoint:0.12.1' = {
  name: 'output-private-endpoint'
  params: {
    name: 'pep-purview-output-dev-ee9a'
    location: location
    tags: tags
    enableTelemetry: false
    subnetResourceId: resourceId('Microsoft.Network/virtualNetworks/subnets', networkName, 'snet-private-endpoints')
    privateLinkServiceConnections: [
      {
        name: 'output-blob'
        properties: {
          privateLinkServiceId: resourceId('Microsoft.Storage/storageAccounts', outputStorageName)
          groupIds: ['blob']
        }
      }
    ]
    privateDnsZoneGroup: {
      name: 'default'
      privateDnsZoneGroupConfigs: [
        {
          name: 'blob'
          privateDnsZoneResourceId: resourceId('Microsoft.Network/privateDnsZones', dnsZoneName)
        }
      ]
    }
  }
  dependsOn: [network, outputStorage, dns]
}

module hostDnsZones 'br/public:avm/res/network/private-dns-zone:0.8.1' = [for service in ['queue', 'table']: {
  name: 'host-${service}-dns'
  params: {
    name: 'privatelink.${service}.${environment().suffixes.storage}'
    tags: tags
    enableTelemetry: false
    virtualNetworkLinks: [
      {
        name: 'link-purview-dev-ee9a'
        virtualNetworkResourceId: resourceId('Microsoft.Network/virtualNetworks', networkName)
        registrationEnabled: false
      }
    ]
  }
  dependsOn: [network]
}]

module hostPrivateEndpoints 'br/public:avm/res/network/private-endpoint:0.12.1' = [for service in ['blob', 'queue', 'table']: {
  name: 'host-${service}-private-endpoint'
  params: {
    name: 'pep-purview-host-${service}-dev-ee9a'
    location: location
    tags: tags
    enableTelemetry: false
    subnetResourceId: resourceId('Microsoft.Network/virtualNetworks/subnets', networkName, 'snet-private-endpoints')
    privateLinkServiceConnections: [
      {
        name: 'host-${service}'
        properties: {
          privateLinkServiceId: resourceId('Microsoft.Storage/storageAccounts', hostStorageName)
          groupIds: [service]
        }
      }
    ]
    privateDnsZoneGroup: {
      name: 'default'
      privateDnsZoneGroupConfigs: [
        {
          name: service
          privateDnsZoneResourceId: resourceId('Microsoft.Network/privateDnsZones', 'privatelink.${service}.${environment().suffixes.storage}')
        }
      ]
    }
  }
  dependsOn: [network, hostStorage, dns, hostDnsZones]
}]

module plan 'br/public:avm/res/web/serverfarm:0.7.0' = {
  name: 'windows-b1'
  params: {
    name: planName
    location: location
    tags: tags
    enableTelemetry: false
    kind: 'app'
    skuName: 'B1'
    skuCapacity: 1
    reserved: false
    zoneRedundant: false
  }
}

// REST accepts fractional GB; the AVM's int parameter cannot represent 0.1.
resource workspace 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: 'log-purview-dev-ee9a'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json('0.1') }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

module insights 'br/public:avm/res/insights/component:0.8.0' = {
  name: 'function-monitoring'
  params: {
    name: insightsName
    location: location
    tags: tags
    enableTelemetry: false
    workspaceResourceId: workspace.id
    applicationType: 'web'
    disableLocalAuth: true
  }
}

module vault 'br/public:avm/res/key-vault/vault:0.14.2' = {
  name: 'certificate-vault'
  params: {
    name: vaultName
    location: location
    tags: tags
    enableTelemetry: false
    sku: 'standard'
    enableRbacAuthorization: true
    enableVaultForDeployment: false
    enableVaultForTemplateDeployment: false
    enableVaultForDiskEncryption: false
    keys: []
    secrets: []
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 7
    diagnosticSettings: [
      {
        name: 'vault-audit'
        workspaceResourceId: workspace.id
        logCategoriesAndGroups: [{ category: 'AuditEvent', enabled: true }]
        metricCategories: []
      }
    ]
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
      ipRules: []
      virtualNetworkRules: []
    }
    roleAssignments: [
      {
        principalId: deployerObjectId
        principalType: 'User'
        roleDefinitionIdOrName: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a4417e6f-fecd-4de8-b567-7b0420556985')
      }
    ]
  }
  dependsOn: [network]
}

module app 'br/public:avm/res/web/site:0.24.0' = {
  name: 'function-app'
  params: {
    name: functionAppName
    location: location
    tags: tags
    enableTelemetry: false
    kind: 'functionapp'
    serverFarmResourceId: resourceId('Microsoft.Web/serverfarms', planName)
    virtualNetworkSubnetResourceId: integrationSubnetId
    managedIdentities: { systemAssigned: true }
    httpsOnly: true
    clientAffinityEnabled: false
    publicNetworkAccess: 'Enabled'
    outboundVnetRouting: { allTraffic: false, applicationTraffic: false }
    basicPublishingCredentialsPolicies: [
      { name: 'ftp', allow: false }
      { name: 'scm', allow: false }
    ]
    siteConfig: {
      netFrameworkVersion: 'v10.0'
      use32BitWorkerProcess: false
      alwaysOn: true
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      vnetRouteAllEnabled: false
      scmIpSecurityRestrictionsUseMain: false
      ipSecurityRestrictionsDefaultAction: 'Deny'
      scmIpSecurityRestrictionsDefaultAction: 'Deny'
      ipSecurityRestrictions: [
        { name: 'approved-workstation', ipAddress: workstationCidr, action: 'Allow', priority: 100 }
      ]
      scmIpSecurityRestrictions: [
        { name: 'approved-workstation', ipAddress: workstationCidr, action: 'Allow', priority: 100 }
      ]
    }
  }
  dependsOn: [plan, hostStorage, outputStorage, privateEndpoint, hostPrivateEndpoints, vault]
}

// Keep the runtime-only connection-string lookup out of AVM module parameters.
resource appSettings 'Microsoft.Web/sites/config@2024-11-01' = {
  name: '${functionAppName}/appsettings'
  properties: {
    FUNCTIONS_WORKER_RUNTIME: 'dotnet-isolated'
    FUNCTIONS_EXTENSION_VERSION: '~4'
    FUNCTIONS_WORKER_PROCESS_COUNT: '1'
    AzureWebJobsFeatureFlags: 'EnableWorkerIndexing'
    AzureWebJobsStorage__accountName: hostStorageName
    AzureWebJobsStorage__credential: 'managedidentity'
    AZURE_FUNCTIONS_ENVIRONMENT: 'Production'
    WEBSITE_RUN_FROM_PACKAGE: '1'
    SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
    WEBSITE_LOAD_CERTIFICATES: certificateThumbprint
    TenantId: tenantId
    ClientId: readerClientId
    CertificateThumbprint: certificateThumbprint
    DelegatedUserEmail: delegatedUserEmail
    SharePointDriveId: sharePointDriveId
    SharePointHostName: sharePointHostName
    BlobServiceUri: outputBlobServiceUri
    BlobContainerName: outputContainerName
    MaxFileSizeMiB: '25'
    MaxDecryptedSizeMiB: '100'
    APPLICATIONINSIGHTS_CONNECTION_STRING: insights.outputs.connectionString
    APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'Authorization=AAD'
  }
  dependsOn: [app]
}

output functionAppName string = functionAppName
output functionEndpoint string = 'https://${functionAppName}.azurewebsites.net'
output outputBlobServiceUri string = outputBlobServiceUri
output vaultName string = vaultName
output runtimePrincipalId string = app.outputs.systemAssignedMIPrincipalId!
output hostStorageName string = hostStorageName
output outputStorageName string = outputStorageName
output outputContainerName string = outputContainerName
output insightsName string = insightsName
