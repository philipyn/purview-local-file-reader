using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.InformationProtection;
using Microsoft.InformationProtection.File;
using Microsoft.InformationProtection.Protection;

namespace purviewLocalFileReader.AppOnly.function;

public interface IDocumentDecryptor
{
    Task DecryptAsync(string inputPath, string outputPath, CancellationToken cancellationToken);
}

public sealed class MipDocumentDecryptor(
    AuthDelegateImplementation authentication,
    IOptions<ReaderOptions> options,
    ILogger<MipDocumentDecryptor> logger) : IDocumentDecryptor, IDisposable
{
    private readonly ReaderOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1);
    private MipContext? _context;
    private IFileProfile? _profile;
    private IFileEngine? _engine;
    private string? _sdkDirectory;

    public async Task DecryptAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await InitializeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            using IFileHandler handler = await _engine!.CreateFileHandlerAsync(inputPath, inputPath, false);
            if (handler.Protection is null)
                throw new ProcessingException("NOT_PROTECTED", "The selected file is not MIP protected.");
            if (!handler.Protection.AccessCheck(Rights.Export))
                throw new ProcessingException("EXPORT_DENIED", "The configured user is not permitted to export unprotected content.");

            string temporary = await handler.GetDecryptedTemporaryFileAsync();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using FileStream source = new(temporary, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (source.Length > _options.MaxDecryptedBytes)
                    throw new ProcessingException("FILE_TOO_LARGE", "The decrypted file exceeds the configured output limit.");
                await using FileStream destination = new(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await BoundedStreams.CopyAsync(source, destination, _options.MaxDecryptedBytes, cancellationToken);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InitializeAsync()
    {
        if (_engine is not null)
            return;
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("The decryption worker requires Windows x64.");

        try
        {
            MIP.Initialize(MipComponent.File);
            ApplicationInfo appInfo = new()
            {
                ApplicationId = _options.ClientId,
                ApplicationName = "Purview SharePoint Decryption Function",
                ApplicationVersion = "1.0.0"
            };
            _sdkDirectory ??= Path.Combine(Path.GetTempPath(), "PurviewLocalFileReader.Mip", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sdkDirectory);
            MipConfiguration configuration = new(
                appInfo, _sdkDirectory, Microsoft.InformationProtection.LogLevel.Warning, false, CacheStorageType.InMemory);
            _context = MIP.CreateMipContext(configuration);
            FileProfileSettings profileSettings = new(
                _context, CacheStorageType.InMemory, new ConsentDelegateImplementation(logger))
            {
                CanCacheLicenses = false
            };
            _profile = await MIP.LoadFileProfileAsync(profileSettings);
            FileEngineSettings settings = new(_options.DelegatedUserEmail, authentication, "", "en-US")
            {
                Identity = new Identity(_options.DelegatedUserEmail),
                DelegatedUserEmail = _options.DelegatedUserEmail
            };
            _engine = await _profile.AddEngineAsync(settings);
        }
        catch
        {
            ShutDown();
            throw;
        }
    }

    private void ShutDown()
    {
        _engine = null;
        _profile = null;
        _context?.ShutDown();
        _context = null;
        if (_sdkDirectory is not null && Directory.Exists(_sdkDirectory))
            Directory.Delete(_sdkDirectory, recursive: true);
        _sdkDirectory = null;
    }

    public void Dispose()
    {
        ShutDown();
        _gate.Dispose();
    }
}
