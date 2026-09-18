namespace Api.Services;

/// <summary>Bank account type, the same three everywhere - CRM, SFA and VRiDDHi: SAVINGS,
/// CURRENT and OD, stored upper case in custom_fields.bank_account_type the way the field app
/// has always saved it. VRiDDHi builds released before the dropdown send whatever was typed;
/// this reads the usual spellings as one of the three and keeps anything else as typed.</summary>
public static class BankAccountTypes
{
    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        var key = new string(text.ToUpperInvariant().Where(char.IsLetter).ToArray());
        if (key.Length == 0) return string.Empty;
        if (key.StartsWith("SAVING", StringComparison.Ordinal)) return "SAVINGS";
        if (key.StartsWith("CURRENT", StringComparison.Ordinal)) return "CURRENT";
        if (key == "OD" || key.StartsWith("OVERDRAFT", StringComparison.Ordinal)) return "OD";
        return text;
    }
}
