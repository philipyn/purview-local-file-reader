using Microsoft.InformationProtection;

public sealed class ConsentDelegateImplementation : IConsentDelegate
{
    public Consent GetUserConsent(string url)
    {
        Console.WriteLine($"MIP consent requested for: {url}");
        return Consent.Accept;
    }
}