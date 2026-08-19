using System.Globalization;
using System.Security.Claims;
using Api.Filters;
using Application.Interfaces.Repositories;
using Application.DTOs.NewInvoices;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Dashboard summaries for the CRM. A dealer/distributor CRM user is a normal user
/// row linked to a customer, so everything here is scoped by that customer id.
/// </summary>
[ApiController]
[Authorize]
[Route("api/dashboard")]
public sealed class DashboardController : ControllerBase
{
    private const ulong RetailerCustomerType = 2;
    private readonly AppDbContext _db;
    private readonly INewInvoiceRepository _invoices;

    public DashboardController(AppDbContext db, INewInvoiceRepository invoices)
    {
        _db = db;
        _invoices = invoices;
    }

    [RequirePermission("dashboard_access")]
    [HttpGet("dealer")]
    public async Task<IActionResult> Dealer(CancellationToken ct)
    {
        var dealerCustomerId = await _db.Users.AsNoTracking()
            .Where(x => x.Id == CurrentUserId())
            .Select(x => x.CustomerId)
            .FirstOrDefaultAsync(ct);

        // Only dealer-linked users get this view; everyone else keeps the plain welcome.
        if (!dealerCustomerId.HasValue || dealerCustomerId.Value == 0)
        {
            return Ok(new { status = "success", is_dealer = false });
        }

        var dealer = await _db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == dealerCustomerId.Value, ct);
        if (dealer is null || dealer.CustomerType != 1)
        {
            return Ok(new { status = "success", is_dealer = false });
        }

        var retailers = await AssignedRetailers(dealer.Id).ToListAsync(ct);
        var retailerIds = retailers.Select(x => x.Id).ToArray();

