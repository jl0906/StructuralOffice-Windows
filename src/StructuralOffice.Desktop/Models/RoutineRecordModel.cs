using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace StructuralOffice.Desktop.Models;

public sealed class RoutineRecordModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<string> TopicIds { get; set; } = [];
    public string Frequency { get; set; } = "monthly";
    public int Interval { get; set; } = 1;
    public string StartDate { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
    public string EndDate { get; set; } = string.Empty;
    public string DueTime { get; set; } = "09:00";
    public string Timezone { get; set; } = "Europe/Berlin";
    public string CatchUpPolicy { get; set; } = "configured_window";
    public List<int> ReminderOffsets { get; set; } = [-1, 0];
    public List<int> Weekdays { get; set; } = [];
    public List<int> MonthDays { get; set; } = [];
    public List<int> Months { get; set; } = [];
    public List<string> Dates { get; set; } = [];
    public string BusinessDayRule { get; set; } = "none";
    public string InvalidDayRule { get; set; } = "skip";

    public static RoutineRecordModel FromJson(JsonObject value)
    {
        var schedule = value["schedule"] as JsonObject ?? new JsonObject();
        return new RoutineRecordModel
        {
            Id = Text(value, "id"),
            Name = Text(value, "name"),
            Description = Text(value, "description"),
            Enabled = Boolean(value, "enabled", true),
            TopicIds = Strings(value["topic_ids"] as JsonArray),
            DueTime = Text(value, "due_time") is { Length: > 0 } due ? due : "09:00",
            Timezone = Text(value, "timezone") is { Length: > 0 } zone ? zone : "Europe/Berlin",
            EndDate = Text(value, "end_date"),
            CatchUpPolicy = Text(value, "catch_up_policy") is { Length: > 0 } catchUp
                ? catchUp : "configured_window",
            ReminderOffsets = Integers(value["reminder_offsets"] as JsonArray),
            Frequency = Text(schedule, "frequency") is { Length: > 0 } frequency
                ? frequency : "monthly",
            Interval = Integer(schedule, "interval", 1),
            StartDate = Text(schedule, "start_date") is { Length: > 0 } start
                ? start : DateTime.Today.ToString("yyyy-MM-dd"),
            Weekdays = Integers(schedule["weekdays"] as JsonArray),
            MonthDays = Integers(schedule["month_days"] as JsonArray),
            Months = Integers(schedule["months"] as JsonArray),
            Dates = Strings(schedule["dates"] as JsonArray),
            BusinessDayRule = Text(schedule, "business_day_rule") is { Length: > 0 } business
                ? business : "none",
            InvalidDayRule = Text(schedule, "invalid_day_rule") is { Length: > 0 } invalid
                ? invalid : "skip"
        };
    }

    public JsonObject ToJson()
    {
        Validate();
        var result = new JsonObject
        {
            ["name"] = Name.Trim(),
            ["description"] = Description.Trim(),
            ["enabled"] = Enabled,
            ["topic_ids"] = Array(TopicIds),
            ["due_time"] = DueTime.Trim(),
            ["timezone"] = Timezone.Trim(),
            ["end_date"] = string.IsNullOrWhiteSpace(EndDate) ? null : EndDate.Trim(),
            ["catch_up_policy"] = CatchUpPolicy,
            ["reminder_offsets"] = Array(ReminderOffsets),
            ["schedule"] = new JsonObject
            {
                ["frequency"] = Frequency,
                ["interval"] = Interval,
                ["start_date"] = StartDate.Trim(),
                ["weekdays"] = Array(Weekdays),
                ["month_days"] = Array(MonthDays),
                ["months"] = Array(Months),
                ["dates"] = Array(Dates),
                ["business_day_rule"] = BusinessDayRule,
                ["invalid_day_rule"] = InvalidDayRule
            }
        };
        if (!string.IsNullOrWhiteSpace(Id)) result["id"] = Id;
        return result;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidDataException("Bitte einen Namen für die Routine eingeben.");
        if (TopicIds.Count == 0)
            throw new InvalidDataException("Bitte mindestens ein Thema auswählen.");
        if (Frequency is not ("once" or "daily" or "weekly" or "monthly" or "yearly"))
            throw new InvalidDataException("Die Wiederholungsart ist ungültig.");
        if (Interval is < 1 or > 100)
            throw new InvalidDataException("Das Intervall muss zwischen 1 und 100 liegen.");
        if (!DateOnly.TryParseExact(StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            throw new InvalidDataException("Das Startdatum muss das Format JJJJ-MM-TT verwenden.");
        if (!string.IsNullOrWhiteSpace(EndDate) && !DateOnly.TryParseExact(
                EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidDataException("Das Enddatum muss das Format JJJJ-MM-TT verwenden.");
        if (!TimeOnly.TryParseExact(DueTime, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            throw new InvalidDataException("Die Uhrzeit muss das Format HH:MM verwenden.");
        if (Weekdays.Any(value => value is < 0 or > 6))
            throw new InvalidDataException("Wochentage müssen zwischen 0 und 6 liegen.");
        if (MonthDays.Any(value => value is < 1 or > 31))
            throw new InvalidDataException("Monatstage müssen zwischen 1 und 31 liegen.");
        if (Months.Any(value => value is < 1 or > 12))
            throw new InvalidDataException("Monate müssen zwischen 1 und 12 liegen.");
        if (ReminderOffsets.Any(value => value is < -365 or > 365))
            throw new InvalidDataException("Erinnerungen müssen zwischen -365 und 365 Tagen liegen.");
    }

    private static JsonArray Array(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static JsonArray Array(IEnumerable<int> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static string Text(JsonObject value, string name) =>
        value[name]?.GetValue<string>() ?? string.Empty;
    private static int Integer(JsonObject value, string name, int fallback) =>
        value[name]?.GetValue<int>() ?? fallback;
    private static bool Boolean(JsonObject value, string name, bool fallback) =>
        value[name]?.GetValue<bool>() ?? fallback;
    private static List<string> Strings(JsonArray? values) =>
        values?.Select(item => item?.GetValue<string>() ?? string.Empty)
            .Where(item => item.Length > 0).ToList() ?? [];
    private static List<int> Integers(JsonArray? values) =>
        values?.Select(item => item?.GetValue<int>() ?? 0).ToList() ?? [];
}

public sealed class RoutineTopicOption
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsSelected { get; set; }
}
