using Microsoft.InformationProtection;
using Microsoft.Extensions.Logging;

namespace purviewLocalFileReader.AppOnly.function;

public sealed class ConsentDelegateImplementation : IConsentDelegate
{
    private readonly ILogger _logger;

    public ConsentDelegateImplementation(ILogger logger) => _logger = logger;

    public Consent GetUserConsent(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && ServiceHosts.IsMipService(uri))
        {
            return Consent.Accept;
        }

        _logger.LogWarning("MIP requested consent for an unapproved service; access was denied.");
        return Consent.Reject;
    }
}