using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StructuralOffice.Desktop.Services;

public sealed class UpdateService : IDisposable
{
    public const string Repository = "jl0906/StructuralOffice-Windows";
    public const string InstallerAssetName = "StructuralOffice_Install.exe";
    public static readonly Uri ReleasesApi = new(
        $"https://api.github.com/repos/{Repository}/releases?per_page=20");

    private const long MaximumInstallerBytes = 250L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly HashSet<string> AllowedDownloadHosts = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly ReleaseVersion _currentVersion;
    private readonly string _updateDirectory;
    private readonly Action<string> _installerLauncher;

    public UpdateService(
        HttpClient? httpClient = null,
        string? currentVersion = null,
        string? updateDirectory = null,
        Action<string>? installerLauncher = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("StructuralOffice", CurrentVersionText()));
        }
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-GitHub-Api-Version", "2022-11-28");

        var versionText = currentVersion ?? CurrentVersionText();
        if (!ReleaseVersion.TryParse(versionText, out var parsedVersion))
        {
            throw new InvalidOperationException($"Invalid application version: {versionText}");
        }

        _currentVersion = parsedVersion!;
        _updateDirectory = updateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StructuralOffice",
            "Updates");
        _installerLauncher = installerLauncher ?? LaunchInstaller;
    }

    public async Task<bool> CheckAndInstallAsync(CancellationToken cancellationToken = default)
    {
        var release = await FindUpdateAsync(cancellationToken);
        if (release is null)
        {
            return false;
        }

        await InstallAsync(release, cancellationToken);
        return true;
    }

    public async Task InstallAsync(
        AvailableUpdate release,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        var installer = await DownloadAndVerifyAsync(release, cancellationToken);
        _installerLauncher(installer);
    }

    public async Task<AvailableUpdate?> FindUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(ReleasesApi, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(
            stream, JsonOptions, cancellationToken) ?? [];

        return releases
            .Where(item => item is { Draft: false, Assets: not null })
            .Select(ToAvailableUpdate)
            .Where(item => item is not null)
            .Select(item => item!)
            .Where(item => item.Version.CompareTo(_currentVersion) > 0)
            .Where(item => _currentVersion.IsPrerelease ||
                (!item.Prerelease && !item.Version.IsPrerelease))
            .OrderByDescending(item => item.Version)
            .FirstOrDefault();
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<string> DownloadAndVerifyAsync(
        AvailableUpdate release,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_updateDirectory);
        var destination = Path.Combine(_updateDirectory, InstallerAssetName);
        var temporary = destination + ".download";

        try
        {
            using var response = await _httpClient.GetAsync(
                release.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri ?? release.DownloadUri;
            if (finalUri.Scheme != Uri.UriSchemeHttps ||
                !AllowedDownloadHosts.Contains(finalUri.Host))
            {
                throw new InvalidDataException("The update download was redirected to an untrusted host.");
            }

            if (response.Content.Headers.ContentLength is > MaximumInstallerBytes)
            {
                throw new InvalidDataException("The update installer exceeds the size limit.");
            }

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(temporary))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            var fileInfo = new FileInfo(temporary);
            if (fileInfo.Length == 0 || fileInfo.Length > MaximumInstallerBytes)
            {
                throw new InvalidDataException("The downloaded update has an invalid size.");
            }

            string actualHash;
            await using (var downloaded = File.OpenRead(temporary))
            {
                actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(downloaded, cancellationToken));
            }
            if (!actualHash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The SHA-256 checksum of the update is invalid.");
            }

            File.Move(temporary, destination, true);
            return destination;
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
            throw;
        }
    }

    private static AvailableUpdate? ToAvailableUpdate(GitHubRelease release)
    {
        if (!ReleaseVersion.TryParse(release.TagName, out var version) || release.Assets is null)
        {
            return null;
        }

        var asset = release.Assets.FirstOrDefault(item =>
            string.Equals(item.Name, InstallerAssetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null || !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(asset.Digest) ||
            !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hash = asset.Digest["sha256:".Length..];
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            return null;
        }

        return new AvailableUpdate(version!, release.Prerelease, downloadUri, hash);
    }

    private static string CurrentVersionText() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.0.0";

    private static void LaunchInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path, "/Q")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path)!
        });
    }

    public sealed record AvailableUpdate(
        ReleaseVersion Version,
        bool Prerelease,
        Uri DownloadUri,
        string Sha256);

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] List<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? DownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest);
}
