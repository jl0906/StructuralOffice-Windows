using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StructuralOffice.Desktop.Models;
using StructuralOffice.Desktop.Services;

namespace StructuralOffice.Desktop;

public partial class TaskEditorDialog : Window
{
    private readonly bool _creating;
    private readonly TaskRecordModel _source;
    private readonly ObservableCollection<TaskChecklistItemModel> _checklist = [];

    public TaskRecordModel ResultTask { get; private set; }

    public TaskEditorDialog(TaskRecordModel? task = null)
    {
        InitializeComponent();
        _creating = task is null;
        _source = task ?? new TaskRecordModel
        {
            DueAt = DateTime.Now.AddDays(1).Date.AddHours(9).ToString("yyyy-MM-ddTHH:mm"),
            SourceType = "manual"
        };
        ResultTask = CopyTask(_source);
        foreach (var item in _source.Checklist)
        {
            _checklist.Add(CopyItem(item));
        }
        ChecklistGrid.ItemsSource = _checklist;
        PopulateFields();
    }

    private bool IsManual => _creating || _source.SourceType == "manual";

    private void PopulateFields()
    {
        DialogTitleText.Text = _creating
            ? UiLocalization.Choose("Create task", "Neue Aufgabe")
            : UiLocalization.Choose("Edit task", "Aufgabe bearbeiten");
        DialogSubtitleText.Text = _creating
            ? UiLocalization.Choose(
                "Plan a new standalone task.", "Plane eine neue eigenständige Aufgabe.")
            : UiLocalization.Choose(
                "Update details, schedule, status, and checklist.",
                "Details, Planung, Status und Checkliste bearbeiten.");
        SaveButton.Content = _creating
            ? UiLocalization.Choose("Create task", "Aufgabe anlegen")
            : UiLocalization.Choose("Save changes", "Änderungen speichern");
        TitleBox.Text = _source.Title;
        DescriptionBox.Text = _source.Description;
        CategoryBox.Text = _source.Category;
        CompletionNoteBox.Text = _source.CompletionNote;
        EstimatedMinutesBox.Text = _source.EstimatedMinutes.ToString();
        var due = DateTime.TryParse(_source.DueAt, out var parsed)
            ? parsed : DateTime.Now.AddDays(1).Date.AddHours(9);
        DueDatePicker.SelectedDate = due.Date;
        DueTimeBox.Text = due.ToString("HH:mm");
        SelectTag(PriorityBox, _source.Priority);
        SelectTag(StatusBox, _source.Status);

        TitleBox.IsReadOnly = !IsManual;
        DescriptionBox.IsReadOnly = !IsManual;
        CategoryBox.IsReadOnly = !IsManual;
        ChecklistButtons.Visibility = IsManual ? Visibility.Visible : Visibility.Collapsed;
        ChecklistTitleColumn.IsReadOnly = !IsManual;
        ChecklistRequiredColumn.IsReadOnly = !IsManual;
        GeneratedTaskNotice.Visibility = IsManual ? Visibility.Collapsed : Visibility.Visible;
        GeneratedTaskNoticeText.Text = UiLocalization.Choose(
            "This task was generated automatically. Its source text and checklist structure stay linked to the routine or invoice workflow; scheduling, priority, status, checklist progress, and notes remain editable.",
            "Diese Aufgabe wurde automatisch erzeugt. Text und Checklistenstruktur bleiben mit der Routine oder dem Rechnungsvorgang verknüpft; Planung, Priorität, Status, Fortschritt und Notizen können bearbeitet werden.");
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            ChecklistGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ChecklistGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (!int.TryParse(EstimatedMinutesBox.Text.Trim(), out var minutes))
            {
                throw new InvalidDataException(UiLocalization.Choose(
                    "Enter the estimate as a whole number.",
                    "Bitte den Aufwand als ganze Zahl eingeben."));
            }
            if (DueDatePicker.SelectedDate is not DateTime date ||
                !TimeSpan.TryParse(DueTimeBox.Text.Trim(), out var time))
            {
                throw new InvalidDataException(UiLocalization.Choose(
                    "Select a valid due date and time.",
                    "Bitte ein gültiges Fälligkeitsdatum und eine gültige Uhrzeit wählen."));
            }
            var task = CopyTask(_source);
            task.Title = TitleBox.Text;
            task.Description = DescriptionBox.Text;
            task.Category = CategoryBox.Text;
            task.CompletionNote = CompletionNoteBox.Text;
            task.DueAt = date.Date.Add(time).ToString("yyyy-MM-ddTHH:mm");
            task.EstimatedMinutes = minutes;
            task.Priority = SelectedTag(PriorityBox) ?? "normal";
            task.Status = SelectedTag(StatusBox) ?? "open";
            task.Checklist.Clear();
            foreach (var item in _checklist)
            {
                task.Checklist.Add(CopyItem(item));
            }
            _ = _creating ? task.ToCreateJson() : task.ToUpdateJson();
            ResultTask = task;
            DialogResult = true;
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            MessageBox.Show(this, exception.Message, "StructuralOffice",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddChecklistButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var item = new TaskChecklistItemModel
        {
            Title = UiLocalization.Choose("New step", "Neuer Schritt")
        };
        _checklist.Add(item);
        ChecklistGrid.SelectedItem = item;
        ChecklistGrid.ScrollIntoView(item);
    }

    private void RemoveChecklistButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ChecklistGrid.SelectedItem is TaskChecklistItemModel item)
        {
            _checklist.Remove(item);
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs eventArgs) => DialogResult = false;

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private static TaskRecordModel CopyTask(TaskRecordModel source) => new()
    {
        Id = source.Id,
        Revision = source.Revision,
        Title = source.Title,
        Description = source.Description,
        Category = source.Category,
        SourceType = source.SourceType,
        DueAt = source.DueAt,
        Status = source.Status,
        Priority = source.Priority,
        EstimatedMinutes = source.EstimatedMinutes,
        CompletionNote = source.CompletionNote,
        AvailableActions = [.. source.AvailableActions],
        SettlementConfirmationRequired = source.SettlementConfirmationRequired
    };

    private static TaskChecklistItemModel CopyItem(TaskChecklistItemModel source) => new()
    {
        Id = source.Id,
        Title = source.Title,
        Required = source.Required,
        Completed = source.Completed,
        Note = source.Note,
        Revision = source.Revision
    };

    private static string? SelectedTag(ComboBox box) =>
        (box.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static void SelectTag(ComboBox box, string value)
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
}
