using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace StructuralOffice.Desktop.Services;

public static class UiLocalization
{
    private static readonly IReadOnlyDictionary<string, string> EnglishToGerman =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WORKSPACE"] = "ARBEITSBEREICH",
            ["Overview"] = "Übersicht",
            ["Contacts"] = "Kontakte",
            ["Contacts · Coming soon"] = "Kontakte · Bald verfügbar",
            ["Topics"] = "Themen",
            ["Routines"] = "Routinen",
            ["Tasks"] = "Aufgaben",
            ["Invoices"] = "Rechnungen",
            ["Documents"] = "Dokumente",
            ["Documents · Coming soon"] = "Dokumente · Bald verfügbar",
            ["Dunning"] = "Mahnwesen",
            ["Dunning · Coming soon"] = "Mahnwesen · Bald verfügbar",
            ["Settings"] = "Einstellungen",
            ["Administration"] = "Administration",
            ["Sign out"] = "Abmelden",
            ["Connected"] = "Verbunden",
            ["SYSTEM"] = "SYSTEM",
            ["Standalone  ·  later"] = "Standalone  ·  später",
            ["Structured office processes at a glance"] = "Büroprozesse strukturiert im Blick",
            ["Secure sign-in through Home Assistant"] = "Sichere Anmeldung über Home Assistant",
            ["Connect to Home Assistant"] = "Mit Home Assistant verbinden",
            ["Sign in securely through Home Assistant. Your password and two-factor code are processed exclusively by Home Assistant."] = "Melde dich sicher über Home Assistant an. Passwort und Zwei-Faktor-Code werden ausschließlich von Home Assistant verarbeitet.",
            ["Home Assistant address"] = "Home-Assistant-Adresse",
            ["For example http://homeassistant.local:8123"] = "Zum Beispiel http://homeassistant.local:8123",
            ["Stay signed in"] = "Angemeldet bleiben",
            ["The refresh token is protected in Windows Credential Manager. Your password is never stored."] = "Das Refresh-Token wird geschützt im Windows-Anmeldeinformationsspeicher abgelegt. Dein Passwort wird nie gespeichert.",
            ["Sign in with Home Assistant"] = "Mit Home Assistant anmelden",
            ["Connection status"] = "Verbindungsstatus",
            ["Welcome to StructuralOffice"] = "Willkommen bei StructuralOffice",
            ["Your central workspace for recurring office processes."] = "Deine zentrale Arbeitsoberfläche für wiederkehrende Büroprozesse.",
            ["Your StructuralOffice workspace"] = "Dein StructuralOffice-Arbeitsbereich",
            ["System status"] = "Systemstatus",
            ["System ready"] = "System bereit",
            ["Ready"] = "Bereit",
            ["CONNECTION"] = "VERBINDUNG",
            ["INTEGRATION"] = "INTEGRATION",
            ["DATA MODE"] = "DATENMODUS",
            ["UPDATES"] = "UPDATES",
            ["Online"] = "Online",
            ["StructuralOffice backend"] = "StructuralOffice Backend",
            ["Standalone prepared"] = "Standalone vorbereitet",
            ["Automatic"] = "Automatisch",
            ["Workspaces"] = "Arbeitsbereiche",
            ["Manage contacts · Coming soon"] = "Kontakte verwalten · Bald verfügbar",
            ["Plan routines"] = "Routinen planen",
            ["Manage tasks"] = "Aufgaben bearbeiten",
            ["Review invoices"] = "Rechnungen prüfen",
            ["Refresh"] = "Aktualisieren",
            ["New"] = "Neu",
            ["Save"] = "Speichern",
            ["Archive"] = "Archivieren",
            ["Search records"] = "Datensätze durchsuchen",
            ["Archived"] = "Archivierte",
            ["All statuses"] = "Alle Status",
            ["Open"] = "Offen",
            ["In progress"] = "In Bearbeitung",
            ["Completed"] = "Erledigt",
            ["Skipped"] = "Übersprungen",
            ["Cancelled"] = "Abgebrochen",
            ["All sources"] = "Alle Quellen",
            ["Manual"] = "Manuell",
            ["Manual task"] = "Manuelle Aufgabe",
            ["Set task status"] = "Aufgabenstatus setzen",
            ["Skip"] = "Überspringen",
            ["Import CSV"] = "CSV importieren",
            ["Import Excel"] = "Excel importieren",
            ["Export Excel"] = "Excel exportieren",
            ["Export CSV"] = "CSV exportieren",
            ["Excel template"] = "Excel-Vorlage",
            ["Payment reminder"] = "Zahlungserinnerung",
            ["Dunning level 1"] = "Mahnung Stufe 1",
            ["Dunning level 2"] = "Mahnung Stufe 2",
            ["Dunning level 3"] = "Mahnung Stufe 3",
            ["Generate document"] = "Dokument erzeugen",
            ["Included invoices"] = "Enthaltene Rechnungen",
            ["Dunning rules"] = "Mahnregeln",
            ["User roles"] = "Benutzerrollen",
            ["Backups"] = "Backups",
            ["Audit log"] = "Änderungsprotokoll",
            ["Events"] = "Ereignisse",
            ["Set role"] = "Rolle setzen",
            ["Create backup"] = "Backup erstellen",
            ["Download backup"] = "Backup laden",
            ["Restore"] = "Wiederherstellen",
            ["Delete backup"] = "Backup löschen",
            ["Test notification"] = "Testbenachrichtigung",
            ["Check for updates"] = "Nach Updates suchen",
            ["Language"] = "Sprache",
            ["English"] = "Englisch",
            ["German"] = "Deutsch",
            ["Developer mode"] = "Entwicklermodus",
            ["Editing protection active"] = "Bearbeitungsschutz aktiv",
            ["Technical details"] = "Technische Details",
            ["Title"] = "Titel",
            ["Title *"] = "Titel *",
            ["Status"] = "Status",
            ["Details"] = "Details",
            ["Record"] = "Datensatz",
            ["Description"] = "Beschreibung",
            ["Category"] = "Kategorie",
            ["Priority"] = "Priorität",
            ["Due date"] = "Fälligkeit",
            ["Completion note"] = "Abschlussnotiz",
            ["Checklist"] = "Checkliste",
            ["Required"] = "Pflicht",
            ["Item"] = "Punkt",
            ["Note"] = "Notiz",
            ["Add item"] = "Punkt hinzufügen",
            ["Remove"] = "Entfernen",
            ["Low"] = "Niedrig",
            ["Normal"] = "Normal",
            ["High"] = "Hoch",
            ["Critical"] = "Kritisch",
            ["Name *"] = "Name *",
            ["Customer number"] = "Kundennummer",
            ["Phone"] = "Telefon",
            ["Address"] = "Adresse",
            ["Instructions"] = "Arbeitsanleitung",
            ["Minutes"] = "Minuten",
            ["Active"] = "Aktiv",
            ["Topic active"] = "Thema aktiv",
            ["Routine active"] = "Routine aktiv",
            ["Recurrence"] = "Wiederholung",
            ["Start date"] = "Startdatum",
            ["End date"] = "Enddatum",
            ["Due at"] = "Fällig um",
            ["Time zone"] = "Zeitzone",
            ["Assigned topics *"] = "Zugeordnete Themen *",
            ["Once"] = "Einmalig",
            ["Daily"] = "Täglich",
            ["Weekly"] = "Wöchentlich",
            ["Monthly"] = "Monatlich",
            ["Yearly"] = "Jährlich",
            ["Interval"] = "Intervall",
            ["Weekdays (for weekly)"] = "Wochentage (für wöchentlich)",
            ["Month days (for example 1,15)"] = "Monatstage (z. B. 1,15)",
            ["Months (1–12)"] = "Monate (1–12)",
            ["Explicit dates (YYYY-MM-DD, comma-separated)"] = "Explizite Termine (JJJJ-MM-TT, kommagetrennt)",
            ["Reminder days (-1,0)"] = "Erinnerungen in Tagen (-1,0)",
            ["Weekend rule"] = "Wochenendregel",
            ["Previous business day"] = "Vorheriger Werktag",
            ["Next business day"] = "Nächster Werktag",
            ["Last day of month"] = "Letzter Monatstag",
            ["Invalid month day"] = "Ungültiger Monatstag",
            ["None"] = "Keine",
            ["min."] = "Min.",
            ["Catch-up rule"] = "Nachholregel",
            ["Skip missed"] = "Verpasste überspringen",
            ["Only latest occurrence"] = "Nur letzter Termin",
            ["Configured window"] = "Konfiguriertes Fenster"
        };

    private static readonly IReadOnlyDictionary<string, string> GermanToEnglish =
        EnglishToGerman.ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);

    public static string LanguageCode { get; private set; } = "en";
    public static bool IsGerman => LanguageCode == "de";

    public static void SetLanguage(string? languageCode)
    {
        LanguageCode = string.Equals(languageCode, "de", StringComparison.OrdinalIgnoreCase)
            ? "de" : "en";
        var culture = CultureInfo.GetCultureInfo(IsGerman ? "de-DE" : "en-US");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static string Text(string value)
    {
        if (IsGerman)
        {
            return EnglishToGerman.TryGetValue(value, out var german) ? german : value;
        }
        return GermanToEnglish.TryGetValue(value, out var english) ? english : value;
    }

    public static string Choose(string english, string german) => IsGerman ? german : english;

    public static void Apply(DependencyObject root)
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        ApplyCore(root, visited);
    }

    private static void ApplyCore(DependencyObject value, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(value)) return;
        if (value is TextBlock textBlock &&
            !BindingOperations.IsDataBound(textBlock, TextBlock.TextProperty))
        {
            textBlock.Text = Text(textBlock.Text);
        }
        if (value is ContentControl contentControl && contentControl.Content is string content &&
            !BindingOperations.IsDataBound(contentControl, ContentControl.ContentProperty))
        {
            contentControl.Content = Text(content);
        }
        if (value is HeaderedContentControl headered && headered.Header is string header)
        {
            headered.Header = Text(header);
        }
        if (value is FrameworkElement element && element.ToolTip is string toolTip)
        {
            element.ToolTip = Text(toolTip);
        }
        if (value is DataGrid grid)
        {
            foreach (var column in grid.Columns.Where(column => column.Header is string))
                column.Header = Text((string)column.Header);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(value).OfType<DependencyObject>())
            ApplyCore(child, visited);
        if (value is not Visual && value is not Visual3D) return;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(value); index++)
            ApplyCore(VisualTreeHelper.GetChild(value, index), visited);
    }
}
