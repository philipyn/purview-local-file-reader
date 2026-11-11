# purview-local-file-reader

## Applications

- [PurviewLocalFileReader](./PurviewLocalFileReader/Program.cs) remains the interactive,
  local-file console application.
- [purviewLocalFileReader.AppOnly.function](./purviewLocalFileReader.AppOnly.function/Program.cs) is a
  Windows x64, .NET 10 isolated-worker Azure Functions application. It accepts a
  SharePoint file reference, downloads the protected file, checks the configured
  user's EXPORT right, decrypts it, and uploads plaintext to an existing private
  Blob container. It never modifies or deletes the SharePoint original.
- [PurviewLocalFileReader.AppOnly.Tests](./PurviewLocalFileReader.AppOnly.Tests/PurviewLocalFileReader.AppOnly.Tests.csproj)
  contains automated tests for the Functions application and is not deployed.

No Azure infrastructure is provisioned by this repository. Deployment, permission
changes, storage creation, and live document processing require separate approval.

## Request and result

The HTTP functions use Function authorization. Trusted callers submit a Microsoft
Graph `driveItemId`, not a local path, download URL, sharing link, numeric list-item
ID, user email, or arbitrary document-library ID. The document library and Purview
user are fixed in server configuration.

```http
POST /api/decryption-jobs
Content-Type: application/json
x-functions-key: <function-key>

{"driveItemId":"01EXAMPLE"}
```

The response is `202 Accepted`, with `jobId`, a relative `statusUrl`, and a
`Location` header. Durable Functions processes the job asynchronously, so the HTTP
request does not wait for decryption. Poll the returned URL with the same
authorization:

```http
GET /api/decryption-jobs/<jobId>
x-functions-key: <function-key>
```

The response contains `status`, `result`, and `error`. On completion, `result`
contains `blobName`, `blobUri`, and `length`. Output names are
`<jobId>/content`; responses never contain plaintext, a public link, or a SAS.
Reading that blob requires separate Blob authorization. A failed job returns
`status: "Failed"` and an approved error code such as `FILE_TOO_LARGE`,
`EXPORT_DENIED`, `SHAREPOINT_ACCESS_DENIED`, or `AUTHENTICATION_FAILED`. Detailed native error text,
preauthenticated SharePoint URLs, credentials, and document bytes are not returned.
MSAL and Azure Identity authentication failures, including unavailable local
credentials, use `AUTHENTICATION_FAILED`; operator logs retain the exception type
without persisting its potentially sensitive message in Durable history.
The status endpoint checks nested Durable failure messages and, for providers that
persist a formatted failure as output, the serialized failure output. Only the
approved codes are returned; unknown failures remain `PROCESSING_FAILED`.

Each submission creates a separate job. Redelivery of an activity reuses an
already committed output for that job. Submitting the same file again creates a
new output. Partially uploaded blocks are not presented as a completed output.
The source is checked for an ETag change during download; submit a changed file again.
There is no automatic retry policy for permanent authorization or file failures.

## Configuration

Use the [local settings example](./purviewLocalFileReader.AppOnly.function/local.settings.example.json)
to populate the ignored local settings file in the AppOnly project. Invoke
`dotnet run` from that project directory; the SDK starts Core Tools from the
compiled output directory so extensions load correctly. In Development only,
startup also attempts to read `appsettings.app-only.local.json` from its content
root. The legacy file is never copied to build or publish output, so the local
startup example below passes its existing authentication values as process
environment variables. Environment variables take precedence.

In Azure, configure all settings as Function App environment settings; no local
configuration file is used. Missing or invalid settings fail startup explicitly.

| Setting | Required value |
|---|---|
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` |
| `AzureWebJobsStorage` | Existing host/Durable storage connection, or its identity-based equivalent |
| `TenantId` | Existing Entra tenant GUID |
| `ClientId` | Existing confidential application GUID |
| `CertificateThumbprint` | Valid private-key certificate in `CurrentUser\My` |
| `DelegatedUserEmail` | Existing approved Purview user context |
| `SharePointDriveId` | Microsoft Graph drive ID for the permitted SharePoint document library |
| `SharePointHostName` | Exact download hostname, for example `tenant.sharepoint.com` |
| `BlobServiceUri` | For example `https://account.blob.core.windows.net` |
| `BlobContainerName` | Existing private output container; the app does not create it |
| `MaxFileSizeMiB` | Default `25`; enforced against metadata and downloaded bytes |
| `MaxDecryptedSizeMiB` | Default `100`; bounds decrypted output copying and upload |
| `AZURE_TOKEN_CREDENTIALS` | Optional local credential selection; use `AzureCliCredential` when testing with Azure CLI |

