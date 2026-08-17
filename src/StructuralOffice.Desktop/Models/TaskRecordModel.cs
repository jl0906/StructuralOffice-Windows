using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using StructuralOffice.Desktop.Services;

namespace StructuralOffice.Desktop.Models;

public sealed class TaskRecordModel
{
    public string Id { get; set; } = string.Empty;
    public int Revision { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string SourceType { get; set; } = "manual";
    public string DueAt { get; set; } = DateTime.Now.AddDays(1).ToString("yyyy-MM-ddTHH:mm");
    public string Status { get; set; } = "open";
    public string Priority { get; set; } = "normal";
    public string CompletionNote { get; set; } = string.Empty;
    public ObservableCollection<TaskChecklistItemModel> Checklist { get; } = [];

    public static TaskRecordModel FromJson(JsonObject value)
    {
        var snapshot = value["snapshot"] as JsonObject ?? new JsonObject();
        var model = new TaskRecordModel
        {
            Id = Text(value, "id"),
            Revision = value["revision"]?.GetValue<int>() ?? 0,
            Title = Text(snapshot, "topic_name") is { Length: > 0 } title
                ? title : Text(value, "title"),
            Description = Text(snapshot, "description"),
            Category = Text(snapshot, "category"),
            SourceType = Text(value, "source_type") is { Length: > 0 } source ? source : "manual",
            DueAt = NormalizeDueAt(Text(value, "due_at")),
            Status = Text(value, "status") is { Length: > 0 } status ? status : "open",
            Priority = Text(value, "priority") is { Length: > 0 } priority ? priority : "normal",
            CompletionNote = Text(value, "completion_note")
        };
        if (Text(value, "source_type") == "accounting_due_batch" &&
            Text(snapshot, "invoice_range") is { Length: > 0 } invoiceRange)
        {
            model.Title = UiLocalization.Choose(
                $"Overdue invoices {invoiceRange}",
                $"Überfällige Rechnungen {invoiceRange}");
            var openCount = snapshot["invoice_count_open"]?.ToString() ?? "0";
            model.Description = UiLocalization.Choose(
                $"{openCount} overdue open invoices.",
                $"{openCount} offene überfällige Rechnungen.");
            model.Category = UiLocalization.Choose("Accounting", "Buchhaltung");
        }
        if (value["checklist"] is JsonArray checklist)
        {
            foreach (var item in checklist.OfType<JsonObject>())
                model.Checklist.Add(TaskChecklistItemModel.FromJson(item));
        }
        return model;
    }

    public JsonObject ToCreateJson()
    {
        Validate();
        var checklist = new JsonArray();
        foreach (var item in Checklist)
        {
            if (!string.IsNullOrWhiteSpace(item.Title))
            {
                checklist.Add(new JsonObject
                {
                    ["title"] = item.Title.Trim(),
                    ["required"] = item.Required
                });
            }
        }
        return new JsonObject
        {
            ["title"] = Title.Trim(),
            ["description"] = Description.Trim(),
            ["category"] = Category.Trim(),
            ["due_at"] = ToBackendDueAt(DueAt),
            ["priority"] = Priority,
            ["checklist"] = checklist
        };
    }

    public JsonObject ToUpdateJson()
    {
        Validate();
        return new JsonObject
        {
            ["due_at"] = ToBackendDueAt(DueAt),
            ["status"] = Status,
            ["priority"] = Priority,
            ["completion_note"] = CompletionNote.Trim()
        };
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Title))
            throw new InvalidDataException(UiLocalization.Choose(
                "Enter a task title.", "Bitte einen Aufgabentitel eingeben."));
        if (!DateTime.TryParse(DueAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out _))
            throw new InvalidDataException(UiLocalization.Choose(
                "The due date is invalid.", "Die Fälligkeit ist ungültig."));
        if (Priority is not ("low" or "normal" or "high" or "critical"))
            throw new InvalidDataException(UiLocalization.Choose(
                "The task priority is invalid.", "Die Aufgabenpriorität ist ungültig."));
        if (Status is not ("open" or "in_progress" or "completed" or "skipped" or "cancelled"))
            throw new InvalidDataException(UiLocalization.Choose(
                "The task status is invalid.", "Der Aufgabenstatus ist ungültig."));
    }

    private static string NormalizeDueAt(string value) =>
        DateTime.TryParse(value, out var parsed) ? parsed.ToString("yyyy-MM-ddTHH:mm") : value;

    private static string ToBackendDueAt(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture).ToString("yyyy-MM-ddTHH:mm:ss");

    private static string Text(JsonObject value, string name) =>
        value[name]?.GetValue<string>() ?? string.Empty;
}

public sealed class TaskChecklistItemModel
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
    public bool Completed { get; set; }
    public string Note { get; set; } = string.Empty;
    public int Revision { get; set; }

    public static TaskChecklistItemModel FromJson(JsonObject value) => new()
    {
        Id = value["id"]?.GetValue<string>() ?? string.Empty,
        Title = value["title"]?.GetValue<string>() ?? string.Empty,
        Required = Boolean(value["required"], true),
        Completed = Boolean(value["completed"], false),
        Note = value["note"]?.GetValue<string>() ?? string.Empty,
        Revision = value["revision"]?.GetValue<int>() ?? 0
    };

    private static bool Boolean(JsonNode? value, bool fallback)
    {
        if (value is not JsonValue jsonValue)
        {
            return fallback;
        }
        if (jsonValue.TryGetValue<bool>(out var boolean))
        {
            return boolean;
        }
        return jsonValue.TryGetValue<int>(out var integer) ? integer != 0 : fallback;
    }
}
