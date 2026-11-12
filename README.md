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

Nothing is provisioned automatically. The opt-in Bicep templates described below
define a Windows B1 private-output pilot. Deployment, permission changes,
certificate transfer, storage creation, and live document processing require
separate approval.

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

- Use Functions v4 on **Windows x64 Premium or Dedicated**, not Linux/Flex with
  this Windows native package. The approved low-volume pilot uses Dedicated B1
  with Always On. Keep .NET 10 and verify native MIP dependencies, including the
  required x64 Visual C++ runtime, memory headroom and startup on the selected host.
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

## Azure pilot infrastructure

The [subscription template](./infra/main.bicep),
[pilot module](./infra/modules/pilot.bicep), and
[parameter file](./infra/main.bicepparam) describe the approved non-production
deployment in East Asia, in a new `rg-purview-function-test` resource group:

| Component | Pilot configuration |
|---|---|
| Functions | One Windows B1 instance, .NET 10 isolated/x64, Always On, one worker |
| Networking | Isolated VNet `10.88.0.0/24`; delegated integration `/26` and separate private-endpoint `/27` |
| Decrypted output | New private `decrypted-output` container, Blob private endpoint and private DNS; public network/shared-key/anonymous access disabled |
| Host/Durable storage | New StorageV2/LRS account, managed identity, Blob/Queue/Table private endpoints, seven-day soft-delete for host metadata; public/shared-key/anonymous access disabled |
| Client certificate | Existing certificate uploaded to App Service only; specific thumbprint loaded in `CurrentUser\My`, no private-key/password files |
| Certificate vault | Existing pilot RBAC-enabled Standard Key Vault remains private and empty, with soft-delete/purge protection; no certificate import |
| Monitoring | Workspace-linked Application Insights, managed-identity ingestion, Vault audit events, 30-day workspace retention |
| Incoming access | Function and SCM/deployment endpoints default-deny; exact approved workstation IPv4 `/32` only |

This is **private host and output Storage**, not a fully private application.
Four Storage private endpoints use three VNet-linked private DNS zones: the host
and output share the Blob zone, and the host also uses Queue and Table zones.
Organization policy requires Storage and Key Vault public-network access to be
disabled; public service-endpoint/IP exceptions cannot override it. The existing
subnet service-endpoint declarations are inert for these public-disabled services.
Vault remains unused; the reader loads its certificate from App Service. No Vault
private endpoint or private administration route is provisioned.
Graph, SharePoint and Purview use authenticated public HTTPS. Default App Service
routing sends private destinations through the VNet while public Internet traffic
uses the App Service outbound route. No NAT
Gateway, firewall, VPN, private incoming Function endpoint or private monitoring
scope is provisioned. The shared NSG is enforced on the private endpoint: it allows
runtime-subnet HTTPS to all four Storage endpoints, denies other VNet inbound traffic and blocks
outbound SSH/RDP to the VNet. These rules do not provide FQDN-based Internet
egress filtering or secure the Function's HTTP endpoint.

The approved workstation IP is `112.118.75.90/32`. Recheck it before deployment;
stop for approval if it changes rather than expanding it to an office prefix.
Keep Function-key authorization. An application on another network will not be
able to call this pilot until its precise outbound IP is separately approved.
Being able to invoke the Function does not provide direct private-network access
to its output account: the workstation cannot download those blobs without
separate private connectivity and Blob data authorization.

The estimated recurring B1/four-private-endpoint/three-DNS-zone subtotal is
**USD 85.45/month** at
730 hours. Small Storage, Key Vault and logging allowances bring the illustrative
light-use estimate to **USD 89.76/month**, before taxes, bandwidth and additional
usage. This is not a spending cap. Stopping the Function does not stop Dedicated
plan charges. B1 has 1.75 GB RAM, no zone redundancy, no scale-to-zero and no
deployment slots. Regional SKU listing does not guarantee allocation capacity.
Validate native compatibility and memory in Azure before approving any live job.

