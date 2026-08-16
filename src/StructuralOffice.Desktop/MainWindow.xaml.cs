using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
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

    private static readonly IReadOnlyDictionary<string, WorkspacePage> WorkspacePages =
        new Dictionary<string, WorkspacePage>(StringComparer.OrdinalIgnoreCase)
        {
            ["contacts"] = new(
                "Kontakte",
                "Kunden, Firmen und Ansprechpartner zentral verwalten.",
                "K"),
            ["topics"] = new(
                "Themen",
                "Wiederkehrende Inhalte und Zuständigkeiten strukturieren.",
                "T"),
            ["routines"] = new(
                "Routinen",
                "Regelmäßige Abläufe planen und nachvollziehbar ausführen.",
                "R"),
            ["tasks"] = new(
                "Aufgaben",
                "Offene Vorgänge, Fälligkeiten und Bearbeitungsstände bündeln.",
                "A"),
            ["invoices"] = new(
                "Rechnungen",
                "Rechnungen, Zahlungsziele und Status übersichtlich verfolgen.",
                "R"),
            ["documents"] = new(
                "Dokumente",
                "Dokumente zu Kontakten und Vorgängen geordnet bereitstellen.",
                "D"),
            ["settings"] = new(
                "Einstellungen",
                "Verbindung, Updates und den zukünftigen Datenmodus konfigurieren.",
                "E")
        };

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
        var result = await backend.CheckAsync();
        ShowResult(result);
        UpdateWorkspaceStatus(session, result);
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
        ConnectionView.Visibility = authenticated ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceView.Visibility = authenticated ? Visibility.Visible : Visibility.Collapsed;
        LoginButton.Visibility = authenticated ? Visibility.Collapsed : Visibility.Visible;
        ServerUrlBox.IsEnabled = !authenticated;
        RememberLoginBox.IsEnabled = !authenticated;

        if (authenticated)
        {
            ShowPage("overview");
        }
    }

    private void NavigationButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: string pageKey })
        {
            ShowPage(pageKey);
        }
    }

    private void ShowPage(string pageKey)
    {
        var isOverview = string.Equals(pageKey, "overview", StringComparison.OrdinalIgnoreCase);
        DashboardPanel.Visibility = isOverview ? Visibility.Visible : Visibility.Collapsed;
        ModulePanel.Visibility = isOverview ? Visibility.Collapsed : Visibility.Visible;

        if (isOverview)
        {
            PageTitleText.Text = "Übersicht";
            PageSubtitleText.Text = "Dein StructuralOffice-Arbeitsbereich";
        }
        else if (WorkspacePages.TryGetValue(pageKey, out var page))
        {
            PageTitleText.Text = page.Title;
            PageSubtitleText.Text = page.Description;
            ModuleTitleText.Text = page.Title;
            ModuleDescriptionText.Text = page.Description;
            ModuleInitialText.Text = page.Initial;
        }
        else
        {
            return;
        }

        foreach (var button in NavigationPanel.Children.OfType<Button>())
        {
            SetNavigationState(button, pageKey);
        }
        SetNavigationState(SettingsNavigationButton, pageKey);
    }

    private static void SetNavigationState(Button button, string activePageKey)
    {
        var active = button.Tag is string tag &&
                     string.Equals(tag, activePageKey, StringComparison.OrdinalIgnoreCase);
        button.Background = active
            ? new SolidColorBrush(Color.FromRgb(27, 45, 72))
            : Brushes.Transparent;
        button.Foreground = active
            ? new SolidColorBrush(Color.FromRgb(232, 237, 247))
            : new SolidColorBrush(Color.FromRgb(170, 180, 198));
    }

    private void UpdateWorkspaceStatus(
        HomeAssistantSession session,
        IntegrationCheckResult result)
    {
        var server = session.ServerAddress.ToString().TrimEnd('/');
        SidebarServerText.Text = server;
        DashboardServerText.Text = server;
        IntegrationVersionText.Text = result.IntegrationVersion ?? "Nicht verfügbar";

        var integrationCheck = result.Checks.FirstOrDefault(
            item => string.Equals(item.Name, "StructuralOffice", StringComparison.OrdinalIgnoreCase));
        var state = integrationCheck?.State ??
                    (result.Checks.Any(item => item.State == CheckState.Error)
                        ? CheckState.Error
                        : CheckState.Warning);

        var color = state switch
        {
            CheckState.Success => Color.FromRgb(63, 199, 132),
            CheckState.Warning => Color.FromRgb(246, 184, 72),
            _ => Color.FromRgb(242, 95, 92)
        };
        var brush = new SolidColorBrush(color);
        SidebarStatusDot.Fill = brush;
        DashboardStatusDot.Fill = brush;
        DashboardStatusText.Text = state switch
        {
            CheckState.Success => "System bereit",
            CheckState.Warning => "Integration prüfen",
            _ => "Verbindung prüfen"
        };
    }

    private sealed record CheckItemView(string Name, string Detail, Brush Color);

    private sealed record WorkspacePage(string Title, string Description, string Initial);
}
