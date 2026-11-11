using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace purviewLocalFileReader.AppOnly.function;

public interface IBlobOutputStore
{
    Task<DecryptionResult?> GetExistingAsync(Guid jobId, CancellationToken cancellationToken);
    Task<DecryptionResult> UploadAsync(Guid jobId, string filePath, CancellationToken cancellationToken);
}

public sealed class BlobOutputStore(BlobContainerClient container) : IBlobOutputStore
{
    public async Task<DecryptionResult?> GetExistingAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await RequirePrivateContainerAsync(cancellationToken);
        BlobClient blob = GetBlob(jobId);
        try
        {
            BlobProperties blobProperties = (await blob.GetPropertiesAsync(
                cancellationToken: cancellationToken)).Value;
            return Result(blob, blobProperties.ContentLength);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<DecryptionResult> UploadAsync(
        Guid jobId, string filePath, CancellationToken cancellationToken)
    {
        await RequirePrivateContainerAsync(cancellationToken);
        BlobClient blob = GetBlob(jobId);
        await using FileStream source = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            await blob.UploadAsync(source, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = "application/octet-stream",
                    ContentDisposition = "attachment",
                    CacheControl = "no-store"
                }
            }, cancellationToken);
            return Result(blob, source.Length);
        }
        catch (RequestFailedException ex) when (ex.Status == 412 || ex.ErrorCode == "BlobAlreadyExists")
        {
            return await GetExistingAsync(jobId, cancellationToken) ??
                throw new ProcessingException("OUTPUT_CONFLICT", "The output changed during processing; investigate the job.");
        }
    }

    private async Task RequirePrivateContainerAsync(CancellationToken cancellationToken)
    {
        BlobContainerProperties properties = (await container.GetPropertiesAsync(
            cancellationToken: cancellationToken)).Value;
        if (properties.PublicAccess != PublicAccessType.None)
            throw new ProcessingException("PUBLIC_OUTPUT_CONTAINER", "The output container must not allow public access.");
    }

    private BlobClient GetBlob(Guid jobId) => container.GetBlobClient($"{jobId:N}/content");
    private static DecryptionResult Result(BlobClient blob, long length) =>
        new(blob.Name, blob.Uri.AbsoluteUri, length);
}
