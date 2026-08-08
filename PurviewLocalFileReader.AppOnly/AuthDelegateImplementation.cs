using System;
using System.Linq;
using Microsoft.Identity.Client;
using Microsoft.InformationProtection;

public sealed class AuthDelegateImplementation : IAuthDelegate
{
    private readonly ApplicationInfo _appInfo;
    private readonly string _tenantId;
    private IPublicClientApplication? _app;

    public AuthDelegateImplementation(
        ApplicationInfo appInfo,
        string tenantId)
    {
        _appInfo = appInfo;
        _tenantId = tenantId;
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

        Uri authorityUri = new(authority);

        string tenantAuthority =
            $"https://{authorityUri.Host}/{_tenantId}";

        _app ??= PublicClientApplicationBuilder
            .Create(_appInfo.ApplicationId)
            .WithAuthority(tenantAuthority)
            .WithDefaultRedirectUri()
            .Build();

        string scope = resource.EndsWith("/", StringComparison.Ordinal)
            ? $"{resource}.default"
            : $"{resource}/.default";

        IAccount? account = _app
            .GetAccountsAsync()
            .GetAwaiter()
            .GetResult()
            .FirstOrDefault();

        AuthenticationResult result = _app
            .AcquireTokenInteractive(new[] { scope })
            .WithAccount(account)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync()
            .GetAwaiter()
            .GetResult();

        return result.AccessToken;
    }
}