This implementation intentionally supports the public Microsoft cloud and Windows
x64. Sovereign clouds, personal OneDrive, arbitrary download hosts, Linux, and
per-caller Purview user impersonation are not supported. An unexpected download
host is rejected; investigate it rather than broadening the allowlist blindly.

To find source identifiers, an authorized administrator can use Microsoft Graph
to list the selected site's document-library drives and retrieve an item's ID.
The `size` field on a drive item, or the SharePoint File Size column, helps choose
a realistic input limit. The 25 MiB development cap is not an Azure platform limit.
Measure decrypted size, native memory use, disk consumption, and execution time
with representative files before increasing it.

## Authentication and permissions

Three independent access checks apply:

1. **SharePoint download:** the certificate-authenticated app needs Microsoft Graph
   access to the selected library. Prefer `Sites.Selected` with an explicit
   site-level `read` grant, or an appropriate narrower selected permission, over
   tenant-wide `Files.Read.All`. Admin consent alone does not grant selected-site
   access. Provisioning these grants is an administrator task, not application code.
2. **Purview decryption/export:** retain the configured user's context. Verify the
   application's `Content.DelegatedReader` and full File-engine
   `UnifiedPolicy.Tenant.Read` permissions, admin consent, tenant configuration,
   licensing, and the user's document rights. The code explicitly requires
   `EXPORT` before writing unprotected content. It does not request
   `Content.SuperUser`, supply a user's password, or perform interactive sign-in.
   The authentication callback accepts only HTTPS root token audiences at
   `aadrm.com`, `api.aadrm.com`, and `syncservice.o365syncservice.com`, with no
   credentials, non-default port, query, or fragment. Both RMS audience aliases
   are required; a licensing-service URL is not itself an approved token audience.
   A native `NoAuthTokenException` can wrap a rejected authentication callback, so
   inspect the challenged audience before changing document rights or app grants.
3. **Blob output:** `DefaultAzureCredential` uses the developer's authorized identity
   locally and an appropriately configured identity in Azure. The output identity
   needs scoped Blob data permissions, normally Storage Blob Data Contributor.
   Authentication failure is not replaced with account keys or anonymous access.

For Azure CLI-based local testing, set `AZURE_TOKEN_CREDENTIALS` to
`AzureCliCredential` in the local settings `Values` object and sign in to the
Storage account's directory. Selecting the right email address alone is not
enough: a token issued by another tenant can fail Storage authentication. The
Storage directory may differ from the Purview application's `TenantId`; do not
change the Purview application's tenant or certificate to fix Blob authentication.

```powershell
az login --tenant <storage-tenant-id> --allow-no-subscriptions
az account get-access-token --resource https://storage.azure.com/ --query "{tenant:tenant,expiresOn:expiresOn}" --output json
```

Replace `<storage-tenant-id>` with the approved Storage directory ID. Verify that
the returned tenant matches it. The query prints metadata only; do not print or
share the access token. Private-container settings, network rules, and Blob data
roles remain separate requirements. If Storage uses a Network Security Perimeter,
check its resource association and access mode rather than assuming ordinary
firewall settings determine access. Do not weaken network rules to fix a missing
credential.

The existing certificate's public key must be registered with the Entra app.
On Windows Azure Functions, make the private-key certificate available using
`WEBSITE_LOAD_CERTIFICATES=<thumbprint>`. Rotate certificates and update the
configuration before expiry. No PFX or password belongs in this repository.

**Before production:** enable App Service Authentication with Entra ID, require
authentication, restrict the audience and allowed client applications, and retain
the Function-key requirement. This implementation trusts those authorized service
callers to use the one configured Purview user; it is not a multi-user API.
Local Core Tools does not enforce production platform authentication and commonly
disables key checks, so expose local endpoints only on a trusted development host.

## Hosting and data handling

- Start with Functions v4 on **Windows x64 Premium**, not Linux/Flex with this
  Windows native package. Keep .NET 10 and verify the native MIP dependencies,
  including the required x64 Visual C++ runtime, on the selected host.
- Durable Functions uses an existing `AzureWebJobsStorage` account for its task
  hub. When a host is eventually started, the Durable extension manages its own
  queues, tables, and blobs. For local execution, the sample uses an independently
  installed/running Azurite emulator. Do not start it against unapproved Azure
  storage. Give each environment/slot a distinct task hub or storage account.
