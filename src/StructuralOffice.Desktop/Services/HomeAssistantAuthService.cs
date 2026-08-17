using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StructuralOffice.Desktop.Services;

public sealed record HomeAssistantSession(
    Uri ServerAddress,
    string ClientId,
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt);

public sealed class HomeAssistantAuthService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Action<Uri> _browserLauncher;

    public HomeAssistantAuthService(
        HttpClient? httpClient = null,
        Action<Uri>? browserLauncher = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _browserLauncher = browserLauncher ?? LaunchBrowser;
    }

    public async Task<HomeAssistantSession> LoginAsync(
        Uri serverAddress,
        CancellationToken cancellationToken = default)
    {
        var normalizedServer = HomeAssistantBackend.NormalizeBaseAddress(serverAddress);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var clientId = $"http://127.0.0.1:{port}/";
        var redirectUri = $"http://127.0.0.1:{port}/callback";
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var authorizeUri = BuildAuthorizeUri(
            normalizedServer, clientId, redirectUri, state);

        _browserLauncher(authorizeUri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var browserConnection = await listener.AcceptTcpClientAsync(timeout.Token);
        var callback = await ReadCallbackAsync(browserConnection, port, timeout.Token);
        if (!FixedTimeEquals(callback.State, state))
        {
            throw new InvalidDataException("The Home Assistant login state did not match.");
        }
        if (!string.IsNullOrWhiteSpace(callback.Error))
        {
            throw new InvalidOperationException($"Home Assistant login failed: {callback.Error}");
        }
        if (string.IsNullOrWhiteSpace(callback.Code))
        {
            throw new InvalidDataException("Home Assistant did not return an authorization code.");
        }

        var token = await RequestTokenAsync(
            normalizedServer,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = callback.Code,
                ["client_id"] = clientId
            },
            cancellationToken);
        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            throw new InvalidDataException("Home Assistant did not return a refresh token.");
        }

        return new HomeAssistantSession(
            normalizedServer,
            clientId,
            token.AccessToken,
            token.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn));
    }

    public async Task<HomeAssistantSession> RefreshAsync(
        Uri serverAddress,
        string clientId,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var normalizedServer = HomeAssistantBackend.NormalizeBaseAddress(serverAddress);
        var token = await RequestTokenAsync(
            normalizedServer,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId
            },
            cancellationToken);
        return new HomeAssistantSession(
            normalizedServer,
            clientId,
            token.AccessToken,
            refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn));
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<TokenResponse> RequestTokenAsync(
        Uri serverAddress,
        Dictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(serverAddress, "auth/token");
        using var content = new FormUrlEncodedContent(values);
        using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Home Assistant authentication failed with HTTP {(int)response.StatusCode}.");
        }

        var token = JsonSerializer.Deserialize<TokenResponse>(body);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresIn <= 0)
        {
            throw new InvalidDataException("Home Assistant returned an invalid token response.");
        }
        return token;
    }

    private static Uri BuildAuthorizeUri(
        Uri serverAddress,
        string clientId,
        string redirectUri,
        string state)
    {
        var builder = new UriBuilder(new Uri(serverAddress, "auth/authorize"))
        {
            Query = string.Join("&", new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["state"] = state
            }.Select(item =>
                $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"))
        };
        return builder.Uri;
    }

    private static async Task<AuthCallback> ReadCallbackAsync(
        TcpClient client,
        int port,
        CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(
            stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
        {
        }

        if (string.IsNullOrWhiteSpace(requestLine))
        {
            throw new InvalidDataException("The browser returned an empty callback.");
        }
        var requestParts = requestLine.Split(' ');
        if (requestParts.Length < 2 ||
            !requestParts[0].Equals("GET", StringComparison.Ordinal) ||
            !requestParts[1].StartsWith("/callback?", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The browser returned an invalid callback.");
        }

        var callbackUri = new Uri($"http://127.0.0.1:{port}{requestParts[1]}");
        var parameters = ParseQuery(callbackUri.Query);
        var successful = parameters.ContainsKey("code") && !parameters.ContainsKey("error");
        var html = successful
            ? UiLocalization.Choose(
                "<html><body><h2>StructuralOffice</h2><p>Sign-in succeeded. You can close this window.</p></body></html>",
                "<html><body><h2>StructuralOffice</h2><p>Login erfolgreich. Dieses Fenster kann geschlossen werden.</p></body></html>")
            : "<html><body><h2>StructuralOffice</h2><p>Login wurde abgebrochen.</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        parameters.TryGetValue("code", out var code);
        parameters.TryGetValue("state", out var state);
        parameters.TryGetValue("error", out var error);
        return new AuthCallback(code, state, error);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2))
            .ToDictionary(
                item => Uri.UnescapeDataString(item[0].Replace('+', ' ')),
                item => item.Length == 2
                    ? Uri.UnescapeDataString(item[1].Replace('+', ' '))
                    : string.Empty,
                StringComparer.Ordinal);
    }

    private static bool FixedTimeEquals(string? left, string right)
    {
        if (left is null)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void LaunchBrowser(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    private sealed record AuthCallback(string? Code, string? State, string? Error);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType);
}