        // Read through the invoice repository so scheme points and stage amounts match
        // the scheme detail page exactly rather than being recomputed here.
        var invoiceRows = (await _invoices.GetInvoicesAsync(new NewInvoiceFilterDto
        {
            DistributorCustomerId = dealer.Id,
            Unpaged = true
        }, null, ct)).Items;
        var invoices = invoiceRows.GroupBy(x => x.Id).Select(x => x.First()).ToList();
        var approvedInvoices = invoices.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo).ToList();
        // SS and Sales are internal stages; a dealer only ever sees these as In Process.
        var inProcessInvoices = invoices.Where(x => x.ApprovalStatus is NewInvoice.StatusApprovedSs or NewInvoice.StatusApprovedSales).ToList();
        var awaitingInvoices = invoices.Where(x => x.ApprovalStatus is not NewInvoice.StatusApprovedHo and not NewInvoice.StatusRejected).ToList();

        var orders = await _db.Orders.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.SellerId == dealer.Id)
            .Select(x => new { x.OrderDate, x.GrandTotal, x.TotalQty })
            .ToListAsync(ct);

        var today = IndiaToday();
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var activeRetailerIds = retailerIds.Length == 0
            ? []
            : await _db.Orders.AsNoTracking()
                .Where(x => x.DeletedAt == null && x.BuyerId.HasValue && retailerIds.Contains(x.BuyerId.Value))
                .Select(x => x.BuyerId!.Value)
                .Distinct()
                .ToListAsync(ct);

        // The slider shows running schemes first, then upcoming ones, and finally
        // those that ended in the last month so a dealer can still see what lapsed.
        var todayOnly = DateOnly.FromDateTime(today);
        var windowStart = todayOnly.AddMonths(-1);
        var schemeRows = await _db.LoyaltySchemes.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.Active == "Y"
                && (x.Status == "Published" || x.Status == "Live")
                && x.SchemeType == "Invoice"
                && x.EndDate >= windowStart)
            .OrderBy(x => x.EndDate)
            .Select(x => new { x.Id, x.SchemeName, x.SchemeCode, x.SchemeTag, x.AreaScope, x.StartDate, x.EndDate })
            .Take(10)
            .ToListAsync(ct);

        var liveSchemes = schemeRows
            .Select(x =>
            {
                var status = x.StartDate > todayOnly ? "upcoming" : x.EndDate < todayOnly ? "expired" : "live";
                return new
                {
                    id = x.Id,
                    name = x.SchemeName,
                    code = x.SchemeCode,
                    tag = string.IsNullOrWhiteSpace(x.SchemeTag) ? "Regular" : x.SchemeTag,
                    area_scope = x.AreaScope,
                    start_date = x.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    end_date = x.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    status,
                    status_label = status == "upcoming" ? "Upcoming" : status == "expired" ? "Expired" : "Live",
                    is_live = status == "live",
                    days_remaining = Math.Max(0, x.EndDate.DayNumber - todayOnly.DayNumber)
                };
            })
            .OrderBy(x => x.status == "live" ? 0 : x.status == "upcoming" ? 1 : 2)
            .ThenBy(x => x.days_remaining)
            .ToList();

        var pendingKyc = retailers.Count(x => !string.Equals(KycStatus(x), "approved", StringComparison.OrdinalIgnoreCase));

        return Ok(new
        {
            status = "success",
            is_dealer = true,
            dealer = new
            {
                id = dealer.Id,
                name = dealer.Name,
                code = dealer.CustomerCode,
                mobile = dealer.Mobile,
                city = CustomFieldValue(dealer, "billing_city_name") ?? CustomFieldValue(dealer, "city_name")
            },
            retailers = new
            {
                total = retailers.Count,
                active = activeRetailerIds.Count,
                pending_kyc = pendingKyc,
                added_this_month = retailers.Count(x => x.CreatedAt >= monthStart)
            },
            invoices = new
            {
                total = invoices.Count,
                // Retailers that actually submitted an invoice, not every assigned retailer.
                retailers = invoices.Select(x => x.SecondaryCustomerId).Distinct().Count(),
                pending = invoices.Count(x => x.ApprovalStatus == NewInvoice.StatusPending),
                in_process = inProcessInvoices.Count,
                approved = approvedInvoices.Count,
                rejected = invoices.Count(x => x.ApprovalStatus == NewInvoice.StatusRejected),
                total_amount = invoices.Sum(x => x.Amount),
                approved_amount = approvedInvoices.Sum(x => x.HoApprovedAmount ?? x.Amount),
                expected_amount = awaitingInvoices.Sum(x => x.SalesApprovedAmount ?? x.SsApprovedAmount ?? x.Amount),
                points_earned = approvedInvoices.Sum(x => x.SchemePoints),
                points_expected = awaitingInvoices.Sum(x => x.ExpectedSchemePoints),
                this_month = invoices.Count(x => x.InvoiceDate >= monthStart)
            },
            orders = new
            {
                total = orders.Count,
                this_month = orders.Count(x => x.OrderDate >= monthStart),
                total_value = orders.Sum(x => x.GrandTotal),
                this_month_value = orders.Where(x => x.OrderDate >= monthStart).Sum(x => x.GrandTotal),
                total_qty = orders.Sum(x => x.TotalQty)
            },
            schemes = liveSchemes
        });
    }


    /// <summary>
    /// Scheme detail for the dealer CRM page. Mirrors the mobile dealer scheme screen:
    /// points are real only after HO approval, anything still in approval is expected.
    /// </summary>
    [RequirePermission("dashboard_access")]
    [HttpGet("dealer/schemes/{id}")]
    public async Task<IActionResult> DealerSchemeDetail(ulong id, CancellationToken ct)
    {
        var dealer = await CurrentDealerAsync(ct);
        if (dealer is null) return NotFound(new { status = "error", message = "Scheme not found." });

        var scheme = await _db.LoyaltySchemes.AsNoTracking().Include(x => x.Slabs)
            .FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null && x.Active == "Y"
                && (x.Status == "Published" || x.Status == "Live") && x.SchemeType == "Invoice", ct);
        if (scheme is null) return NotFound(new { status = "error", message = "Scheme not found." });

        var invoices = (await _invoices.GetInvoicesAsync(new NewInvoiceFilterDto
        {
            DistributorCustomerId = dealer.Id,
            SchemeId = scheme.Id,
            Unpaged = true
        }, null, ct)).Items;

        var distinct = invoices.GroupBy(x => x.Id).Select(x => x.First()).ToList();
        var approved = distinct.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo).ToList();
        var awaiting = distinct.Where(x => x.ApprovalStatus is not NewInvoice.StatusApprovedHo and not NewInvoice.StatusRejected).ToList();

        var today = DateOnly.FromDateTime(IndiaToday());
        var status = scheme.StartDate > today ? "upcoming" : scheme.EndDate < today ? "expired" : "live";

        return Ok(new
        {
            status = "success",
            data = new
            {
                id = scheme.Id,
                name = scheme.SchemeName,
                code = scheme.SchemeCode,
                description = scheme.SchemeDescription,
                tag = string.IsNullOrWhiteSpace(scheme.SchemeTag) ? "Regular" : scheme.SchemeTag,
                based_on = scheme.BasedOn,
                area_scope = scheme.AreaScope,
                customer_type = scheme.CustomerType,
                start_date = scheme.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                end_date = scheme.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                scheme_status = status,
                status_label = status == "upcoming" ? "Upcoming" : status == "expired" ? "Expired" : "Live",
                is_live = status == "live",
                days_remaining = status == "live" ? Math.Max(0, scheme.EndDate.DayNumber - today.DayNumber) : 0,
                summary = new
                {
                    scheme_retailers = distinct.Select(x => x.SecondaryCustomerId).Distinct().Count(),
                    total_invoices = distinct.Count,
                    approved_invoices = approved.Count,
                    pending_invoices = awaiting.Count,
                    rejected_invoices = distinct.Count(x => x.ApprovalStatus == NewInvoice.StatusRejected),
                    total_invoice_amount = distinct.Sum(x => x.Amount),
                    approved_invoice_amount = approved.Sum(x => x.HoApprovedAmount ?? x.Amount),
                    expected_invoice_amount = awaiting.Sum(x => x.SalesApprovedAmount ?? x.SsApprovedAmount ?? x.Amount),
                    points_earned = approved.Sum(x => x.SchemePoints),
                    points_expected = awaiting.Sum(x => x.ExpectedSchemePoints)
                },
                slabs = scheme.Slabs.Where(x => x.DeletedAt == null).OrderBy(x => x.ValueFrom).ThenBy(x => x.SortOrder)
                    .Select(x => new { tier_name = x.TierName, value_from = x.ValueFrom, value_to = x.ValueTo, reward_value = x.RewardValue }),
                retailers = distinct.GroupBy(x => x.SecondaryCustomerId)
                    .Select(group => new
                    {
                        retailer_id = group.Key,
                        retailer_name = group.First().CustomerName,
                        shop_name = group.First().ShopName,
                        invoice_count = group.Count(),
                        invoice_amount = group.Sum(x => x.Amount),
                        points_earned = group.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo).Sum(x => x.SchemePoints),
                        points_expected = group.Where(x => x.ApprovalStatus is not NewInvoice.StatusApprovedHo and not NewInvoice.StatusRejected).Sum(x => x.ExpectedSchemePoints)
                    })
                    .OrderByDescending(x => x.invoice_amount)
                    .ToList()
            }
        });
    }

    /// <summary>The customer row behind the signed-in dealer CRM user, if any.</summary>
    private async Task<Customer?> CurrentDealerAsync(CancellationToken ct)
    {
        var customerId = await _db.Users.AsNoTracking()
            .Where(x => x.Id == CurrentUserId())
            .Select(x => x.CustomerId)
            .FirstOrDefaultAsync(ct);
        if (!customerId.HasValue || customerId.Value == 0) return null;

        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == customerId.Value, ct);
        return customer?.CustomerType == 1 ? customer : null;
    }

    /// <summary>
    /// Retailers whose legacy custom_fields assignment points at this dealer. Mirrors
    /// the mobile dealer APIs so both surfaces count the same set.
    /// </summary>
    private IQueryable<Customer> AssignedRetailers(ulong dealerId)
    {
        var value = dealerId.ToString(CultureInfo.InvariantCulture);
        return _db.Customers
            .FromSqlInterpolated($@"
                SELECT *
                FROM customers
                WHERE active = 'Y'
                  AND customertype = {RetailerCustomerType}
                  AND ISJSON(custom_fields) = 1
                  AND (
                       JSON_VALUE(custom_fields, '$.distributor_name') = {value}
                    OR JSON_VALUE(custom_fields, '$.agri_distributor') = {value}
                  )")
            .AsNoTracking();
    }

    private static string KycStatus(Customer customer)
    {
        var keys = new[] { "gst_kyc_status", "pan_kyc_status", "aadhar_kyc_status", "bank_kyc_status" };
        var statuses = keys.Select(key => CustomFieldValue(customer, key)).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (statuses.Length == 0) return "pending";
        return statuses.All(x => string.Equals(x, "approved", StringComparison.OrdinalIgnoreCase)) ? "approved" : "pending";
    }

    private static string? CustomFieldValue(Customer customer, string key) =>
        string.IsNullOrWhiteSpace(customer.CustomFields) ? null : ReadJson(customer.CustomFields, key);

    private static string? ReadJson(string json, string key)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(key, out var value)) return null;
            var text = value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : value.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static DateTime IndiaToday() => DateTime.UtcNow.AddHours(5).AddMinutes(30).Date;

    private ulong CurrentUserId() =>
        ulong.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
}
