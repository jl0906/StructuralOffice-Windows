using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using StructuralOffice.Desktop.Models;

namespace StructuralOffice.Desktop.Services;

public sealed class HomeAssistantBackend : IStructuralOfficeBackend, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public HomeAssistantBackend(Uri baseAddress, string accessToken, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        BaseAddress = NormalizeBaseAddress(baseAddress);
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress = BaseAddress;
        _httpClient.Timeout = TimeSpan.FromSeconds(12);
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Uri BaseAddress { get; }

    public string DisplayName => "Home Assistant";

    public async Task<IntegrationCheckResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        var checks = new List<CheckItem>();
        string? version = null;

        try
        {
            using var apiResponse = await _httpClient.GetAsync("api/", cancellationToken);
            if (apiResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                checks.Add(new CheckItem(
                    "Home Assistant", CheckState.Success, "Server ist erreichbar."));
                checks.Add(new CheckItem(
                    "Zugriffstoken", CheckState.Error,
                    "Das Token wurde von Home Assistant abgelehnt."));
                return Result(checks, version);
            }

            if (!apiResponse.IsSuccessStatusCode)
            {
                checks.Add(new CheckItem(
                    "Home Assistant", CheckState.Error,
                    $"Unerwartete Serverantwort: HTTP {(int)apiResponse.StatusCode}."));
                return Result(checks, version);
            }

            checks.Add(new CheckItem(
                "Home Assistant", CheckState.Success, "Server ist erreichbar."));
            checks.Add(new CheckItem(
                "Zugriffstoken", CheckState.Success, "Authentifizierung erfolgreich."));

            using var integrationResponse = await _httpClient.GetAsync(
                "api/structuraloffice/v1/status", cancellationToken);
            switch (integrationResponse.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    checks.Add(new CheckItem(
                        "StructuralOffice", CheckState.Warning,
                        "Integration ist nicht installiert oder Home Assistant wurde noch nicht neu gestartet."));
                    break;
                case HttpStatusCode.ServiceUnavailable:
                    checks.Add(new CheckItem(
                        "StructuralOffice", CheckState.Warning,
                        "Integration ist installiert, aber noch nicht konfiguriert."));
                    break;
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    checks.Add(new CheckItem(
                        "StructuralOffice", CheckState.Error,
                        "Das Home-Assistant-Konto hat keinen Zugriff auf StructuralOffice."));
                    break;
                default:
                    if (!integrationResponse.IsSuccessStatusCode)
                    {
                        checks.Add(new CheckItem(
                            "StructuralOffice", CheckState.Error,
                            $"Prüfung fehlgeschlagen: HTTP {(int)integrationResponse.StatusCode}."));
                        break;
                    }

                    version = await ReadVersionAsync(integrationResponse, cancellationToken);
                    checks.Add(new CheckItem(
                        "StructuralOffice", CheckState.Success,
                        version is null
                            ? "Integration ist installiert und bereit."
                            : $"Integration {version} ist installiert und bereit."));
                    break;
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            checks.Add(new CheckItem(
                "Home Assistant", CheckState.Error,
                "Zeitüberschreitung. Bitte Adresse und Netzwerk prüfen."));
        }
        catch (HttpRequestException exception)
        {
            checks.Add(new CheckItem(
                "Home Assistant", CheckState.Error, FriendlyNetworkError(exception)));
        }

        return Result(checks, version);
    }

    public static Uri NormalizeBaseAddress(Uri value)
    {
        if (!value.IsAbsoluteUri || value.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Only absolute HTTP or HTTPS addresses are supported.");
        }

        var builder = new UriBuilder(value) { Path = "/", Query = string.Empty, Fragment = string.Empty };
        return builder.Uri;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static async Task<string?> ReadVersionAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("version", out var version)
            ? version.GetString()
            : null;
    }

    private static IntegrationCheckResult Result(List<CheckItem> checks, string? version) =>
        new(checks, version, DateTimeOffset.Now);

    private static string FriendlyNetworkError(HttpRequestException exception)
    {
        if (exception.InnerException is System.Security.Authentication.AuthenticationException)
        {
            return "Die TLS-Zertifikatsprüfung ist fehlgeschlagen. Zertifikat und Adresse prüfen.";
        }

        return "Server nicht erreichbar. Bitte Adresse, Netzwerk und Port prüfen.";
    }
}
