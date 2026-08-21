using System.Globalization;
using System.Text.Json;
using Application.DTOs.NewInvoices;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Loyalty seen from one retailer. The dealer CRM already has a scheme screen, but it
/// reports every retailer under the dealer; the SFA app opens this from a single
/// retailer's Customer Details, so every figure here is that retailer's alone.
/// </summary>
[ApiController]
[Authorize]
[Route("api")]
public sealed class FieldKonnectLoyaltyController : ControllerBase
{
    private const ulong RetailerCustomerType = 2;
    private const ulong InfluencerCustomerType = 3;
    private readonly AppDbContext _db;
    private readonly INewInvoiceRepository _invoices;

    public FieldKonnectLoyaltyController(AppDbContext db, INewInvoiceRepository invoices)
    {
        _db = db;
        _invoices = invoices;
    }

    [AcceptVerbs("GET", "POST")]
    [Route("getRetailerLoyalty")]
    public async Task<IActionResult> RetailerLoyalty([FromQuery(Name = "retailer_id")] ulong retailerId, CancellationToken ct)
    {
        var retailer = await FindRetailerAsync(retailerId, ct);
        if (retailer is null) return NotFound(new { status = "error", message = "Retailer not found." });

        var invoices = await RetailerInvoicesAsync(retailerId, null, ct);
        var bySchemeId = invoices.Where(x => x.SchemeId.HasValue).GroupBy(x => x.SchemeId!.Value).ToDictionary(x => x.Key, x => x.ToList());

        // Schemes the retailer can earn on right now, plus any it already has invoices
        // under so past and expired schemes keep their history on the list.
        var today = IndiaToday();
        var eligible = await _invoices.GetEligibleSchemeOptionsAsync(retailerId, today, ct);
        var schemeIds = eligible.Select(x => x.Id).Concat(bySchemeId.Keys).Distinct().ToArray();

        var schemes = await _db.LoyaltySchemes.AsNoTracking().Include(x => x.Slabs)
            .Where(x => schemeIds.Contains(x.Id) && x.DeletedAt == null)
            .ToListAsync(ct);

        var todayOnly = DateOnly.FromDateTime(today);
        var rows = schemes
            .Select(scheme =>
            {
                var schemeInvoices = bySchemeId.GetValueOrDefault(scheme.Id) ?? [];
                var approved = schemeInvoices.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo).ToList();
                var awaiting = schemeInvoices.Where(x => x.ApprovalStatus is not NewInvoice.StatusApprovedHo and not NewInvoice.StatusRejected).ToList();
                var status = SchemeStatus(scheme, todayOnly);

                return new
                {
                    id = scheme.Id,
                    name = scheme.SchemeName,
                    code = scheme.SchemeCode,
                    tag = string.IsNullOrWhiteSpace(scheme.SchemeTag) ? "Regular" : scheme.SchemeTag,
                    based_on = scheme.BasedOn,
                    start_date = scheme.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    end_date = scheme.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    scheme_status = status,
                    status_label = StatusLabel(status),
                    is_live = status == "live",
                    days_remaining = status == "live" ? Math.Max(0, scheme.EndDate.DayNumber - todayOnly.DayNumber) : 0,
                    invoice_count = schemeInvoices.Count,
                    invoice_amount = schemeInvoices.Sum(x => x.Amount),
                    approved_amount = approved.Sum(x => x.HoApprovedAmount ?? x.Amount),
                    points_earned = approved.Sum(x => x.SchemePoints),
                    points_expected = awaiting.Sum(x => x.ExpectedSchemePoints),
                    tier_name = schemeInvoices.Select(x => x.TierName).LastOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    slab_count = scheme.Slabs.Count(x => x.DeletedAt == null)
                };
            })
            // Live schemes first, then upcoming, then expired; newest end date leads.
            .OrderBy(x => x.scheme_status == "live" ? 0 : x.scheme_status == "upcoming" ? 1 : 2)
            .ThenByDescending(x => x.end_date)
            .ToList();

