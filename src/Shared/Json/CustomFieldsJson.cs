using System.Text.Json;

namespace Shared.Json;

/// <summary>
/// Reads a customers.custom_fields document into the flat string map the rest of the
/// application works with.
///
/// The stored document is not all strings. Numbers appear throughout it - city_id,
/// pincode_id, created_by, legacy_id and more - and booleans and nested values show up
/// too. Deserializing straight into <c>Dictionary&lt;string, string?&gt;</c> throws on the
/// first of those, and every copy of that code caught the exception and handed back an
/// empty dictionary. Readers then showed blank names and addresses, and writers wrote
/// that empty document back, erasing the customer's whole field set - KYC approvals and
/// the distributor assignment along with it.
///
/// Reading through <see cref="JsonElement"/> keeps every value whatever its JSON type.
/// This lives in one place because the broken version had been copied six times.
/// </summary>
public static class CustomFieldsJson
{
    public static Dictionary<string, string?> Read(string? json)
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return fields;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return fields;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                fields[property.Name] = ToText(property.Value);
            }
        }
        catch (JsonException)
        {
            // A document that is not valid JSON at all has nothing to offer. Returning
            // empty is safe now that writers merge onto what is stored rather than
            // replacing it with whatever this returns.
            return fields;
        }

        return fields;
    }

    private static string? ToText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        // Numbers, booleans, arrays and objects keep their JSON text, so nothing is lost
        // on the way back out when the document is written again.
        _ => value.GetRawText()
    };
}
