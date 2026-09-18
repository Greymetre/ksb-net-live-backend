using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Filters;
using Api.Services;
using Application.DTOs.Customers;
using Application.Interfaces.Services;
using Domain.Entities;
using Infrastructure.Caching;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Json;

namespace Api.Controllers;

/// <summary>The KYC menu under Customers Management. It reads the same customers the
/// Master screen does - and through the same data scope - but reports where each one's
/// paperwork has reached rather than their details.</summary>
[ApiController]
[Authorize]
[Route("api/customer-kyc")]
public sealed class CustomerKycController : ControllerBase
{
    private const ulong DealerType = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // State code, PAN, entity number, the letter Z, a check character.
    private static readonly Regex GstinPattern = new("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$", RegexOptions.Compiled);

    private readonly ICustomerService _customerService;
    private readonly AppDbContext _db;
    private readonly CustomerKycIndex _kycIndex;
    private readonly IGstLookupService _gstLookup;

    public CustomerKycController(ICustomerService customerService, AppDbContext db, CustomerKycIndex kycIndex, IGstLookupService gstLookup)
    {
        _customerService = customerService;
        _db = db;
        _kycIndex = kycIndex;
        _gstLookup = gstLookup;
    }

    [RequirePermission("customer_kyc.view")]
    [HttpGet]
    public async Task<IActionResult> GetKycList(
        [FromQuery] CustomerKycFilterDto filter,
        [FromQuery(Name = "customer_type")] ulong? customerType,
        [FromQuery(Name = "kyc_status")] string? kycStatus,
        [FromQuery(Name = "page_size")] int? pageSize,
        [FromQuery(Name = "dealer_id")] ulong? dealerId,
        [FromQuery(Name = "invoice_active")] bool? invoiceActive,
        CancellationToken cancellationToken)
    {
        if (invoiceActive == true) filter.InvoiceActive = true;
        filter.CustomerType ??= customerType;
        filter.KycStatus ??= kycStatus;
        filter.DealerCustomerId ??= dealerId;
        if (pageSize.HasValue) filter.PageSize = pageSize.Value;
        filter.ActorUserId = CurrentUserId();
        var response = await _customerService.GetKycListAsync(filter, cancellationToken);
        return Ok(response);
    }

    /// <summary>The dealer filter's options. Kept off the permission check the way the other
    /// filter dropdowns are - the listing behind it is what carries the gate.</summary>
    [HttpGet("dealers")]
    public async Task<IActionResult> GetDealers(CancellationToken cancellationToken)
    {
        return Ok(await _customerService.GetKycDealerOptionsAsync(CurrentUserId(), cancellationToken));
    }

    /// <summary>The shop and owner name as the customer master stores them - the pair every
    /// KYC document popup shows, so a reviewer can compare them with the document.</summary>
    [RequirePermission("customer_kyc.view")]
    [HttpGet("{id}/names")]
    public async Task<IActionResult> GetNames(ulong id, CancellationToken cancellationToken)
    {
        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (customer is null) return NotFound(new { status = "error", message = "Customer not found." });
        return Ok(new { status = "success", names = NamesOf(customer, CustomFieldsJson.Read(customer.CustomFields)) });
    }

    /// <summary>Corrects the shop and owner name from a KYC popup. They are written to the same
    /// fields the customer form edits - shop_name / owner_name on a retailer, legal_name /
    /// contact_person on a dealer - and the customer's name column follows the shop name, as it
    /// does when the form is saved.</summary>
    [RequirePermission("customer.edit")]
    [HttpPut("{id}/names")]
    public async Task<IActionResult> UpdateNames(ulong id, [FromBody] KycNamesRequest request, CancellationToken cancellationToken)
    {
        var shopName = request.ShopName?.Trim();
        var ownerName = request.OwnerName?.Trim();
        if (string.IsNullOrWhiteSpace(shopName) || string.IsNullOrWhiteSpace(ownerName))
            return BadRequest(new { status = "error", message = "Shop name and owner name are both required." });
        if (shopName.Length > 200 || ownerName.Length > 200)
            return BadRequest(new { status = "error", message = "Shop name and owner name can be at most 200 characters." });

        var customer = await _db.Customers.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (customer is null) return NotFound(new { status = "error", message = "Customer not found." });

        var fields = CustomFieldsJson.Read(customer.CustomFields);
        if (customer.CustomerType == DealerType)
        {
            fields["legal_name"] = shopName;
            fields["contact_person"] = ownerName;
        }
        else
        {
            fields["shop_name"] = shopName;
            fields["owner_name"] = ownerName;
        }

        customer.Name = shopName;
        customer.CustomFields = Serialize(fields);
        customer.UpdatedBy = CurrentUserId();
        customer.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        _kycIndex.Invalidate();

        return Ok(new { status = "success", message = "Names updated successfully.", names = NamesOf(customer, fields) });
    }

