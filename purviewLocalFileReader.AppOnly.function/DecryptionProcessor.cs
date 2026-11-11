using Microsoft.Extensions.Logging;
using Microsoft.InformationProtection.Exceptions;

namespace purviewLocalFileReader.AppOnly.function;

public sealed class DecryptionProcessor(
    ISharePointFileClient sharePoint,
    IDocumentDecryptor decryptor,
    IBlobOutputStore output,
    ILogger<DecryptionProcessor> logger)
{
    public async Task<DecryptionResult> ProcessAsync(DecryptionJob job, CancellationToken cancellationToken)
    {
        if (job.JobId == Guid.Empty || !FileReference.IsValidId(job.DriveItemId))
            throw new ProcessingException("INVALID_REFERENCE", "The job reference is invalid.");

        try
        {
            DecryptionResult? existing = await output.GetExistingAsync(job.JobId, cancellationToken);
            if (existing is not null)
                return existing;

            SharePointDocument document = await sharePoint.GetDocumentAsync(job.DriveItemId, cancellationToken);
            using TemporaryWorkspace workspace = new();
            string extension = Path.GetExtension(document.Name);
            if (extension.Length > 16 || extension.Any(c => c != '.' && !char.IsAsciiLetterOrDigit(c)))
                throw new ProcessingException("UNSUPPORTED_NAME", "The source filename extension is not supported.");
            string input = Path.Combine(workspace.DirectoryPath, "input" + extension);
            string decrypted = Path.Combine(workspace.DirectoryPath, "plaintext");
            await sharePoint.DownloadAsync(document, input, cancellationToken);
            await decryptor.DecryptAsync(input, decrypted, cancellationToken);
            DecryptionResult result = await output.UploadAsync(job.JobId, decrypted, cancellationToken);
            logger.LogInformation("Decryption job {JobId} uploaded {Length} bytes.", job.JobId, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            // Do not persist native errors or preauthenticated download URLs in Durable history.
            string code = ex switch
            {
                ProcessingException failure => failure.Code,
                AccessDeniedException or NoPermissionsException or UnauthorizedAccessException => "DECRYPTION_ACCESS_DENIED",
                PlatformNotSupportedException => "UNSUPPORTED_PLATFORM",
                DllNotFoundException => "NATIVE_DEPENDENCY_MISSING",
                Microsoft.Identity.Client.MsalException or Azure.Identity.AuthenticationFailedException => "AUTHENTICATION_FAILED",
                Azure.RequestFailedException => "OUTPUT_STORAGE_UNAVAILABLE",
                NotSupportedException or ContentFormatNotSupportedException => "UNSUPPORTED_FILE",
                OperationCanceledException => "PROCESSING_CANCELLED",
                _ => "PROCESSING_FAILED"
            };
            logger.LogError("Decryption job {JobId} failed with {Code} ({ExceptionType}).",
                job.JobId, code, ex.GetType().Name);
            throw new ProcessingException(code, $"{code}: The job failed. Contact the operator with the job ID.");
        }
    }
}

public sealed class TemporaryWorkspace : IDisposable
{
    public string DirectoryPath { get; } =
        Path.Combine(Path.GetTempPath(), "PurviewLocalFileReader.Function", Guid.NewGuid().ToString("N"));

    public TemporaryWorkspace() => Directory.CreateDirectory(DirectoryPath);

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
