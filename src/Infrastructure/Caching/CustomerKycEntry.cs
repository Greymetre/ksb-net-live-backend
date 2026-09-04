using System.Globalization;

namespace Infrastructure.Caching;

/// <summary>One customer's KYC standing, already worked out. Everything the KYC screen
/// lists, counts or filters on is here, so a request never has to open the JSON again.</summary>
public sealed class CustomerKycEntry
{
    public const string StageApproved = "approved";
    public const string StageCompletePending = "complete_pending";
    public const string StagePartial = "partial";
    public const string StageNone = "none";

    public const string StatusApproved = "approved";
    public const string StatusRejected = "rejected";
    public const string StatusPending = "pending";

    /// <summary>The four documents every customer is asked for, with the field names live data
    /// carries them under. The first spelling that holds a value wins, which is how the
    /// customer screen reads them too.</summary>
    private static readonly (string Key, string Label, string[] FileFields, (string Label, string[] Keys)[] Details)[] Definitions =
    [
        ("gst", "GST", ["gst_attachment", "gst_image"],
            [("GST Number", ["gst_number", "gstin_no"])]),
        ("pan", "PAN", ["pan_attachment", "pan_image"],
            [("PAN Number", ["pan_number", "pan_no"])]),
        ("aadhar", "Aadhaar", ["aadhar_attachment", "aadhaar_attachment", "adharcard"],
            [("Aadhaar Number", ["aadhar_no", "aadhaar_no", "aadhaar_number", "aadhar_number"])]),
        ("bank", "Bank Proof", ["bank_proof", "blank_cheque", "passbook"],
            [
                ("Bank Name", ["bank_name"]),
                ("Account Number", ["bank_account_number", "account_number"]),
                ("IFSC Code", ["ifsc_code", "ifsc"]),
                ("Account Holder Name", ["account_holder_name"])
            ])
    ];

    private static readonly string[] DealerFields = ["distributor_name", "dealer_name", "agri_distributor"];

    public ulong Id { get; init; }
    public string OwnerName { get; init; } = string.Empty;
    public string FirmName { get; init; } = string.Empty;
    public string? Mobile { get; init; }
    public string? CustomerCode { get; init; }
    public ulong? CustomerType { get; init; }
    public ulong? CreatedBy { get; init; }
    public string Active { get; init; } = "Y";
    /// <summary>Everything the search box looks through, lower-cased once at build time.</summary>
    public string SearchText { get; init; } = string.Empty;
    /// <summary>The dealers this customer is assigned to, for the dealer filter.</summary>
    public IReadOnlyList<ulong> DealerIds { get; init; } = [];
    public IReadOnlyList<CustomerKycDocument> Documents { get; init; } = [];

    public int UploadedCount { get; init; }
    public int DetailsCount { get; init; }
    public int ApprovedCount { get; init; }
    public int RejectedCount { get; init; }
    public string Stage { get; init; } = StageNone;
    public DateTime? LastActionAt { get; init; }