    /// <summary>The GST register details saved from the last check, if any. Opening a KYC popup
    /// reads only this, so it never spends a lookup credit.</summary>
    [RequirePermission("customer_kyc.view")]
    [HttpGet("{id}/gst-lookup")]
    public async Task<IActionResult> GetSavedGst(ulong id, CancellationToken cancellationToken)
    {
        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (customer is null) return NotFound(new { status = "error", message = "Customer not found." });

        var fields = CustomFieldsJson.Read(customer.CustomFields);
        var gstin = GstinOf(fields);
        if (gstin.Length == 0)
            return Ok(new { status = "success", configured = _gstLookup.Configured, gst = (object?)null, message = "No GST number is entered for this customer." });

        var saved = string.Equals(First(fields, "gst_lookup_gstin"), gstin, StringComparison.OrdinalIgnoreCase);
        return Ok(new { status = "success", configured = _gstLookup.Configured, gst = saved ? CachedGst(fields, gstin, "saved") : null });
    }

    /// <summary>Checks the GST register now - the eye button. This is the only call that spends a
    /// credit, so it has a permission of its own. The answer is saved on the customer.</summary>
    [RequirePermission("customer_kyc.gst_lookup")]
    [HttpPost("{id}/gst-lookup")]
    public async Task<IActionResult> CheckGst(ulong id, CancellationToken cancellationToken)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (customer is null) return NotFound(new { status = "error", message = "Customer not found." });

        var fields = CustomFieldsJson.Read(customer.CustomFields);
        var gstin = GstinOf(fields);
        var saved = gstin.Length > 0 && string.Equals(First(fields, "gst_lookup_gstin"), gstin, StringComparison.OrdinalIgnoreCase);
        if (gstin.Length == 0)
            return Ok(new { status = "error", configured = _gstLookup.Configured, gst = (object?)null, message = "No GST number is entered for this customer." });
        // An invalid number is refused here, so it never costs a lookup.
        if (!GstinPattern.IsMatch(gstin))
            return Ok(new { status = "error", configured = _gstLookup.Configured, gst = (object?)null, message = $"{gstin} is not a valid GSTIN." });
        if (!_gstLookup.Configured)
            return Ok(new { status = "error", configured = false, gst = saved ? CachedGst(fields, gstin, "saved") : null, message = "GST lookup is not set up on this server." });

        var result = await _gstLookup.LookupAsync(gstin, cancellationToken);
        if (!result.Ok)
            return Ok(new { status = "error", configured = true, gst = saved ? CachedGst(fields, gstin, "saved") : null, message = result.Error });

        fields["gst_lookup_gstin"] = gstin;
        fields["gst_lookup_legal_name"] = result.LegalName;
        fields["gst_lookup_trade_name"] = result.TradeName;
        fields["gst_lookup_status"] = result.Status;
        fields["gst_lookup_constitution"] = result.Constitution;
        fields["gst_lookup_at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        customer.CustomFields = Serialize(fields);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new { status = "success", configured = true, gst = CachedGst(fields, gstin, "live") });
    }

    private static string GstinOf(IReadOnlyDictionary<string, string?> fields) =>
        (First(fields, "gst_number", "gstin_no") ?? string.Empty).Replace(" ", string.Empty).ToUpperInvariant();

    private static object NamesOf(Customer customer, IReadOnlyDictionary<string, string?> fields)
    {
        var person = string.Join(" ", new[] { customer.FirstName, customer.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        var isDealer = customer.CustomerType == DealerType;
        return new
        {
            customer_id = customer.Id,
            customer_type = customer.CustomerType,
            shop_name = isDealer
                ? First(fields, "legal_name", "shop_name", "trade_name") ?? customer.Name
                : First(fields, "shop_name", "legal_name", "trade_name") ?? customer.Name,
            owner_name = isDealer
                ? First(fields, "contact_person", "owner_name") ?? NullIfBlank(person)
                : First(fields, "owner_name", "contact_person") ?? NullIfBlank(person)
        };
    }

    private static object CachedGst(IReadOnlyDictionary<string, string?> fields, string gstin, string source) => new
    {
        gstin,
        trade_name = First(fields, "gst_lookup_trade_name"),
        legal_name = First(fields, "gst_lookup_legal_name"),
        status = First(fields, "gst_lookup_status"),
        constitution = First(fields, "gst_lookup_constitution"),
        looked_up_at = First(fields, "gst_lookup_at"),
        source
    };

    private static string? First(IReadOnlyDictionary<string, string?> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // Same writer the customer repository uses: blank values are dropped.
    private static string Serialize(Dictionary<string, string?> fields) =>
        JsonSerializer.Serialize(fields.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToDictionary(x => x.Key, x => x.Value), JsonOptions);

    private ulong? CurrentUserId()
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return ulong.TryParse(subject, out var userId) ? userId : null;
    }
}

public sealed class KycNamesRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("shop_name")] public string? ShopName { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("owner_name")] public string? OwnerName { get; set; }
}
