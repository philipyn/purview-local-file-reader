using System.Text.Json;
using Microsoft.InformationProtection;
using Microsoft.InformationProtection.File;


string configurationPath = Path.Combine(
    AppContext.BaseDirectory,
    "appsettings.app-only.local.json");
const string mipDataPath = "mip_data";

if (!File.Exists(configurationPath))
{
    Console.Error.WriteLine(
        $"Configuration file not found: {configurationPath}");

    return 1;
}

LocalConfiguration? configuration;

try
{
    string json = await File.ReadAllTextAsync(configurationPath);

    configuration = JsonSerializer.Deserialize<LocalConfiguration>(
        json,
        new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
}
catch (JsonException)
{
    Console.Error.WriteLine(
        "The configuration file contains invalid JSON.");

    return 1;
}

if (configuration is null ||
    string.IsNullOrWhiteSpace(configuration.TenantId) ||
    string.IsNullOrWhiteSpace(configuration.ClientId))
{
    Console.Error.WriteLine(
        "Configuration must contain non-empty TenantId and ClientId values.");

    return 1;
}

MipContext? mipContext = null;
IFileProfile? fileProfile = null;
IFileEngine? fileEngine = null;

try
{
    Console.WriteLine("Initializing Microsoft Information Protection SDK...");

    MIP.Initialize(MipComponent.File);

    ApplicationInfo appInfo = new()
    {
        ApplicationId = configuration.ClientId,
        ApplicationName = "Purview Local File Reader",
        ApplicationVersion = "1.0.0"
    };

AuthDelegateImplementation authDelegate =
    new(
        appInfo,
        configuration.TenantId,
        configuration.CertificateThumbprint);

    ConsentDelegateImplementation consentDelegate = new();

    MipConfiguration mipConfiguration = new(
        appInfo,
        mipDataPath,
        LogLevel.Trace,
        false,
        CacheStorageType.OnDiskEncrypted);

    mipContext = MIP.CreateMipContext(mipConfiguration);

    FileProfileSettings profileSettings = new(
        mipContext,
        CacheStorageType.OnDiskEncrypted,
        consentDelegate);

    fileProfile = await MIP.LoadFileProfileAsync(profileSettings);
    Console.WriteLine("MIP File profile loaded successfully.");

    FileEngineSettings engineSettings = new(
        configuration.ClientId,
        authDelegate,
        string.Empty,
        "en-US");

    engineSettings.Identity = new Identity(configuration.ClientId);

    fileEngine = await fileProfile.AddEngineAsync(engineSettings);

    Console.WriteLine("MIP File engine loaded successfully.");

    Console.Write("Enter the path of a protected file: ");
    string? filePathInput = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(filePathInput))
    {
        Console.Error.WriteLine("A file path is required.");
        return 1;
    }

    string filePath = Path.GetFullPath(
        filePathInput.Trim().Trim('"'));

    if (!File.Exists(filePath))
    {
        Console.Error.WriteLine($"File not found: {filePath}");
        return 1;
    }

    try
    {
        using IFileHandler fileHandler =
            await fileEngine.CreateFileHandlerAsync(
                filePath,
                filePath,
                false);

        Console.WriteLine("File handler created successfully.");

        string sdkTemporaryFile =
            await fileHandler.GetDecryptedTemporaryFileAsync();

        string outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "PurviewLocalFileReader");

        Directory.CreateDirectory(outputDirectory);

        string outputFileName =
            $"{Path.GetFileNameWithoutExtension(filePath)}" +
            $".decrypted.{Guid.NewGuid():N}" +
            $"{Path.GetExtension(filePath)}";

        string decryptedOutputPath =
            Path.Combine(outputDirectory, outputFileName);

        File.Copy(
            sdkTemporaryFile,
            decryptedOutputPath,
            overwrite: false);

        Console.WriteLine();
        Console.WriteLine("Decryption completed.");
        Console.WriteLine($"Decrypted output: {decryptedOutputPath}");

        return 0;
    }
    catch (NotSupportedException)
    {
        Console.Error.WriteLine(
            "The selected file is not protected or cannot be decrypted by the MIP File SDK.");

        return 1;
    }
    catch (UnauthorizedAccessException)
    {
        Console.Error.WriteLine(
            "The signed-in user does not have permission to decrypt this file.");

        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Decryption failed.");
        Console.Error.WriteLine(ex.Message);

        return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("MIP File SDK initialization failed.");
    Console.Error.WriteLine(ex.Message);

    return 1;
}
finally
{
    fileEngine = null;
    fileProfile = null;

    if (mipContext is not null)
    {
        mipContext.ShutDown();
        mipContext = null;
    }
}

internal sealed class LocalConfiguration
{
    public string TenantId { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string CertificateThumbprint { get; set; } = string.Empty;
}