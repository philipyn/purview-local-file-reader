using System.Text;
using System.Text.Json;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using purviewLocalFileReader.AppOnly.function;
using Xunit;

namespace PurviewLocalFileReader.AppOnly.Tests;

public sealed class ProcessingTests
{
    [Theory]
    [InlineData("01ABCDEF123")]
    [InlineData("b!ABC-def_123")]
    public void AcceptsGraphIds(string id) => Assert.True(FileReference.IsValidId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../secret")]
    [InlineData("https://example.com/file")]
    [InlineData("abc?query=1")]
    [InlineData("abc\\def")]
    public void RejectsPathsAndUrls(string? id) => Assert.False(FileReference.IsValidId(id));

    [Fact]
    public void RejectsOverlongId() => Assert.False(FileReference.IsValidId(new string('A', 257)));

    [Fact]
    public void ValidConfigurationPasses() =>
        Assert.True(new ReaderOptionsValidator().Validate(null, Options()).Succeeded);

    [Fact]
    public void MissingConfigurationFailsExplicitly()
    {
        var result = new ReaderOptionsValidator().Validate(null, new ReaderOptions());
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.StartsWith("TenantId", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.StartsWith("SharePointDriveId", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    public void RejectsInvalidInputLimits(int limit)
    {
        ReaderOptions options = Options();
        options.MaxFileSizeMiB = limit;
        Assert.True(new ReaderOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("http://tenant.sharepoint.com/file")]
    [InlineData("https://tenant.sharepoint.com.attacker.example/file")]
    [InlineData("https://other.sharepoint.com/file")]
    [InlineData("https://tenant.sharepoint.com:8443/file")]
    [InlineData("https://user@tenant.sharepoint.com/file")]
    public void RejectsUnapprovedDownloadUrls(string url) =>
        Assert.False(ServiceHosts.IsDownloadUri(new Uri(url), "tenant.sharepoint.com"));

    [Fact]
    public void AcceptsApprovedHttpsDownloadUrl() =>
        Assert.True(ServiceHosts.IsDownloadUri(
            new Uri("https://tenant.sharepoint.com/sites/example/download?token=redacted"),
            "tenant.sharepoint.com"));

    [Theory]
    [InlineData("https://api.aadrm.com", true)]
    [InlineData("https://tenant.rms.eu.aadrm.com", true)]
    [InlineData("https://dataservice.protection.outlook.com", true)]
    [InlineData("https://aadrm.com.attacker.example", false)]
    [InlineData("http://api.aadrm.com", false)]
    public void RestrictsMipServiceHosts(string url, bool expected) =>
        Assert.Equal(expected, ServiceHosts.IsMipService(new Uri(url)));

    [Theory]
    [InlineData("https://login.windows.net/common", true)]
    [InlineData("https://login.microsoftonline.com/tenant", true)]
    [InlineData("https://login.windows.net.attacker.example/common", false)]
    [InlineData("http://login.windows.net/common", false)]
    public void AllowsActualSdkAuthoritiesOnly(string url, bool expected) =>
        Assert.Equal(expected, ServiceHosts.IsAuthority(new Uri(url)));

    [Theory]
    [InlineData("https://aadrm.com", true)]
    [InlineData("https://aadrm.com/", true)]
    [InlineData("https://api.aadrm.com", true)]
    [InlineData("https://syncservice.o365syncservice.com/", true)]
    [InlineData("http://aadrm.com", false)]
    [InlineData("https://aadrm.com:8443", false)]
    [InlineData("https://user@aadrm.com", false)]
    [InlineData("https://aadrm.com/other", false)]
    [InlineData("https://aadrm.com?token=private-token", false)]
    [InlineData("https://aadrm.com#fragment", false)]
    [InlineData("https://aadrm.com.attacker.example", false)]
    [InlineData("https://tenant.rms.eu.aadrm.com", false)]
    [InlineData("https://aadrm.us", false)]
    [InlineData("https://syncservice.o365syncservice.com/other", false)]
    [InlineData("https://graph.microsoft.com", false)]
    [InlineData("https://api.aadrm.com.attacker.example", false)]
    public void AllowsActualSdkTokenAudiencesOnly(string url, bool expected) =>
        Assert.Equal(expected, ServiceHosts.IsMipResource(new Uri(url)));

    [Fact]
    public void DurableOutputDeserializesWithWebCasing()
    {
        DecryptionResult? result = JsonSerializer.Deserialize<DecryptionResult>(
            """{"blobName":"job/content","blobUri":"https://example.blob.core.windows.net/output/job/content","length":9}""",
            JsonSerializerOptions.Web);
        Assert.NotNull(result);
        Assert.Equal("job/content", result.BlobName);
        Assert.Equal(9, result.Length);
    }

    [Fact]
    public void RejectsNonNumericMetadataSize()
    {
        using JsonDocument metadata = JsonDocument.Parse(
            """{"id":"01ABC","name":"file.docx","size":"10","eTag":"version1","file":{}}""");
        Assert.Equal("INVALID_METADATA", Assert.Throws<ProcessingException>(
            () => SharePointFileClient.ParseDocument(metadata.RootElement, "01ABC", Options())).Code);
    }

    [Fact]
    public async Task CopyAllowsExactlyConfiguredLimit()
    {
        await using MemoryStream source = new(new byte[25 * 1024 * 1024]);
        await using MemoryStream destination = new();
        long length = await BoundedStreams.CopyAsync(source, destination, 25 * 1024 * 1024, default);
        Assert.Equal(25 * 1024 * 1024, length);
        Assert.Equal(length, destination.Length);
    }

    [Fact]
    public async Task CopyRejectsOneByteOverLimit()
    {
        await using MemoryStream source = new(new byte[9]);
        await using MemoryStream destination = new();
        ProcessingException failure = await Assert.ThrowsAsync<ProcessingException>(
            () => BoundedStreams.CopyAsync(source, destination, 8, default));
        Assert.Equal("FILE_TOO_LARGE", failure.Code);
        Assert.True(destination.Length <= 8);
    }

    [Fact]
    public async Task CopyHonorsCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await using MemoryStream source = new(new byte[10]);
        await using MemoryStream destination = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BoundedStreams.CopyAsync(source, destination, 20, cancellation.Token));
    }

    [Theory]
    [InlineData(26214400, true)]
    [InlineData(26214401, false)]
    public void MetadataEnforcesExact25MiBThreshold(long size, bool accepted)
    {
        using JsonDocument metadata = Metadata(size);
        if (accepted)
            Assert.Equal(size, SharePointFileClient.ParseDocument(metadata.RootElement, "01ABC", Options()).Size);
        else
            Assert.Equal("FILE_TOO_LARGE", Assert.Throws<ProcessingException>(
                () => SharePointFileClient.ParseDocument(metadata.RootElement, "01ABC", Options())).Code);
    }

    [Fact]
    public void RejectsRemoteItemReferences()
    {
        using JsonDocument metadata = JsonDocument.Parse(
            """{"id":"01ABC","name":"file.docx","size":10,"file":{},"remoteItem":{}}""");
        Assert.Equal("NOT_A_FILE", Assert.Throws<ProcessingException>(
            () => SharePointFileClient.ParseDocument(metadata.RootElement, "01ABC", Options())).Code);
    }

    [Fact]
    public void RejectsUnexpectedMetadataId()
    {
        using JsonDocument metadata = Metadata(10);
        Assert.Equal("INVALID_METADATA", Assert.Throws<ProcessingException>(
            () => SharePointFileClient.ParseDocument(metadata.RootElement, "OTHER", Options())).Code);
    }

    [Fact]
    public void RejectsUnapprovedMetadataDownloadHost()
    {
        using JsonDocument metadata = Metadata(10, "https://attacker.example/file");
        Assert.Equal("UNAPPROVED_DOWNLOAD", Assert.Throws<ProcessingException>(
            () => SharePointFileClient.ParseDocument(metadata.RootElement, "01ABC", Options())).Code);
    }

    [Fact]
    public async Task ProcessingUploadsPlaintextAndCleansWorkspace()
    {
        FakeSharePoint source = new();
        FakeDecryptor decryptor = new();
        FakeOutput output = new();
        DecryptionProcessor processor = Processor(source, decryptor, output);
        Guid jobId = Guid.NewGuid();
        DecryptionResult result = await processor.ProcessAsync(new(jobId, "01ABC"), default);
        Assert.Equal($"{jobId:N}/content", result.BlobName);
        Assert.Equal("plaintext", Encoding.UTF8.GetString(output.Uploaded!));
        Assert.False(Directory.Exists(Path.GetDirectoryName(source.DownloadPath)!));
        Assert.Equal(1, decryptor.Calls);
    }

    [Fact]
    public async Task RedeliveryUsesExistingOutputWithoutReadingOrDecrypting()
    {
        FakeSharePoint source = new();
        FakeDecryptor decryptor = new();
        FakeOutput output = new() { Existing = new("job/content", "https://example.blob.core.windows.net/output/job/content", 9) };
        DecryptionResult result = await Processor(source, decryptor, output)
            .ProcessAsync(new(Guid.NewGuid(), "01ABC"), default);
        Assert.Same(output.Existing, result);
        Assert.Null(source.DownloadPath);
        Assert.Equal(0, decryptor.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailuresCleanWorkspaceAndDoNotExposeSensitiveErrors(bool uploadFailure)
    {
        FakeSharePoint source = new();
        FakeDecryptor decryptor = new() { Fail = !uploadFailure };
        FakeOutput output = new() { Fail = uploadFailure };
        ProcessingException failure = await Assert.ThrowsAsync<ProcessingException>(() =>
            Processor(source, decryptor, output).ProcessAsync(new(Guid.NewGuid(), "01ABC"), default));
        Assert.Equal("PROCESSING_FAILED", failure.Code);
        Assert.DoesNotContain("private-token", failure.Message);
        Assert.False(Directory.Exists(Path.GetDirectoryName(source.DownloadPath)!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AzureAuthenticationFailuresAreClassifiedAndSanitized(
        bool credentialUnavailable, bool uploadFailure)
    {
        Azure.Identity.AuthenticationFailedException authenticationFailure = credentialUnavailable
            ? new Azure.Identity.CredentialUnavailableException("private-token")
            : new Azure.Identity.AuthenticationFailedException("private-token");
        FakeSharePoint source = new();
        FakeDecryptor decryptor = new();
        FakeOutput output = new()
        {
            AuthenticationFailure = authenticationFailure,
            AuthenticationFailureOnUpload = uploadFailure
        };

        ProcessingException failure = await Assert.ThrowsAsync<ProcessingException>(() =>
            Processor(source, decryptor, output).ProcessAsync(new(Guid.NewGuid(), "01ABC"), default));

        Assert.Equal("AUTHENTICATION_FAILED", failure.Code);
        Assert.Equal("AUTHENTICATION_FAILED", DecryptionFunctions.PublicFailureCode(failure.Message));
        Assert.DoesNotContain("private-token", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Null(output.Uploaded);
        Assert.Equal(uploadFailure ? 1 : 0, decryptor.Calls);
        if (uploadFailure)
        {
            Assert.NotNull(source.DownloadPath);
            Assert.False(Directory.Exists(Path.GetDirectoryName(source.DownloadPath)!));
        }
        else
        {
            Assert.Null(source.DownloadPath);
        }
    }

    [Theory]
    [InlineData("Task failed: FILE_TOO_LARGE: limit exceeded", "FILE_TOO_LARGE")]
    [InlineData("EXPORT_DENIED: cannot export", "EXPORT_DENIED")]
    [InlineData("AUTHENTICATION_FAILED: private-token", "AUTHENTICATION_FAILED")]
    [InlineData("OUTPUT_STORAGE_UNAVAILABLE: private-token", "OUTPUT_STORAGE_UNAVAILABLE")]
    [InlineData("DECRYPTION_ACCESS_DENIED: private-token", "DECRYPTION_ACCESS_DENIED")]
    [InlineData("Unexpected native message with private-token", "PROCESSING_FAILED")]
    [InlineData(null, "PROCESSING_FAILED")]
    public void StatusOnlyExposesApprovedFailureCodes(string? failure, string expected) =>
        Assert.Equal(expected, DecryptionFunctions.PublicFailureCode(failure));

    [Theory]
    [InlineData("OUTPUT_STORAGE_UNAVAILABLE")]
    [InlineData("DECRYPTION_ACCESS_DENIED")]
    [InlineData("AUTHENTICATION_FAILED")]
    public void DurableFailureReportsKnownCodeFromInnerDetails(string code)
    {
        TaskFailureDetails leaf = new(
            nameof(ProcessingException), $"{code}: private-token", "private-stack-trace", null, null);
        TaskFailureDetails activity = new(
            "ActivityFailure", "The activity failed.", "private-stack-trace", leaf, null);
        TaskFailureDetails wrapper = new(
            "OrchestrationFailure", "The orchestration failed.", "private-stack-trace", activity, null);

        Assert.Equal(code, DecryptionFunctions.PublicFailureCode(wrapper, "private-token"));
    }

    [Fact]
    public void DurableFailurePrefersKnownDetailsOverInnerDetailsAndLegacyOutput()
    {
        TaskFailureDetails inner = new(
            "InnerFailure", "OUTPUT_STORAGE_UNAVAILABLE: private-token", null, null, null);
        TaskFailureDetails failure = new(
            "OuterFailure", "AUTHENTICATION_FAILED: private-token", null, inner, null);

        Assert.Equal("AUTHENTICATION_FAILED",
            DecryptionFunctions.PublicFailureCode(failure, "EXPORT_DENIED: private-token"));
    }

    [Theory]
    [InlineData(null, "PROCESSING_FAILED")]
    [InlineData("private-token", "PROCESSING_FAILED")]
    [InlineData("UNAPPROVED_CODE: private-token", "PROCESSING_FAILED")]
    [InlineData("Task failed: OUTPUT_STORAGE_UNAVAILABLE: private-token", "OUTPUT_STORAGE_UNAVAILABLE")]
    [InlineData("""{"errorMessage":"DECRYPTION_ACCESS_DENIED: private-token"}""", "DECRYPTION_ACCESS_DENIED")]
    [InlineData("""{"errorMessage":"AUTHENTICATION_FAILED: private-token"}""", "AUTHENTICATION_FAILED")]
    public void DurableFailureUsesLegacyOutputWithoutExposingItsMessage(string? output, string expected)
    {
        TaskFailureDetails wrapper = new(
            "OrchestrationFailure", "The orchestration failed.", "private-stack-trace", null, null);

        Assert.Equal(expected, DecryptionFunctions.PublicFailureCode(null, output));
        Assert.Equal(expected, DecryptionFunctions.PublicFailureCode(wrapper, output));
    }

    [Fact]
    public void DurableFailureDoesNotExposeUnknownDetailsOrInspectTheirStackTraces()
    {
        TaskFailureDetails inner = new(
            "InnerFailure", "UNAPPROVED_CODE: private-token", "EXPORT_DENIED: private-stack-trace", null, null);
        TaskFailureDetails wrapper = new(
            "OrchestrationFailure", "private-token", "AUTHENTICATION_FAILED: private-stack-trace", inner, null);

        Assert.Equal("PROCESSING_FAILED", DecryptionFunctions.PublicFailureCode(wrapper, "private-token"));
    }

    private static DecryptionProcessor Processor(FakeSharePoint source, FakeDecryptor decryptor, FakeOutput output) =>
        new(source, decryptor, output, NullLogger<DecryptionProcessor>.Instance);

    private static ReaderOptions Options() => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        ClientId = "22222222-2222-2222-2222-222222222222",
        CertificateThumbprint = new string('A', 40),
        DelegatedUserEmail = "reader@example.com",
        SharePointDriveId = "b!EXAMPLE",
        SharePointHostName = "tenant.sharepoint.com",
        BlobServiceUri = "https://example.blob.core.windows.net",
        BlobContainerName = "decrypted-output"
    };

    private static JsonDocument Metadata(long size, string url = "https://tenant.sharepoint.com/download") =>
        JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = "01ABC", ["name"] = "file.docx", ["size"] = size, ["eTag"] = "version1",
            ["file"] = new { }, ["@microsoft.graph.downloadUrl"] = url
        }));

    private sealed class FakeSharePoint : ISharePointFileClient
    {
        public string? DownloadPath { get; private set; }
        public Task<SharePointDocument> GetDocumentAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(new SharePointDocument(itemId, "file.docx", 9, "version1", new Uri("https://tenant.sharepoint.com/download")));
        public async Task DownloadAsync(SharePointDocument document, string destination, CancellationToken cancellationToken)
        {
            DownloadPath = destination;
            await File.WriteAllTextAsync(destination, "encrypted", cancellationToken);
        }
    }

    private sealed class FakeDecryptor : IDocumentDecryptor
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        public async Task DecryptAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
        {
            Calls++;
            await File.WriteAllTextAsync(outputPath, "plaintext", cancellationToken);
            if (Fail)
                throw new IOException("private-token");
        }
    }

    private sealed class FakeOutput : IBlobOutputStore
    {
        public DecryptionResult? Existing { get; init; }
        public bool Fail { get; init; }
        public Azure.Identity.AuthenticationFailedException? AuthenticationFailure { get; init; }
        public bool AuthenticationFailureOnUpload { get; init; }
        public byte[]? Uploaded { get; private set; }
        public Task<DecryptionResult?> GetExistingAsync(Guid jobId, CancellationToken cancellationToken)
        {
            if (AuthenticationFailure is not null && !AuthenticationFailureOnUpload)
                throw AuthenticationFailure;
            return Task.FromResult(Existing);
        }
        public async Task<DecryptionResult> UploadAsync(Guid jobId, string filePath, CancellationToken cancellationToken)
        {
            if (AuthenticationFailure is not null && AuthenticationFailureOnUpload)
                throw AuthenticationFailure;
            if (Fail)
                throw new IOException("private-token");
            Uploaded = await File.ReadAllBytesAsync(filePath, cancellationToken);
            return new($"{jobId:N}/content", $"https://example.blob.core.windows.net/output/{jobId:N}/content", Uploaded.Length);
        }
    }
}
