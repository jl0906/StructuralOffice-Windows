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

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        var savedUrl = await _settingsStore.LoadServerUrlAsync();
        if (!string.IsNullOrWhiteSpace(savedUrl))
        {
            ServerUrlBox.Text = savedUrl;
        }

        await CheckForUpdatesAsync();
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

    private async void CheckButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        ResultPanel.Visibility = Visibility.Collapsed;

        if (!Uri.TryCreate(ServerUrlBox.Text.Trim(), UriKind.Absolute, out var serverUri) ||
            serverUri.Scheme is not ("http" or "https"))
        {
            ShowValidation("Bitte eine vollständige HTTP- oder HTTPS-Adresse eingeben.");
            return;
        }

        if (string.IsNullOrWhiteSpace(AccessTokenBox.Password))
        {
            ShowValidation("Bitte ein langlebiges Home-Assistant-Zugriffstoken eingeben.");
            return;
        }

        SetBusy(true);
        try
        {
            using var backend = new HomeAssistantBackend(serverUri, AccessTokenBox.Password);
            var result = await backend.CheckAsync();
            await _settingsStore.SaveServerUrlAsync(backend.BaseAddress.ToString().TrimEnd('/'));
            ShowResult(result);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowValidation(string detail)
    {
        ShowResult(new IntegrationCheckResult(
            [new CheckItem("Eingabe", CheckState.Error, detail)],
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

    private void SetBusy(bool busy)
    {
        CheckButton.IsEnabled = !busy;
        ProgressText.Text = busy ? "Prüfung läuft …" : string.Empty;
    }

    private sealed record CheckItemView(string Name, string Detail, Brush Color);
}