The templates reuse the approved reader application and fixed Pim configuration;
they do not grant Graph/Purview permissions or alter document rights. Fixed Pim is
an application setting, **not an Entra-enforced restriction of the app credential
to that user**. Trusted administrators who can change code/configuration or use
the certificate remain in the security boundary. Do not treat this pilot as a
production multi-user authorization design.

Populate the certificate-thumbprint environment variable from the approved local
configuration before evaluating the parameter file. A thumbprint is lookup
metadata, not a PFX or password. Any future certificate transfer requires a
separately approved operation; never put the private key in Bicep, parameters,
command-line arguments, logs or the application package.

For the approved local certificate, set the lookup metadata without exporting it.
Local Bicep and parameter compilation do not provision resources:

```powershell
$auth = Get-Content .\purviewLocalFileReader.AppOnly.function\appsettings.app-only.local.json -Raw -ErrorAction Stop | ConvertFrom-Json
$env:PURVIEW_CERTIFICATE_THUMBPRINT = [string]$auth.CertificateThumbprint
az bicep build --file .\infra\main.bicep
az bicep build-params --file .\infra\main.bicepparam --outfile "$env:TEMP\purview-pilot.parameters.json"
```

Review the resolved parameters, generated ARM, security scan, Azure validation and
incremental what-if before any deployment. Stop if the preview changes existing
resources, changes SKU/region, adds private endpoints or widens network access.
Use Entra-authenticated ZIP deployment with remote build disabled after separate
deployment approval. The Dedicated plan uses run-from-package and does not
require a customer Azure Files content share.

Deploying infrastructure is not publishing the application or transferring its
certificate. After those separately approved operations, verify all four indexed
Functions, the loaded private-key certificate, Windows x64/native dependencies,
and private DNS/Storage connectivity. Use the existing unknown-job GET for a
non-mutating endpoint smoke test, not a POST. Neither these templates nor smoke
checks authorize a live document-processing job, changes to existing
Storage/NSP/site/document grants, plaintext retention/deletion, or a Git push.
Pilot logging includes sanitized Function-host telemetry and Key Vault audit
events, retained for 30 days in the pilot workspace. Storage transaction
diagnostics and production retention policies are not added automatically.
Host-metadata soft-delete does not retain extra decrypted-output copies: output
soft-delete and versioning remain disabled. Workspace ingestion caps are best
effort, not financial limits.

The [runtime role module](./infra/modules/roles.bicep) creates scoped data roles
after the Function identity and target resources exist, avoiding a circular
storage/application dependency. The subscription entry point runs this as a
separate phase after the pilot module. Predictable resource IDs are constructed
directly, with explicit deployment dependencies, rather than passed through
runtime module-output references. Application settings are deployed as a child
resource so the runtime Application Insights connection-string lookup does not
prevent Azure from previewing the Function's hosting and security configuration.
That lookup uses the monitoring module output, not a duplicate `existing`
declaration that could be read before the component is created. Unused Key Vault
VM-certificate deployment, template-secret deployment and disk-encryption
integrations are explicitly disabled; none requires widening `bypass: None` to
trusted Azure services.
All 22 settings and the Function/SCM security controls are preserved.

The `deployRuntimeRoles` parameter defaults to `true`: a normal deployment includes
the required runtime roles. Setting it to `false` is **bootstrap-only**, not a
complete or usable deployment. The staged sequence, after explicit approval, is:

1. Validate and preview the bootstrap with
   `--parameters .\infra\main.bicepparam deployRuntimeRoles=false`, then provision
   only the reviewed new resources.
2. Read the actual Function system-assigned principal ID. Validate and preview
   the unchanged role module with that ID and the provisioned target names before
   granting its five scoped roles. Never substitute a guessed principal or change
   the role GUIDs to hide this dependency. Stop on any unexpected change or error.
