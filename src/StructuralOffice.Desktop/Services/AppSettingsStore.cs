using System.IO;
using System.Text.Json;

namespace StructuralOffice.Desktop.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public AppSettingsStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StructuralOffice");
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public async Task<string?> LoadServerUrlAsync()
    {
        return (await LoadAsync()).ServerUrl;
    }

    public async Task<DateTimeOffset?> LoadLastUpdateCheckAsync()
    {
        return (await LoadAsync()).LastUpdateCheck;
    }

    public async Task<string> LoadLanguageAsync()
    {
        return (await LoadAsync()).Language is "de" ? "de" : "en";
    }

    public async Task<SavedConnection> LoadConnectionAsync()
    {
        var settings = await LoadAsync();
        return new SavedConnection(
            settings.ServerUrl,
            settings.RememberLogin,
            settings.AuthClientId);
    }

    private async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            await using var stream = File.OpenRead(_settingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream);
            return settings ?? new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveServerUrlAsync(string serverUrl)
    {
        var settings = await LoadAsync();
        await SaveAsync(settings with { ServerUrl = serverUrl });
    }

    public async Task SaveLastUpdateCheckAsync(DateTimeOffset checkedAt)
    {
        var settings = await LoadAsync();
        await SaveAsync(settings with { LastUpdateCheck = checkedAt });
    }

    public async Task SaveLanguageAsync(string language)
    {
        var settings = await LoadAsync();
        await SaveAsync(settings with { Language = language == "de" ? "de" : "en" });
    }

    public async Task SaveConnectionAsync(
        string serverUrl,
        bool rememberLogin,
        string? authClientId)
    {
        var settings = await LoadAsync();
        await SaveAsync(settings with
        {
            ServerUrl = serverUrl,
            RememberLogin = rememberLogin,
            AuthClientId = authClientId
        });
    }

    public async Task ClearRememberedLoginAsync()
    {
        var settings = await LoadAsync();
        await SaveAsync(settings with { RememberLogin = false, AuthClientId = null });
    }

    private async Task SaveAsync(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
    }

    private sealed record AppSettings(
        string? ServerUrl = null,
        DateTimeOffset? LastUpdateCheck = null,
        bool RememberLogin = false,
        string? AuthClientId = null,
        string Language = "en");

    public sealed record SavedConnection(
        string? ServerUrl,
        bool RememberLogin,
        string? AuthClientId);
}
