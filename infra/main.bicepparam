using './main.bicep'

param location = 'eastasia'
param resourceGroupName = 'rg-purview-function-test'
param workstationCidr = '112.118.75.90/32'
param tenantId = '62cece83-4b19-4636-84f4-60d52b2d31b8'
param readerClientId = '78d62852-a6cb-4f3a-b50d-17e148f3bd54'
param delegatedUserEmail = 'pimuser@MngEnvMCAP187873.onmicrosoft.com'
param sharePointDriveId = 'b!elZj3CSVLECIqSEDhuWZWZCQUvLVapFOvCshfv2GiPzka3ut7bCWRqwpXASHh6vs'
param sharePointHostName = 'mngenvmcap187873.sharepoint.com'
param deployerObjectId = 'ee8482e4-c36e-493d-9b6a-ac90ac39c7ce'
param certificateThumbprint = readEnvironmentVariable('PURVIEW_CERTIFICATE_THUMBPRINT')