        var allApproved = invoices.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo).ToList();
        var allAwaiting = invoices.Where(x => x.ApprovalStatus is not NewInvoice.StatusApprovedHo and not NewInvoice.StatusRejected).ToList();

        return Ok(new
        {
            status = "success",
            message = "Retailer loyalty retrieved successfully.",
            retailer = new
            {
                id = retailer.Id,
                name = retailer.Name,
                shop_name = ShopName(retailer),
                code = retailer.CustomerCode,
                mobile = retailer.Mobile,
                dealer_id = DealerId(retailer),
                dealer_name = await DealerNameAsync(retailer, ct)
            },
            summary = new
            {
                total_schemes = rows.Count,
                live_schemes = rows.Count(x => x.is_live),
                total_invoices = invoices.Count,
                total_invoice_amount = invoices.Sum(x => x.Amount),
                points_earned = allApproved.Sum(x => x.SchemePoints),
                points_expected = allAwaiting.Sum(x => x.ExpectedSchemePoints)
            },
            data = rows
        });
    }

    [AcceptVerbs("GET", "POST")]
    [Route("getRetailerSchemeDetail")]
    public async Task<IActionResult> RetailerSchemeDetail(
        [FromQuery(Name = "retailer_id")] ulong retailerId,
        [FromQuery(Name = "scheme_id")] ulong schemeId,
        CancellationToken ct)
    {
        var retailer = await FindRetailerAsync(retailerId, ct);
        if (retailer is null) return NotFound(new { status = "error", message = "Retailer not found." });

        var scheme = await _db.LoyaltySchemes.AsNoTracking().Include(x => x.Slabs)
            .FirstOrDefaultAsync(x => x.Id == schemeId && x.DeletedAt == null, ct);
        if (scheme is null) return NotFound(new { status = "error", message = "Scheme not found." });

        var invoices = await RetailerInvoicesAsync(retailerId, schemeId, ct);
        var approved = invoices.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo).ToList();
        var awaiting = invoices.Where(x => x.ApprovalStatus is not NewInvoice.StatusApprovedHo and not NewInvoice.StatusRejected).ToList();

        var today = DateOnly.FromDateTime(IndiaToday());
        var status = SchemeStatus(scheme, today);
        var slabs = scheme.Slabs.Where(x => x.DeletedAt == null).OrderBy(x => x.ValueFrom).ThenBy(x => x.SortOrder).ToList();

        // Progress is measured on approved business, the same basis the points are.
        var achieved = approved.Sum(x => x.HoApprovedAmount ?? x.Amount);
        var currentSlab = slabs.LastOrDefault(x => achieved >= x.ValueFrom);
        var nextSlab = slabs.FirstOrDefault(x => achieved < x.ValueFrom);

        return Ok(new
        {
            status = "success",
            message = "Scheme detail retrieved successfully.",
            retailer = new
            {
                id = retailer.Id,
                name = retailer.Name,
                shop_name = ShopName(retailer),
                code = retailer.CustomerCode,
                dealer_name = await DealerNameAsync(retailer, ct)
            },
            data = new
            {
                id = scheme.Id,
                name = scheme.SchemeName,
                code = scheme.SchemeCode,
                description = scheme.SchemeDescription,
                tag = string.IsNullOrWhiteSpace(scheme.SchemeTag) ? "Regular" : scheme.SchemeTag,
                based_on = scheme.BasedOn,
                area_scope = scheme.AreaScope,
                start_date = scheme.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                end_date = scheme.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                scheme_status = status,
                status_label = StatusLabel(status),
                is_live = status == "live",
                days_remaining = status == "live" ? Math.Max(0, scheme.EndDate.DayNumber - today.DayNumber) : 0,
                summary = new
                {
                    total_invoices = invoices.Count,
                    approved_invoices = approved.Count,
                    pending_invoices = awaiting.Count,
                    rejected_invoices = invoices.Count(x => x.ApprovalStatus == NewInvoice.StatusRejected),
                    total_invoice_amount = invoices.Sum(x => x.Amount),
                    approved_invoice_amount = achieved,
                    expected_invoice_amount = awaiting.Sum(x => x.SalesApprovedAmount ?? x.SsApprovedAmount ?? x.Amount),
                    points_earned = approved.Sum(x => x.SchemePoints),
                    points_expected = awaiting.Sum(x => x.ExpectedSchemePoints)
                },
                progress = new
                {
                    achieved_amount = achieved,
                    current_tier = currentSlab?.TierName,
                    current_reward = currentSlab?.RewardValue,
                    next_tier = nextSlab?.TierName,
                    next_tier_from = nextSlab?.ValueFrom,
                    amount_to_next_tier = nextSlab is null ? 0m : Math.Max(0m, nextSlab.ValueFrom - achieved),
                    percent_to_next_tier = nextSlab is null || nextSlab.ValueFrom <= 0
                        ? 100m
                        : Math.Round(Math.Min(100m, achieved / nextSlab.ValueFrom * 100m), 2)
                },
                slabs = slabs.Select(x => new
                {
                    tier_name = x.TierName,
                    value_from = x.ValueFrom,
                    value_to = x.ValueTo,
                    reward_value = x.RewardValue,
                    is_achieved = achieved >= x.ValueFrom
                }),
                invoices = invoices
                    .OrderByDescending(x => x.InvoiceDate)
                    .ThenByDescending(x => x.Id)
                    .Select(x => new
                    {
                        id = x.Id,
                        invoice_number = x.InvoiceNumber,
                        invoice_date = x.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        amount = x.Amount,
                        approval_status = x.ApprovalStatus,
                        status_label = x.ApprovalStatusLabel,
                        points_earned = x.ApprovalStatus == NewInvoice.StatusApprovedHo ? x.SchemePoints : 0m,
                        points_expected = x.ApprovalStatus is not NewInvoice.StatusApprovedHo and not NewInvoice.StatusRejected
                            ? x.ExpectedSchemePoints
                            : 0m
                    })
                    .ToList()
            }
        });
    }

    /// <summary>Every invoice this retailer has, optionally narrowed to one scheme.
    /// An invoice split across schemes repeats by id, so it is de-duplicated here.</summary>
    private async Task<List<NewInvoiceDto>> RetailerInvoicesAsync(ulong retailerId, ulong? schemeId, CancellationToken ct)
    {
        var result = await _invoices.GetInvoicesAsync(new NewInvoiceFilterDto
        {
            SecondaryCustomerIds = [retailerId],
            SchemeId = schemeId,
            Unpaged = true
        }, null, ct);

        return result.Items.GroupBy(x => x.Id).Select(x => x.First()).ToList();
    }

    private async Task<Customer?> FindRetailerAsync(ulong retailerId, CancellationToken ct) =>
        await _db.Customers.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == retailerId
                && x.DeletedAt == null
                && (x.CustomerType == RetailerCustomerType || x.CustomerType == InfluencerCustomerType), ct);

    private async Task<string?> DealerNameAsync(Customer retailer, CancellationToken ct)
    {
        var dealerId = DealerId(retailer);
        if (!dealerId.HasValue) return null;
        return await _db.Customers.AsNoTracking().Where(x => x.Id == dealerId.Value).Select(x => x.Name).FirstOrDefaultAsync(ct);
    }

    private static ulong? DealerId(Customer retailer) =>
        FirstId(CustomField(retailer, "distributor_name"))
        ?? FirstId(CustomField(retailer, "agri_distributor"))
        ?? retailer.ParentId;

    private static string ShopName(Customer retailer) =>
        FirstNonEmpty(CustomField(retailer, "shop_name"), CustomField(retailer, "trade_name"), retailer.Name) ?? retailer.Name;

    private static string? CustomField(Customer customer, string key)
    {
        if (string.IsNullOrWhiteSpace(customer.CustomFields)) return null;
        try
        {
            using var document = JsonDocument.Parse(customer.CustomFields);
            return document.RootElement.TryGetProperty(key, out var value) ? value.ToString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ulong? FirstId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var first = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return ulong.TryParse(first, out var parsed) && parsed > 0 ? parsed : null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    private static string SchemeStatus(LoyaltyScheme scheme, DateOnly today) =>
        scheme.StartDate > today ? "upcoming" : scheme.EndDate < today ? "expired" : "live";

    private static string StatusLabel(string status) =>
        status == "upcoming" ? "Upcoming" : status == "expired" ? "Expired" : "Live";

    private static DateTime IndiaToday() => DateTime.UtcNow.AddHours(5.5).Date;
}
