using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;

namespace StructuralOffice.Desktop.Models;

public sealed class TopicRecordModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Priority { get; set; } = "normal";
    public int EstimatedMinutes { get; set; }
    public string Instructions { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public ObservableCollection<TopicStepModel> Steps { get; } = [];

    public static TopicRecordModel FromJson(JsonObject value)
    {
        var model = new TopicRecordModel
        {
            Id = Text(value, "id"),
            Name = Text(value, "name"),
            Description = Text(value, "description"),
            Category = Text(value, "category"),
            Priority = Text(value, "priority") is { Length: > 0 } priority ? priority : "normal",
            EstimatedMinutes = Integer(value, "estimated_minutes"),
            Instructions = Text(value, "instructions"),
            Enabled = Boolean(value, "enabled", true)
        };
        if (value["steps"] is JsonArray steps)
        {
            foreach (var item in steps.OfType<JsonObject>())
            {
                model.Steps.Add(TopicStepModel.FromJson(item));
            }
        }
        else if (value["checklist"] is JsonArray checklist)
        {
            foreach (var item in checklist)
            {
                model.Steps.Add(new TopicStepModel
                {
                    Id = $"step-{model.Steps.Count}",
                    Title = item?.GetValue<string>() ?? string.Empty
                });
            }
        }
        return model;
    }

    public JsonObject ToJson()
    {
        Validate();
        var steps = new JsonArray();
        foreach (var step in Steps)
        {
            steps.Add(step.ToJson());
        }
        var result = new JsonObject
        {
            ["name"] = Name.Trim(),
            ["description"] = Description.Trim(),
            ["category"] = Category.Trim(),
            ["priority"] = Priority,
            ["estimated_minutes"] = EstimatedMinutes,
            ["instructions"] = Instructions.Trim(),
            ["enabled"] = Enabled,
            ["steps"] = steps
        };
        if (!string.IsNullOrWhiteSpace(Id))
        {
            result["id"] = Id;
        }
        return result;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidDataException("Bitte einen Namen für das Thema eingeben.");
        }
        if (Priority is not ("low" or "normal" or "high" or "critical"))
        {
            throw new InvalidDataException("Die ausgewählte Priorität ist ungültig.");
        }
        if (EstimatedMinutes is < 0 or > 100_000)
        {
            throw new InvalidDataException("Die Bearbeitungsdauer muss zwischen 0 und 100.000 Minuten liegen.");
        }
        if (Steps.Any(step => string.IsNullOrWhiteSpace(step.Title)))
        {
            throw new InvalidDataException("Jeder Checklistenpunkt benötigt einen Titel.");
        }
        if (Steps.Any(step => step.EstimatedMinutes is < 0 or > 100_000))
        {
            throw new InvalidDataException(
                "Die Dauer eines Checklistenpunkts muss zwischen 0 und 100.000 Minuten liegen.");
        }
        var ids = Steps.Select(step => step.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count != ids.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidDataException("Checklisten-IDs müssen eindeutig sein.");
        }
    }

    private static string Text(JsonObject value, string name) =>
        value[name]?.GetValue<string>() ?? string.Empty;

    private static int Integer(JsonObject value, string name) =>
        value[name]?.GetValue<int>() ?? 0;

    private static bool Boolean(JsonObject value, string name, bool fallback) =>
        value[name]?.GetValue<bool>() ?? fallback;
}

public sealed class TopicStepModel
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
    public int EstimatedMinutes { get; set; }
    public bool Enabled { get; set; } = true;

    public static TopicStepModel FromJson(JsonObject value) => new()
    {
        Id = value["id"]?.GetValue<string>() ?? string.Empty,
        Title = value["title"]?.GetValue<string>() ?? string.Empty,
        Required = value["required"]?.GetValue<bool>() ?? true,
        EstimatedMinutes = value["estimated_minutes"]?.GetValue<int>() ?? 0,
        Enabled = value["enabled"]?.GetValue<bool>() ?? true
    };

    public JsonObject ToJson() => new()
    {
        ["id"] = string.IsNullOrWhiteSpace(Id) ? $"step-{Guid.NewGuid():N}" : Id,
        ["title"] = Title.Trim(),
        ["required"] = Required,
        ["estimated_minutes"] = EstimatedMinutes,
        ["enabled"] = Enabled
    };
}
