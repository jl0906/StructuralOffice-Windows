using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using StructuralOffice.Desktop.Models;
using StructuralOffice.Desktop.Services;

namespace StructuralOffice.Desktop;

public partial class MainWindow : Window
{
    private readonly AppSettingsStore _settingsStore = new();
    private readonly ICredentialStore _credentialStore = new WindowsCredentialStore();
    private HomeAssistantSession? _session;
    private bool _authenticated;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        var connection = await _settingsStore.LoadConnectionAsync();
        if (!string.IsNullOrWhiteSpace(connection.ServerUrl))
        {
            ServerUrlBox.Text = connection.ServerUrl;
        }
        RememberLoginBox.IsChecked = connection.RememberLogin;

        if (connection is { RememberLogin: true, AuthClientId: not null } &&
            Uri.TryCreate(connection.ServerUrl, UriKind.Absolute, out var serverUri))
        {
            var refreshToken = _credentialStore.ReadRefreshToken();
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                SetBusy(true, "Automatische Anmeldung …");
                try
                {
                    using var auth = new HomeAssistantAuthService();
                    _session = await auth.RefreshAsync(
                        serverUri, connection.AuthClientId, refreshToken);
                    await ShowConnectionResultAsync(_session);
                    SetAuthenticated(true);
                }
                catch (Exception exception)
                {
                    _credentialStore.DeleteRefreshToken();
                    await _settingsStore.ClearRememberedLoginAsync();
                    RememberLoginBox.IsChecked = false;
                    ShowValidation(
                        $"Die gespeicherte Anmeldung ist nicht mehr gültig: {exception.Message}");
                }
                finally
                {
                    SetBusy(false);
                }
            }
        }

        await CheckForUpdatesAsync();
    }

    private async void LoginButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        ResultPanel.Visibility = Visibility.Collapsed;
        if (!TryGetServerUri(out var serverUri))
        {
            return;
        }

        SetBusy(true, "Browser-Anmeldung wird geöffnet …");
        try
        {
            using var auth = new HomeAssistantAuthService();
            _session = await auth.LoginAsync(serverUri!);
            var remember = RememberLoginBox.IsChecked == true;
            if (remember)
            {
                _credentialStore.WriteRefreshToken(_session.RefreshToken!);
            }
            else
            {
                _credentialStore.DeleteRefreshToken();
            }

            await _settingsStore.SaveConnectionAsync(
                _session.ServerAddress.ToString().TrimEnd('/'),
                remember,
                remember ? _session.ClientId : null);
            await ShowConnectionResultAsync(_session);
            SetAuthenticated(true);
        }
        catch (OperationCanceledException)
        {
            ShowValidation("Die Anmeldung wurde abgebrochen oder hat zu lange gedauert.");
        }
        catch (Exception exception)
        {
            ShowValidation($"Anmeldung fehlgeschlagen: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void LogoutButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        _credentialStore.DeleteRefreshToken();
        await _settingsStore.ClearRememberedLoginAsync();
        _session = null;
        RememberLoginBox.IsChecked = false;
        SetAuthenticated(false);
        ResultPanel.Visibility = Visibility.Collapsed;
    }

    private bool TryGetServerUri(out Uri? serverUri)
    {
        if (!Uri.TryCreate(ServerUrlBox.Text.Trim(), UriKind.Absolute, out serverUri) ||
            serverUri.Scheme is not ("http" or "https"))
        {
            ShowValidation("Bitte eine vollständige HTTP- oder HTTPS-Adresse eingeben.");
            return false;
        }
        return true;
    }

    private async Task ShowConnectionResultAsync(HomeAssistantSession session)
    {
        using var backend = new HomeAssistantBackend(session.ServerAddress, session.AccessToken);
        ShowResult(await backend.CheckAsync());
    }

    private async Task CheckForUpdatesAsync()
    {
        var lastCheck = await _settingsStore.LoadLastUpdateCheckAsync();
        if (lastCheck is not null && DateTimeOffset.UtcNow - lastCheck < TimeSpan.FromHours(12))
        {
            return;
        }

        await _settingsStore.SaveLastUpdateCheckAsync(DateTimeOffset.UtcNow);
        try
        {
            using var updater = new UpdateService();
            if (await updater.CheckAndInstallAsync())
            {
                await UpdateLog.WriteAsync("A verified update was downloaded; installer started.");
                Application.Current.Shutdown();
            }
        }
        catch (Exception exception)
        {
            await UpdateLog.WriteAsync($"Update check failed: {exception.Message}");
        }
    }

    private void ShowValidation(string detail)
    {
        ShowResult(new IntegrationCheckResult(
            [new CheckItem("Verbindung", CheckState.Error, detail)],
            null,
            DateTimeOffset.Now));
    }

    private void ShowResult(IntegrationCheckResult result)
    {
        ResultItems.ItemsSource = result.Checks.Select(item => new CheckItemView(
            item.Name,
            item.Detail,
            item.State switch
            {
                CheckState.Success => new SolidColorBrush(Color.FromRgb(63, 199, 132)),
                CheckState.Warning => new SolidColorBrush(Color.FromRgb(246, 184, 72)),
                _ => new SolidColorBrush(Color.FromRgb(242, 95, 92))
            })).ToList();
        CheckedAtText.Text = $"Geprüft am {result.CheckedAt:dd.MM.yyyy 'um' HH:mm:ss}";
        ResultPanel.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy, string message = "")
    {
        LoginButton.IsEnabled = !busy;
        ServerUrlBox.IsEnabled = !busy && !_authenticated;
        RememberLoginBox.IsEnabled = !busy && !_authenticated;
        ProgressText.Text = busy ? message : string.Empty;
    }

    private void SetAuthenticated(bool authenticated)
    {
        _authenticated = authenticated;
        LoginButton.Visibility = authenticated ? Visibility.Collapsed : Visibility.Visible;
        LogoutButton.Visibility = authenticated ? Visibility.Visible : Visibility.Collapsed;
        ServerUrlBox.IsEnabled = !authenticated;
        RememberLoginBox.IsEnabled = !authenticated;
    }

    private sealed record CheckItemView(string Name, string Detail, Brush Color);
}
