using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.InformationProtection;

namespace purviewLocalFileReader.AppOnly.function;

public interface IGraphTokenProvider
{
    Task<string> AcquireGraphTokenAsync(CancellationToken cancellationToken);
}

public sealed class AuthDelegateImplementation : IAuthDelegate, IGraphTokenProvider, IDisposable
{
    private readonly ReaderOptions _options;
    private readonly Lazy<(X509Certificate2 Certificate, IConfidentialClientApplication Client)> _credentials;

    public AuthDelegateImplementation(IOptions<ReaderOptions> options)
    {
        _options = options.Value;
        _credentials = new(CreateCredentials);
    }

    public string AcquireToken(Identity identity, string authority, string resource, string claims)
    {
        if (!Uri.TryCreate(authority, UriKind.Absolute, out Uri? authorityUri) ||
            !ServiceHosts.IsAuthority(authorityUri) ||
            !Uri.TryCreate(resource, UriKind.Absolute, out Uri? resourceUri) ||
            !ServiceHosts.IsMipResource(resourceUri))
        {
            throw new InvalidOperationException("The MIP authentication service is not approved.");
        }

        var request = _credentials.Value.Client
            .AcquireTokenForClient([$"{resource.TrimEnd('/')}/.default"]);
        if (!string.IsNullOrEmpty(claims))
        {
            request = request.WithClaims(claims);
        }

        return request.ExecuteAsync().GetAwaiter().GetResult().AccessToken;
    }

    public async Task<string> AcquireGraphTokenAsync(CancellationToken cancellationToken)
    {
        AuthenticationResult result = await _credentials.Value.Client
            .AcquireTokenForClient(["https://graph.microsoft.com/.default"])
            .ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }

    private (X509Certificate2, IConfidentialClientApplication) CreateCredentials()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This MIP package and certificate loader require Windows x64.");
        }

        string thumbprint = _options.CertificateThumbprint.Replace(" ", "").Trim();
        using X509Store store = new(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        X509Certificate2Collection matches = store.Certificates.Find(
            X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        X509Certificate2? selected = null;
        foreach (X509Certificate2 certificate in matches)
        {
            if (selected is null && certificate.HasPrivateKey &&
                certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
                certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow)
            {
                selected = certificate;
            }
            else
            {
                certificate.Dispose();
            }
        }

        if (selected is null)
        {
            throw new InvalidOperationException(
                "A currently valid certificate with a private key was not found in CurrentUser\\My.");
        }

        try
        {
            IConfidentialClientApplication client = ConfidentialClientApplicationBuilder
                .Create(_options.ClientId)
                .WithAuthority($"https://login.microsoftonline.com/{_options.TenantId}")
                .WithCertificate(selected)
                .Build();
            return (selected, client);
        }
        catch
        {
            selected.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_credentials.IsValueCreated)
        {
            _credentials.Value.Certificate.Dispose();
        }
    }
}