    public static CustomerKycEntry From(
        ulong id,
        string name,
        string? firstName,
        string? lastName,
        string? mobile,
        string? customerCode,
        ulong? customerType,
        ulong? createdBy,
        string? active,
        IReadOnlyDictionary<string, string?> fields)
    {
        var documents = Definitions.Select(definition =>
        {
            var prefix = $"{definition.Key}_kyc";
            var details = definition.Details
                .Select(row => new CustomerKycDetail { Label = row.Label, Value = First(fields, row.Keys), Field = row.Keys[0] })
                .ToList();

            // A bank proof is only usable with the account it belongs to, so the IFSC counts
            // towards the details being complete the way the account number does. For the rest,
            // the one number the document exists to carry is the whole of it.
            var detailsFilled = definition.Key == "bank"
                ? details.Where(row => row.Label is "Account Number" or "IFSC Code").All(row => row.Value is not null)
                : details[0].Value is not null;

            return new CustomerKycDocument
            {
                Key = definition.Key,
                Label = definition.Label,
                Uploaded = First(fields, definition.FileFields) is not null,
                AttachmentPath = First(fields, definition.FileFields),
                DetailsFilled = detailsFilled,
                DetailSummary = details.FirstOrDefault(row => row.Value is not null)?.Value,
                Details = details,
                Status = NormalizeStatus(Value(fields, $"{prefix}_status")),
                Remark = Value(fields, $"{prefix}_remark"),
                ActionByName = Value(fields, $"{prefix}_action_by_name") ?? Value(fields, $"{prefix}_action_by"),
                ActionAt = ParseDate(Value(fields, $"{prefix}_action_at"))
            };
        }).ToList();

        var approved = documents.Count(x => x.Status == StatusApproved);
        var rejected = documents.Count(x => x.Status == StatusRejected);
        var uploaded = documents.Count(x => x.Uploaded);
        var details = documents.Count(x => x.DetailsFilled);
        // "Complete" means the customer's side of the work is finished: every document
        // attached and every matching number typed in. Until then it is partial, however
        // many pieces have arrived.
        var complete = documents.All(x => x.Uploaded && x.DetailsFilled);
        // Partial is about effort, not about a document being usable: a bank proof needs both
        // the account number and the IFSC to count as filled in, but somebody who typed only
        // one of them has still started.
        var anyInput = documents.Any(x => x.Uploaded || x.Details.Any(detail => detail.Value is not null));

        var person = string.Join(" ", new[] { firstName, lastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        var ownerName = First(fields, ["owner_name"]) ?? (!string.IsNullOrWhiteSpace(person) ? person : name);
        var firmName = First(fields, ["shop_name", "trade_name", "legal_name", "firm_name"]) ?? name;
        var resolvedMobile = First(fields, ["mobile_numbers"])?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            ?? Trim(mobile);

        return new CustomerKycEntry
        {
            Id = id,
            OwnerName = ownerName,
            FirmName = firmName,
            Mobile = resolvedMobile,
            CustomerCode = Trim(customerCode),
            CustomerType = customerType,
            CreatedBy = createdBy,
            Active = string.IsNullOrWhiteSpace(active) ? "Y" : active,
            SearchText = string.Join(' ', new[] { ownerName, firmName, name, resolvedMobile, Trim(customerCode) }
                .Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant(),
            DealerIds = ReadDealerIds(fields),
            Documents = documents,
            UploadedCount = uploaded,
            DetailsCount = details,
            ApprovedCount = approved,
            RejectedCount = rejected,
            Stage = approved == documents.Count ? StageApproved
                : complete ? StageCompletePending
                : anyInput ? StagePartial
                : StageNone,
            LastActionAt = documents.Where(x => x.ActionAt.HasValue).Max(x => x.ActionAt)
        };
    }

    private static IReadOnlyList<ulong> ReadDealerIds(IReadOnlyDictionary<string, string?> fields)
    {
        var ids = new List<ulong>();
        foreach (var key in DealerFields)
        {
            var value = Value(fields, key);
            if (value is null) continue;
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ulong.TryParse(part, out var dealerId) && dealerId > 0 && !ids.Contains(dealerId)) ids.Add(dealerId);
            }
        }

        return ids;
    }

    private static string? Value(IReadOnlyDictionary<string, string?> fields, string key) =>
        fields.TryGetValue(key, out var value) ? Trim(value) : null;

    private static string? First(IReadOnlyDictionary<string, string?> fields, string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Value(fields, key);
            if (value is not null) return value;
        }

        return null;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeStatus(string? value)
    {
        var status = value?.ToLowerInvariant();
        return status is StatusApproved or StatusRejected ? status : StatusPending;
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
}

public sealed class CustomerKycDocument
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public bool Uploaded { get; init; }
    /// <summary>Where the file is stored, as the customer record holds it. The screen turns
    /// it into a URL the same way the customer page does.</summary>
    public string? AttachmentPath { get; init; }
    public bool DetailsFilled { get; init; }
    public string? DetailSummary { get; init; }
    /// <summary>What the document is meant to carry - the number, and for a bank proof the
    /// account it belongs to - so the popup can show it without a second call.</summary>
    public IReadOnlyList<CustomerKycDetail> Details { get; init; } = [];
    public string Status { get; init; } = CustomerKycEntry.StatusPending;
    public string? Remark { get; init; }
    public string? ActionByName { get; init; }
    public DateTime? ActionAt { get; init; }
}

public sealed class CustomerKycDetail
{
    public string Label { get; init; } = string.Empty;
    public string? Value { get; init; }
    /// <summary>The custom_fields key this row is written back to. Live data spells some of
    /// these more than one way and the reader accepts every spelling, but an edit has to
    /// land on exactly one - the first, which is the spelling this system writes.</summary>
    public string Field { get; init; } = string.Empty;
}
