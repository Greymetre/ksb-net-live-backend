using System.Globalization;
using System.Security.Claims;
using Api.Filters;
using Application.DTOs.NewInvoices;
using Application.Interfaces.Repositories;
using Domain.Constants;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Team dashboard for internal CRM users, mirroring the SFA mobile home screen.
/// Every figure is scoped through the same reporting visibility the mobile app
/// uses, so an admin role sees every internal user, a branch manager sees the
/// users of their branches, and everyone else sees their own reporting tree.
/// </summary>
[ApiController]
[Authorize]
[Route("api/dashboard")]
public sealed class TeamDashboardController : ControllerBase
{
    private const ulong DealerCustomerType = 1;
    private const ulong RetailerCustomerType = 2;
    private const ulong AsrDesignationId = 3;
    private const ulong DsrDesignationId = 6;

    private readonly AppDbContext _db;
    private readonly IHrRepository _hr;
    private readonly INewInvoiceRepository _invoices;

    public TeamDashboardController(AppDbContext db, IHrRepository hr, INewInvoiceRepository invoices)
    {
        _db = db;
        _hr = hr;
        _invoices = invoices;
    }

    [RequirePermission("dashboard_secondary")]
    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct);
        if (scope is null) return Ok(new { status = "success", is_team = false });

        var effectiveIds = scope.TeamIds;
        var teamLongIds = effectiveIds.Select(x => (long)x).ToArray();
        var asrIds = scope.AsrIds;
        var dsrIds = scope.DsrIds;
        var team = scope.Team;

        var now = DateTime.UtcNow.AddHours(5).AddMinutes(30);
        var today = now.Date;
        var tomorrow = today.AddDays(1);
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var yearStart = new DateTime(now.Year, 1, 1);
        var yearEnd = yearStart.AddYears(1);

        // ---------- Attendance, today ----------
        var todayAttendance = await _db.Attendances.AsNoTracking()
            .Where(x => x.UserId.HasValue && effectiveIds.Contains(x.UserId.Value)
                && x.PunchinDate >= today && x.PunchinDate < tomorrow)
            .Select(x => new AttendanceRow(x.UserId!.Value, x.PunchoutTime, x.WorkingType))
            .ToListAsync(ct);

        // ---------- Orders ----------
        var todayOrders = await Orders(effectiveIds, today, tomorrow, ct);
        var monthOrders = await Orders(effectiveIds, monthStart, monthEnd, ct);
        var yearOrders = await Orders(effectiveIds, yearStart, yearEnd, ct);
        var asrMonthOrders = await Orders(asrIds, monthStart, monthEnd, ct);
        var dsrMonthOrders = await Orders(dsrIds, monthStart, monthEnd, ct);

        var trendRows = effectiveIds.Length == 0
            ? []
            : await _db.Orders.AsNoTracking()
                .Where(x => x.DeletedAt == null && x.CreatedBy.HasValue && effectiveIds.Contains(x.CreatedBy.Value)
                    && x.OrderDate >= yearStart && x.OrderDate < yearEnd)
                .GroupBy(x => x.OrderDate!.Value.Month)
                .Select(group => new { Month = group.Key, Count = group.Count(), Qty = group.Sum(x => x.TotalQty), Value = group.Sum(x => x.GrandTotal) })
                .ToListAsync(ct);

        var trend = Enumerable.Range(1, 12).Select(month =>
        {
            var row = trendRows.FirstOrDefault(x => x.Month == month);
            return new
            {
                month,
                label = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month),
                orders = row?.Count ?? 0,
                quantity = row?.Qty ?? 0L,
                value = Math.Round(row?.Value ?? 0m, 2),
                is_current = month == now.Month
            };
        }).ToList();

        // ---------- Target vs achievement ----------
        var targetRows = effectiveIds.Length == 0
            ? []
            : await _db.SalesTargetUsers.AsNoTracking()
                .Where(x => x.UserId.HasValue && effectiveIds.Contains(x.UserId.Value) && x.Type == "secondary" && x.Year == now.Year)
                .Select(x => new TargetRow(x.UserId!.Value, x.Month, x.Target, x.Achievement, x.QuantityTarget, x.QuantityAchievement))
                .ToListAsync(ct);

        var currentMonthName = now.ToString("MMM", CultureInfo.InvariantCulture);
        var ytdMonths = Enumerable.Range(1, now.Month)
            .Select(month => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var monthTargets = targetRows.Where(x => string.Equals(x.Month, currentMonthName, StringComparison.OrdinalIgnoreCase)).ToList();
        var ytdTargets = targetRows.Where(x => x.Month is not null && ytdMonths.Contains(x.Month)).ToList();

        // ---------- Customers ----------
        var customerBase = _db.Customers.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.Active == "Y" && x.CreatedBy.HasValue && effectiveIds.Contains(x.CreatedBy.Value));
        var approvedBase = customerBase.Where(x => x.CustomFields != null && EF.Functions.Like(x.CustomFields, "%\"status\":\"approved\"%"));

        var totalRetailers = await customerBase.CountAsync(x => x.CustomerType == RetailerCustomerType, ct);
        var totalDealers = await customerBase.CountAsync(x => x.CustomerType == DealerCustomerType, ct);
        var retailersThisMonth = await customerBase.CountAsync(x => x.CustomerType == RetailerCustomerType && x.CreatedAt >= monthStart && x.CreatedAt < monthEnd, ct);
        var approvedToday = await approvedBase.CountAsync(x => x.CreatedAt >= today && x.CreatedAt < tomorrow, ct);

        // A retailer counts as pending KYC until every document status on the record
        // is approved, the same rule the dealer dashboard and the SFA app apply.
        var retailerKycFields = await customerBase
            .Where(x => x.CustomerType == RetailerCustomerType)
            .Select(x => x.CustomFields)
            .ToListAsync(ct);
        var pendingKycRetailers = retailerKycFields.Count(x => !IsKycApproved(x));
        var approvedYear = await approvedBase.CountAsync(x => x.CreatedAt >= yearStart && x.CreatedAt < yearEnd, ct);

        var orderedBuyerIds = await _db.Orders.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.OrderDate >= yearStart && x.OrderDate < yearEnd && x.BuyerId.HasValue)
            .Select(x => x.BuyerId!.Value).Distinct().ToListAsync(ct);
        var buyersFromTeam = orderedBuyerIds.Count == 0 ? 0 : await customerBase.CountAsync(x => orderedBuyerIds.Contains(x.Id), ct);

        // ---------- Field activity, taken from the attendance working type ----------
        var workingToday = WorkingTypes(todayAttendance.Select(x => x.WorkingType));
        var workingMonth = WorkingTypes(await WorkingTypeRange(effectiveIds, monthStart, monthEnd, ct));
        var workingYear = WorkingTypes(await WorkingTypeRange(effectiveIds, yearStart, yearEnd, ct));

        var promotional = await _db.PromotionalActivities.AsNoTracking()
            .Where(x => x.DeletedAt == null && (teamLongIds.Contains(x.UserId) || teamLongIds.Contains(x.CreatedById))
                && x.ActivityDate >= yearStart && x.ActivityDate < yearEnd)
            .Select(x => new { x.ActivityDate, x.ActivityType, x.GiftCount, x.TotalExpense })
            .ToListAsync(ct);

        // ---------- Top SKUs ----------
        var topByQuantity = await TopProducts(effectiveIds, yearStart, yearEnd, false, ct);
        var topByValue = await TopProducts(effectiveIds, yearStart, yearEnd, true, ct);

        // ---------- Who is selling ----------
        var performerRows = effectiveIds.Length == 0
            ? []
            : await _db.Orders.AsNoTracking()
                .Where(x => x.DeletedAt == null && x.CreatedBy.HasValue && effectiveIds.Contains(x.CreatedBy.Value)
                    && x.OrderDate >= yearStart && x.OrderDate < yearEnd)
                .GroupBy(x => x.CreatedBy!.Value)
                .Select(group => new { UserId = group.Key, Orders = group.Count(), Qty = group.Sum(x => x.TotalQty), Value = group.Sum(x => x.GrandTotal) })
                .ToListAsync(ct);

        var performers = performerRows
            .OrderByDescending(x => x.Value)
            .Take(10)
            .Select(row =>
            {
                var member = team.FirstOrDefault(x => x.Id == row.UserId);
                return new
                {
                    user_id = row.UserId,
                    name = member?.Name ?? $"User {row.UserId}",
                    designation = member?.Designation ?? "",
                    orders = row.Orders,
                    quantity = row.Qty,
                    value = Math.Round(row.Value, 2)
                };
            })
            .ToList();

        return Ok(new
        {
            status = "success",
            is_team = true,
            user = UserBlock(scope),
            team = TeamBlock(scope),
            attendance = new
            {
                all = AttendanceBlock(effectiveIds, todayAttendance),
                asr = AttendanceBlock(asrIds, todayAttendance),
                dsr = AttendanceBlock(dsrIds, todayAttendance)
            },
            target = new
            {
                month_label = now.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                month = TargetBlock(monthTargets),
                ytd = TargetBlock(ytdTargets),
                asr = TargetBlock(monthTargets.Where(x => asrIds.Contains(x.UserId))),
                dsr = TargetBlock(monthTargets.Where(x => dsrIds.Contains(x.UserId)))
            },
            orders = new
            {
                today = OrderBlock(todayOrders),
                month = OrderBlock(monthOrders),
                year = OrderBlock(yearOrders),
                asr_month = OrderBlock(asrMonthOrders),
                dsr_month = OrderBlock(dsrMonthOrders),
                trend
            },
            customers = new
            {
                retailers = totalRetailers,
                dealers = totalDealers,
                retailers_this_month = retailersThisMonth,
                approved_today = approvedToday,
                approved_year = approvedYear,
                with_order_year = buyersFromTeam,
                unique_buyers_month = await UniqueBuyers(effectiveIds, monthStart, monthEnd, ct),
                unique_buyers_year = await UniqueBuyers(effectiveIds, yearStart, yearEnd, ct),
                pending_kyc = pendingKycRetailers
            },
            activities = new
            {
                today = workingToday,
                month = workingMonth,
                year = workingYear,
                promotional = new
                {
                    year = promotional.Count,
                    month = promotional.Count(x => x.ActivityDate >= monthStart && x.ActivityDate < monthEnd),
                    gifts = promotional.Sum(x => x.GiftCount),
                    expense = Math.Round(promotional.Sum(x => x.TotalExpense), 2),
                    types = promotional.GroupBy(x => string.IsNullOrWhiteSpace(x.ActivityType) ? "Other" : x.ActivityType)
                        .OrderByDescending(group => group.Count())
                        .Take(5)
                        .Select(group => new { name = group.Key, count = group.Count() })
                        .ToList()
                }
            },
            top_products = new
            {
                quantity = topByQuantity,
                value = topByValue
            },
            performers
        });
    }


    /// <summary>
    /// Loyalty dashboard: the invoice programme seen from the office side, so the
    /// full approval chain is shown. SS and Sales collapse into "In Process" only
    /// for dealer and retailer logins, never here.
    /// </summary>
    [RequirePermission("dashboard_loyalty")]
    [HttpGet("loyalty")]
    public async Task<IActionResult> Loyalty(CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct);
        if (scope is null) return Ok(new { status = "success", is_team = false });

        var now = DateTime.UtcNow.AddHours(5).AddMinutes(30);
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var yearStart = new DateTime(now.Year, 1, 1);
        var yearEnd = yearStart.AddYears(1).AddDays(-1);

        // Read through the invoice repository so scheme points match the invoice
        // listing and the dealer scheme page exactly. Bounded to the current year.
        var rows = (await _invoices.GetInvoicesAsync(new NewInvoiceFilterDto
        {
            Unpaged = true,
            FromDate = yearStart,
            ToDate = yearEnd
        }, null, ct)).Items;
        var invoices = rows.GroupBy(x => x.Id).Select(x => x.First()).ToList();

        // An admin-wide view keeps every invoice. Anything narrower keeps only the
        // invoices whose retailer belongs to a user this actor can see.
        if (scope.Scope != "all" && invoices.Count > 0)
        {
            var retailerIds = invoices.Select(x => x.SecondaryCustomerId).Distinct().ToArray();
            var owners = await _db.Customers.AsNoTracking()
                .Where(x => retailerIds.Contains(x.Id))
                .Select(x => new { x.Id, x.CreatedBy, x.ExecutiveId, x.CustomFields })
                .ToListAsync(ct);
            var team = scope.TeamIds.ToHashSet();
            var allowed = owners
                .Where(x => (x.CreatedBy.HasValue && team.Contains(x.CreatedBy.Value))
                    || (x.ExecutiveId.HasValue && team.Contains(x.ExecutiveId.Value))
                    || AssignedEmployeeIds(x.CustomFields).Any(team.Contains))
                .Select(x => x.Id)
                .ToHashSet();
            invoices = invoices.Where(x => allowed.Contains(x.SecondaryCustomerId)).ToList();
        }

        var approved = invoices.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo).ToList();
        var awaiting = invoices.Where(x => x.ApprovalStatus is not NewInvoice.StatusApprovedHo and not NewInvoice.StatusRejected).ToList();

        var trend = Enumerable.Range(1, 12).Select(month =>
        {
            var slice = invoices.Where(x => x.InvoiceDate.Month == month).ToList();
            return new
            {
                month,
                label = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month),
                invoices = slice.Count,
                amount = Math.Round(slice.Sum(x => x.Amount), 2),
                points = Math.Round(slice.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo).Sum(x => x.SchemePoints), 2),
                is_current = month == now.Month
            };
        }).ToList();

        var today = DateOnly.FromDateTime(now.Date);
        var schemeRows = await _db.LoyaltySchemes.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.SchemeType == "Invoice")
            .Select(x => new { x.Id, x.SchemeName, x.SchemeCode, x.SchemeTag, x.Status, x.StartDate, x.EndDate })
            .ToListAsync(ct);

        var schemes = schemeRows.Select(row =>
        {
            var slice = invoices.Where(x => x.SchemeId == row.Id).ToList();
            var status = row.StartDate > today ? "upcoming" : row.EndDate < today ? "expired" : "live";
            return new
            {
                id = row.Id,
                name = row.SchemeName,
                code = row.SchemeCode,
                tag = string.IsNullOrWhiteSpace(row.SchemeTag) ? "Regular" : row.SchemeTag,
                status,
                status_label = status == "upcoming" ? "Upcoming" : status == "expired" ? "Expired" : "Live",
                start_date = row.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                end_date = row.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                invoices = slice.Count,
                retailers = slice.Select(x => x.SecondaryCustomerId).Distinct().Count(),
                amount = Math.Round(slice.Sum(x => x.Amount), 2),
                points = Math.Round(slice.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo).Sum(x => x.SchemePoints), 2)
            };
        })
        .OrderBy(x => x.status == "live" ? 0 : x.status == "upcoming" ? 1 : 2)
        .ThenByDescending(x => x.invoices)
        .ToList();

        var topRetailers = invoices.GroupBy(x => x.SecondaryCustomerId)
            .Select(group => new
            {
                retailer_id = group.Key,
                name = group.First().CustomerName,
                shop_name = group.First().ShopName,
                dealer = group.First().AssignedDistributorName ?? "",
                invoices = group.Count(),
                amount = Math.Round(group.Sum(x => x.Amount), 2),
                points = Math.Round(group.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo).Sum(x => x.SchemePoints), 2)
            })
            .OrderByDescending(x => x.amount).Take(10).ToList();

        var topDealers = invoices.Where(x => !string.IsNullOrWhiteSpace(x.AssignedDistributorName))
            .GroupBy(x => x.AssignedDistributorName!)
            .Select(group => new
            {
                name = group.Key,
                retailers = group.Select(x => x.SecondaryCustomerId).Distinct().Count(),
                invoices = group.Count(),
                amount = Math.Round(group.Sum(x => x.Amount), 2),
                points = Math.Round(group.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo).Sum(x => x.SchemePoints), 2)
            })
            .OrderByDescending(x => x.amount).Take(10).ToList();

        return Ok(new
        {
            status = "success",
            is_team = true,
            user = UserBlock(scope),
            team = TeamBlock(scope),
            invoices = new
            {
                total = invoices.Count,
                pending = invoices.Count(x => x.ApprovalStatus == NewInvoice.StatusPending),
                approved_ss = invoices.Count(x => x.ApprovalStatus == NewInvoice.StatusApprovedSs),
                approved_sales = invoices.Count(x => x.ApprovalStatus == NewInvoice.StatusApprovedSales),
                approved_ho = approved.Count,
                rejected = invoices.Count(x => x.ApprovalStatus == NewInvoice.StatusRejected),
                this_month = invoices.Count(x => x.InvoiceDate >= monthStart),
                retailers = invoices.Select(x => x.SecondaryCustomerId).Distinct().Count(),
                dealers = invoices.Where(x => !string.IsNullOrWhiteSpace(x.AssignedDistributorName))
                    .Select(x => x.AssignedDistributorName).Distinct().Count(),
                total_amount = Math.Round(invoices.Sum(x => x.Amount), 2),
                ss_amount = Math.Round(invoices.Sum(x => x.SsApprovedAmount ?? 0), 2),
                sales_amount = Math.Round(invoices.Sum(x => x.SalesApprovedAmount ?? 0), 2),
                ho_amount = Math.Round(approved.Sum(x => x.HoApprovedAmount ?? x.Amount), 2),
                expected_amount = Math.Round(awaiting.Sum(x => x.SalesApprovedAmount ?? x.SsApprovedAmount ?? x.Amount), 2),
                points_earned = Math.Round(approved.Sum(x => x.SchemePoints), 2),
                points_expected = Math.Round(awaiting.Sum(x => x.ExpectedSchemePoints), 2)
            },
            trend,
            schemes = new
            {
                live = schemes.Count(x => x.status == "live"),
                upcoming = schemes.Count(x => x.status == "upcoming"),
                expired = schemes.Count(x => x.status == "expired"),
                total = schemes.Count,
                rows = schemes.Take(10).ToList()
            },
            top_retailers = topRetailers,
            top_dealers = topDealers
        });
    }

    /// <summary>
    /// Activity dashboard: attendance, field work, tours, leaves and expenses for
    /// every user in scope.
    /// </summary>
    [RequirePermission("dashboard_activity")]
    [HttpGet("activity")]
    public async Task<IActionResult> Activity(CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(ct);
        if (scope is null) return Ok(new { status = "success", is_team = false });

        var ids = scope.TeamIds;
        var longIds = ids.Select(x => (long)x).ToArray();
        var now = DateTime.UtcNow.AddHours(5).AddMinutes(30);
        var today = now.Date;
        var tomorrow = today.AddDays(1);
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var yearStart = new DateTime(now.Year, 1, 1);
        var yearEnd = yearStart.AddYears(1);
        var trendStart = today.AddDays(-13);

        var monthAttendance = ids.Length == 0
            ? []
            : await _db.Attendances.AsNoTracking()
                .Where(x => x.UserId.HasValue && ids.Contains(x.UserId.Value) && x.PunchinDate >= monthStart && x.PunchinDate < monthEnd)
                .Select(x => new AttendanceDay(x.UserId!.Value, x.PunchinDate, x.PunchoutTime, x.WorkingType))
                .ToListAsync(ct);
        var todayRows = monthAttendance
            .Where(x => x.Date >= today && x.Date < tomorrow)
            .Select(x => new AttendanceRow(x.UserId, x.PunchoutTime, x.WorkingType))
            .ToList();

        // Last fourteen days, so the chart shows the current working rhythm.
        var trendRows = ids.Length == 0
            ? []
            : await _db.Attendances.AsNoTracking()
                .Where(x => x.UserId.HasValue && ids.Contains(x.UserId.Value) && x.PunchinDate >= trendStart && x.PunchinDate < tomorrow)
                .Select(x => new { x.UserId, x.PunchinDate })
                .ToListAsync(ct);
        var trend = Enumerable.Range(0, 14).Select(offset =>
        {
            var day = trendStart.AddDays(offset);
            return new
            {
                date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                label = day.ToString("dd MMM", CultureInfo.InvariantCulture),
                weekday = day.ToString("ddd", CultureInfo.InvariantCulture),
                present = trendRows.Where(x => x.PunchinDate.Date == day).Select(x => x.UserId).Distinct().Count(),
                is_today = day == today
            };
        }).ToList();

        var promotional = await _db.PromotionalActivities.AsNoTracking()
            .Where(x => x.DeletedAt == null && (longIds.Contains(x.UserId) || longIds.Contains(x.CreatedById))
                && x.ActivityDate >= yearStart && x.ActivityDate < yearEnd)
            .Select(x => new { x.ActivityDate, x.ActivityType, x.GiftCount, x.TotalExpense, x.Status })
            .ToListAsync(ct);

        var tours = ids.Length == 0
            ? []
            : await _db.TourProgrammes.AsNoTracking()
                .Where(x => x.UserId.HasValue && ids.Contains(x.UserId.Value)
                    && x.Date >= monthStart && x.Date < monthEnd)
                .Select(x => new { x.UserId, x.Status })
                .ToListAsync(ct);

        var leaves = ids.Length == 0
            ? []
            : await _db.Leaves.AsNoTracking()
                .Where(x => x.UserId.HasValue && ids.Contains(x.UserId.Value)
                    && x.FromDate < monthEnd && x.ToDate >= monthStart)
                .Select(x => new { x.Status })
                .ToListAsync(ct);

        var expenses = ids.Length == 0
            ? []
            : await _db.Expenses.AsNoTracking()
                .Where(x => x.UserId.HasValue && ids.Contains(x.UserId.Value))
                .Select(x => new { x.Date, x.ClaimAmount, x.ApproveAmount, x.CheckerStatus, x.AccountantStatus })
                .ToListAsync(ct);
        // expenses.date is stored as text, so the month filter has to happen here.
        var monthExpenses = expenses.Where(x => ParseDate(x.Date) is { } value && value >= monthStart && value < monthEnd).ToList();

        var workingDays = monthAttendance.Select(x => x.Date.Date).Distinct().Count();
        var manDays = monthAttendance.Select(x => new { x.UserId, Day = x.Date.Date }).Distinct().Count();
        var perUser = scope.Team.Select(member =>
        {
            var mine = monthAttendance.Where(x => x.UserId == member.Id).ToList();
            return new
            {
                user_id = member.Id,
                name = member.Name,
                designation = member.Designation ?? "",
                present_days = mine.Select(x => x.Date.Date).Distinct().Count(),
                leave_days = mine.Sum(x => x.WorkingType == "Full Day Leave" ? 1m
                    : x.WorkingType is "First Half Leave" or "Second Half Leave" ? 0.5m : 0m),
                punch_out_pending = mine.Count(x => !x.PunchoutTime.HasValue),
                last_seen = mine.Count == 0 ? null : mine.Max(x => x.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };
        })
        .OrderByDescending(x => x.present_days).ThenBy(x => x.name)
        .Take(15).ToList();

        return Ok(new
        {
            status = "success",
            is_team = true,
            user = UserBlock(scope),
            team = TeamBlock(scope),
            attendance = new
            {
                all = AttendanceBlock(ids, todayRows),
                asr = AttendanceBlock(scope.AsrIds, todayRows),
                dsr = AttendanceBlock(scope.DsrIds, todayRows),
                month = new
                {
                    working_days = workingDays,
                    punch_ins = monthAttendance.Count,
                    unique_users = monthAttendance.Select(x => x.UserId).Distinct().Count(),
                    average_present = workingDays == 0 ? 0m : Math.Round((decimal)manDays / workingDays, 1),
                    punch_out_pending = monthAttendance.Count(x => !x.PunchoutTime.HasValue)
                },
                trend
            },
            working = new
            {
                today = WorkingTypes(todayRows.Select(x => x.WorkingType)),
                month = WorkingTypes(monthAttendance.Select(x => x.WorkingType)),
                year = WorkingTypes(await WorkingTypeRange(ids, yearStart, yearEnd, ct))
            },
            promotional = new
            {
                year = promotional.Count,
                month = promotional.Count(x => x.ActivityDate >= monthStart && x.ActivityDate < monthEnd),
                gifts = promotional.Sum(x => x.GiftCount),
                expense = Math.Round(promotional.Sum(x => x.TotalExpense), 2),
                completed = promotional.Count(x => string.Equals(x.Status, "completed", StringComparison.OrdinalIgnoreCase)),
                draft = promotional.Count(x => string.Equals(x.Status, "draft", StringComparison.OrdinalIgnoreCase)),
                types = promotional.GroupBy(x => string.IsNullOrWhiteSpace(x.ActivityType) ? "Other" : x.ActivityType)
                    .OrderByDescending(group => group.Count())
                    .Select(group => new { name = group.Key, count = group.Count(), expense = Math.Round(group.Sum(x => x.TotalExpense), 2) })
                    .Take(6).ToList()
            },
            tours = new
            {
                month = tours.Count,
                users = tours.Select(x => x.UserId).Distinct().Count(),
                approved = tours.Count(x => x.Status == 1),
                pending = tours.Count(x => x.Status == 0)
            },
            leaves = new
            {
                month = leaves.Count,
                pending = leaves.Count(x => x.Status == null || x.Status == 0),
                approved = leaves.Count(x => x.Status == 1),
                rejected = leaves.Count(x => x.Status == 2)
            },
            expenses = new
            {
                month_claims = monthExpenses.Count,
                claimed = Math.Round(monthExpenses.Sum(x => x.ClaimAmount ?? 0), 2),
                approved = Math.Round(monthExpenses.Sum(x => x.ApproveAmount ?? 0), 2),
                pending = monthExpenses.Count(x => x.CheckerStatus == 0 || x.AccountantStatus == 0)
            },
            users = perUser
        });
    }

    // ---------------------------------------------------------------- scope

    /// <summary>
    /// Who is signed in, which users their data covers, and how that set was
    /// decided. All three dashboards go through this so they agree on scope.
    /// </summary>
    private async Task<DashboardScope?> ResolveScopeAsync(CancellationToken ct)
    {
        var actorId = CurrentUserId();
        var actor = await _db.Users.AsNoTracking()
            .Where(x => x.Id == actorId)
            .Select(x => new { x.Id, x.Name, x.BranchId, x.DesignationId, x.CustomerId })
            .FirstOrDefaultAsync(ct);

        // Dealer/distributor logins have their own dashboard; these are staff only.
        if (actor is null || actor.CustomerId is > 0) return null;

        var visible = await _hr.GetVisibleUserIdsAsync(actorId, ct);
        var candidateIds = visible.Append(actorId).Distinct().ToArray();

        var candidates = await (from user in _db.Users.AsNoTracking()
                          join designation in _db.Designations.AsNoTracking()
                              on user.DesignationId equals designation.Id into designations
                          from designation in designations.DefaultIfEmpty()
                          where candidateIds.Contains(user.Id) && user.Active == "Y" && !user.IsDeleted
                          select new TeamMember(user.Id, user.Name, designation != null ? designation.DesignationName : null,
                              user.DesignationId))
                         .ToListAsync(ct);

        var roles = await _db.ModelHasRoles.AsNoTracking()
            .Where(x => x.ModelId == actorId && x.ModelType == LaravelModelTypes.User)
            .Join(_db.Roles.AsNoTracking(), modelRole => modelRole.RoleId, role => role.Id, (modelRole, role) => new { modelRole.RoleId, role.Name })
            .ToListAsync(ct);

        var branchIds = SplitCsv(actor.BranchId).Select(x => ulong.TryParse(x, out var id) ? id : 0).Where(x => x > 0).ToArray();
        var branchNames = branchIds.Length == 0
            ? []
            : await _db.Branches.AsNoTracking().Where(x => branchIds.Contains(x.Id)).Select(x => x.BranchName).ToListAsync(ct);
        var actorDesignation = await _db.Designations.AsNoTracking()
            .Where(x => x.Id == actor.DesignationId).Select(x => x.DesignationName).FirstOrDefaultAsync(ct);
        var totalInternal = await _db.Users.AsNoTracking()
            .CountAsync(x => !x.CustomerId.HasValue && x.Active == "Y" && !x.IsDeleted, ct);

        // Visibility is decided on the full user set, so a national role still reads
        // as "all branches" even though the figures below only cover the field force.
        var scope = candidates.Count >= totalInternal ? "all"
            : roles.Any(role => role.RoleId == RoleIds.BranchManager) ? "branch"
            : candidates.Count > 1 ? "team" : "self";

        // Every dashboard figure reports on the field force only: ASR and DSR users.
        // Managers and back-office staff are dropped here so the team size, the
        // attendance, order, target and activity counts all cover the same people.
        var asrIds = candidates.Where(x => IsDesignation(x.DesignationId, x.Designation, AsrDesignationId, "ASR")).Select(x => x.Id).ToArray();
        var dsrIds = candidates.Where(x => IsDesignation(x.DesignationId, x.Designation, DsrDesignationId, "DSR")).Select(x => x.Id).ToArray();
        var fieldForce = asrIds.Concat(dsrIds).ToHashSet();
        var team = candidates.Where(x => fieldForce.Contains(x.Id)).ToList();
        var teamIds = team.Select(x => x.Id).ToArray();

        return new DashboardScope(
            actor.Id,
            actor.Name,
            actorDesignation ?? "",
            roles.Select(x => x.Name).ToList(),
            branchNames,
            scope,
            scope switch
            {
                "all" => "All branches",
                "branch" => branchNames.Count > 0 ? string.Join(", ", branchNames) : "My branch",
                "team" => "My reporting team",
                _ => "My own activity"
            },
            teamIds,
            asrIds,
            dsrIds,
            team);
    }

    private static object UserBlock(DashboardScope scope) => new
    {
        id = scope.ActorId,
        name = scope.ActorName,
        designation = scope.Designation,
        roles = scope.Roles,
        branches = scope.Branches,
        scope = scope.Scope,
        scope_label = scope.ScopeLabel
    };

    private static object TeamBlock(DashboardScope scope) => new
    {
        total = scope.TeamIds.Length,
        asr = scope.AsrIds.Length,
        dsr = scope.DsrIds.Length
    };

    // ---------------------------------------------------------------- helpers

    private async Task<OrderAggregate> Orders(ulong[] ids, DateTime start, DateTime end, CancellationToken ct)
    {
        if (ids.Length == 0) return new OrderAggregate(0, 0, 0);
        var rows = await _db.Orders.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.CreatedBy.HasValue && ids.Contains(x.CreatedBy.Value)
                && x.OrderDate >= start && x.OrderDate < end)
            .Select(x => new { x.TotalQty, x.GrandTotal })
            .ToListAsync(ct);
        return new OrderAggregate(rows.Count, rows.Sum(x => x.TotalQty), Math.Round(rows.Sum(x => x.GrandTotal), 2));
    }

    private async Task<int> UniqueBuyers(ulong[] ids, DateTime start, DateTime end, CancellationToken ct) =>
        ids.Length == 0 ? 0 : await _db.Orders.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.CreatedBy.HasValue && ids.Contains(x.CreatedBy.Value)
                && x.OrderDate >= start && x.OrderDate < end && x.BuyerId.HasValue)
            .Select(x => x.BuyerId).Distinct().CountAsync(ct);

    private async Task<List<object>> TopProducts(ulong[] ids, DateTime start, DateTime end, bool valueWise, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var grouped = await (from detail in _db.OrderDetails.AsNoTracking()
                             join order in _db.Orders.AsNoTracking() on detail.OrderId equals order.Id
                             join product in _db.Products.AsNoTracking() on detail.ProductId equals product.Id
                             where order.DeletedAt == null && order.CreatedBy.HasValue && ids.Contains(order.CreatedBy.Value)
                                 && order.OrderDate >= start && order.OrderDate < end
                             group detail by new { detail.ProductId, product.ProductName } into rows
                             select new { rows.Key.ProductName, Quantity = rows.Sum(x => x.Quantity), Value = rows.Sum(x => x.LineTotal) })
                            .ToListAsync(ct);

        var ordered = valueWise
            ? grouped.OrderByDescending(x => x.Value)
            : grouped.OrderByDescending(x => x.Quantity);

        return ordered.Take(5)
            .Select(object (x) => new { name = x.ProductName, quantity = x.Quantity, value = Math.Round(x.Value, 2) })
            .ToList();
    }

    private async Task<List<string>> WorkingTypeRange(ulong[] ids, DateTime start, DateTime end, CancellationToken ct) =>
        ids.Length == 0 ? [] : await _db.Attendances.AsNoTracking()
            .Where(x => x.UserId.HasValue && ids.Contains(x.UserId.Value)
                && x.PunchinDate >= start && x.PunchinDate < end && x.WorkingType != "")
            .Select(x => x.WorkingType).ToListAsync(ct);

    /// <summary>Present, on leave and punch-out pending, matching the SFA attendance tiles.</summary>
    private static object AttendanceBlock(ulong[] ids, List<AttendanceRow> rows)
    {
        var mine = rows.Where(x => ids.Contains(x.UserId)).ToList();
        var present = mine.Select(x => x.UserId).Distinct().Count();
        var onLeave = mine.Sum(x => x.WorkingType == "Full Day Leave" ? 1m
            : x.WorkingType is "First Half Leave" or "Second Half Leave" ? 0.5m : 0m);
        return new
        {
            total = ids.Length,
            present,
            on_leave = onLeave,
            mis_punch = mine.Count(x => !x.PunchoutTime.HasValue),
            not_punched = Math.Max(0, ids.Length - present)
        };
    }

    private static object TargetBlock(IEnumerable<TargetRow> rows)
    {
        var list = rows.ToList();
        var target = list.Sum(x => x.Target ?? 0);
        var achievement = list.Sum(x => x.Achievement ?? 0);
        var quantityTarget = list.Sum(x => x.QuantityTarget ?? 0);
        var quantityAchievement = list.Sum(x => x.QuantityAchievement ?? 0);
        return new
        {
            target = Math.Round(target, 2),
            achievement = Math.Round(achievement, 2),
            achievement_percent = target > 0 ? Math.Round(achievement / target * 100, 2) : 0,
            quantity_target = Math.Round(quantityTarget, 2),
            quantity_achievement = Math.Round(quantityAchievement, 2),
            quantity_achievement_percent = quantityTarget > 0 ? Math.Round(quantityAchievement / quantityTarget * 100, 2) : 0,
            users = list.Select(x => x.UserId).Distinct().Count()
        };
    }

    private static object WorkingTypes(IEnumerable<string> values)
    {
        var rows = values
            .SelectMany(x => (x ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        var known = new[] { "Retailer Visit", "Retailer Meet", "Nukkad Meet", "Field Demo", "Full Day Leave", "First Half Leave", "Second Half Leave" };
        return new
        {
            retailer_visit = rows.Count(x => x.Equals("Retailer Visit", StringComparison.OrdinalIgnoreCase)),
            retailer_meet = rows.Count(x => x.Equals("Retailer Meet", StringComparison.OrdinalIgnoreCase)),
            nukkad_meet = rows.Count(x => x.Equals("Nukkad Meet", StringComparison.OrdinalIgnoreCase)),
            field_demo = rows.Count(x => x.Equals("Field Demo", StringComparison.OrdinalIgnoreCase)),
            other = rows.Count(x => !known.Contains(x, StringComparer.OrdinalIgnoreCase))
        };
    }


    /// <summary>Reads the assigned internal user ids out of a customer's legacy custom_fields.</summary>
    private static IEnumerable<ulong> AssignedEmployeeIds(string? customFields)
    {
        if (string.IsNullOrWhiteSpace(customFields)) yield break;
        foreach (var key in new[] { "employee_id", "sales_executive_id" })
        {
            var raw = ReadJsonValue(customFields, key);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ulong.TryParse(part, out var id) && id > 0) yield return id;
            }
        }
    }

    /// <summary>KYC is only approved once every document status stored on the customer is approved.</summary>
    private static bool IsKycApproved(string? customFields)
    {
        if (string.IsNullOrWhiteSpace(customFields)) return false;
        var fields = customFields;
        var statuses = new[] { "gst_kyc_status", "pan_kyc_status", "aadhar_kyc_status", "bank_kyc_status" }
            .Select(key => ReadJsonValue(fields, key))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        return statuses.Length > 0 && statuses.All(x => string.Equals(x, "approved", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadJsonValue(string json, string key)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty(key, out var value)) return null;
            return value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : value.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;

    private static object OrderBlock(OrderAggregate row) =>
        new { orders = row.Count, quantity = row.Quantity, value = row.Value };

    /// <summary>Designation ids differ between environments, so the name is checked too.</summary>
    private static bool IsDesignation(ulong? designationId, string? designationName, ulong knownId, string code) =>
        designationId == knownId || string.Equals(designationName?.Trim(), code, StringComparison.OrdinalIgnoreCase);

    private static string[] SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private ulong CurrentUserId() =>
        ulong.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    private sealed record OrderAggregate(int Count, long Quantity, decimal Value);
    private sealed record AttendanceDay(ulong UserId, DateTime Date, TimeSpan? PunchoutTime, string WorkingType);
    private sealed record AttendanceRow(ulong UserId, TimeSpan? PunchoutTime, string WorkingType);
    private sealed record TeamMember(ulong Id, string Name, string? Designation, ulong? DesignationId);
    private sealed record DashboardScope(
        ulong ActorId, string ActorName, string Designation, List<string> Roles, List<string> Branches,
        string Scope, string ScopeLabel, ulong[] TeamIds, ulong[] AsrIds, ulong[] DsrIds, List<TeamMember> Team);
    private sealed record TargetRow(ulong UserId, string? Month, decimal? Target, decimal? Achievement, decimal? QuantityTarget, decimal? QuantityAchievement);
}