3. Only after the role phase succeeds, perform the separately authorized
   certificate transfer, application publication and non-mutating smoke checks.

Bootstrap resources incur charges even if the role phase fails. A cold preview of
the complete plan still cannot resolve the principal-dependent role GUIDs before
the Function exists; this is why the role phase needs its own real-ID preflight.
These stages do not authorize a document-processing POST.

Pinned official Azure Verified Modules are used
where compatible. The workspace is defined directly because its AVM exposes an
integer daily quota while the approved `0.1` GB quota requires a fractional REST
number; Bicep emits the standard ARM `json('0.1')` expression.

**Local validation:** Bicep/parameter compilation and targeted compiled-setting
assertions passed. Bicep 0.44.1 still warns that its type catalog lacks the NSG
AVM's `2025-09-01` API. Checkov 3.4.3 fails while parsing the subscription ARM graph,
and its alternative Bicep/workload scans check zero resources; none are security
passes.

The compatible alternative is **PSRule 2.9.0 / PSRule.Rules.Azure 1.47.0**, using
offline nested-template/parameter expansion. The
[policy configuration](./infra/ps-rule.yaml) records nine target-scoped exceptions
for the approved B1/single-region/LRS availability compromises, avoiding retained
deleted plaintext copies, and the unused AVM key-configuration parameter
heuristic. No security rule is globally excluded. The revised private-host scan
passes 356 checks across 50 analyzed targets, with no remaining failures after
those exceptions.
Role-assignment targets have no matching baseline rules and require the separate
scope/role-ID checks; offline dynamic lookups use placeholders, not real secrets.

With the specified PSRule modules available through your approved package source
and the thumbprint environment variable set as above:

```powershell
$env:PSRULE_AZURE_BICEP_PATH = Join-Path $env:USERPROFILE '.azure\bin\bicep.exe'
$option = New-PSRuleOption -Path .\infra\ps-rule.yaml
Invoke-PSRule -InputPath .\infra\main.bicepparam -Module PSRule.Rules.Azure -Format File -Option $option -Baseline Azure.GA_2025_12
```

PSRule tooling was installed only in the session's isolated validation directory,
not globally or in the application. This is not a successful Checkov scan or a
production-readiness certification.

**Azure preflight history:** after removing avoidable module-output dependencies,
the original bootstrap what-if succeeded with
**24 Create, 0 Modify, 0 Delete and no diagnostics**.
All changes are confined to the new resource group and include the Function,
publishing-auth restrictions, application settings, private endpoint and DNS zone
group, which were previously skipped. Provider validation exits zero and lists
all 24 resource IDs as validated, but still emits one
`NestedDeploymentSkippedFromInternalExpansion` batch warning. This warning also
persists with the latest advertised ARM validation API; do not describe the
provider result as warning-free. The complete default plan's cold preview still
skips the five runtime role assignments until their actual principal exists.
Staged provisioning requires explicit approval, fresh identity/IP/name checks,
the real-ID role preflight and live native/runtime verification.
Provisioning began after explicit staged approval. The first bootstrap attempt
partially created the new pilot resources but failed on the unused Vault platform
integration defaults and an early monitoring lookup. Both template issues were
corrected without widening access or changing SKU/region, and the incremental
bootstrap retry succeeded.

**Policy-compliant networking revision:** the management-group
`MCAPSGov Deploy and Modify Policies` assignment forcibly changes Storage and
Key Vault public-network access to `Disabled`. The host account's approved
service-endpoint exception and the workstation's Vault IP rule therefore do not
provide connectivity. A successful ARM deployment and a Function state of
`Running` are not proof that the Function is usable. Do not repeatedly enable
public access, add exception tags or bypass the policy.

The user explicitly approved three additional host Storage private endpoints
(Blob, Queue and Table) and two private DNS zones, reusing the existing Blob zone.
This adds approximately USD 22.90/month to the original estimate. The revised
template passed compilation, PSRule and Provider validation. Its incremental
what-if reported 10 Create, 13 Modify, 11 NoChange and 2 Ignore, with no deletes or
diagnostics; the ten creates were exactly the approved endpoints, DNS zone groups,
zones and VNet links. The networking deployment succeeded without policy changes
or modifications outside the pilot.

