using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace purviewLocalFileReader.AppOnly.function;

public sealed class DecryptionFunctions(DecryptionProcessor processor, ILogger<DecryptionFunctions> logger)
{
    [Function(nameof(SubmitDecryption))]
    public async Task<HttpResponseData> SubmitDecryption(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "decryption-jobs")] HttpRequestData request,
        [DurableClient] DurableTaskClient client,
        CancellationToken cancellationToken)
    {
        FileReference? reference;
        try
        {
            using MemoryStream body = new();
            await BoundedStreams.CopyAsync(request.Body, body, 4096, cancellationToken);
            reference = JsonSerializer.Deserialize<FileReference>(body.ToArray(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            });
        }
        catch (JsonException)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest,
                new { error = "INVALID_JSON", message = "Send a JSON object containing only driveItemId." });
        }
        catch (ProcessingException)
        {
            return await JsonAsync(request, HttpStatusCode.RequestEntityTooLarge,
                new { error = "REQUEST_TOO_LARGE", message = "The request body must not exceed 4096 bytes." });
        }

        if (!FileReference.IsValidId(reference?.DriveItemId))
            return await JsonAsync(request, HttpStatusCode.BadRequest,
                new { error = "INVALID_REFERENCE", message = "A valid driveItemId from the configured library is required." });

        Guid jobId = Guid.NewGuid();
        await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(DecryptOrchestration), new DecryptionJob(jobId, reference!.DriveItemId),
            new StartOrchestrationOptions { InstanceId = jobId.ToString("N") }, cancellationToken);
        logger.LogInformation("Decryption job {JobId} accepted.", jobId);
        string statusPath = $"/api/decryption-jobs/{jobId:N}";
        HttpResponseData response = await JsonAsync(request, HttpStatusCode.Accepted,
            new { jobId = jobId.ToString("N"), statusUrl = statusPath });
        response.Headers.Add("Location", statusPath);
        response.Headers.Add("Retry-After", "5");
        return response;
    }

    [Function(nameof(GetDecryptionStatus))]
    public async Task<HttpResponseData> GetDecryptionStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "decryption-jobs/{jobId}")] HttpRequestData request,
        string jobId,
        [DurableClient] DurableTaskClient client,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(jobId, "N", out Guid parsedId))
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "INVALID_JOB_ID" });
        OrchestrationMetadata? metadata = await client.GetInstanceAsync(
            parsedId.ToString("N"), getInputsAndOutputs: true, cancellationToken);
        if (metadata is null)
            return await JsonAsync(request, HttpStatusCode.NotFound, new { error = "JOB_NOT_FOUND" });

        DecryptionResult? result = metadata.RuntimeStatus == OrchestrationRuntimeStatus.Completed
            ? JsonSerializer.Deserialize<DecryptionResult>(metadata.SerializedOutput!, JsonSerializerOptions.Web) ??
                throw new InvalidOperationException("The completed job has no result.")
            : null;
        string? error = metadata.RuntimeStatus is OrchestrationRuntimeStatus.Failed or OrchestrationRuntimeStatus.Terminated
            ? PublicFailureCode(metadata.FailureDetails, metadata.SerializedOutput) : null;
        return await JsonAsync(request, HttpStatusCode.OK, new
        {
            jobId,
            status = metadata.RuntimeStatus.ToString(),
            result,
            error
        });
    }

    [Function(nameof(DecryptOrchestration))]
    public static Task<DecryptionResult> DecryptOrchestration(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        DecryptionJob job = context.GetInput<DecryptionJob>() ??
            throw new InvalidOperationException("The decryption job input is missing.");
        return context.CallActivityAsync<DecryptionResult>(nameof(DecryptSharePointFile), job);
    }

    [Function(nameof(DecryptSharePointFile))]
    public Task<DecryptionResult> DecryptSharePointFile(
        [ActivityTrigger] DecryptionJob job, CancellationToken cancellationToken) =>
        processor.ProcessAsync(job, cancellationToken);

    public static string PublicFailureCode(TaskFailureDetails? failureDetails, string? serializedOutput)
    {
        for (TaskFailureDetails? failure = failureDetails; failure is not null; failure = failure.InnerFailure)
        {
            string? code = FindPublicFailureCode(failure.ErrorMessage);
            if (code is not null)
                return code;
        }

        return PublicFailureCode(serializedOutput);
    }

    public static string PublicFailureCode(string? failure) =>
        FindPublicFailureCode(failure) ?? "PROCESSING_FAILED";

    private static string? FindPublicFailureCode(string? failure)
    {
        string[] knownCodes =
        [
            "INVALID_REFERENCE", "FILE_TOO_LARGE", "SOURCE_CHANGED", "SOURCE_NOT_FOUND",
            "NOT_A_FILE", "INVALID_METADATA", "UNAPPROVED_DOWNLOAD", "DOWNLOAD_FAILED",
            "SHAREPOINT_ACCESS_DENIED", "SHAREPOINT_UNAVAILABLE", "EXPORT_DENIED",
            "NOT_PROTECTED", "UNSUPPORTED_NAME", "UNSUPPORTED_FILE", "DECRYPTION_ACCESS_DENIED",
            "PUBLIC_OUTPUT_CONTAINER", "OUTPUT_CONFLICT", "PROCESSING_CANCELLED",
            "UNSUPPORTED_PLATFORM", "NATIVE_DEPENDENCY_MISSING", "AUTHENTICATION_FAILED", "OUTPUT_STORAGE_UNAVAILABLE"
        ];
        return knownCodes.FirstOrDefault(code => failure?.Contains(code + ":", StringComparison.Ordinal) == true);
    }

    private static async Task<HttpResponseData> JsonAsync(
        HttpRequestData request, HttpStatusCode status, object payload)
    {
        HttpResponseData response = request.CreateResponse(status);
        await response.WriteAsJsonAsync(payload);
        response.Headers.Add("Cache-Control", "no-store");
        return response;
    }
}