- The output container must already exist and be private. Its public-access
  setting is checked before reuse/upload. Disable public access at storage-account
  level as well; do not grant other identities permission to make it public.
- Each job uses an isolated random scratch directory. SDK-created plaintext is
  deleted after copying; job input/output scratch files are removed on success or
  failure. Worker crashes and forced termination can bypass cleanup; ephemeral
  disk controls and recovery/retention procedures remain necessary.
- Input/output copies are bounded, but the SDK may allocate memory or create its
  own temporary output before the output-length check. This is not a complete
  native memory/disk quota. Leave headroom and test oversized/complex documents.
- MIP initialization is process-scoped and decryption is serialized per worker.
  The host limits activity concurrency to one per instance. Scale-out creates
  additional independent instances and therefore additional service traffic.
- The activity timeout is ten minutes. Native MIP operations do not expose a
  cancellation token; cancellation is checked around them, not implemented by
  abandoning native work. A timed-out/restarted worker may need recovery.
- MIP policy/license caches are in memory, license caching is disabled, and SDK
  log level is Warning. HTTP client URL logging is disabled to avoid logging
  preauthenticated download links. SDK diagnostic files still need restricted
  access and retention.
- Blob encryption at rest does **not** restore the document's original Purview
  restrictions. Approve access, retention, lifecycle deletion, soft-delete/version
  retention, backups, audit requirements, network restrictions, and Durable-history
  retention before production. No lifecycle policy is created automatically.
- Original user-staged SDK log/telemetry files have not been unstaged or removed.
  New ignore rules do not remove files already in Git's index. Review those staged
  files separately before committing.

## Local verification

Use a stable .NET 10 SDK. Credential-free tests exercise input validation, exact
size thresholds, allowed service hosts, processing, idempotent redelivery, failure
sanitization, and scratch cleanup without contacting Graph, Purview, or Azure.

```powershell
dotnet test .\PurviewLocalFileReader.AppOnly.Tests\PurviewLocalFileReader.AppOnly.Tests.csproj
dotnet publish .\purviewLocalFileReader.AppOnly.function\purviewLocalFileReader.AppOnly.function.csproj -c Release
```

Starting the Function host or running live integration tests is a separate step:
it can create Durable task-hub data, read real SharePoint documents, and write
plaintext blobs. Before doing so, approve the source library and test documents,
configure storage and credentials, validate unauthorized/oversized/non-exportable
files, inspect the published x64 native assets, and verify cleanup and private
output access in the intended environment.

### Local host startup (separate approval required)

Use Windows x64 Core Tools v4 and a separately approved local Azurite instance
listening on loopback ports 10000, 10001, and 10002. Store emulator data outside
the repository. After restoring dependencies, configuring the approved source
and output, and obtaining host-startup approval, run this from the repository root:

```powershell
Set-Location .\purviewLocalFileReader.AppOnly.function
$auth = Get-Content .\appsettings.app-only.local.json -Raw -ErrorAction Stop | ConvertFrom-Json
foreach ($name in @('TenantId', 'ClientId', 'CertificateThumbprint', 'DelegatedUserEmail')) {
    [Environment]::SetEnvironmentVariable($name, [string]$auth.$name, 'Process')
}
$env:FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT = '1'
dotnet run --no-restore -- --address 127.0.0.1
```

Do not use `func start` directly against this SDK project: Core Tools warns that
extensions may not load correctly. The command above starts the host only; a
live decryption request needs separate approval. Keep the Azurite terminal
running. Authentication values stay process-scoped and are not printed or copied
into build files. Close the host terminal after testing to clear those values.

If startup reports `Unable to load Function` because a function ID already exists,
do not treat the function list or `/admin/host/status` reporting `Running` as proof
that invocations work. Stop only the Function host, leave Azurite and its persisted
job history intact, and restart using the command above. Do not start overlapping
hosts or delete emulator data to recover. Verify the worker using a read-only
`GET /api/decryption-jobs/00000000000000000000000000000000`: when that ID has never
been submitted, expect HTTP 404 with `{"error":"JOB_NOT_FOUND"}` and
`Cache-Control: no-store`. An invocation returning HTTP 500 still needs diagnosis;
do not submit another job as a health check. Restarting does not retry a failed
job, and a new live request still requires separate approval.

If the default NuGet feed has a TLS connectivity failure, an approved restore can
use Microsoft's public `dotnet-public` feed. For SDK-triggered restores, setting
`RestoreSources` to that feed in the same terminal avoids a repository-wide
NuGet configuration change. Do not disable TLS certificate validation.
