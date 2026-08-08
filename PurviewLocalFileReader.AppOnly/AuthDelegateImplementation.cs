using System;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Identity.Client;
using Microsoft.InformationProtection;

public sealed class AuthDelegateImplementation : IAuthDelegate
{
    private readonly ApplicationInfo _appInfo;
    private readonly string _tenantId;
    private readonly string _certificateThumbprint;
    private IConfidentialClientApplication? _app;

    public AuthDelegateImplementation(
        ApplicationInfo appInfo,
        string tenantId,
        string certificateThumbprint)
    {
        _appInfo = appInfo;
        _tenantId = tenantId;
        _certificateThumbprint = certificateThumbprint;
    }

    public string AcquireToken(
        Identity identity,
        string authority,
        string resource,
        string claims)
    {
        if (string.IsNullOrWhiteSpace(_tenantId))
        {
            throw new InvalidOperationException(
                "TenantId is required for MIP authentication.");
        }

        if (string.IsNullOrWhiteSpace(_certificateThumbprint))
        {
            throw new InvalidOperationException(
                "CertificateThumbprint is required for app-only authentication.");
        }

        X509Certificate2 certificate = FindCertificate(
            _certificateThumbprint);

        Uri authorityUri = new(authority);

        string tenantAuthority =
            $"https://{authorityUri.Host}/{_tenantId}";

        _app ??= ConfidentialClientApplicationBuilder
            .Create(_appInfo.ApplicationId)
            .WithAuthority(tenantAuthority)
            .WithCertificate(certificate)
            .Build();

        string scope = resource.EndsWith("/", StringComparison.Ordinal)
            ? $"{resource}.default"
            : $"{resource}/.default";

        AuthenticationResult result = _app
            .AcquireTokenForClient(new[] { scope })
            .ExecuteAsync()
            .GetAwaiter()
            .GetResult();

        return result.AccessToken;
    }

    private static X509Certificate2 FindCertificate(
        string configuredThumbprint)
    {
        string expectedThumbprint = configuredThumbprint
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();

        using X509Store store = new(
            StoreName.My,
            StoreLocation.CurrentUser);

        store.Open(OpenFlags.ReadOnly);

        X509Certificate2? certificate = store.Certificates
            .Find(
                X509FindType.FindByThumbprint,
                expectedThumbprint,
                validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault();

        if (certificate is null)
        {
            throw new InvalidOperationException(
                "The configured certificate was not found in " +
                "CurrentUser\\My.");
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();

            throw new InvalidOperationException(
                "The configured certificate does not have a private key.");
        }

        return certificate;
    }
}