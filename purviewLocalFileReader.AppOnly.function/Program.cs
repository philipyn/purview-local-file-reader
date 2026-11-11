using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using purviewLocalFileReader.AppOnly.function;

var builder = FunctionsApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration
        .AddJsonFile("appsettings.app-only.local.json", optional: true)
        .AddEnvironmentVariables();
}

builder.Services.AddSingleton<IValidateOptions<ReaderOptions>, ReaderOptionsValidator>();
builder.Services.AddOptions<ReaderOptions>()
    .Bind(builder.Configuration)
    .ValidateOnStart();
builder.Services.AddSingleton<AuthDelegateImplementation>();
builder.Services.AddSingleton<IGraphTokenProvider>(
    services => services.GetRequiredService<AuthDelegateImplementation>());
builder.Services.AddHttpClient<SharePointFileClient>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false
});
builder.Services.AddTransient<ISharePointFileClient>(
    services => services.GetRequiredService<SharePointFileClient>());
builder.Services.AddSingleton(services =>
{
    ReaderOptions options = services.GetRequiredService<IOptions<ReaderOptions>>().Value;
    return new BlobServiceClient(
        new Uri(options.BlobServiceUri),
        new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeInteractiveBrowserCredential = true
        })).GetBlobContainerClient(options.BlobContainerName);
});
builder.Services.AddSingleton<IBlobOutputStore, BlobOutputStore>();
builder.Services.AddSingleton<IDocumentDecryptor, MipDocumentDecryptor>();
builder.Services.AddTransient<DecryptionProcessor>();
builder.Build().Run();
