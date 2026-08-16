using System.IO;
using System.Linq;
using System.Net.Http;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using StructuralOffice.Desktop.Models;
using StructuralOffice.Desktop.Services;

namespace StructuralOffice.Desktop;

public partial class MainWindow : Window
{
    private readonly AppSettingsStore _settingsStore = new();
    private readonly ICredentialStore _credentialStore = new WindowsCredentialStore();
    private HomeAssistantSession? _session;
    private HomeAssistantBackend? _backend;
    private bool _authenticated;
    private string _currentPage = "overview";
    private string _currentDataMode = string.Empty;
    private DisplayRecord? _selectedRecord;
    private bool _moduleBusy;
    private string? _editSessionId;
    private string? _editRecordId;
    private string? _editCollection;
    private List<DisplayRecord> _allDisplayRows = [];
    private readonly ObservableCollection<TopicStepModel> _topicSteps = [];

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

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
            ["accounting"] = new(
                "Mahnwesen",
                "Automatische Mahnläufe, Rechnungsgruppen und Eskalationsregeln verwalten.",
                "M"),
            ["settings"] = new(
                "Einstellungen",
                "Verbindung, Updates und den zukünftigen Datenmodus konfigurieren.",
                "E"),
            ["administration"] = new(
                "Administration",
                "Benutzerrollen, Backups und revisionssichere Änderungsprotokolle verwalten.",
                "A")
        };

    public MainWindow()
    {
        InitializeComponent();
        TopicStepsGrid.ItemsSource = _topicSteps;
        Loaded += OnLoaded;
        Closed += (_, _) => _backend?.Dispose();
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
        await EndCurrentEditSessionCoreAsync();
        _credentialStore.DeleteRefreshToken();
        await _settingsStore.ClearRememberedLoginAsync();
        _backend?.Dispose();
        _backend = null;
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
        _backend?.Dispose();
        _backend = new HomeAssistantBackend(session.ServerAddress, session.AccessToken);
        var result = await _backend.CheckAsync();
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

    private async void NavigationButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: string pageKey })
        {
            await EndCurrentEditSessionCoreAsync();
            await ShowPageAsync(pageKey);
        }
    }

    private void ShowPage(string pageKey)
    {
        _ = ShowPageAsync(pageKey);
    }

    private async Task ShowPageAsync(string pageKey)
    {
        var isOverview = string.Equals(pageKey, "overview", StringComparison.OrdinalIgnoreCase);
        _currentPage = pageKey;
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
        SetNavigationState(AdministrationNavigationButton, pageKey);

        if (!isOverview)
        {
            ConfigureModuleActions(pageKey);
            await LoadCurrentPageAsync();
        }
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

    private void ConfigureModuleActions(string pageKey)
    {
        var all = new UIElement[]
        {
            NewRecordButton, SaveRecordButton, ArchiveRecordButton, StartEditingButton,
            ShowEditorsButton, EndEditingButton, TaskStatusBox,
            SetTaskStatusButton, ImportInvoicesButton, ImportExcelButton,
            ExportInvoicesButton, ExportCsvButton,
            DownloadTemplateButton, DocumentTypeBox, GenerateDocumentButton,
            AccountingMembersButton, AccountingRulesButton, AdministrationSectionBox,
            RoleBox, SetRoleButton, CreateBackupButton, DownloadBackupButton,
            RestoreBackupButton, DeleteBackupButton, TestNotificationButton
        };
        foreach (var element in all)
        {
            element.Visibility = Visibility.Collapsed;
        }

        var isLiveEditor = pageKey is "contacts" or "topics" or "routines" or "invoices";
        SetVisibility(isLiveEditor, NewRecordButton, SaveRecordButton, ArchiveRecordButton,
            StartEditingButton, ShowEditorsButton, EndEditingButton);
        SetVisibility(pageKey == "tasks", TaskStatusBox, SetTaskStatusButton);
        SetVisibility(pageKey == "invoices", ImportInvoicesButton, ImportExcelButton, ExportInvoicesButton,
            ExportCsvButton, DownloadTemplateButton);
        SetVisibility(pageKey == "documents", DocumentTypeBox, GenerateDocumentButton);
        SetVisibility(pageKey == "accounting", AccountingMembersButton, AccountingRulesButton);
        SetVisibility(pageKey == "administration", AdministrationSectionBox,
            TestNotificationButton);
        SetVisibility(pageKey == "settings", TestNotificationButton);
        ContextActionsPanel.Visibility = all.Any(item => item.Visibility == Visibility.Visible)
            ? Visibility.Visible
            : Visibility.Collapsed;
        var isContactEditor = pageKey == "contacts";
        var isTopicEditor = pageKey == "topics";
        ContactEditorPanel.Visibility = isContactEditor ? Visibility.Visible : Visibility.Collapsed;
        TopicEditorPanel.Visibility = isTopicEditor ? Visibility.Visible : Visibility.Collapsed;
        RecordEditorText.Visibility = isContactEditor || isTopicEditor
            ? Visibility.Collapsed
            : Visibility.Visible;
        RecordEditorText.IsReadOnly = !isLiveEditor;
        IncludeArchivedBox.Visibility = isLiveEditor ? Visibility.Visible : Visibility.Collapsed;
        EditorHelpText.Text = isContactEditor
            ? "Pflichtfelder sind mit * gekennzeichnet. Änderungen werden revisionssicher gespeichert."
            : isTopicEditor
                ? "Checklistenpunkte können direkt bearbeitet und per Auswahl entfernt werden."
                : isLiveEditor
                    ? "JSON-Felder können bearbeitet werden. Revisionen schützen vor parallelen Änderungen."
                    : "Diese Ansicht zeigt die vom StructuralOffice-Backend gespeicherten Daten vollständig an.";
    }

    private static void SetVisibility(bool visible, params UIElement[] elements)
    {
        foreach (var element in elements)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async Task LoadCurrentPageAsync()
    {
        if (_backend is null || _moduleBusy)
        {
            return;
        }

        await RunModuleActionAsync(LoadCurrentPageCoreAsync);
    }

    private async Task LoadCurrentPageCoreAsync()
    {
        if (_backend is null)
        {
            return;
        }
        switch (_currentPage)
        {
            case "contacts":
            case "topics":
            case "routines":
                case "invoices":
                    _currentDataMode = "live";
                    LoadRows((await _backend.GetLiveRecordsAsync(
                        _currentPage, IncludeArchivedBox.IsChecked == true)).Items);
                break;
            case "tasks":
                _currentDataMode = "tasks";
                LoadRows((await _backend.GetTasksAsync()).Items);
                break;
            case "documents":
                _currentDataMode = "documents";
                LoadRows((await _backend.GetLiveRecordsAsync("invoices")).Items);
                break;
            case "accounting":
                _currentDataMode = "accounting-tasks";
                RecordEditorText.IsReadOnly = true;
                SaveRecordButton.Visibility = Visibility.Collapsed;
                LoadRows((await _backend.GetAccountingTasksAsync()).Items);
                break;
            case "administration":
                if (AdministrationSectionBox.SelectedIndex < 0)
                {
                    AdministrationSectionBox.SelectedIndex = 0;
                }
                await LoadAdministrationSectionAsync();
                break;
            case "settings":
                _currentDataMode = "settings";
                var check = await _backend.CheckAsync();
                var data = new JsonObject
                {
                    ["application_version"] = "0.5.0-alpha",
                    ["backend"] = _backend.DisplayName,
                    ["integration_version"] = check.IntegrationVersion,
                    ["server"] = _session?.ServerAddress.ToString(),
                    ["standalone"] = "prepared"
                };
                LoadRows([new BackendRecord("settings", 0, data)]);
                break;
        }
    }

    private async Task LoadAdministrationSectionAsync()
    {
        if (_backend is null)
        {
            return;
        }
        var section = SelectedTag(AdministrationSectionBox) ?? "roles";
        _currentDataMode = $"administration-{section}";
        RecordEditorText.IsReadOnly = true;
        SetVisibility(section == "roles", RoleBox, SetRoleButton);
        SetVisibility(section == "backups", CreateBackupButton, DownloadBackupButton,
            RestoreBackupButton, DeleteBackupButton);
        switch (section)
        {
            case "roles":
                LoadRows(await _backend.GetRolesAsync());
                break;
            case "backups":
                LoadRows(await _backend.GetBackupsAsync());
                break;
            case "audit":
                LoadRows((await _backend.GetAuditAsync()).Items);
                break;
            case "events":
                LoadRows((await _backend.GetEventsAsync()).Items);
                break;
        }
    }

    private void LoadRows(IEnumerable<BackendRecord> records)
    {
        _allDisplayRows = records.Select(CreateDisplayRecord).ToList();
        _selectedRecord = null;
        ClearEditor();
        EditorTitleText.Text = "Datensatz auswählen";
        EditorMetaText.Text = string.Empty;
        ApplyModuleFilter();
    }

    private void ApplyModuleFilter()
    {
        var query = ModuleSearchBox.Text.Trim();
        var rows = string.IsNullOrWhiteSpace(query)
            ? _allDisplayRows
            : _allDisplayRows.Where(item =>
                item.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        ModuleDataGrid.ItemsSource = rows;
        ModuleBusyText.Text = string.IsNullOrWhiteSpace(query)
            ? $"{rows.Count} Einträge"
            : $"{rows.Count} von {_allDisplayRows.Count} Einträgen";
        if (rows.Count > 0)
        {
            ModuleDataGrid.SelectedIndex = 0;
        }
    }

    private static DisplayRecord CreateDisplayRecord(BackendRecord record)
    {
        var data = record.Data;
        var title = FirstText(data, "name", "invoice_number", "topic_name", "routine_name",
            "filename", "user_name", "task_type", "action", "id");
        if (string.IsNullOrWhiteSpace(title))
        {
            title = record.Id;
        }
        var status = record.ArchivedAt is not null
            ? "archiviert"
            : FirstText(data, "status", "role", "due_state", "enabled", "operation");
        var detail = FirstText(data, "email", "category", "due_date", "due_at",
            "source_name", "collection", "updated_at");
        var searchText = string.Join(' ', title, status, detail, data.ToJsonString());
        return new DisplayRecord(record, title, status, detail, record.Revision, searchText);
    }

    private static string FirstText(JsonObject data, params string[] names)
    {
        foreach (var name in names)
        {
            if (data[name] is JsonValue value)
            {
                if (value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
                return value.ToJsonString().Trim('"');
            }
        }
        return string.Empty;
    }

    private void ModuleDataGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        _selectedRecord = ModuleDataGrid.SelectedItem as DisplayRecord;
        if (_selectedRecord is null)
        {
            return;
        }
        EditorTitleText.Text = _selectedRecord.Title;
        var archived = _selectedRecord.Record.ArchivedAt is not null;
        EditorMetaText.Text = $"ID: {_selectedRecord.Record.Id}  ·  Revision {_selectedRecord.Record.Revision}" +
                              (archived ? "  ·  archiviert" : string.Empty);
        SaveRecordButton.IsEnabled = !archived;
        ArchiveRecordButton.IsEnabled = !archived;
        StartEditingButton.IsEnabled = !archived;
        ContactEditorPanel.IsEnabled = !archived;
        TopicEditorPanel.IsEnabled = !archived;
        RecordEditorText.IsReadOnly = archived ||
                                      _currentDataMode is not ("live" or "accounting-rules");
        LoadRecordIntoEditor(_selectedRecord.Record);
        if (_currentDataMode == "administration-roles")
        {
            SelectComboValue(RoleBox, FirstText(_selectedRecord.Record.Data, "role") ?? "viewer");
        }
    }

    private void LoadRecordIntoEditor(BackendRecord record)
    {
        if (_currentPage == "contacts")
        {
            var contact = ContactRecordModel.FromJson(record.Data);
            ContactNameBox.Text = contact.Name;
            ContactCustomerNumberBox.Text = contact.CustomerNumber;
            ContactEmailBox.Text = contact.Email;
            ContactPhoneBox.Text = contact.Phone;
            ContactAddressBox.Text = contact.Address;
            ContactNoteBox.Text = contact.Note;
            return;
        }
        if (_currentPage == "topics")
        {
            var topic = TopicRecordModel.FromJson(record.Data);
            TopicNameBox.Text = topic.Name;
            TopicDescriptionBox.Text = topic.Description;
            TopicCategoryBox.Text = topic.Category;
            TopicMinutesBox.Text = topic.EstimatedMinutes.ToString();
            TopicInstructionsBox.Text = topic.Instructions;
            TopicEnabledBox.IsChecked = topic.Enabled;
            SelectComboTag(TopicPriorityBox, topic.Priority);
            _topicSteps.Clear();
            foreach (var step in topic.Steps)
            {
                _topicSteps.Add(step);
            }
            return;
        }
        RecordEditorText.Text = record.Data.ToJsonString(PrettyJson);
    }

    private void ClearEditor()
    {
        RecordEditorText.Clear();
        ContactNameBox.Clear();
        ContactCustomerNumberBox.Clear();
        ContactEmailBox.Clear();
        ContactPhoneBox.Clear();
        ContactAddressBox.Clear();
        ContactNoteBox.Clear();
        TopicNameBox.Clear();
        TopicDescriptionBox.Clear();
        TopicCategoryBox.Clear();
        TopicMinutesBox.Text = "0";
        TopicInstructionsBox.Clear();
        TopicEnabledBox.IsChecked = true;
        TopicPriorityBox.SelectedIndex = 1;
        _topicSteps.Clear();
    }

    private void ModuleSearchBox_OnTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (IsLoaded)
        {
            ApplyModuleFilter();
        }
    }

    private async void IncludeArchivedBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_authenticated && _currentDataMode == "live" && !_moduleBusy)
        {
            await LoadCurrentPageAsync();
        }
    }

    private async void RefreshModuleButton_OnClick(object sender, RoutedEventArgs eventArgs) =>
        await LoadCurrentPageAsync();

    private void NewRecordButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        _selectedRecord = null;
        ModuleDataGrid.SelectedItem = null;
        EditorTitleText.Text = "Neuer Datensatz";
        EditorMetaText.Text = "Wird beim Speichern angelegt";
        SaveRecordButton.IsEnabled = true;
        ArchiveRecordButton.IsEnabled = false;
        StartEditingButton.IsEnabled = false;
        ContactEditorPanel.IsEnabled = true;
        TopicEditorPanel.IsEnabled = true;
        RecordEditorText.IsReadOnly = false;
        ClearEditor();
        if (_currentPage is "contacts" or "topics")
        {
            if (_currentPage == "topics")
            {
                _topicSteps.Add(new TopicStepModel { Id = "step-0", Title = "" });
            }
        }
        else
        {
            RecordEditorText.Text = NewRecordTemplate(_currentPage).ToJsonString(PrettyJson);
            RecordEditorText.Focus();
        }
    }

    private static JsonObject NewRecordTemplate(string pageKey) => pageKey switch
    {
        "contacts" => new JsonObject
        {
            ["name"] = "", ["customer_number"] = "", ["email"] = "",
            ["phone"] = "", ["address"] = "", ["note"] = ""
        },
        "topics" => new JsonObject
        {
            ["name"] = "", ["description"] = "", ["category"] = "",
            ["priority"] = "normal", ["estimated_minutes"] = 0,
            ["instructions"] = "", ["enabled"] = true, ["checklist"] = new JsonArray()
        },
        "routines" => new JsonObject
        {
            ["name"] = "", ["description"] = "", ["enabled"] = true,
            ["topic_ids"] = new JsonArray(), ["due_time"] = "09:00",
            ["timezone"] = "Europe/Berlin", ["reminder_offsets"] = new JsonArray(-1, 0),
            ["catch_up_policy"] = "configured_window",
            ["schedule"] = new JsonObject
            {
                ["frequency"] = "monthly", ["interval"] = 1,
                ["start_date"] = DateTime.Today.ToString("yyyy-MM-dd"),
                ["month_days"] = new JsonArray(DateTime.Today.Day),
                ["business_day_rule"] = "none", ["invalid_day_rule"] = "last_day"
            }
        },
        "invoices" => new JsonObject
        {
            ["direction"] = "receivable", ["contact"] = "", ["contact_address"] = "",
            ["invoice_number"] = "", ["invoice_date"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["due_date"] = DateTime.Today.AddDays(14).ToString("yyyy-MM-dd"),
            ["gross_cents"] = 0, ["outstanding_cents"] = 0, ["currency"] = "EUR",
            ["status"] = "open", ["note"] = ""
        },
        _ => new JsonObject()
    };

    private async void SaveRecordButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null)
        {
            return;
        }
        await RunModuleActionAsync(async () =>
        {
            var data = ReadEditorData();
            if (_currentDataMode == "accounting-rules" && _selectedRecord is not null)
            {
                await _backend.UpdateAccountingRuleAsync(
                    _selectedRecord.Record.Id, _selectedRecord.Record.Revision, data);
                await LoadAccountingRulesAsync();
                return;
            }
            if (_currentDataMode != "live")
            {
                throw new InvalidOperationException("Dieser Bereich ist schreibgeschützt.");
            }
            if (_selectedRecord is null)
            {
                await _backend.CreateRecordAsync(_currentPage, data);
            }
            else
            {
                await _backend.UpdateRecordAsync(
                    _currentPage, _selectedRecord.Record.Id,
                    _selectedRecord.Record.Revision, data);
            }
            await EndCurrentEditSessionCoreAsync();
            await LoadCurrentPageCoreAsync();
        }, "Datensatz gespeichert.");
    }

    private JsonObject ReadEditorData()
    {
        if (_currentPage == "contacts")
        {
            return new ContactRecordModel
            {
                Id = _selectedRecord?.Record.Id ?? string.Empty,
                Name = ContactNameBox.Text,
                CustomerNumber = ContactCustomerNumberBox.Text,
                Email = ContactEmailBox.Text,
                Phone = ContactPhoneBox.Text,
                Address = ContactAddressBox.Text,
                Note = ContactNoteBox.Text
            }.ToJson();
        }
        if (_currentPage == "topics")
        {
            TopicStepsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            TopicStepsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (!int.TryParse(TopicMinutesBox.Text.Trim(), out var minutes))
            {
                throw new InvalidDataException("Bitte die Bearbeitungsdauer als ganze Zahl eingeben.");
            }
            var topic = new TopicRecordModel
            {
                Id = _selectedRecord?.Record.Id ?? string.Empty,
                Name = TopicNameBox.Text,
                Description = TopicDescriptionBox.Text,
                Category = TopicCategoryBox.Text,
                Priority = SelectedTag(TopicPriorityBox) ?? "normal",
                EstimatedMinutes = minutes,
                Instructions = TopicInstructionsBox.Text,
                Enabled = TopicEnabledBox.IsChecked == true
            };
            foreach (var step in _topicSteps)
            {
                topic.Steps.Add(step);
            }
            return topic.ToJson();
        }
        return JsonNode.Parse(RecordEditorText.Text) as JsonObject
               ?? throw new InvalidDataException("Der Inhalt muss ein JSON-Objekt sein.");
    }

    private void AddTopicStepButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var step = new TopicStepModel
        {
            Id = $"step-{Guid.NewGuid():N}",
            Title = "Neuer Checklistenpunkt"
        };
        _topicSteps.Add(step);
        TopicStepsGrid.SelectedItem = step;
        TopicStepsGrid.ScrollIntoView(step);
    }

    private void RemoveTopicStepButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (TopicStepsGrid.SelectedItem is TopicStepModel step)
        {
            _topicSteps.Remove(step);
        }
    }

    private async void ArchiveRecordButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null || _selectedRecord is null || _currentDataMode != "live")
        {
            return;
        }
        if (MessageBox.Show(
                $"'{_selectedRecord.Title}' wirklich archivieren?",
                "StructuralOffice", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        await RunModuleActionAsync(async () =>
        {
            await EndCurrentEditSessionCoreAsync();
            await _backend.ArchiveRecordAsync(
                _currentPage, _selectedRecord.Record.Id, _selectedRecord.Record.Revision);
            await LoadCurrentPageCoreAsync();
        }, "Datensatz archiviert.");
    }

    private async void StartEditingButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null || _selectedRecord is null || _currentDataMode != "live")
        {
            return;
        }
        await RunModuleActionAsync(async () =>
        {
            await EndCurrentEditSessionCoreAsync();
            var result = await _backend.StartEditingAsync(
                _currentPage, _selectedRecord.Record.Id);
            _editSessionId = result["session_id"]?.GetValue<string>();
            _editRecordId = _selectedRecord.Record.Id;
            _editCollection = _currentPage;
            EditorMetaText.Text += "  ·  Bearbeitung reserviert";
        }, "Bearbeitungssitzung gestartet.");
    }

    private async void ShowEditorsButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null || _selectedRecord is null || _currentDataMode != "live")
        {
            return;
        }
        await RunModuleActionAsync(async () =>
        {
            var result = await _backend.GetEditorsAsync(_currentPage, _selectedRecord.Record.Id);
            var editors = result["editors"] is JsonArray items
                ? items.OfType<JsonObject>().Select(item =>
                    $"• {FirstText(item, "user_name")} – bis {FormatTimestamp(FirstText(item, "expires_at"))}")
                    .ToList()
                : [];
            var message = editors.Count == 0
                ? "Dieser Datensatz wird aktuell von niemandem bearbeitet."
                : string.Join(Environment.NewLine, editors);
            MessageBox.Show(message, "Aktive Bearbeiter",
                MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private async void EndEditingButton_OnClick(object sender, RoutedEventArgs eventArgs) =>
        await RunModuleActionAsync(EndCurrentEditSessionCoreAsync, "Bearbeitungssitzung beendet.");

    private async Task EndCurrentEditSessionCoreAsync()
    {
        if (_backend is null || string.IsNullOrWhiteSpace(_editSessionId) ||
            string.IsNullOrWhiteSpace(_editCollection) || string.IsNullOrWhiteSpace(_editRecordId))
        {
            return;
        }
        var sessionId = _editSessionId;
        var collection = _editCollection;
        var recordId = _editRecordId;
        _editSessionId = null;
        _editCollection = null;
        _editRecordId = null;
        try
        {
            await _backend.EndEditingAsync(collection, recordId, sessionId);
        }
        catch
        {
            // Sessions expire automatically; cleanup must never block navigation or logout.
        }
    }

    private async void SetTaskStatusButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null || _selectedRecord is null)
        {
            return;
        }
        var status = SelectedContent(TaskStatusBox) ?? "open";
        await RunModuleActionAsync(async () =>
        {
            await _backend.SetOccurrenceStatusAsync(_selectedRecord.Record.Id, status);
            await LoadCurrentPageCoreAsync();
        }, "Aufgabenstatus aktualisiert.");
    }

    private async void ImportInvoicesButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null)
        {
            return;
        }
        var dialog = new OpenFileDialog { Filter = "CSV-Dateien (*.csv)|*.csv|Alle Dateien (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        await RunModuleActionAsync(async () =>
        {
            var content = await File.ReadAllBytesAsync(dialog.FileName);
            var preview = await _backend.ImportInvoiceCsvAsync(dialog.SafeFileName, content, false);
            var decision = MessageBox.Show(
                "Importvorschau:\n\n" + preview.ToJsonString(PrettyJson) +
                "\n\nDiesen Import jetzt anwenden?",
                "Rechnungsimport", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (decision == MessageBoxResult.Yes)
            {
                await _backend.ImportInvoiceCsvAsync(dialog.SafeFileName, content, true);
                await LoadCurrentPageCoreAsync();
            }
        });
    }

    private async void ExportInvoicesButton_OnClick(object sender, RoutedEventArgs eventArgs) =>
        await DownloadActionAsync(() => _backend!.ExportInvoicesAsync(false));

    private async void ImportExcelButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null)
        {
            return;
        }
        var dialog = new OpenFileDialog
        {
            Filter = "Excel-Dateien (*.xlsx)|*.xlsx|Alle Dateien (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        await RunModuleActionAsync(async () =>
        {
            var preview = await _backend.PreviewInvoiceExcelAsync(
                await File.ReadAllBytesAsync(dialog.FileName));
            if (preview["records"] is not JsonArray records)
            {
                throw new InvalidDataException("Die Importvorschau enthält keine Rechnungen.");
            }
            var decision = MessageBox.Show(
                $"Excel-Import: {records.Count} Datensätze.\n\n" +
                $"Neu: {preview["created"]}  ·  Aktualisiert: {preview["updated"]}\n\n" +
                "Diesen Import jetzt anwenden?",
                "Excel-Rechnungsimport", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (decision == MessageBoxResult.Yes)
            {
                await _backend.ApplyInvoiceRecordsAsync(records);
                await LoadCurrentPageCoreAsync();
            }
        });
    }

    private async void ExportCsvButton_OnClick(object sender, RoutedEventArgs eventArgs) =>
        await DownloadActionAsync(() => _backend!.ExportInvoicesCsvAsync());

    private async void DownloadTemplateButton_OnClick(object sender, RoutedEventArgs eventArgs) =>
        await DownloadActionAsync(() => _backend!.ExportInvoicesAsync(true));

    private async void GenerateDocumentButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null || _selectedRecord is null)
        {
            MessageBox.Show("Bitte zuerst eine Rechnung auswählen.", "StructuralOffice");
            return;
        }
        var selected = ModuleDataGrid.SelectedItems.OfType<DisplayRecord>().ToList();
        if (selected.Count == 0)
        {
            selected.Add(_selectedRecord);
        }
        var numbers = new JsonArray();
        foreach (var item in selected)
        {
            var number = FirstText(item.Record.Data, "invoice_number");
            if (!string.IsNullOrWhiteSpace(number))
            {
                numbers.Add(number);
            }
        }
        var type = SelectedTag(DocumentTypeBox) ?? "payment_reminder";
        await DownloadActionAsync(() => _backend.GenerateDocumentsAsync(new JsonObject
        {
            ["document_type"] = type,
            ["invoice_numbers"] = numbers
        }));
    }

    private async void AccountingMembersButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null || _selectedRecord is null)
        {
            return;
        }
        await RunModuleActionAsync(async () =>
        {
            _currentDataMode = "accounting-members";
            LoadRows(await _backend.GetAccountingTaskInvoicesAsync(_selectedRecord.Record.Id));
        });
    }

    private async void AccountingRulesButton_OnClick(object sender, RoutedEventArgs eventArgs) =>
        await RunModuleActionAsync(LoadAccountingRulesAsync);

    private async Task LoadAccountingRulesAsync()
    {
        if (_backend is null)
        {
            return;
        }
        _currentDataMode = "accounting-rules";
        RecordEditorText.IsReadOnly = false;
        SaveRecordButton.Visibility = Visibility.Visible;
        LoadRows(await _backend.GetAccountingRulesAsync());
    }

    private async void AdministrationSectionBox_OnSelectionChanged(
        object sender, SelectionChangedEventArgs eventArgs)
    {
        if (_authenticated && _currentPage == "administration" && !_moduleBusy)
        {
            await RunModuleActionAsync(LoadAdministrationSectionAsync);
        }
    }

    private async void SetRoleButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null || _selectedRecord is null)
        {
            return;
        }
        await RunModuleActionAsync(async () =>
        {
            await _backend.SetRoleAsync(
                _selectedRecord.Record.Id, SelectedContent(RoleBox) ?? "viewer");
            await LoadAdministrationSectionAsync();
        }, "Rolle aktualisiert.");
    }

    private async void CreateBackupButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null) return;
        await RunModuleActionAsync(async () =>
        {
            await _backend.CreateBackupAsync();
            await LoadAdministrationSectionAsync();
        }, "Backup erstellt.");
    }

    private async void DownloadBackupButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null || _selectedRecord is null) return;
        var filename = BackupFilename(_selectedRecord);
        await DownloadActionAsync(() => _backend.DownloadBackupAsync(filename));
    }

    private async void RestoreBackupButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null || _selectedRecord is null) return;
        var filename = BackupFilename(_selectedRecord);
        if (MessageBox.Show(
                $"Backup '{filename}' wirklich wiederherstellen? Die aktuellen Daten werden ersetzt.",
                "Backup wiederherstellen", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunModuleActionAsync(async () =>
        {
            await _backend.RestoreBackupAsync(filename);
            await LoadAdministrationSectionAsync();
        }, "Backup wiederhergestellt.");
    }

    private async void DeleteBackupButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null || _selectedRecord is null) return;
        var filename = BackupFilename(_selectedRecord);
        if (MessageBox.Show($"Backup '{filename}' löschen?", "Backup löschen",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunModuleActionAsync(async () =>
        {
            await _backend.DeleteBackupAsync(filename);
            await LoadAdministrationSectionAsync();
        }, "Backup gelöscht.");
    }

    private async void TestNotificationButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null) return;
        await RunModuleActionAsync(
            () => _backend.SendTestNotificationAsync(), "Testbenachrichtigung gesendet.");
    }

    private async Task DownloadActionAsync(Func<Task<BackendDownload>> action)
    {
        if (_backend is null) return;
        await RunModuleActionAsync(async () =>
        {
            var download = await action();
            var dialog = new SaveFileDialog { FileName = download.Filename, AddExtension = true };
            if (dialog.ShowDialog(this) == true)
            {
                await File.WriteAllBytesAsync(dialog.FileName, download.Content);
            }
        });
    }

    private async Task RunModuleActionAsync(Func<Task> action, string? successMessage = null)
    {
        if (_moduleBusy)
        {
            return;
        }
        _moduleBusy = true;
        RefreshModuleButton.IsEnabled = false;
        ModuleBusyText.Text = "Backend wird verarbeitet …";
        try
        {
            await action();
            if (!string.IsNullOrWhiteSpace(successMessage))
            {
                ModuleBusyText.Text = successMessage;
            }
        }
        catch (BackendApiException exception)
        {
            if (exception.ErrorCode == "revision_conflict" && exception.CurrentRecord is not null)
            {
                _selectedRecord = CreateDisplayRecord(exception.CurrentRecord);
                LoadRecordIntoEditor(exception.CurrentRecord);
                EditorMetaText.Text = $"Neueste Backendversion · Revision {exception.CurrentRecord.Revision}";
                MessageBox.Show(
                    "Der Datensatz wurde zwischenzeitlich von einer anderen Person geändert. " +
                    "Die aktuelle Backendversion wurde geladen. Bitte prüfe deine Eingaben erneut.",
                    "Änderungskonflikt", MessageBoxButton.OK, MessageBoxImage.Warning);
                ModuleBusyText.Text = "Aktuelle Version geladen";
                return;
            }
            MessageBox.Show(exception.Message, "StructuralOffice-Backend",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ModuleBusyText.Text = "Aktion fehlgeschlagen";
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "StructuralOffice",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ModuleBusyText.Text = "Aktion fehlgeschlagen";
        }
        finally
        {
            _moduleBusy = false;
            RefreshModuleButton.IsEnabled = true;
        }
    }

    private static string? SelectedTag(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static string? SelectedContent(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Content?.ToString();

    private static void SelectComboValue(ComboBox box, string value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static void SelectComboTag(ComboBox box, string value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static string BackupFilename(DisplayRecord record) =>
        FirstText(record.Record.Data, "filename") is { Length: > 0 } filename
            ? filename
            : record.Record.Id;

    private static string FormatTimestamp(string value) =>
        DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            : value;

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

    private sealed record DisplayRecord(
        BackendRecord Record,
        string Title,
        string Status,
        string Detail,
        int Revision,
        string SearchText);
}
