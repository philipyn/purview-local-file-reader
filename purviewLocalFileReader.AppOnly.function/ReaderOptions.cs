using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace purviewLocalFileReader.AppOnly.function;

public sealed class ReaderOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string CertificateThumbprint { get; set; } = "";
    public string DelegatedUserEmail { get; set; } = "";
    public string SharePointDriveId { get; set; } = "";
    public string SharePointHostName { get; set; } = "";
    public string BlobServiceUri { get; set; } = "";
    public string BlobContainerName { get; set; } = "";
    public int MaxFileSizeMiB { get; set; } = 25;
    public int MaxDecryptedSizeMiB { get; set; } = 100;

    public long MaxFileBytes => (long)MaxFileSizeMiB * 1024 * 1024;
    public long MaxDecryptedBytes => (long)MaxDecryptedSizeMiB * 1024 * 1024;
}

public sealed class ReaderOptionsValidator : IValidateOptions<ReaderOptions>
{
    public ValidateOptionsResult Validate(string? name, ReaderOptions options)
    {
        List<string> errors = [];
        if (!Guid.TryParse(options.TenantId, out Guid tenant) || tenant == Guid.Empty)
            errors.Add("TenantId must be a non-empty GUID.");
        if (!Guid.TryParse(options.ClientId, out Guid client) || client == Guid.Empty)
            errors.Add("ClientId must be a non-empty GUID.");
        string thumbprint = options.CertificateThumbprint.Replace(" ", "");
        if (thumbprint.Length != 40 || !thumbprint.All(char.IsAsciiHexDigit))
            errors.Add("CertificateThumbprint must contain 40 hexadecimal digits.");
        if (!MailAddress.TryCreate(options.DelegatedUserEmail, out MailAddress? address) ||
            address.Address != options.DelegatedUserEmail)
            errors.Add("DelegatedUserEmail must be an email address.");
        if (!FileReference.IsValidId(options.SharePointDriveId))
            errors.Add("SharePointDriveId must be a Microsoft Graph document-library drive ID.");
        if (!ServiceHosts.IsSharePointHost(options.SharePointHostName))
            errors.Add("SharePointHostName must be your exact public-cloud SharePoint hostname.");
        if (!Uri.TryCreate(options.BlobServiceUri, UriKind.Absolute, out Uri? blobUri) ||
            !ServiceHosts.IsAzureBlobService(blobUri))
            errors.Add("BlobServiceUri must be an HTTPS public-cloud Azure Blob service endpoint.");
        if (!IsContainerName(options.BlobContainerName))
            errors.Add("BlobContainerName must be a valid, existing private container name.");
        if (options.MaxFileSizeMiB is < 1 or > 1024)
            errors.Add("MaxFileSizeMiB must be between 1 and 1024.");
        if (options.MaxDecryptedSizeMiB < options.MaxFileSizeMiB ||
            options.MaxDecryptedSizeMiB > 4096)
            errors.Add("MaxDecryptedSizeMiB must be at least MaxFileSizeMiB and at most 4096.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsContainerName(string name) =>
        name.Length is >= 3 and <= 63 &&
        char.IsAsciiLetterOrDigit(name[0]) && char.IsAsciiLetterOrDigit(name[^1]) &&
        name.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'z' || c == '-') &&
        !name.Contains("--", StringComparison.Ordinal);
}

public static class ServiceHosts
{
    public static bool IsAuthority(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host is "login.microsoftonline.com" or "login.windows.net";

    public static bool IsMipResource(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.Host is "aadrm.com" or "api.aadrm.com" or "syncservice.o365syncservice.com";

    private static bool HasSuffix(string host, string suffix) =>
        host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

    public static bool IsMipService(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        new[] { "aadrm.com", "protection.outlook.com", "protection.office.com" }
            .Any(suffix => HasSuffix(uri.Host, suffix));

    public static bool IsSharePointHost(string host) =>
        Uri.CheckHostName(host) == UriHostNameType.Dns &&
        host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsAzureBlobService(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath == "/" && uri.Query.Length == 0 &&
        uri.Fragment.Length == 0 && uri.UserInfo.Length == 0;

    public static bool IsDownloadUri(Uri uri, string approvedHost) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 &&
        uri.Host.Equals(approvedHost, StringComparison.OrdinalIgnoreCase);
}
