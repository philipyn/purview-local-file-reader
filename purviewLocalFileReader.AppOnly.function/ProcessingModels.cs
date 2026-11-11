namespace purviewLocalFileReader.AppOnly.function;

public sealed record FileReference(string DriveItemId)
{
    public static bool IsValidId(string? value) =>
        value is { Length: > 0 and <= 256 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '!' or '-' or '_');
}

public sealed record DecryptionJob(Guid JobId, string DriveItemId);
public sealed record DecryptionResult(string BlobName, string BlobUri, long Length);
public sealed record SharePointDocument(string Id, string Name, long Size, string ETag, Uri DownloadUri);

public sealed class ProcessingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class BoundedStreams
{
    public static async Task<long> CopyAsync(
        Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (read > limit - total)
            {
                throw new ProcessingException("FILE_TOO_LARGE", "The file exceeds the configured size limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }

        return total;
    }
}
