using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Core.FunctionMetadata;
using purviewLocalFileReader.AppOnly.function;
using Xunit;

namespace PurviewLocalFileReader.AppOnly.Tests;

public sealed class FunctionMetadataTests
{
    [Fact]
    public async Task GeneratedMetadataRegistersProtectedHttpAndDurableBindings()
    {
        Assert.Equal("purviewLocalFileReader.AppOnly.function",
            typeof(DecryptionFunctions).Assembly.GetName().Name);
        Assert.Equal("purviewLocalFileReader.AppOnly.function", typeof(DecryptionFunctions).Namespace);
        Type providerType = Assert.Single(typeof(DecryptionFunctions).Assembly.GetTypes(),
            type => !type.IsAbstract && typeof(IFunctionMetadataProvider).IsAssignableFrom(type));
        IFunctionMetadataProvider provider = Assert.IsAssignableFrom<IFunctionMetadataProvider>(
            Activator.CreateInstance(providerType, nonPublic: true));
        var metadata = await provider.GetFunctionMetadataAsync("");
        Assert.Equal(
            ["DecryptOrchestration", "DecryptSharePointFile", "GetDecryptionStatus", "SubmitDecryption"],
            metadata.Select(function => function.Name ?? throw new InvalidOperationException("Missing function name."))
                .OrderBy(name => name, StringComparer.Ordinal).ToArray());

        foreach (IFunctionMetadata function in metadata)
        {
            var bindings = function.RawBindings;
            Assert.NotNull(bindings);
            foreach (string bindingJson in bindings)
            {
                using JsonDocument binding = JsonDocument.Parse(bindingJson);
                string type = binding.RootElement.GetProperty("type").GetString()!;
                if (type != "httpTrigger")
                    continue;
                Assert.Equal("Function", binding.RootElement.GetProperty("authLevel").GetString(), ignoreCase: true);
                Assert.StartsWith("decryption-jobs", binding.RootElement.GetProperty("route").GetString());
            }
        }
        Assert.Contains(Assert.IsAssignableFrom<IEnumerable<string>>(
                metadata.Single(function => function.Name == "DecryptOrchestration").RawBindings),
            binding => binding.Contains("orchestrationTrigger", StringComparison.Ordinal));
        Assert.Contains(Assert.IsAssignableFrom<IEnumerable<string>>(
                metadata.Single(function => function.Name == "DecryptSharePointFile").RawBindings),
            binding => binding.Contains("activityTrigger", StringComparison.Ordinal));
    }
}