### Deployed pilot and verified limits

The approved deployment completed on **2026-11-11**:

- Resource group: `rg-purview-function-test`; East Asia; Windows B1, one instance.
- Function App: `func-purview-dev-ee9a`, reachable at
  `https://func-purview-dev-ee9a.azurewebsites.net` only from the approved `/32`.
  Both HTTP endpoints retain Function-key authorization.
- The real system-assigned identity's five scoped roles passed a fresh five-create
  Provider/what-if preflight, deployed successfully, and were verified against the
  approved scopes/role IDs. Output access is container-scoped; the Function has
  no Vault role.
- The existing certificate was transferred in process memory through authenticated
  ARM HTTPS to App Service only. No private-key/password files were created.
  The loaded `CurrentUser\My` private key successfully signed and verified a
  non-credential probe; the certificate expires **2027-08-08 08:12:36 UTC**.
  Plan rotation before expiry. Vault is private and empty; no Vault import or
  unattended rotation was implemented.
- Entra-authenticated ZIP deployment completed successfully with basic publishing
  authentication still disabled. All four expected Functions are indexed.
- Azure sandbox checks confirmed x64 execution and successful load/unload of all
  six native MIP DLLs. This does not prove MIP engine initialization or document
  decryption succeeds.
- All four Storage service hostnames resolved to private addresses from the Azure
  environment. Managed-identity reads of host Blob/Queue/Table and a HEAD of the
  private output container returned HTTP 200; no output document was downloaded.
- An unknown-job GET returned HTTP 401 without a key, then HTTP 404 with
  `{"error":"JOB_NOT_FOUND"}` and `Cache-Control: no-store` using a key header.
- Azure Resource Health reported Available. The monitoring connection string,
  AAD authentication and Metrics Publisher role match the approved component.
  Routine request telemetry was not observed: the existing host default is
  Warning, without a `Host.Results: Information` override. Logging was not
  changed, and observed monitoring ingestion is **not** claimed as verified.

Those initial deployment smoke checks did not submit a document job. The later
single-copy live test below was separately approved.
Existing SharePoint documents/grants, the old Storage account/shared NSP and the
earlier local plaintext output were not changed. No Git commit or push was made.
The Dedicated plan and private endpoints remain billable while idle or stopped.

### Approved Azure live test

On **2026-11-11**, the user separately authorized exactly one job using the
previously EXPORT-enabled test copy, not the original protected document.

| Result | Verified value |
|---|---|
| Job ID | `3fcb4e14f2f3488ba7c4a71cd293967f` |
| Durable status | Completed |
| Output account / container | `stpurviewoutdevee9a` / `decrypted-output` |
| Blob name | `3fcb4e14f2f3488ba7c4a71cd293967f/content` |
| Output | 29,236-byte unencrypted DOCX; ZIP package and readable Word XML verified inside Azure memory |
| Privacy / caching | Container public access None; output `Cache-Control: no-store` |
| Application scratch | Zero remaining job directories after completion |

This validates real certificate-based SharePoint/Purview authentication, MIP engine
initialization, the configured Pim user's EXPORT check, decryption and
managed-identity private Blob upload for this particular copy. No document contents
were displayed or downloaded to the workstation. The blob is named `content`
without a file extension; its verified format is DOCX.

**The unencrypted test output remains in the private container.** No cleanup was
authorized. The single-job approval is consumed: it does not permit more jobs,
local plaintext downloads, source/permission/policy/resource changes, or a Git
commit/push. Other formats, denial/size-limit integration cases, load, memory and
crash recovery still require separately scoped validation. Monitoring and
trusted-admin/production-authorization limitations above remain unchanged.

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
