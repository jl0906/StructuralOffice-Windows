using System.IO;
using System.Net.Mail;
using System.Text.Json.Nodes;
using StructuralOffice.Desktop.Services;

namespace StructuralOffice.Desktop.Models;

public sealed class ContactRecordModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CustomerNumber { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    public static ContactRecordModel FromJson(JsonObject value) => new()
    {
        Id = Text(value, "id"),
        Name = Text(value, "name"),
        CustomerNumber = Text(value, "customer_number"),
        Email = Text(value, "email"),
        Phone = Text(value, "phone"),
        Address = Text(value, "address"),
        Note = Text(value, "note")
    };

    public JsonObject ToJson()
    {
        Validate();
        var result = new JsonObject
        {
            ["name"] = Name.Trim(),
            ["customer_number"] = CustomerNumber.Trim(),
            ["email"] = Email.Trim(),
            ["phone"] = Phone.Trim(),
            ["address"] = Address.Trim(),
            ["note"] = Note.Trim()
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
            throw new InvalidDataException(UiLocalization.Choose(
                "Enter a contact name.", "Bitte einen Namen für den Kontakt eingeben."));
        }
        if (!string.IsNullOrWhiteSpace(Email))
        {
            try
            {
                _ = new MailAddress(Email.Trim());
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(UiLocalization.Choose(
                    "Enter a valid email address.",
                    "Bitte eine gültige E-Mail-Adresse eingeben."), exception);
            }
        }
    }

    private static string Text(JsonObject value, string name) =>
        value[name]?.GetValue<string>() ?? string.Empty;
}
