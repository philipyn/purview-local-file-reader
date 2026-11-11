using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace purviewLocalFileReader.AppOnly.function;

public interface ISharePointFileClient
{
    Task<SharePointDocument> GetDocumentAsync(string itemId, CancellationToken cancellationToken);
    Task DownloadAsync(SharePointDocument document, string destination, CancellationToken cancellationToken);
}

public sealed class SharePointFileClient(
    HttpClient httpClient,
    IGraphTokenProvider authentication,
    IOptions<ReaderOptions> options) : ISharePointFileClient
{
    private readonly ReaderOptions _options = options.Value;

    public async Task<SharePointDocument> GetDocumentAsync(
        string itemId, CancellationToken cancellationToken)
    {
        if (!FileReference.IsValidId(itemId))
            throw new ProcessingException("INVALID_REFERENCE", "A valid driveItemId is required.");

        string token = await authentication.AcquireGraphTokenAsync(cancellationToken);
        string path = $"https://graph.microsoft.com/v1.0/drives/" +
            $"{Uri.EscapeDataString(_options.SharePointDriveId)}/items/{Uri.EscapeDataString(itemId)}";
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        CheckStatus(response);
        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream metadata = new();
        await BoundedStreams.CopyAsync(content, metadata, 1024 * 1024, cancellationToken);
        metadata.Position = 0;
        using JsonDocument json = await JsonDocument.ParseAsync(metadata, cancellationToken: cancellationToken);
        return ParseDocument(json.RootElement, itemId, _options);
    }

    public static SharePointDocument ParseDocument(JsonElement item, string expectedId, ReaderOptions options)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("file", out JsonElement file) || file.ValueKind != JsonValueKind.Object ||
            item.TryGetProperty("remoteItem", out _) || item.TryGetProperty("deleted", out _))
            throw new ProcessingException("NOT_A_FILE", "The reference must identify a file in the configured library.");
        string? id = GetString(item, "id");
        string? name = GetString(item, "name");
        string? etag = GetString(item, "eTag");
        if (id != expectedId || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(etag) ||
            !item.TryGetProperty("size", out JsonElement sizeElement) ||
            sizeElement.ValueKind != JsonValueKind.Number ||
            !sizeElement.TryGetInt64(out long size) || size <= 0)
            throw new ProcessingException("INVALID_METADATA", "SharePoint returned incomplete file metadata.");
        if (size > options.MaxFileBytes)
            throw new ProcessingException("FILE_TOO_LARGE", "The SharePoint file exceeds the configured size limit.");
        if (!Uri.TryCreate(GetString(item, "@microsoft.graph.downloadUrl"), UriKind.Absolute, out Uri? download) ||
            !ServiceHosts.IsDownloadUri(download, options.SharePointHostName))
            throw new ProcessingException("UNAPPROVED_DOWNLOAD", "SharePoint returned an unapproved download host.");
        return new SharePointDocument(id, name, size, etag, download);
    }

    public async Task DownloadAsync(
        SharePointDocument document, string destination, CancellationToken cancellationToken)
    {
        Uri downloadUri = document.DownloadUri;
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            if (!ServiceHosts.IsDownloadUri(downloadUri, _options.SharePointHostName))
                throw new ProcessingException("UNAPPROVED_DOWNLOAD", "The download destination is not approved.");
            // Preauthenticated download URLs must never receive the Graph bearer token.
            using HttpRequestMessage request = new(HttpMethod.Get, downloadUri);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                Uri? location = response.Headers.Location;
                if (location is null)
                    throw new ProcessingException("DOWNLOAD_FAILED", "SharePoint returned an invalid redirect.");
                downloadUri = location.IsAbsoluteUri ? location : new Uri(downloadUri, location);
                continue;
            }

            CheckStatus(response);
            if (response.Content.Headers.ContentLength > _options.MaxFileBytes)
                throw new ProcessingException("FILE_TOO_LARGE", "The download exceeds the configured size limit.");
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                long length = await BoundedStreams.CopyAsync(source, output, _options.MaxFileBytes, cancellationToken);
                if (length != document.Size)
                    throw new ProcessingException("SOURCE_CHANGED", "The SharePoint file changed; submit it again.");
            }

            SharePointDocument current = await GetDocumentAsync(document.Id, cancellationToken);
            if (current.ETag != document.ETag)
                throw new ProcessingException("SOURCE_CHANGED", "The SharePoint file changed; submit it again.");
            return;
        }

        throw new ProcessingException("DOWNLOAD_FAILED", "The download exceeded the redirect limit.");
    }

    private static string? GetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static void CheckStatus(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ProcessingException("SHAREPOINT_ACCESS_DENIED", "The application cannot read this SharePoint file.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ProcessingException("SOURCE_NOT_FOUND", "The SharePoint file was not found.");
        if (!response.IsSuccessStatusCode)
            throw new ProcessingException("SHAREPOINT_UNAVAILABLE", "The SharePoint request failed; check service availability and permissions.");
    }
}
