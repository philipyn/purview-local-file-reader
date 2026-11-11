using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using purviewLocalFileReader.AppOnly.function;
using Xunit;

namespace PurviewLocalFileReader.AppOnly.Tests;

public sealed class SharePointDownloadTests
{
    [Fact]
    public async Task DownloadNeverForwardsGraphBearerTokenAndChecksSourceVersion()
    {
        FakeHandler handler = new();
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("protected"))
        });
        handler.Responses.Enqueue(Metadata("v1"));
        using HttpClient http = new(handler);
        SharePointFileClient client = Client(http);
        using TemporaryWorkspace workspace = new();
        string destination = Path.Combine(workspace.DirectoryPath, "input");

        await client.DownloadAsync(Document(), destination, default);

        Assert.Equal("protected", await File.ReadAllTextAsync(destination));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("tenant.sharepoint.com", handler.Requests[0].Uri.Host);
        Assert.Null(handler.Requests[0].Authorization);
        Assert.Equal("graph.microsoft.com", handler.Requests[1].Uri.Host);
        Assert.Equal("Bearer graph-token-for-test", handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task RejectsRedirectBeforeSendingRequestToAnotherHost()
    {
        FakeHandler handler = new();
        HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://attacker.example/file");
        handler.Responses.Enqueue(redirect);
        using HttpClient http = new(handler);
        using TemporaryWorkspace workspace = new();

        ProcessingException failure = await Assert.ThrowsAsync<ProcessingException>(() =>
            Client(http).DownloadAsync(Document(), Path.Combine(workspace.DirectoryPath, "input"), default));

        Assert.Equal("UNAPPROVED_DOWNLOAD", failure.Code);
        Assert.Single(handler.Requests);
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath));
    }

    [Fact]
    public async Task RejectsSourceChangedDuringDownload()
    {
        FakeHandler handler = new();
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("protected"))
        });
        handler.Responses.Enqueue(Metadata("v2"));
        using HttpClient http = new(handler);
        using TemporaryWorkspace workspace = new();

        ProcessingException failure = await Assert.ThrowsAsync<ProcessingException>(() =>
            Client(http).DownloadAsync(Document(), Path.Combine(workspace.DirectoryPath, "input"), default));

        Assert.Equal("SOURCE_CHANGED", failure.Code);
    }

    [Fact]
    public async Task EnforcesStreamingLimitWithoutContentLength()
    {
        FakeHandler handler = new();
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[1024 * 1024 + 1])
        });
        using HttpClient http = new(handler);
        using TemporaryWorkspace workspace = new();
        string destination = Path.Combine(workspace.DirectoryPath, "input");

        ProcessingException failure = await Assert.ThrowsAsync<ProcessingException>(() =>
            Client(http).DownloadAsync(Document(), destination, default));

        Assert.Equal("FILE_TOO_LARGE", failure.Code);
        Assert.True(new FileInfo(destination).Length <= 1024 * 1024);
    }

    [Fact]
    public async Task RejectsAdvertisedOversizeBeforeWriting()
    {
        FakeHandler handler = new();
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[1024 * 1024 + 1])
        });
        using HttpClient http = new(handler);
        using TemporaryWorkspace workspace = new();

        ProcessingException failure = await Assert.ThrowsAsync<ProcessingException>(() =>
            Client(http).DownloadAsync(Document(), Path.Combine(workspace.DirectoryPath, "input"), default));

        Assert.Equal("FILE_TOO_LARGE", failure.Code);
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath));
    }

    [Fact]
    public async Task MetadataRequestCannotUseAnotherLibraryOrCallerSuppliedUrl()
    {
        FakeHandler handler = new();
        handler.Responses.Enqueue(Metadata("v1"));
        using HttpClient http = new(handler);

        SharePointDocument document = await Client(http).GetDocumentAsync("01ABC", default);

        Assert.Equal("01ABC", document.Id);
        Assert.Equal("/v1.0/drives/b%21APPROVED/items/01ABC", handler.Requests.Single().Uri.AbsolutePath);
    }

    private static SharePointFileClient Client(HttpClient http) => new(http, new FakeTokenProvider(),
        Options.Create(new ReaderOptions
        {
            SharePointDriveId = "b!APPROVED",
            SharePointHostName = "tenant.sharepoint.com",
            MaxFileSizeMiB = 1
        }));

    private static SharePointDocument Document() =>
        new("01ABC", "file.docx", 9, "v1", new Uri("https://tenant.sharepoint.com/download?secret=test"));

    private static HttpResponseMessage Metadata(string etag) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = "01ABC", ["name"] = "file.docx", ["size"] = 9, ["eTag"] = etag,
            ["file"] = new { }, ["@microsoft.graph.downloadUrl"] = "https://tenant.sharepoint.com/download"
        }), Encoding.UTF8, "application/json")
    };

    private sealed class FakeTokenProvider : IGraphTokenProvider
    {
        public Task<string> AcquireGraphTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("graph-token-for-test");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> Responses { get; } = new();
        public List<(Uri Uri, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            return Task.FromResult(Responses.Dequeue());
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();
    }
}
