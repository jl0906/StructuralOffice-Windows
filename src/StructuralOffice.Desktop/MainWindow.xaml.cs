using System.IO;
using System.Linq;
using System.Net.Http;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private bool _creatingRecord;
    private bool _suppressTodayTaskNavigation;
    private List<DisplayRecord> _allDisplayRows = [];
    private List<BackendRecord> _allYearTaskRecords = [];
    private readonly ObservableCollection<TopicStepModel> _topicSteps = [];
    private readonly ObservableCollection<RoutineTopicOption> _routineTopics = [];
    private readonly ObservableCollection<TaskChecklistItemModel> _taskChecklist = [];
    private CancellationTokenSource? _liveUpdatesCancellation;
    private bool _newManualTask;
    private bool _languageReady;
    private bool _developerMode;

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private static readonly IReadOnlyDictionary<string, WorkspacePage> WorkspacePages =
        new Dictionary<string, WorkspacePage>(StringComparer.OrdinalIgnoreCase)
        {
            ["contacts"] = new(
                "Contacts",
                "Manage customers, companies, and contacts in one place.",
                "K"),
            ["topics"] = new(
                "Topics",
                "Structure recurring content and responsibilities.",
                "T"),
            ["routines"] = new(
                "Routines",
                "Plan recurring workflows and execute them consistently.",
                "R"),
            ["tasks"] = new(
                "Tasks",
                "Keep open work, due dates, and progress together.",
                "A"),
            ["invoices"] = new(
                "Invoices",
                "Track invoices, payment terms, and statuses.",
                "R"),
            ["documents"] = new(
                "Documents",
                "Organize documents for contacts and workflows.",
                "D"),
            ["accounting"] = new(
                "Dunning",
                "Manage automated reminders, invoice groups, and escalation rules.",
                "M"),
            ["settings"] = new(
                "Settings",
                "Configure the connection, language, updates, and data mode.",
                "E"),
            ["administration"] = new(
                "Administration",
                "Manage user roles, backups, and revision-safe audit logs.",
                "A")
        };

    public MainWindow()
    {
        InitializeComponent();
        TopicStepsGrid.ItemsSource = _topicSteps;
        RoutineTopicsList.ItemsSource = _routineTopics;
        TaskChecklistGrid.ItemsSource = _taskChecklist;
        UpdateRoutineFrequencyFields();
        UpdateRoutinePriorityHint();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _liveUpdatesCancellation?.Cancel();
            _backend?.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        var language = await _settingsStore.LoadLanguageAsync();
        UiLocalization.SetLanguage(language);
        SelectComboTag(LanguageBox, language);
        _developerMode = await _settingsStore.LoadDeveloperModeAsync();
        DeveloperModeBox.IsChecked = _developerMode;
        UiLocalization.Apply(this);
        ConfigureDeveloperVisibility();
        _languageReady = true;

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
                SetBusy(true, UiLocalization.Choose(
                    "Signing in automatically …", "Automatische Anmeldung …"));
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
                    ShowValidation(UiLocalization.Choose(
                        $"The saved sign-in is no longer valid: {exception.Message}",
                        $"Die gespeicherte Anmeldung ist nicht mehr gültig: {exception.Message}"));
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

        SetBusy(true, UiLocalization.Choose(
            "Opening browser sign-in …", "Browser-Anmeldung wird geöffnet …"));
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
            ShowValidation(UiLocalization.Choose(
                "Sign-in was cancelled or timed out.",
                "Die Anmeldung wurde abgebrochen oder hat zu lange gedauert."));
        }
        catch (Exception exception)
        {
            ShowValidation(UiLocalization.Choose(
                $"Sign-in failed: {exception.Message}",
                $"Anmeldung fehlgeschlagen: {exception.Message}"));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void LogoutButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        _liveUpdatesCancellation?.Cancel();
        _liveUpdatesCancellation = null;
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
            ShowValidation(UiLocalization.Choose(
                "Enter a complete HTTP or HTTPS address.",
                "Bitte eine vollständige HTTP- oder HTTPS-Adresse eingeben."));
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
            [new CheckItem(UiLocalization.Choose("Connection", "Verbindung"),
                CheckState.Error, detail)],
            null,
            DateTimeOffset.Now));
    }

    private void ShowResult(IntegrationCheckResult result)
    {
        ResultItems.ItemsSource = result.Checks.Select(item => new CheckItemView(
            UiLocalization.Text(item.Name),
            UiLocalization.Text(item.Detail),
            item.State switch
            {
                CheckState.Success => new SolidColorBrush(Color.FromRgb(63, 199, 132)),
                CheckState.Warning => new SolidColorBrush(Color.FromRgb(246, 184, 72)),
                _ => new SolidColorBrush(Color.FromRgb(242, 95, 92))
            })).ToList();
        CheckedAtText.Text = UiLocalization.IsGerman
            ? $"Geprüft am {result.CheckedAt:dd.MM.yyyy 'um' HH:mm:ss}"
            : $"Checked on {result.CheckedAt:yyyy-MM-dd 'at' HH:mm:ss}";
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
            StartLiveUpdates();
            ShowPage("overview");
        }
    }

    private async void NavigationButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: string pageKey })
        {
            await ShowPageAsync(pageKey);
        }
    }

    private async void LanguageBox_OnSelectionChanged(
        object sender, SelectionChangedEventArgs eventArgs)
    {
        if (!_languageReady) return;
        var language = SelectedTag(LanguageBox) == "de" ? "de" : "en";
        UiLocalization.SetLanguage(language);
        UiLocalization.Apply(this);
        await _settingsStore.SaveLanguageAsync(language);
        if (_authenticated)
        {
            await ShowPageAsync(_currentPage);
        }
    }

    private async void DeveloperModeBox_OnChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (!_languageReady) return;
        _developerMode = DeveloperModeBox.IsChecked == true;
        await _settingsStore.SaveDeveloperModeAsync(_developerMode);
        if (!_developerMode && SelectedTag(AdministrationSectionBox) is "audit" or "events")
        {
            AdministrationSectionBox.SelectedIndex = 0;
        }
        ConfigureDeveloperVisibility();
        if (_authenticated && _currentPage != "overview")
        {
            ConfigureModuleActions(_currentPage);
            await LoadCurrentPageAsync();
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
            PageTitleText.Text = UiLocalization.Choose("Today", "Heute");
            PageSubtitleText.Text = UiLocalization.Choose(
                "Your estimated office workload at a glance", "Dein geschätzter Büroaufwand auf einen Blick");
        }
        else if (WorkspacePages.TryGetValue(pageKey, out var page))
        {
            PageTitleText.Text = UiLocalization.Text(page.Title);
            var englishDescription = pageKey == "administration" && !_developerMode
                ? "Manage user roles and backups."
                : page.Description;
            PageSubtitleText.Text = UiLocalization.Choose(
                englishDescription,
                pageKey switch
                {
                    "contacts" => "Kunden, Firmen und Ansprechpartner zentral verwalten.",
                    "topics" => "Wiederkehrende Inhalte und Zuständigkeiten strukturieren.",
                    "routines" => "Regelmäßige Abläufe planen und nachvollziehbar ausführen.",
                    "tasks" => "Offene Vorgänge, Fälligkeiten und Bearbeitungsstände bündeln.",
                    "invoices" => "Rechnungen, Zahlungsziele und Status übersichtlich verfolgen.",
                    "documents" => "Dokumente zu Kontakten und Vorgängen geordnet bereitstellen.",
                    "accounting" => "Automatische Mahnläufe, Rechnungsgruppen und Eskalationsregeln verwalten.",
                    "settings" => "Verbindung, Sprache, Updates und Datenmodus konfigurieren.",
                    _ => _developerMode
                        ? "Benutzerrollen, Backups und technische Änderungsprotokolle verwalten."
                        : "Benutzerrollen und Backups verwalten."
                });
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

        if (isOverview)
        {
            await LoadDashboardAsync();
        }
        else
        {
            ConfigureModuleActions(pageKey);
            await LoadCurrentPageAsync();
        }
    }

    private async Task LoadDashboardAsync()
    {
        if (_backend is null) return;
        try
        {
            var dashboard = await _backend.GetTodayDashboardAsync();
            var minutes = IntValue(dashboard, "estimated_minutes_total");
            var count = IntValue(dashboard, "open_task_count");
            TodayMinutesText.Text = FormatDuration(minutes);
            TodayTaskCountText.Text = UiLocalization.Choose(
                $"{count} open {(count == 1 ? "task" : "tasks")} due today or earlier",
                $"{count} offene {(count == 1 ? "Aufgabe" : "Aufgaben")} heute oder überfällig");
            if (dashboard["longest_task"] is JsonObject longest)
            {
                LongestTaskTitleText.Text = FriendlyTaskTitle(longest);
                LongestTaskMetaText.Text = UiLocalization.Choose(
                    $"About {FormatDuration(IntValue(longest, "estimated_minutes"))} · due {FormatDueAt(FirstText(longest, "due_at"))}",
                    $"Ca. {FormatDuration(IntValue(longest, "estimated_minutes"))} · fällig {FormatDueAt(FirstText(longest, "due_at"))}");
            }
            else
            {
                LongestTaskTitleText.Text = UiLocalization.Choose("No task due", "Keine Aufgabe fällig");
                LongestTaskMetaText.Text = UiLocalization.Choose(
                    "Nothing is currently planned for today.", "Für heute ist aktuell nichts eingeplant.");
            }
            await LoadTodayTaskListAsync();
        }
        catch (Exception exception)
        {
            TodayMinutesText.Text = "–";
            TodayTaskCountText.Text = UiLocalization.Choose(
                "Workload could not be loaded.", "Tagesaufwand konnte nicht geladen werden.");
            LongestTaskMetaText.Text = exception.Message;
            TodayTasksList.ItemsSource = null;
            TodayTasksEmptyText.Visibility = Visibility.Visible;
        }
    }

    private async Task LoadTodayTaskListAsync()
    {
        if (_backend is null) return;
        var endOfToday = DateTime.Today.AddDays(1);
        var tasks = (await _backend.GetTasksAsync()).Items
            .Select(record => (Record: record, Task: TaskRecordModel.FromJson(record.Data)))
            .Where(item => item.Task.Status is "open" or "in_progress" &&
                           DateTime.TryParse(item.Task.DueAt, out var due) && due < endOfToday)
            .OrderBy(item => DateTime.Parse(item.Task.DueAt))
            .ThenByDescending(item => item.Task.EstimatedMinutes)
            .Select(item =>
            {
                var due = DateTime.Parse(item.Task.DueAt);
                var overdue = due.Date < DateTime.Today;
                var source = item.Task.SourceType switch
                {
                    "routine" => UiLocalization.Choose("Routine", "Routine"),
                    "accounting_due_batch" => UiLocalization.Choose("Payment follow-up", "Zahlungslauf"),
                    _ => UiLocalization.Choose("Manual task", "Manuelle Aufgabe")
                };
                return new TodayTaskItem(
                    item.Record.Id,
                    overdue ? UiLocalization.Choose("OVERDUE", "ÜBERFÄLLIG") : UiLocalization.Choose("TODAY", "HEUTE"),
                    overdue ? due.ToString("dd.MM. · HH:mm") : due.ToString("HH:mm"),
                    item.Task.Title,
                    $"{source} · {LocalizedStatus(item.Task.Status)}",
                    FormatDuration(item.Task.EstimatedMinutes),
                    new SolidColorBrush(overdue
                        ? Color.FromRgb(220, 38, 38)
                        : item.Task.Status == "in_progress"
                            ? Color.FromRgb(245, 158, 11)
                            : Color.FromRgb(37, 99, 235)),
                    item.Task.SourceType != "accounting_due_batch" ||
                    item.Task.AvailableActions.Any(action =>
                        action is "schedule_dunning" or "confirm_settled"));
            })
            .ToList();
        TodayTasksList.ItemsSource = tasks;
        TodayTasksEmptyText.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatDuration(int minutes) => minutes < 60
        ? $"{minutes} Min."
        : minutes % 60 == 0 ? $"{minutes / 60} Std." : $"{minutes / 60} Std. {minutes % 60} Min.";

    private static string FormatDueAt(string value) => DateTime.TryParse(value, out var due)
        ? (UiLocalization.IsGerman ? due.ToString("dd.MM.yyyy, HH:mm") : due.ToString("yyyy-MM-dd, HH:mm"))
        : value;

    private static string FriendlyTaskTitle(JsonObject task)
    {
        var title = FirstText(task, "title");
        if (task["snapshot"] is not JsonObject snapshot) return LocalizeCanonicalTaskTitle(title);
        if (FirstText(task, "source_type") != "accounting_due_batch")
            return FirstText(snapshot, "topic_name") is { Length: > 0 } topic
                ? LocalizeCanonicalTaskTitle(topic) : LocalizeCanonicalTaskTitle(title);
        var count = IntValue(snapshot, "invoice_count_open");
        var currency = FirstText(snapshot, "currency");
        var range = FirstText(snapshot, "invoice_range");
        var subject = FirstText(snapshot, "task_type") == "dunning"
            ? UiLocalization.Choose("Process dunning notices", "Mahnungen bearbeiten")
            : UiLocalization.Choose("Process payment reminders", "Zahlungserinnerungen bearbeiten");
        var invoices = UiLocalization.Choose(
            count == 1 ? "invoice" : "invoices",
            count == 1 ? "Rechnung" : "Rechnungen");
        return string.IsNullOrWhiteSpace(range)
            ? $"{subject} · {count} {invoices} · {currency}"
            : $"{subject} · {count} {invoices} · {range} · {currency}";
    }

    private static string LocalizeCanonicalTaskTitle(string title)
    {
        const string reminderPrefix = "Write payment reminders ";
        const string dunningPrefix = "Write dunning notice ";
        if (UiLocalization.IsGerman && title.StartsWith(reminderPrefix, StringComparison.Ordinal))
            return $"Zahlungserinnerungen {title[reminderPrefix.Length..]} schreiben";
        if (UiLocalization.IsGerman && title.StartsWith(dunningPrefix, StringComparison.Ordinal))
            return $"Mahnung {title[dunningPrefix.Length..]} schreiben";
        return title;
    }

    private async void TodayTasksList_OnSelectionChanged(
        object sender, SelectionChangedEventArgs eventArgs)
    {
        if (_suppressTodayTaskNavigation)
        {
            TodayTasksList.SelectedItem = null;
            _suppressTodayTaskNavigation = false;
            return;
        }
        if (TodayTasksList.SelectedItem is not TodayTaskItem task) return;
        await ShowPageAsync("tasks");
        var row = TaskYearList.Items.OfType<YearTaskItem>()
            .FirstOrDefault(item => item.Record.Id == task.Id);
        if (row is not null)
        {
            row.IsSelected = true;
            TaskYearList.SelectedItem = row;
            TaskYearList.ScrollIntoView(row);
        }
        TodayTasksList.SelectedItem = null;
    }

    private void TodayTaskCompletionCheckBox_OnPreviewMouseLeftButtonDown(
        object sender, MouseButtonEventArgs eventArgs) => _suppressTodayTaskNavigation = true;

    private async void TodayTaskCompletionCheckBox_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        _suppressTodayTaskNavigation = false;
        if (sender is not CheckBox { DataContext: TodayTaskItem item } checkBox ||
            _backend is null || !item.CanComplete)
            return;
        checkBox.IsChecked = false;
        await RunModuleActionAsync(async () =>
        {
            if (!await CompleteTaskAsync(item.Id)) return;
            await LoadDashboardAsync();
            ModuleBusyText.Text = UiLocalization.Choose("Task completed.", "Aufgabe erledigt.");
        });
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
            NewRecordButton, SaveRecordButton, ArchiveRecordButton,
            TaskStatusFilterBox, TaskSourceFilterBox,
            NewTaskButton, TaskActionPanel,
            ImportInvoicesButton, ImportExcelButton,
            ExportInvoicesButton, ExportCsvButton,
            DownloadTemplateButton, DocumentTypeBox, GenerateDocumentButton,
            AccountingMembersButton, AccountingRulesButton, AdministrationSectionBox,
            RoleBox, SetRoleButton, CreateBackupButton, DownloadBackupButton,
            RestoreBackupButton, DeleteBackupButton, TestNotificationButton,
            ManualUpdateButton, LanguageBox, DeveloperModeBox
        };
        foreach (var element in all)
        {
            element.Visibility = Visibility.Collapsed;
        }

        var isLiveEditor = pageKey is "contacts" or "topics" or "routines" or "invoices";
        var hasFriendlyEditor = pageKey is "contacts" or "topics" or "routines" or "tasks";
        var canEditLive = pageKey is "topics" or "routines" ||
                          (_developerMode && pageKey == "invoices");
        SetVisibility(canEditLive, NewRecordButton, SaveRecordButton, ArchiveRecordButton);
        SetVisibility(pageKey == "invoices", ImportInvoicesButton, ImportExcelButton, ExportInvoicesButton,
            ExportCsvButton, DownloadTemplateButton);
        SetVisibility(pageKey == "documents", DocumentTypeBox, GenerateDocumentButton);
        SetVisibility(pageKey == "accounting", AccountingMembersButton, AccountingRulesButton);
        SetVisibility(pageKey == "administration", AdministrationSectionBox,
            TestNotificationButton);
        SetVisibility(pageKey == "settings", TestNotificationButton, ManualUpdateButton,
            LanguageBox, DeveloperModeBox);
        ContextActionsPanel.Visibility = all.Any(item => item.Visibility == Visibility.Visible)
            ? Visibility.Visible
            : Visibility.Collapsed;
        var isContactEditor = pageKey == "contacts";
        var isTopicEditor = pageKey == "topics";
        var isRoutineEditor = pageKey == "routines";
        var isTaskEditor = pageKey == "tasks";
        ModuleToolbarPanel.Visibility = isTaskEditor ? Visibility.Collapsed : Visibility.Visible;
        TaskYearPanel.Visibility = isTaskEditor ? Visibility.Visible : Visibility.Collapsed;
        ContactEditorPanel.Visibility = isContactEditor ? Visibility.Visible : Visibility.Collapsed;
        TopicEditorPanel.Visibility = isTopicEditor ? Visibility.Visible : Visibility.Collapsed;
        RoutineEditorPanel.Visibility = isRoutineEditor ? Visibility.Visible : Visibility.Collapsed;
        TaskEditorPanel.Visibility = isTaskEditor ? Visibility.Visible : Visibility.Collapsed;
        var showRawEditor = _developerMode && !hasFriendlyEditor;
        RecordEditorText.Visibility = showRawEditor ? Visibility.Visible : Visibility.Collapsed;
        FriendlyDetailsText.Visibility = !hasFriendlyEditor && !showRawEditor
            ? Visibility.Visible : Visibility.Collapsed;
        RecordEditorText.IsReadOnly = !isLiveEditor;
        IncludeArchivedBox.Visibility = _developerMode && isLiveEditor
            ? Visibility.Visible : Visibility.Collapsed;
        var fullWidthWithoutRecordList = isTaskEditor || pageKey == "settings" && !_developerMode;
        RecordListCard.Visibility = fullWidthWithoutRecordList ? Visibility.Collapsed : Visibility.Visible;
        EditorCard.Visibility = isTaskEditor ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(EditorCard, fullWidthWithoutRecordList ? 0 : 2);
        Grid.SetColumnSpan(EditorCard, fullWidthWithoutRecordList ? 3 : 1);
        ConfigureDeveloperVisibility();
        EditorHelpText.Text = UiLocalization.IsGerman
            ? isContactEditor
                ? "Pflichtfelder sind mit * gekennzeichnet. Änderungen werden automatisch geschützt."
                : isTopicEditor
                    ? "Checklistenpunkte können direkt bearbeitet und per Auswahl entfernt werden."
                    : isRoutineEditor
                        ? "Beim Speichern wird eine direkte Aufgabe mit der gewählten Dauer und Priorität geplant."
                        : isTaskEditor
                            ? "Aufgabenstatus, Fälligkeit und Checkliste werden live mit dem Backend synchronisiert."
                            : isLiveEditor && _developerMode
                                ? "JSON-Felder können bearbeitet werden. Revisionen schützen vor parallelen Änderungen."
                                : "Hier findest du die wichtigsten Informationen auf einen Blick."
            : isContactEditor
                ? "Required fields are marked with *. Changes are protected automatically."
                : isTopicEditor
                    ? "Checklist items can be edited directly and removed by selection."
                : isRoutineEditor
                    ? "Saving schedules a direct task with the selected duration and priority."
                        : isTaskEditor
                            ? "Task status, due date, and checklist are synchronized live with the backend."
                            : isLiveEditor && _developerMode
                                ? "JSON fields can be edited. Revisions protect concurrent changes."
                                : "Find the most important information here at a glance.";
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
                _currentDataMode = "live";
                LoadRows((await _backend.GetLiveRecordsAsync(
                    _currentPage, IncludeArchivedBox.IsChecked == true)).Items);
                break;
            case "invoices":
                _currentDataMode = "live";
                var invoices = (await _backend.GetLiveRecordsAsync("invoices")).Items
                    .Where(item => FirstText(item.Data, "status") == "open" &&
                                   IntValue(item.Data, "outstanding_cents") > 0);
                LoadRows(invoices);
                break;
            case "routines":
                _currentDataMode = "live";
                LoadRows((await _backend.GetLiveRecordsAsync(
                    "routines", IncludeArchivedBox.IsChecked == true)).Items);
                break;
            case "tasks":
                _currentDataMode = "tasks";
                _newManualTask = false;
                _allYearTaskRecords = [.. (await _backend.GetTasksAsync()).Items];
                ApplyYearTaskFilter();
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
                    ["application_version"] = "1.0.0",
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
        if (!_developerMode && section is "audit" or "events")
        {
            AdministrationSectionBox.SelectedIndex = 0;
            section = "roles";
        }
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
        _creatingRecord = false;
        _allDisplayRows = records.Select(CreateDisplayRecord).ToList();
        _selectedRecord = null;
        ClearEditor();
        EditorTitleText.Text = UiLocalization.Choose("Select a record", "Datensatz auswählen");
        EditorMetaText.Text = string.Empty;
        ApplyModuleFilter();
    }

    private void ConfigureDeveloperVisibility()
    {
        RevisionColumn.Visibility = _developerMode ? Visibility.Visible : Visibility.Collapsed;
        AuditSectionItem.Visibility = _developerMode ? Visibility.Visible : Visibility.Collapsed;
        EventsSectionItem.Visibility = _developerMode ? Visibility.Visible : Visibility.Collapsed;
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
            ? UiLocalization.Choose($"{rows.Count} records", $"{rows.Count} Einträge")
            : UiLocalization.Choose(
                $"{rows.Count} of {_allDisplayRows.Count} records",
                $"{rows.Count} von {_allDisplayRows.Count} Einträgen");
        if (rows.Count > 0)
        {
            ModuleDataGrid.SelectedIndex = 0;
        }
    }

    private async Task LoadRoutineTopicsAsync()
    {
        if (_backend is null) return;
        var selected = _routineTopics.Where(item => item.IsSelected)
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var topics = await _backend.GetLiveRecordsAsync("topics");
        _routineTopics.Clear();
        foreach (var topic in topics.Items.OrderBy(item => FirstText(item.Data, "name")))
        {
            _routineTopics.Add(new RoutineTopicOption
            {
                Id = topic.Id,
                Name = FirstText(topic.Data, "name") is { Length: > 0 } name ? name : topic.Id,
                IsSelected = selected.Contains(topic.Id)
            });
        }
    }

    private static DisplayRecord CreateDisplayRecord(BackendRecord record)
    {
        var data = record.Data;
        var title = FirstText(data, "name", "invoice_number", "topic_name", "routine_name",
            "filename", "user_name", "task_type", "action", "id");
        if (string.IsNullOrWhiteSpace(title) && data["snapshot"] is JsonObject snapshot)
        {
            title = FirstText(snapshot, "topic_name", "task_type", "routine_name");
        }
        if (data["snapshot"] is JsonObject accountingSnapshot &&
            FirstText(data, "source_type") == "accounting_due_batch" &&
            FirstText(accountingSnapshot, "invoice_range") is { Length: > 0 } invoiceRange)
        {
            title = FirstText(accountingSnapshot, "task_type") == "dunning"
                ? UiLocalization.Choose($"Write dunning notice {invoiceRange}", $"Mahnung {invoiceRange} schreiben")
                : UiLocalization.Choose($"Write payment reminders {invoiceRange}", $"Zahlungserinnerungen {invoiceRange} schreiben");
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            title = record.Id;
        }
        if (FirstText(data, "source_type") == "routine" && LooksTechnicalIdentifier(title))
        {
            title = UiLocalization.Choose("Routine task", "Routine-Aufgabe");
        }
        var status = record.ArchivedAt is not null
            ? UiLocalization.Choose("archived", "archiviert")
            : FirstText(data, "status", "role", "due_state", "enabled", "operation");
        status = status switch
        {
            "open" => UiLocalization.Text("Open"),
            "in_progress" => UiLocalization.Text("In progress"),
            "completed" or "auto_completed" => UiLocalization.Text("Completed"),
            "skipped" => UiLocalization.Text("Skipped"),
            "cancelled" => UiLocalization.Text("Cancelled"),
            _ => status
        };
        var detail = FirstText(data, "email", "category", "due_date", "due_at",
            "source_name", "collection", "updated_at");
        if (string.IsNullOrWhiteSpace(detail) && data["snapshot"] is JsonObject detailSnapshot)
        {
            detail = FirstText(detailSnapshot, "category", "description", "source_due_date");
        }
        if (FirstText(data, "due_at") is { Length: > 0 } taskDueAt)
        {
            var estimate = IntValue(data, "estimated_minutes");
            var priority = LocalizedPriority(FirstText(data, "priority"));
            detail = estimate > 0
                ? $"{FormatDueAt(taskDueAt)} · {FormatDuration(estimate)} · {priority}"
                : FormatDueAt(taskDueAt);
        }
        else if (data["schedule"] is JsonObject schedule)
        {
            var frequency = FirstText(schedule, "frequency") switch
            {
                "once" => UiLocalization.Choose("Once", "Einmalig"),
                "daily" => UiLocalization.Choose("Daily", "Täglich"),
                "weekly" => UiLocalization.Choose("Weekly", "Wöchentlich"),
                "yearly" => UiLocalization.Choose("Yearly", "Jährlich"),
                _ => UiLocalization.Choose("Monthly", "Monatlich")
            };
            detail = $"{frequency} · {FormatDuration(IntValue(data, "estimated_minutes"))} · " +
                     LocalizedPriority(FirstText(data, "priority"));
        }
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

    private static bool LooksTechnicalIdentifier(string value) =>
        Guid.TryParse(value, out _) ||
        (value.Length >= 24 && value.All(char.IsAsciiHexDigit));

    private string FormatFriendlyDetails(BackendRecord record)
    {
        var data = record.Data;
        if (_currentPage == "invoices")
        {
            var currency = FirstText(data, "currency") is { Length: > 0 } code ? code : "EUR";
            var outstanding = IntValue(data, "outstanding_cents") / 100m;
            var gross = IntValue(data, "gross_cents") / 100m;
            return string.Join(Environment.NewLine,
                UiLocalization.Choose(
                    $"Invoice number: {FirstText(data, "invoice_number")}",
                    $"Rechnungsnummer: {FirstText(data, "invoice_number")}"),
                UiLocalization.Choose(
                    $"Recipient: {FirstText(data, "contact")}",
                    $"Empfänger: {FirstText(data, "contact")}"),
                UiLocalization.Choose(
                    $"Invoice date: {FirstText(data, "invoice_date")}",
                    $"Rechnungsdatum: {FirstText(data, "invoice_date")}"),
                UiLocalization.Choose(
                    $"Due date: {FirstText(data, "due_date")}",
                    $"Zahlungsziel: {FirstText(data, "due_date")}"),
                UiLocalization.Choose(
                    $"Gross amount: {gross:N2} {currency}",
                    $"Bruttobetrag: {gross:N2} {currency}"),
                UiLocalization.Choose(
                    $"Outstanding: {outstanding:N2} {currency}",
                    $"Offen: {outstanding:N2} {currency}"),
                UiLocalization.Choose(
                    $"Status: {LocalizedStatus(FirstText(data, "status"))}",
                    $"Status: {LocalizedStatus(FirstText(data, "status"))}"));
        }
        if (_currentPage == "settings")
        {
            return string.Join(Environment.NewLine,
                UiLocalization.Choose(
                    $"Application version: {FirstText(data, "application_version")}",
                    $"Anwendungsversion: {FirstText(data, "application_version")}"),
                UiLocalization.Choose(
                    $"Integration version: {FirstText(data, "integration_version")}",
                    $"Integrationsversion: {FirstText(data, "integration_version")}"),
                UiLocalization.Choose(
                    $"Connected backend: {FirstText(data, "backend")}",
                    $"Verbundenes Backend: {FirstText(data, "backend")}"),
                UiLocalization.Choose(
                    $"Server: {FirstText(data, "server")}",
                    $"Server: {FirstText(data, "server")}"),
                string.Empty,
                UiLocalization.Choose(
                    "Use the controls above to choose the language, check for updates, " +
                    "or enable developer mode.",
                    "Über die Bedienelemente oben kannst du die Sprache wählen, nach Updates " +
                    "suchen oder den Entwicklermodus aktivieren."));
        }
        var lines = new List<string>();
        AddFriendlyLine(lines, UiLocalization.Choose("Name", "Name"),
            FirstText(data, "name", "user_name"));
        AddFriendlyLine(lines, UiLocalization.Choose("Role", "Rolle"), FirstText(data, "role"));
        AddFriendlyLine(lines, UiLocalization.Choose("File", "Datei"), FirstText(data, "filename"));
        AddFriendlyLine(lines, UiLocalization.Choose("Status", "Status"),
            LocalizedStatus(FirstText(data, "status")));
        AddFriendlyLine(lines, UiLocalization.Choose("Created", "Erstellt"),
            FirstText(data, "created_at"));
        return lines.Count > 0
            ? string.Join(Environment.NewLine, lines)
            : UiLocalization.Choose(
                "No additional details are available.",
                "Keine weiteren Details verfügbar.");
    }

    private static string LocalizedStatus(string status) => status switch
    {
        "open" => UiLocalization.Text("Open"),
        "in_progress" => UiLocalization.Text("In progress"),
        "completed" or "auto_completed" => UiLocalization.Text("Completed"),
        "skipped" => UiLocalization.Text("Skipped"),
        "cancelled" => UiLocalization.Text("Cancelled"),
        _ => status
    };

    private static string LocalizedPriority(string priority) => priority switch
    {
        "low" => UiLocalization.Choose("Low", "Niedrig"),
        "high" => UiLocalization.Choose("High", "Hoch"),
        "critical" => UiLocalization.Choose("Critical", "Kritisch"),
        _ => UiLocalization.Choose("Normal", "Normal")
    };

    private static int IntValue(JsonObject data, string name)
    {
        if (data[name] is JsonValue value && value.TryGetValue<int>(out var integer))
            return integer;
        return int.TryParse(data[name]?.ToString(), out integer) ? integer : 0;
    }

    private static void AddFriendlyLine(List<string> lines, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) lines.Add($"{label}: {value}");
    }

    private async void ModuleDataGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        var nextRecord = ModuleDataGrid.SelectedItem as DisplayRecord;
        if (nextRecord is null)
        {
            _selectedRecord = null;
            return;
        }
        _creatingRecord = false;
        _selectedRecord = nextRecord;
        EditorTitleText.Text = _selectedRecord.Title;
        var archived = _selectedRecord.Record.ArchivedAt is not null;
        EditorMetaText.Text = _developerMode
            ? $"ID: {_selectedRecord.Record.Id}  ·  Revision {_selectedRecord.Record.Revision}" +
              (archived ? "  ·  archived" : string.Empty)
            : archived
                ? UiLocalization.Choose("Archived", "Archiviert")
                : _selectedRecord.Detail;
        SaveRecordButton.IsEnabled = !archived;
        TaskSaveButton.IsEnabled = !archived;
        DeleteTaskButton.IsEnabled = !archived;
        ArchiveRecordButton.IsEnabled = !archived;
        ContactEditorPanel.IsEnabled = !archived;
        TopicEditorPanel.IsEnabled = !archived;
        RecordEditorText.IsReadOnly = archived ||
                                      _currentDataMode is not ("live" or "accounting-rules");
        LoadRecordIntoEditor(_selectedRecord.Record);
        if (_currentPage == "tasks" && _backend is not null)
        {
            var selectedId = _selectedRecord.Record.Id;
            try
            {
                var task = await _backend.GetTaskAsync(selectedId);
                if (_selectedRecord?.Record.Id == selectedId)
                {
                    _selectedRecord = CreateDisplayRecord(task);
                    EditorMetaText.Text = _developerMode
                        ? $"ID: {task.Id}  ·  Revision {task.Revision}"
                        : _selectedRecord.Detail;
                    LoadRecordIntoEditor(task);
                }
            }
            catch (BackendApiException exception)
            {
                ModuleBusyText.Text = exception.Message;
            }
        }
        if (_currentDataMode == "administration-roles" && _selectedRecord is not null)
        {
            SelectComboValue(RoleBox, FirstText(_selectedRecord.Record.Data, "role"));
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
        if (_currentPage == "routines")
        {
            var routine = RoutineRecordModel.FromJson(record.Data);
            RoutineNameBox.Text = routine.Name;
            RoutineDescriptionBox.Text = routine.Description;
            RoutineEnabledBox.IsChecked = routine.Enabled;
            RoutineEstimatedMinutesBox.Text = routine.EstimatedMinutes.ToString();
            SelectComboTag(RoutinePriorityBox, routine.Priority);
            foreach (var option in _routineTopics)
                option.IsSelected = routine.TopicIds.Contains(option.Id, StringComparer.Ordinal);
            RoutineTopicsList.Items.Refresh();
            SelectComboTag(RoutineFrequencyBox, routine.Frequency);
            RoutineIntervalBox.Text = routine.Interval.ToString();
            RoutineStartDateBox.Text = routine.StartDate;
            RoutineEndDateBox.Text = routine.EndDate;
            RoutineStartDatePicker.SelectedDate = DateTime.TryParse(routine.StartDate, out var start)
                ? start : DateTime.Today;
            RoutineEndDatePicker.SelectedDate = DateTime.TryParse(routine.EndDate, out var end)
                ? end : null;
            RoutineDueTimeBox.Text = routine.DueTime;
            RoutineTimezoneBox.Text = routine.Timezone;
            SelectComboTag(RoutineCatchUpBox, routine.CatchUpPolicy);
            RoutineRemindersBox.Text = string.Join(',', routine.ReminderOffsets);
            SetWeekdayChecks(routine.Weekdays);
            RoutineMonthDaysBox.Text = string.Join(',', routine.MonthDays);
            RoutineMonthsBox.Text = string.Join(',', routine.Months);
            RoutineDatesBox.Text = string.Join(',', routine.Dates);
            SelectComboTag(RoutineBusinessDayBox, routine.BusinessDayRule);
            SelectComboTag(RoutineInvalidDayBox, routine.InvalidDayRule);
            UpdateRoutineFrequencyFields();
            UpdateRoutinePriorityHint();
            return;
        }
        if (_currentPage == "tasks")
        {
            var task = TaskRecordModel.FromJson(record.Data);
            TaskTitleBox.Text = task.Title;
            TaskDescriptionBox.Text = task.Description;
            TaskCategoryBox.Text = task.Category;
            if (DateTime.TryParse(task.DueAt, out var due))
            {
                TaskDueDatePicker.SelectedDate = due.Date;
                TaskDueTimeBox.Text = due.ToString("HH:mm");
            }
            TaskCompletionNoteBox.Text = task.CompletionNote;
            TaskEstimatedMinutesBox.Text = task.EstimatedMinutes.ToString();
            SelectComboTag(TaskPriorityBox, task.Priority);
            SelectComboTag(TaskEditorStatusBox, task.Status);
            SelectComboTag(TaskStatusBox, task.Status);
            _taskChecklist.Clear();
            foreach (var item in task.Checklist) _taskChecklist.Add(item);
            SetTaskCreationMode(false);
            return;
        }
        RecordEditorText.Text = record.Data.ToJsonString(PrettyJson);
        FriendlyDetailsText.Text = FormatFriendlyDetails(record);
    }

    private void ClearEditor()
    {
        RecordEditorText.Clear();
        FriendlyDetailsText.Text = string.Empty;
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
        RoutineNameBox.Clear();
        RoutineDescriptionBox.Clear();
        RoutineEnabledBox.IsChecked = true;
        RoutineEstimatedMinutesBox.Text = "15";
        RoutinePriorityBox.SelectedIndex = 1;
        foreach (var topic in _routineTopics) topic.IsSelected = false;
        RoutineTopicsList.Items.Refresh();
        RoutineFrequencyBox.SelectedIndex = 3;
        RoutineIntervalBox.Text = "1";
        RoutineStartDateBox.Text = DateTime.Today.ToString("yyyy-MM-dd");
        RoutineEndDateBox.Clear();
        RoutineStartDatePicker.SelectedDate = DateTime.Today;
        RoutineEndDatePicker.SelectedDate = null;
        RoutineDueTimeBox.Text = "09:00";
        RoutineTimezoneBox.Text = "Europe/Berlin";
        RoutineCatchUpBox.SelectedIndex = 0;
        RoutineRemindersBox.Text = "-1,0";
        SetWeekdayChecks([]);
        RoutineMonthDaysBox.Text = DateTime.Today.Day.ToString();
        RoutineMonthsBox.Text = DateTime.Today.Month.ToString();
        RoutineDatesBox.Clear();
        RoutineBusinessDayBox.SelectedIndex = 0;
        RoutineInvalidDayBox.SelectedIndex = 1;
        UpdateRoutineFrequencyFields();
        UpdateRoutinePriorityHint();
        TaskTitleBox.Clear();
        TaskDescriptionBox.Clear();
        TaskCategoryBox.Clear();
        TaskDueDatePicker.SelectedDate = DateTime.Today.AddDays(1);
        TaskDueTimeBox.Text = "09:00";
        TaskCompletionNoteBox.Clear();
        TaskEstimatedMinutesBox.Text = "15";
        TaskPriorityBox.SelectedIndex = 1;
        TaskEditorStatusBox.SelectedIndex = 0;
        TaskStatusBox.SelectedIndex = 0;
        _taskChecklist.Clear();
        SetTaskCreationMode(false);
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

    private async void TaskFilterBox_OnChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (IsLoaded && _authenticated && _currentPage == "tasks" && !_moduleBusy)
        {
            await LoadCurrentPageAsync();
        }
    }

    private void YearTaskFilterBox_OnChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (IsLoaded && _currentPage == "tasks")
        {
            ApplyYearTaskFilter();
        }
    }

    private void ApplyYearTaskFilter()
    {
        if (TaskYearList is null || YearTaskStatusFilterBox is null || YearTaskSourceFilterBox is null)
            return;

        var year = DateTime.Today.Year;
        var firstDay = new DateTime(year, 1, 1);
        var nextYear = firstDay.AddYears(1);
        var statusFilter = NullIfEmpty(SelectedTag(YearTaskStatusFilterBox));
        var sourceFilter = NullIfEmpty(SelectedTag(YearTaskSourceFilterBox));
        var parsed = _allYearTaskRecords
            .Select(record => (Record: record, Task: TaskRecordModel.FromJson(record.Data)))
            .Select(item => (item.Record, item.Task,
                Due: DateTime.TryParse(item.Task.DueAt, out var due) ? due : (DateTime?)null))
            .Where(item => item.Due is not null && item.Due >= firstDay && item.Due < nextYear)
            .Where(item => statusFilter is null || item.Task.Status == statusFilter)
            .Where(item => sourceFilter is null || item.Task.SourceType == sourceFilter)
            .OrderBy(item => item.Due)
            .ThenByDescending(item => PriorityRank(item.Task.Priority))
            .ToList();

        var items = new List<YearTaskItem>();
        var previousMonth = -1;
        var calendarCulture = CultureInfo.GetCultureInfo(
            UiLocalization.IsGerman ? "de-DE" : "en-US");
        foreach (var item in parsed)
        {
            var due = item.Due!.Value;
            var source = item.Task.SourceType switch
            {
                "routine" => UiLocalization.Choose("Routine", "Routine"),
                "accounting_due_batch" => UiLocalization.Choose("Accounting package", "Buchhaltungspaket"),
                _ => UiLocalization.Choose("Manual task", "Manuelle Aufgabe")
            };
            var active = item.Task.Status is "open" or "in_progress";
            var overdue = active && due < DateTime.Now;
            var canComplete = active &&
                              (item.Task.SourceType != "accounting_due_batch" ||
                               item.Task.AvailableActions.Any(action =>
                                   action is "schedule_dunning" or "confirm_settled"));
            items.Add(new YearTaskItem
            {
                Record = item.Record,
                Task = item.Task,
                MonthTitle = calendarCulture.DateTimeFormat.GetMonthName(due.Month),
                IsMonthStart = due.Month != previousMonth,
                DayLabel = due.ToString("dd.MM."),
                WeekdayAndTime = due.ToString("ddd · HH:mm", calendarCulture),
                Title = item.Task.Title,
                Meta = $"{source} · {LocalizedStatus(item.Task.Status)} · {LocalizedPriority(item.Task.Priority)}",
                Duration = FormatDuration(item.Task.EstimatedMinutes),
                Status = LocalizedStatus(item.Task.Status),
                Accent = new SolidColorBrush(overdue
                    ? Color.FromRgb(220, 38, 38)
                    : item.Task.Status switch
                    {
                        "in_progress" => Color.FromRgb(245, 158, 11),
                        "completed" => Color.FromRgb(22, 163, 74),
                        "skipped" or "cancelled" => Color.FromRgb(148, 163, 184),
                        _ => Color.FromRgb(37, 99, 235)
                    }),
                IsActive = active,
                IsCompleted = item.Task.Status is "completed" or "auto_completed",
                CanComplete = canComplete
            });
            previousMonth = due.Month;
        }

        TaskYearList.ItemsSource = items;
        TaskYearTitleText.Text = UiLocalization.Choose($"Tasks {year}", $"Aufgaben {year}");
        var open = items.Count(item => item.IsActive);
        TaskYearSummaryText.Text = UiLocalization.Choose(
            $"{items.Count} tasks in this calendar year · {open} active",
            $"{items.Count} Aufgaben in diesem Kalenderjahr · {open} aktiv");
        TaskYearEffortText.Text = UiLocalization.Choose(
            $"{FormatDuration(items.Where(item => item.IsActive).Sum(item => item.Task.EstimatedMinutes))} open effort",
            $"{FormatDuration(items.Where(item => item.IsActive).Sum(item => item.Task.EstimatedMinutes))} offener Aufwand");
        TaskYearEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateYearTaskSelection();
    }

    private static int PriorityRank(string priority) => priority switch
    {
        "critical" => 4, "high" => 3, "normal" => 2, "low" => 1, _ => 0
    };

    private void TaskYearItem_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is not ListBoxItem container || container.DataContext is not YearTaskItem item)
            return;
        if (eventArgs.OriginalSource is DependencyObject source &&
            FindVisualParent<CheckBox>(source) is not null)
            return;
        item.IsSelected = !item.IsSelected;
        container.IsSelected = item.IsSelected;
        eventArgs.Handled = true;
        UpdateYearTaskSelection();
    }

    private static T? FindVisualParent<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void TaskYearList_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs) =>
        UpdateYearTaskSelection();

    private async void TaskCompletionCheckBox_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not CheckBox { DataContext: YearTaskItem item } checkBox ||
            _backend is null || !item.CanComplete)
            return;
        checkBox.IsChecked = item.IsCompleted;
        await RunModuleActionAsync(async () =>
        {
            if (!await CompleteTaskAsync(item.Record.Id)) return;
            await LoadCurrentPageCoreAsync();
            ModuleBusyText.Text = UiLocalization.Choose("Task completed.", "Aufgabe erledigt.");
        });
    }

    private async Task<bool> CompleteTaskAsync(string taskId)
    {
        if (_backend is null) return false;
        var record = await _backend.GetTaskAsync(taskId);
        var task = TaskRecordModel.FromJson(record.Data);
        if (task.AvailableActions.Contains("schedule_dunning"))
        {
            var dialog = new DunningScheduleDialog { Owner = this };
            if (dialog.ShowDialog() != true) return false;
            await _backend.ScheduleDunningAsync(
                record.Id, record.Revision, dialog.DueDate.ToString("yyyy-MM-dd"));
            return true;
        }
        if (task.AvailableActions.Contains("confirm_settled"))
        {
            var confirmed = MessageBox.Show(this, UiLocalization.Choose(
                    "All linked invoices must be paid before this package is completed. Confirm settlement?",
                    "Alle zugehörigen Rechnungen müssen bezahlt sein. Zahlung jetzt bestätigen?"),
                "StructuralOffice", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirmed != MessageBoxResult.Yes) return false;
            await _backend.ConfirmSettledAsync(record.Id, record.Revision);
            return true;
        }
        await _backend.UpdateTaskAsync(
            record.Id, record.Revision, new JsonObject { ["status"] = "completed" });
        return true;
    }

    private void UpdateYearTaskSelection()
    {
        if (TaskYearList is null) return;
        var selected = TaskYearList.Items.OfType<YearTaskItem>().Where(item => item.IsSelected).ToList();
        var active = selected.Count(item => item.IsActive);
        TaskSelectionText.Text = selected.Count == 0
            ? UiLocalization.Choose("No selection", "Keine Auswahl")
            : UiLocalization.Choose($"{selected.Count} selected", $"{selected.Count} ausgewählt");
        TaskEditButton.IsEnabled = selected.Count == 1;
        TaskBulkDeleteButton.IsEnabled = active > 0;
    }

    private async void TaskEditButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null) return;
        var selected = TaskYearList.Items.OfType<YearTaskItem>()
            .SingleOrDefault(item => item.IsSelected);
        if (selected is null) return;
        await RunModuleActionAsync(async () =>
        {
            var record = await _backend.GetTaskAsync(selected.Record.Id);
            var original = TaskRecordModel.FromJson(record.Data);
            var dialog = new TaskEditorDialog(original) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await SaveTaskDialogAsync(record, original, dialog.ResultTask);
            await LoadCurrentPageCoreAsync();
        }, UiLocalization.Choose("Task updated.", "Aufgabe aktualisiert."));
    }

    private async Task SaveTaskDialogAsync(
        BackendRecord record, TaskRecordModel original, TaskRecordModel edited)
    {
        if (_backend is null) return;
        var changes = edited.ToUpdateJson();
        var specialAction = edited.Status == "completed" &&
                            original.Status != "completed" &&
                            original.SourceType == "accounting_due_batch" &&
                            original.AvailableActions.Any(action =>
                                action is "schedule_dunning" or "confirm_settled");
        DateTime? dunningDueDate = null;
        if (specialAction)
        {
            changes.Remove("status");
            if (original.AvailableActions.Contains("schedule_dunning"))
            {
                var dialog = new DunningScheduleDialog { Owner = this };
                if (dialog.ShowDialog() != true) return;
                dunningDueDate = dialog.DueDate;
            }
            else if (original.AvailableActions.Contains("confirm_settled"))
            {
                var confirmed = MessageBox.Show(this, UiLocalization.Choose(
                        "All linked invoices must be paid before this package is completed. Confirm settlement?",
                        "Alle zugehörigen Rechnungen müssen bezahlt sein. Zahlung jetzt bestätigen?"),
                    "StructuralOffice", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirmed != MessageBoxResult.Yes) return;
            }
        }

        var updated = await _backend.UpdateTaskAsync(record.Id, record.Revision, changes);
        if (original.SourceType != "manual")
        {
            foreach (var item in edited.Checklist.Where(item => !string.IsNullOrWhiteSpace(item.Id)))
            {
                var before = original.Checklist.FirstOrDefault(existing => existing.Id == item.Id);
                if (before is not null && before.Completed == item.Completed && before.Note == item.Note)
                    continue;
                await _backend.UpdateTaskChecklistItemAsync(record.Id, item.Id, item.Revision,
                    new JsonObject { ["completed"] = item.Completed, ["note"] = item.Note.Trim() });
            }
        }

        if (!specialAction) return;
        updated = await _backend.GetTaskAsync(record.Id);
        if (original.AvailableActions.Contains("schedule_dunning"))
        {
            await _backend.ScheduleDunningAsync(
                record.Id, updated.Revision, dunningDueDate!.Value.ToString("yyyy-MM-dd"));
        }
        else if (original.AvailableActions.Contains("confirm_settled"))
        {
            await _backend.ConfirmSettledAsync(record.Id, updated.Revision);
        }
    }

    private async void TaskBulkDeleteButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null) return;
        var selected = TaskYearList.Items.OfType<YearTaskItem>()
            .Where(item => item.IsSelected && item.IsActive).ToList();
        if (selected.Count == 0) return;
        var result = MessageBox.Show(this, UiLocalization.Choose(
                $"Remove {selected.Count} selected active tasks from the list? Their history remains available.",
                $"{selected.Count} ausgewählte aktive Aufgaben aus der Liste entfernen? Der Verlauf bleibt erhalten."),
            UiLocalization.Choose("Remove tasks", "Aufgaben löschen"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        await RunModuleActionAsync(async () =>
        {
            await _backend.CancelTasksAsync(selected.Select(item =>
                (item.Record.Id, item.Record.Revision)).ToList());
            await LoadCurrentPageCoreAsync();
        }, UiLocalization.Choose("Tasks removed.", "Aufgaben entfernt."));
    }

    private void RoutineFrequencyBox_OnSelectionChanged(
        object sender, SelectionChangedEventArgs eventArgs) => UpdateRoutineFrequencyFields();

    private void UpdateRoutineFrequencyFields()
    {
        if (RoutineWeekdayPanel is null || RoutineMonthDayPanel is null ||
            RoutineYearMonthPanel is null) return;
        var frequency = SelectedTag(RoutineFrequencyBox) ?? "monthly";
        RoutineWeekdayPanel.Visibility = frequency == "weekly"
            ? Visibility.Visible : Visibility.Collapsed;
        RoutineMonthDayPanel.Visibility = frequency is "monthly" or "yearly"
            ? Visibility.Visible : Visibility.Collapsed;
        RoutineYearMonthPanel.Visibility = frequency == "yearly"
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RoutinePriorityBox_OnSelectionChanged(
        object sender, SelectionChangedEventArgs eventArgs) => UpdateRoutinePriorityHint();

    private void UpdateRoutinePriorityHint()
    {
        if (RoutinePriorityHintText is null) return;
        var label = SelectedTag(RoutinePriorityBox) switch
        {
            "low" => UiLocalization.Choose("Low", "Niedrig"),
            "high" => UiLocalization.Choose("High", "Hoch"),
            "critical" => UiLocalization.Choose("Critical", "Kritisch"),
            _ => UiLocalization.Choose("Normal", "Normal")
        };
        RoutinePriorityHintText.Text = UiLocalization.Choose(
            $"New tasks will use priority {label}.",
            $"Neue Aufgaben erhalten die Priorität {label}.");
    }

    private async void NewTaskButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null) return;
        var dialog = new TaskEditorDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RunModuleActionAsync(async () =>
        {
            await _backend.CreateTaskAsync(dialog.ResultTask.ToCreateJson());
            await LoadCurrentPageCoreAsync();
        }, UiLocalization.Choose("Task created.", "Aufgabe angelegt."));
    }

    private void NewRecordButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        _creatingRecord = true;
        _selectedRecord = null;
        ModuleDataGrid.SelectedItem = null;
        EditorTitleText.Text = UiLocalization.Choose("New record", "Neuer Datensatz");
        EditorMetaText.Text = UiLocalization.Choose(
            "Created when saved", "Wird beim Speichern angelegt");
        SaveRecordButton.IsEnabled = true;
        ArchiveRecordButton.IsEnabled = false;
        ContactEditorPanel.IsEnabled = true;
        TopicEditorPanel.IsEnabled = true;
        RecordEditorText.IsReadOnly = false;
        ClearEditor();
        if (_currentPage is "contacts" or "topics" or "routines")
        {
            if (_currentPage == "topics")
            {
                _topicSteps.Add(new TopicStepModel { Id = "step-0", Title = "" });
            }
            if (_currentPage == "routines")
            {
                RoutineNameBox.Focus();
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
            ["estimated_minutes"] = 15, ["priority"] = "normal",
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
            if (_currentDataMode == "tasks")
            {
                var task = ReadTaskEditorModel();
                if (_newManualTask || _selectedRecord is null)
                {
                    await _backend.CreateTaskAsync(task.ToCreateJson());
                }
                else
                {
                    var taskId = _selectedRecord.Record.Id;
                    await _backend.UpdateTaskAsync(
                        taskId, _selectedRecord.Record.Revision, task.ToUpdateJson());
                    foreach (var item in task.Checklist.Where(item =>
                                 !string.IsNullOrWhiteSpace(item.Id)))
                    {
                        await _backend.UpdateTaskChecklistItemAsync(
                            taskId, item.Id, item.Revision, new JsonObject
                            {
                                ["completed"] = item.Completed,
                                ["note"] = item.Note.Trim()
                            });
                    }
                }
                _newManualTask = false;
                await LoadCurrentPageCoreAsync();
                return;
            }
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
                throw new InvalidOperationException(UiLocalization.Choose(
                    "This area is read-only.", "Dieser Bereich ist schreibgeschützt."));
            }
            if (_creatingRecord || _selectedRecord is null)
            {
                await _backend.CreateRecordAsync(_currentPage, data);
            }
            else
            {
                await _backend.UpdateRecordAsync(
                    _currentPage, _selectedRecord.Record.Id,
                    _selectedRecord.Record.Revision, data);
            }
            _creatingRecord = false;
            await LoadCurrentPageCoreAsync();
        }, UiLocalization.Choose("Record saved.", "Datensatz gespeichert."));
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
                throw new InvalidDataException(UiLocalization.Choose(
                    "Enter the duration as a whole number.",
                    "Bitte die Bearbeitungsdauer als ganze Zahl eingeben."));
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
        if (_currentPage == "routines")
        {
            if (!int.TryParse(RoutineIntervalBox.Text.Trim(), out var interval))
                throw new InvalidDataException(UiLocalization.Choose(
                    "Enter the interval as a whole number.",
                    "Bitte das Intervall als ganze Zahl eingeben."));
            if (!int.TryParse(RoutineEstimatedMinutesBox.Text.Trim(), out var estimatedMinutes))
                throw new InvalidDataException(UiLocalization.Choose(
                    "Enter the estimate as a whole number.",
                    "Bitte den geschätzten Aufwand als ganze Zahl eingeben."));
            if (RoutineStartDatePicker.SelectedDate is not DateTime routineStart)
                throw new InvalidDataException(UiLocalization.Choose(
                    "Select a start date.", "Bitte ein Startdatum auswählen."));
            var frequency = SelectedTag(RoutineFrequencyBox) ?? "monthly";
            var weekdays = ReadWeekdayChecks();
            if (frequency == "weekly" && weekdays.Count == 0)
                weekdays.Add(((int)routineStart.DayOfWeek + 6) % 7);
            var monthDays = ParseIntegers(RoutineMonthDaysBox.Text,
                UiLocalization.Choose("Month days", "Monatstage"));
            if (frequency is "monthly" or "yearly" && monthDays.Count == 0)
                monthDays.Add(routineStart.Day);
            var months = ParseIntegers(RoutineMonthsBox.Text,
                UiLocalization.Choose("Months", "Monate"));
            if (frequency == "yearly" && months.Count == 0)
                months.Add(routineStart.Month);
            return new RoutineRecordModel
            {
                Id = _selectedRecord?.Record.Id ?? string.Empty,
                Name = RoutineNameBox.Text,
                Description = RoutineDescriptionBox.Text,
                Enabled = RoutineEnabledBox.IsChecked == true,
                EstimatedMinutes = estimatedMinutes,
                Priority = SelectedTag(RoutinePriorityBox) ?? "normal",
                TopicIds = [],
                Frequency = frequency,
                Interval = interval,
                StartDate = routineStart.ToString("yyyy-MM-dd"),
                EndDate = RoutineEndDatePicker.SelectedDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                DueTime = RoutineDueTimeBox.Text,
                Timezone = RoutineTimezoneBox.Text,
                CatchUpPolicy = SelectedTag(RoutineCatchUpBox) ?? "configured_window",
                ReminderOffsets = ParseIntegers(RoutineRemindersBox.Text,
                    UiLocalization.Choose("Reminders", "Erinnerungen")),
                Weekdays = weekdays,
                MonthDays = monthDays,
                Months = months,
                Dates = ParseStrings(RoutineDatesBox.Text),
                BusinessDayRule = SelectedTag(RoutineBusinessDayBox) ?? "none",
                InvalidDayRule = SelectedTag(RoutineInvalidDayBox) ?? "skip"
            }.ToJson();
        }
        return JsonNode.Parse(RecordEditorText.Text) as JsonObject
               ?? throw new InvalidDataException(UiLocalization.Choose(
                   "The content must be a JSON object.",
                   "Der Inhalt muss ein JSON-Objekt sein."));
    }

    private void AddTopicStepButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var step = new TopicStepModel
        {
            Id = $"step-{Guid.NewGuid():N}",
            Title = UiLocalization.Choose("New checklist item", "Neuer Checklistenpunkt")
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

    private void AddTaskChecklistButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!_newManualTask) return;
        var item = new TaskChecklistItemModel
        {
            Title = UiLocalization.Choose("New checklist item", "Neuer Checklistenpunkt")
        };
        _taskChecklist.Add(item);
        TaskChecklistGrid.SelectedItem = item;
        TaskChecklistGrid.ScrollIntoView(item);
    }

    private void RemoveTaskChecklistButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_newManualTask && TaskChecklistGrid.SelectedItem is TaskChecklistItemModel item)
        {
            _taskChecklist.Remove(item);
        }
    }

    private TaskRecordModel ReadTaskEditorModel()
    {
        TaskChecklistGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        TaskChecklistGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!int.TryParse(TaskEstimatedMinutesBox.Text.Trim(), out var estimatedMinutes))
            throw new InvalidDataException(UiLocalization.Choose(
                "Enter the estimate as a whole number.",
                "Bitte den geschätzten Aufwand als ganze Zahl eingeben."));
        if (TaskDueDatePicker.SelectedDate is not DateTime dueDate ||
            !TimeSpan.TryParse(TaskDueTimeBox.Text.Trim(), out var dueTime))
            throw new InvalidDataException(UiLocalization.Choose(
                "Select a valid due date and time.",
                "Bitte ein gültiges Fälligkeitsdatum und eine gültige Uhrzeit wählen."));
        var task = new TaskRecordModel
        {
            Id = _selectedRecord?.Record.Id ?? string.Empty,
            Revision = _selectedRecord?.Record.Revision ?? 0,
            Title = TaskTitleBox.Text,
            Description = TaskDescriptionBox.Text,
            Category = TaskCategoryBox.Text,
            DueAt = dueDate.Date.Add(dueTime).ToString("yyyy-MM-ddTHH:mm"),
            Priority = SelectedTag(TaskPriorityBox) ?? "normal",
            EstimatedMinutes = estimatedMinutes,
            Status = SelectedTag(TaskEditorStatusBox) ?? "open",
            CompletionNote = TaskCompletionNoteBox.Text
        };
        foreach (var item in _taskChecklist) task.Checklist.Add(item);
        return task;
    }

    private void SetTaskCreationMode(bool creating)
    {
        _newManualTask = creating;
        TaskTitleBox.IsReadOnly = !creating;
        TaskDescriptionBox.IsReadOnly = !creating;
        TaskCategoryBox.IsReadOnly = !creating;
        AddTaskChecklistButton.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;
        RemoveTaskChecklistButton.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;
        TaskChecklistTitleColumn.IsReadOnly = !creating;
        TaskChecklistRequiredColumn.IsReadOnly = !creating;
        SetTaskStatusButton.IsEnabled = !creating && _selectedRecord is not null;
        TaskStatusBox.IsEnabled = !creating;
        DeleteTaskButton.Visibility = !creating && _selectedRecord is not null
            ? Visibility.Visible : Visibility.Collapsed;
        TaskSaveButton.IsEnabled = creating || _selectedRecord is not null;
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
            await _backend.ArchiveRecordAsync(
                _currentPage, _selectedRecord.Record.Id, _selectedRecord.Record.Revision);
            await LoadCurrentPageCoreAsync();
        }, "Datensatz archiviert.");
    }

    private async void SetTaskStatusButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null || _selectedRecord is null)
        {
            return;
        }
        var status = SelectedTag(TaskStatusBox) ?? "open";
        var task = TaskRecordModel.FromJson(_selectedRecord.Record.Data);
        if (status == "completed" && task.AvailableActions.Contains("schedule_dunning"))
        {
            var dialog = new DunningScheduleDialog { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await RunModuleActionAsync(async () =>
            {
                await _backend.ScheduleDunningAsync(
                    _selectedRecord.Record.Id, _selectedRecord.Record.Revision,
                    dialog.DueDate.ToString("yyyy-MM-dd"));
                await LoadCurrentPageCoreAsync();
            }, UiLocalization.Choose(
                "Payment reminder completed and dunning task scheduled.",
                "Zahlungserinnerung erledigt und Mahnaufgabe eingeplant."));
            return;
        }
        if (status == "completed" && task.AvailableActions.Contains("confirm_settled"))
        {
            if (MessageBox.Show(
                    UiLocalization.Choose(
                        "The CSV import shows that all linked invoices are paid. Mark this dunning task as completed?",
                        "Der CSV-Import zeigt, dass alle zugehörigen Rechnungen bezahlt sind. Mahnaufgabe wirklich erledigen?"),
                    "StructuralOffice", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            await RunModuleActionAsync(async () =>
            {
                await _backend.ConfirmSettledAsync(
                    _selectedRecord.Record.Id, _selectedRecord.Record.Revision);
                await LoadCurrentPageCoreAsync();
            }, UiLocalization.Choose("Settlement confirmed.", "Zahlung bestätigt."));
            return;
        }
        await RunModuleActionAsync(async () =>
        {
            await _backend.UpdateTaskAsync(
                _selectedRecord.Record.Id, _selectedRecord.Record.Revision,
                new JsonObject { ["status"] = status });
            await LoadCurrentPageCoreAsync();
        }, UiLocalization.Choose("Task status updated.", "Aufgabenstatus aktualisiert."));
    }

    private async void DeleteTaskButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null || _selectedRecord is null) return;
        var task = TaskRecordModel.FromJson(_selectedRecord.Record.Data);
        var accountingHint = task.SourceType == "accounting_due_batch"
            ? UiLocalization.Choose(
                " This also stops the active payment follow-up for this invoice range.",
                " Dadurch wird auch die aktive Zahlungsnachverfolgung für diesen Rechnungsbereich beendet.")
            : string.Empty;
        if (MessageBox.Show(this,
                UiLocalization.Choose(
                    $"Remove '{_selectedRecord.Title}' from the active task list?{accountingHint} The history is retained.",
                    $"'{_selectedRecord.Title}' aus der aktiven Aufgabenliste entfernen?{accountingHint} Der Verlauf bleibt erhalten."),
                UiLocalization.Choose("Delete task", "Aufgabe löschen"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunModuleActionAsync(async () =>
        {
            await _backend.UpdateTaskAsync(
                _selectedRecord.Record.Id, _selectedRecord.Record.Revision,
                new JsonObject { ["status"] = "cancelled" });
            await LoadCurrentPageCoreAsync();
        }, UiLocalization.Choose("Task removed.", "Aufgabe entfernt."));
    }

    private async void ImportInvoicesButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_backend is null)
        {
            return;
        }
        var dialog = new OpenFileDialog
        {
            Filter = UiLocalization.Choose(
                "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                "CSV-Dateien (*.csv)|*.csv|Alle Dateien (*.*)|*.*")
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        await RunModuleActionAsync(async () =>
        {
            var content = await File.ReadAllBytesAsync(dialog.FileName);
            var preview = await _backend.ImportInvoiceCsvAsync(dialog.SafeFileName, content, false);
            if (preview["records"] is not JsonArray records || records.Count == 0)
            {
                throw new InvalidDataException(UiLocalization.Choose(
                    "The CSV file contains no processable invoices. Check the column " +
                    "headers, delimiter, and content.",
                    "Die CSV-Datei enthält keine verarbeitbaren Rechnungen. " +
                    "Bitte prüfe Spaltenüberschriften, Trennzeichen und Inhalt."));
            }
            if (preview["errors"] is JsonArray errors && errors.Count > 0)
            {
                var details = errors.Take(5).Select(item =>
                {
                    if (item is not JsonObject error)
                    {
                        return item?.ToJsonString() ?? UiLocalization.Choose(
                            "Unknown CSV error", "Unbekannter CSV-Fehler");
                    }
                    var row = error["row"]?.ToString();
                    var message = error["message"]?.GetValue<string>() ?? UiLocalization.Choose(
                        "Invalid record", "Ungültiger Datensatz");
                    return string.IsNullOrWhiteSpace(row)
                        ? message
                        : UiLocalization.Choose($"Row {row}: {message}", $"Zeile {row}: {message}");
                });
                var suffix = errors.Count > 5
                    ? UiLocalization.Choose(
                        $"\n… and {errors.Count - 5} more errors",
                        $"\n… und {errors.Count - 5} weitere Fehler")
                    : string.Empty;
                throw new InvalidDataException(UiLocalization.Choose(
                    $"The CSV file contains {errors.Count} validation errors:\n\n",
                    $"Die CSV-Datei enthält {errors.Count} Validierungsfehler:\n\n") +
                    string.Join('\n', details) + suffix);
            }

            var created = preview["created"]?.GetValue<int>() ?? 0;
            var updated = preview["updated"]?.GetValue<int>() ?? 0;
            var unchanged = preview["unchanged"]?.GetValue<int>() ?? 0;
            var warnings = (preview["warnings"] as JsonArray)?.Count ?? 0;
            var decision = MessageBox.Show(
                this,
                UiLocalization.Choose(
                    $"CSV import preview for {dialog.SafeFileName}\n\n" +
                    $"Invoices: {records.Count}\n" +
                    $"New: {created}  ·  Updated: {updated}  ·  Unchanged: {unchanged}\n" +
                    $"Warnings: {warnings}\n\nApply this import now?",
                    $"CSV-Importvorschau für {dialog.SafeFileName}\n\n" +
                    $"Rechnungen: {records.Count}\n" +
                    $"Neu: {created}  ·  Aktualisiert: {updated}  ·  Unverändert: {unchanged}\n" +
                    $"Warnungen: {warnings}\n\nDiesen Import jetzt anwenden?"),
                UiLocalization.Choose("Invoice import", "Rechnungsimport"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (decision != MessageBoxResult.Yes)
            {
                ModuleBusyText.Text = UiLocalization.Choose(
                    "CSV import not applied", "CSV-Import nicht angewendet");
                return;
            }

            var result = await _backend.ImportInvoiceCsvAsync(
                dialog.SafeFileName, content, true);
            await LoadCurrentPageCoreAsync();
            if (result["already_imported"]?.GetValue<bool>() == true)
            {
                ModuleBusyText.Text = UiLocalization.Choose(
                    "CSV file was already fully imported",
                    "CSV-Datei war bereits vollständig importiert");
                return;
            }
            ModuleBusyText.Text =
                UiLocalization.Choose(
                    $"CSV import complete: {result["created"]?.GetValue<int>() ?? 0} new, " +
                    $"{result["updated"]?.GetValue<int>() ?? 0} updated",
                    $"CSV-Import abgeschlossen: {result["created"]?.GetValue<int>() ?? 0} neu, " +
                    $"{result["updated"]?.GetValue<int>() ?? 0} aktualisiert");
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
            Filter = UiLocalization.Choose(
                "Excel files (*.xlsx)|*.xlsx|All files (*.*)|*.*",
                "Excel-Dateien (*.xlsx)|*.xlsx|Alle Dateien (*.*)|*.*")
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
                throw new InvalidDataException(UiLocalization.Choose(
                    "The import preview contains no invoices.",
                    "Die Importvorschau enthält keine Rechnungen."));
            }
            var decision = MessageBox.Show(
                this,
                UiLocalization.Choose(
                    $"Excel import: {records.Count} records.\n\n" +
                    $"New: {preview["created"]}  ·  Updated: {preview["updated"]}\n\n" +
                    "Apply this import now?",
                    $"Excel-Import: {records.Count} Datensätze.\n\n" +
                    $"Neu: {preview["created"]}  ·  Aktualisiert: {preview["updated"]}\n\n" +
                    "Diesen Import jetzt anwenden?"),
                UiLocalization.Choose("Excel invoice import", "Excel-Rechnungsimport"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
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
            MessageBox.Show(this, UiLocalization.Choose(
                "Select an invoice first.", "Bitte zuerst eine Rechnung auswählen."),
                "StructuralOffice");
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

    private async void ManualUpdateButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        ManualUpdateButton.IsEnabled = false;
        var previousText = ManualUpdateButton.Content;
        ManualUpdateButton.Content = "Suche läuft …";
        ModuleBusyText.Text = "GitHub-Releases werden geprüft …";
        try
        {
            await _settingsStore.SaveLastUpdateCheckAsync(DateTimeOffset.UtcNow);
            using var updater = new UpdateService();
            var release = await updater.FindUpdateAsync();
            if (release is null)
            {
                MessageBox.Show(
                    "StructuralOffice ist bereits auf dem neuesten verfügbaren Stand.",
                    "Updateprüfung", MessageBoxButton.OK, MessageBoxImage.Information);
                ModuleBusyText.Text = "Keine neue Version gefunden";
                return;
            }

            var install = MessageBox.Show(
                $"Version {release.Version} ist verfügbar. Jetzt herunterladen und installieren?",
                "StructuralOffice-Update", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (install != MessageBoxResult.Yes)
            {
                ModuleBusyText.Text = $"Update {release.Version} verfügbar";
                return;
            }

            ManualUpdateButton.Content = "Update wird geladen …";
            await updater.InstallAsync(release);
            await UpdateLog.WriteAsync(
                $"Manual update {release.Version} verified; installer started.");
            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            await UpdateLog.WriteAsync($"Manual update check failed: {exception.Message}");
            MessageBox.Show(
                "Die Updateprüfung ist fehlgeschlagen.\n\n" + exception.Message,
                "StructuralOffice-Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            ModuleBusyText.Text = "Updateprüfung fehlgeschlagen";
        }
        finally
        {
            ManualUpdateButton.Content = previousText;
            ManualUpdateButton.IsEnabled = true;
        }
    }

    private void StartLiveUpdates()
    {
        _liveUpdatesCancellation?.Cancel();
        _liveUpdatesCancellation = new CancellationTokenSource();
        _ = RunLiveUpdatesAsync(_liveUpdatesCancellation.Token);
    }

    private async Task RunLiveUpdatesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _authenticated)
        {
            var backend = _backend;
            if (backend is null) return;
            try
            {
                await backend.SubscribeLiveAsync(HandleLiveUpdateAsync, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                await UpdateLog.WriteAsync($"Live update connection interrupted: {exception.Message}");
                await Dispatcher.InvokeAsync(() =>
                    DashboardStatusText.Text = UiLocalization.Choose(
                        "Reconnecting live updates …",
                        "Live-Verbindung wird neu aufgebaut …"));
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private Task HandleLiveUpdateAsync(JsonObject liveEvent) =>
        Dispatcher.InvokeAsync(() =>
        {
            DashboardStatusText.Text = UiLocalization.Choose(
                "Live connected", "Live verbunden");
            var collection = liveEvent["collection"]?.GetValue<string>();
            var relevant = string.Equals(collection, _currentPage, StringComparison.OrdinalIgnoreCase) ||
                           (_currentPage == "documents" && collection == "invoices") ||
                           (_currentPage == "accounting" && collection is "tasks" or "invoices" or "accounting_rules");
            if (relevant && !_moduleBusy && !_creatingRecord)
            {
                _ = LoadCurrentPageAsync();
            }
        }).Task;

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
        ModuleBusyText.Text = UiLocalization.Choose(
            "Processing backend …", "Backend wird verarbeitet …");
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
                if (_currentPage == "tasks")
                {
                    await LoadCurrentPageCoreAsync();
                    MessageBox.Show(this, UiLocalization.Choose(
                            "At least one task changed in the meantime. The current annual list was reloaded; please select the tasks again.",
                            "Mindestens eine Aufgabe wurde zwischenzeitlich geändert. Die aktuelle Jahresliste wurde neu geladen; bitte wähle die Aufgaben erneut aus."),
                        UiLocalization.Choose("Task conflict", "Aufgabenkonflikt"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    ModuleBusyText.Text = UiLocalization.Choose(
                        "Current task list loaded", "Aktuelle Aufgabenliste geladen");
                    return;
                }
                _selectedRecord = CreateDisplayRecord(exception.CurrentRecord);
                LoadRecordIntoEditor(exception.CurrentRecord);
                EditorMetaText.Text = UiLocalization.Choose(
                    $"Latest backend version · Revision {exception.CurrentRecord.Revision}",
                    $"Neueste Backendversion · Revision {exception.CurrentRecord.Revision}");
                MessageBox.Show(
                    this,
                    UiLocalization.Choose(
                        "The record was changed by another person. The latest backend " +
                        "version was loaded. Review your changes again.",
                        "Der Datensatz wurde zwischenzeitlich von einer anderen Person geändert. " +
                        "Die aktuelle Backendversion wurde geladen. Bitte prüfe deine Eingaben erneut."),
                    UiLocalization.Choose("Edit conflict", "Änderungskonflikt"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                ModuleBusyText.Text = UiLocalization.Choose(
                    "Latest version loaded", "Aktuelle Version geladen");
                return;
            }
            MessageBox.Show(this, UiLocalization.Text(exception.Message),
                "StructuralOffice Backend",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ModuleBusyText.Text = UiLocalization.Choose(
                "Action failed", "Aktion fehlgeschlagen");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, UiLocalization.Text(exception.Message), "StructuralOffice",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ModuleBusyText.Text = UiLocalization.Choose(
                "Action failed", "Aktion fehlgeschlagen");
        }
        finally
        {
            _moduleBusy = false;
            RefreshModuleButton.IsEnabled = true;
        }
    }

    private static string? SelectedTag(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static List<string> ParseStrings(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static List<int> ParseIntegers(string value, string fieldName)
    {
        var result = new List<int>();
        foreach (var item in ParseStrings(value))
        {
            if (!int.TryParse(item, out var number))
                throw new InvalidDataException(UiLocalization.Choose(
                    $"{fieldName}: '{item}' is not a whole number.",
                    $"{fieldName}: '{item}' ist keine ganze Zahl."));
            result.Add(number);
        }
        return result;
    }

    private void SetWeekdayChecks(IEnumerable<int> weekdays)
    {
        var selected = weekdays.ToHashSet();
        RoutineMondayBox.IsChecked = selected.Contains(0);
        RoutineTuesdayBox.IsChecked = selected.Contains(1);
        RoutineWednesdayBox.IsChecked = selected.Contains(2);
        RoutineThursdayBox.IsChecked = selected.Contains(3);
        RoutineFridayBox.IsChecked = selected.Contains(4);
        RoutineSaturdayBox.IsChecked = selected.Contains(5);
        RoutineSundayBox.IsChecked = selected.Contains(6);
    }

    private List<int> ReadWeekdayChecks()
    {
        var boxes = new[]
        {
            RoutineMondayBox, RoutineTuesdayBox, RoutineWednesdayBox, RoutineThursdayBox,
            RoutineFridayBox, RoutineSaturdayBox, RoutineSundayBox
        };
        return boxes.Select((box, index) => (box, index))
            .Where(item => item.box.IsChecked == true)
            .Select(item => item.index).ToList();
    }

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

    private void UpdateWorkspaceStatus(
        HomeAssistantSession session,
        IntegrationCheckResult result)
    {
        var server = session.ServerAddress.ToString().TrimEnd('/');
        SidebarServerText.Text = server;
        DashboardServerText.Text = server;
        IntegrationVersionText.Text = result.IntegrationVersion ?? UiLocalization.Choose(
            "Not available", "Nicht verfügbar");

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
            CheckState.Success => UiLocalization.Choose("System ready", "System bereit"),
            CheckState.Warning => UiLocalization.Choose(
                "Check integration", "Integration prüfen"),
            _ => UiLocalization.Choose("Check connection", "Verbindung prüfen")
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

    private sealed record TodayTaskItem(
        string Id,
        string DayLabel,
        string TimeLabel,
        string Title,
        string Meta,
        string Duration,
        Brush Accent,
        bool CanComplete);

    private sealed class YearTaskItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public required BackendRecord Record { get; init; }
        public required TaskRecordModel Task { get; init; }
        public required string MonthTitle { get; init; }
        public bool IsMonthStart { get; init; }
        public required string DayLabel { get; init; }
        public required string WeekdayAndTime { get; init; }
        public required string Title { get; init; }
        public required string Meta { get; init; }
        public required string Duration { get; init; }
        public required string Status { get; init; }
        public required Brush Accent { get; init; }
        public bool IsActive { get; init; }
        public bool IsCompleted { get; init; }
        public bool CanComplete { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
