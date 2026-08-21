using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class FieldKonnectCheckinController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public FieldKonnectCheckinController(
        AppDbContext dbContext,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    [AcceptVerbs("GET", "POST")]
    [Route("getCheckin")]
    public async Task<IActionResult> GetCheckin([FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        try
        {
            var inactive = await InactiveUser(cancellationToken);
            if (inactive is not null) return inactive;

            var limit = pageSize.HasValue ? $"LIMIT {Math.Clamp(pageSize.Value, 1, 50000)}" : string.Empty;
            var rows = await QueryRows($@"SELECT id, customer_id, entity_type, entity_id, checkin_date, checkin_time, checkin_latitude,
checkin_longitude, checkin_address, checkout_date, checkout_time, checkout_latitude, checkout_longitude, checkout_address, beatscheduleid
FROM check_in
WHERE user_id = @user_id AND deleted_at IS NULL
ORDER BY checkin_date DESC, checkin_time DESC {limit}", cancellationToken, ("@user_id", CurrentUserId()));

            var data = new List<object>();
            foreach (var row in rows)
            {
                var entityType = NormalizeEntityType(FirstNonEmpty(Str(row, "entity_type"), "customer"));
                var entityId = ULong(row, "entity_id") == 0 ? ULong(row, "customer_id") : ULong(row, "entity_id");
                var entity = await EntityLabel(entityType, entityId, cancellationToken);
                data.Add(new
                {
                    checkin_id = ULong(row, "id"),
                    entity_type = entityType,
                    entity_id = entityId,
                    customer_name = entity.Name,
                    customer_type = entity.Type,
                    checkin_date = DateString(row, "checkin_date"),
                    checkin_time = TimeString(row, "checkin_time"),
                    checkin_latitude = Str(row, "checkin_latitude"),
                    checkin_longitude = Str(row, "checkin_longitude"),
                    checkin_address = Str(row, "checkin_address"),
                    checkout_date = DateString(row, "checkout_date"),
                    checkout_time = TimeString(row, "checkout_time"),
                    checkout_latitude = Str(row, "checkout_latitude"),
                    checkout_longitude = Str(row, "checkout_longitude"),
                    checkout_address = Str(row, "checkout_address"),
                    beat_schedule_id = Obj(row, "beatscheduleid") ?? 0
                });
            }

            if (data.Count == 0) return Ok(new { status = "error", message = "No Record Found.", data });
            return Ok(new { status = "success", message = "Data retrieved successfully.", data });
        }
        catch (Exception exception)
        {
            return StatusCode(500, new { status = "error", message = exception.Message });
        }
    }

    [HttpPost("submitCheckin")]
    public async Task<IActionResult> SubmitCheckin([FromForm] CheckinRequest formRequest, CancellationToken cancellationToken)
    {
        try
        {
            var inactive = await InactiveUser(cancellationToken, inactiveStatusCode: 406, inactiveMessage: "Your account is inactive. Contact support.");
            if (inactive is not null) return inactive;

            var request = await ReadCheckinRequest(formRequest, cancellationToken);
            request = request with { EntityType = NormalizeEntityType(request.EntityType) };
            var validation = ValidateCheckin(request);
            if (validation is not null) return BadRequest(validation);

            if (!await HasOpenAttendance(cancellationToken))
            {
                return BadRequest(new { status = false, message = "Please punch in before customer check-in." });
            }

            var open = (await QueryRows(@"SELECT id, customer_id, entity_type, entity_id FROM check_in
WHERE user_id = @user_id AND checkout_date IS NULL AND checkout_time IS NULL AND deleted_at IS NULL
ORDER BY checkin_date DESC, checkin_time DESC LIMIT 1", cancellationToken, ("@user_id", CurrentUserId()))).FirstOrDefault();
            if (open is not null)
            {
                var openType = FirstNonEmpty(Str(open, "entity_type"), "customer")!;
                var openId = ULong(open, "entity_id") == 0 ? ULong(open, "customer_id") : ULong(open, "entity_id");
                var entity = await EntityLabel(openType, openId, cancellationToken);
                return BadRequest(new { status = false, message = $"You have already checked in to {entity.Name}. Please check out first." });
            }

            if (!await EntityExists(request.EntityType!, request.EntityId!.Value, cancellationToken))
            {
                return StatusCode(404, new { status = false, message = "Entity not found" });
            }

            var blockedReason = await EntityBlockedReason(request.EntityType!, request.EntityId.Value, cancellationToken);
            if (blockedReason is not null) return BadRequest(new { status = false, message = blockedReason });

            var now = IndiaNow();
            var beatScheduleId = request.BeatScheduleId ?? await DetectBeatSchedule(request.EntityType!, request.EntityId.Value, now.Date, cancellationToken);
            var distance = await Distance(request.EntityType!, request.EntityId.Value, request.CheckinLatitude!, request.CheckinLongitude!, cancellationToken);
            var address = FirstNonEmpty(request.CheckinAddress, request.Address)
                ?? await ReverseGeocodeAsync(request.CheckinLatitude!, request.CheckinLongitude!, cancellationToken);

            var insertedId = await QueryScalar(@"INSERT INTO check_in (active, user_id, customer_id, entity_type, entity_id, checkin_date, checkin_time,
checkin_latitude, checkin_longitude, checkin_address, distance, beatscheduleid, created_at, updated_at)
OUTPUT INSERTED.id
VALUES ('Y', @user_id, @customer_id, @entity_type, @entity_id, @checkin_date, @checkin_time, @lat, @lng, @address, @distance, @beat_schedule_id, @now, @now)",
                cancellationToken,
                ("@user_id", CurrentUserId()),
                // All mobile entities now live in the unified customers table.
                // Keep customer_id populated for legacy reports while entity_type/entity_id
                // retain the exact mobile entity semantics.
                ("@customer_id", request.EntityId),
                ("@entity_type", request.EntityType),
                ("@entity_id", request.EntityId),
                ("@checkin_date", now.Date),
                ("@checkin_time", now.TimeOfDay),
                ("@lat", request.CheckinLatitude),
                ("@lng", request.CheckinLongitude),
                ("@address", address),
                ("@distance", distance),
                ("@beat_schedule_id", beatScheduleId),
                ("@now", now));

            if (insertedId is null or DBNull)
            {
                throw new InvalidOperationException("Check-in was saved but its generated ID could not be returned.");
            }

            var checkinId = Convert.ToUInt64(insertedId, CultureInfo.InvariantCulture);
            var entityLabel = await EntityLabel(request.EntityType!, request.EntityId.Value, cancellationToken);
            return Ok(new
            {
                status = true,
                message = "Checked in successfully",
                checkin_id = checkinId,
                data = new
                {
                    checkin_id = checkinId,
                    entity_id = request.EntityId.Value,
                    entity_type = request.EntityType,
                    entity_name = entityLabel.Name,
                    entity_type_name = entityLabel.Type,
                    distance
                }
            });
        }
        catch (Exception exception)
        {
            return StatusCode(500, new { status = false, message = exception.Message });
        }
    }

    [HttpGet("getVisitTypes")]
    public async Task<IActionResult> GetVisitTypes(CancellationToken cancellationToken)
    {
        try
        {
            var rows = await QueryRows(@"SELECT id AS type_id, type_name
FROM visit_types
WHERE active = 'Y' AND deleted_at IS NULL
ORDER BY type_name ASC", cancellationToken);
            return Ok(new { status = true, data = rows.Select(CleanRow).ToList() });
        }
        catch (Exception exception)
        {
            return StatusCode(500, new { status = false, message = exception.Message });
        }
    }

    [HttpPost("submitCheckout")]
    public async Task<IActionResult> SubmitCheckout([FromForm] CheckoutRequest formRequest, CancellationToken cancellationToken)
    {
        try
        {
            var inactive = await InactiveUser(cancellationToken);
            if (inactive is not null) return inactive;

            var request = await ReadCheckoutRequest(formRequest, cancellationToken);
            request = request with { EntityType = NormalizeEntityType(request.EntityType) };
            var validation = ValidateCheckout(request);
            if (validation is not null) return BadRequest(validation);
            if (await QueryScalarLong("SELECT COUNT(*) FROM visit_types WHERE id = @id AND active = 'Y' AND deleted_at IS NULL", cancellationToken, ("@id", request.VisitTypeId!.Value)) == 0)
            {
                return BadRequest(new { status = false, message = "Invalid visit_type_id" });
            }

            var checkin = (await QueryRows(@"SELECT id, customer_id, entity_type, entity_id, checkin_date, checkin_time, checkout_date
FROM check_in
WHERE id = @id AND user_id = @user_id AND deleted_at IS NULL
LIMIT 1", cancellationToken, ("@id", request.CheckinId!.Value), ("@user_id", CurrentUserId()))).FirstOrDefault();
            if (checkin is null) return StatusCode(404, new { status = false, message = "Check-in record not found" });
            if (Obj(checkin, "checkout_date") is not null) return BadRequest(new { status = false, message = "Check-in is already checked out" });

            var checkinType = NormalizeEntityType(FirstNonEmpty(Str(checkin, "entity_type"), "customer"));
            var checkinEntityId = ULong(checkin, "entity_id") == 0 ? ULong(checkin, "customer_id") : ULong(checkin, "entity_id");
            if (checkinType != request.EntityType || checkinEntityId != request.EntityId.Value)
            {
                return BadRequest(new { status = false, message = "Checkout entity does not match the open check-in." });
            }

            var now = IndiaNow();
            var checkinAt = CombineDateTime(Obj(checkin, "checkin_date"), Obj(checkin, "checkin_time"));
            var timeInterval = checkinAt.HasValue ? now.Subtract(checkinAt.Value).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : "00:00:00";
            var address = FirstNonEmpty(request.CheckoutAddress, request.Address)
                ?? await ReverseGeocodeAsync(request.CheckoutLatitude!, request.CheckoutLongitude!, cancellationToken);

            var updated = await Execute(@"UPDATE check_in SET checkout_date = @checkout_date, checkout_time = @checkout_time,
checkout_latitude = @lat, checkout_longitude = @lng, checkout_address = @address, time_interval = @time_interval, updated_at = @now
WHERE id = @id AND user_id = @user_id AND checkout_date IS NULL AND checkout_time IS NULL", cancellationToken,
                ("@checkout_date", now.Date),
                ("@checkout_time", now.TimeOfDay),
                ("@lat", request.CheckoutLatitude),
                ("@lng", request.CheckoutLongitude),
                ("@address", address),
                ("@time_interval", timeInterval),
                ("@now", now),
                ("@user_id", CurrentUserId()),
                ("@id", request.CheckinId.Value));
            if (updated == 0) return BadRequest(new { status = false, message = "Failed to update checkout" });

            await Execute(@"INSERT INTO visit_reports (checkin_id, user_id, customer_id, visit_type_id, description, visit_image, created_by, next_visit, created_at, updated_at)
VALUES (@checkin_id, @user_id, @customer_id, @visit_type_id, @description, '', @user_id, @next_visit, @now, @now)", cancellationToken,
                ("@checkin_id", request.CheckinId.Value),
                ("@user_id", CurrentUserId()),
                ("@customer_id", await QueryScalarLong("SELECT COUNT(*) FROM customers WHERE id = @id AND deleted_at IS NULL", cancellationToken, ("@id", request.EntityId)) > 0 ? request.EntityId : null),
                ("@visit_type_id", request.VisitTypeId),
                ("@description", request.Description),
                ("@next_visit", ParseDateTime(request.NextVisit)),
                ("@now", now));

            await Execute("DELETE FROM check_in_drafts WHERE checkin_id = @checkin_id", cancellationToken, ("@checkin_id", request.CheckinId.Value));
            return Ok(new
            {
                status = true,
                message = "Checkout submitted successfully",
                data = new
                {
                    checkin_id = request.CheckinId.Value,
                    entity_id = request.EntityId,
                    entity_type = request.EntityType,
                    checkout_date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    checkout_time = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    time_interval = timeInterval
                }
            });
        }
        catch (Exception exception)
        {
            return StatusCode(500, new { status = false, message = exception.Message });
        }
    }

    [HttpGet("getCurrentOpenCheckin")]
    public async Task<IActionResult> GetCurrentOpenCheckin(CancellationToken cancellationToken)
    {
        try
        {
            var inactive = await InactiveUser(cancellationToken, inactiveStatusCode: 403, inactiveMessage: "Your account is inactive. Contact support.");
            if (inactive is not null) return inactive;

            var row = (await QueryRows(@"SELECT id, customer_id, entity_type, entity_id, checkin_date, checkin_time, checkin_latitude, checkin_longitude,
checkin_address, distance, beatscheduleid FROM check_in
WHERE user_id = @user_id AND checkout_date IS NULL AND checkout_time IS NULL AND deleted_at IS NULL
ORDER BY checkin_date DESC, checkin_time DESC LIMIT 1", cancellationToken, ("@user_id", CurrentUserId()))).FirstOrDefault();

            if (row is null)
            {
                return Ok(new { status = "success", message = "No active check-in found. You are currently checked out.", has_open_checkin = false, open_checkin = (object?)null });
            }

            var entityType = NormalizeEntityType(FirstNonEmpty(Str(row, "entity_type"), "customer"));
            var entityId = ULong(row, "entity_id") == 0 ? ULong(row, "customer_id") : ULong(row, "entity_id");
            var entity = await EntityLabel(entityType, entityId, cancellationToken);
            var entityDetails = await OpenEntityDetails(entityType, entityId, cancellationToken);
            return Ok(new
            {
                status = "success",
                message = "Active check-in found",
                has_open_checkin = true,
                open_checkin = new
                {
                    checkin_id = ULong(row, "id"),
                    checkin_date = DateString(row, "checkin_date"),
                    checkin_time = TimeString(row, "checkin_time"),
                    checkin_datetime = DateTimeString(row, "checkin_date", "checkin_time"),
                    entity_type = entityType,
                    entity_id = entityId,
                    entity_name = entity.Name,
                    entity_type_name = entity.Type,
                    checkin_latitude = Str(row, "checkin_latitude"),
                    checkin_longitude = Str(row, "checkin_longitude"),
                    checkin_address = Str(row, "checkin_address"),
                    distance = Str(row, "distance"),
                    beatscheduleid = Obj(row, "beatscheduleid"),
                    entity_details = entityDetails
                }
            });
        }
        catch (Exception exception)
        {
            return StatusCode(500, new { status = "error", message = exception.Message });
        }
    }

    [AcceptVerbs("GET", "POST")]
    [Route("getCheckinByEntity")]
    public async Task<IActionResult> GetCheckinByEntity(CancellationToken cancellationToken)
    {
        try
        {
            var inactive = await InactiveUser(cancellationToken);
            if (inactive is not null) return inactive;
            var entityType = RequestValue("entity_type");
            var entityId = ULongValue("entity_id");
            if (!IsValidEntityType(entityType) || !entityId.HasValue || entityId.Value < 1)
            {
                return BadRequest(new { status = "error", message = new { entity_type = new[] { "The entity type field is required." }, entity_id = new[] { "The entity id field is required." } } });
            }

            var row = (await QueryRows(@"SELECT id, entity_type, entity_id, checkin_date, checkin_time, checkin_latitude, checkin_longitude,
checkin_address, checkout_date, checkout_time, checkout_latitude, checkout_longitude, checkout_address, time_interval, distance, beatscheduleid
FROM check_in WHERE user_id = @user_id AND entity_type = @entity_type AND entity_id = @entity_id AND deleted_at IS NULL
ORDER BY checkin_date DESC, checkin_time DESC LIMIT 1", cancellationToken,
                ("@user_id", CurrentUserId()), ("@entity_type", entityType), ("@entity_id", entityId.Value))).FirstOrDefault();

            if (row is null) return Ok(new { status = "error", message = "No check-in found for this entity.", data = (object?)null });
            var entity = await EntityLabel(entityType!, entityId.Value, cancellationToken);
            var data = new
            {
                checkin_id = ULong(row, "id"),
                entity_type = Str(row, "entity_type"),
                entity_id = ULong(row, "entity_id"),
                entity_name = entity.Name,
                entity_type_label = entity.Type,
                checkin_date = DateString(row, "checkin_date"),
                checkin_time = TimeString(row, "checkin_time"),
                checkin_latitude = Str(row, "checkin_latitude"),
                checkin_longitude = Str(row, "checkin_longitude"),
                checkin_address = Str(row, "checkin_address"),
                checkout_date = DateString(row, "checkout_date"),
                checkout_time = TimeString(row, "checkout_time"),
                checkout_latitude = Str(row, "checkout_latitude"),
                checkout_longitude = Str(row, "checkout_longitude"),
                checkout_address = Str(row, "checkout_address"),
                time_interval = TimeString(row, "time_interval"),
                distance = Str(row, "distance"),
                beat_schedule_id = Obj(row, "beatscheduleid") ?? 0
            };
            return Ok(new { status = "success", message = "Check-in details retrieved successfully.", data });
        }
        catch (Exception exception)
        {
            return StatusCode(500, new { status = "error", message = exception.Message });
        }
    }

    [AcceptVerbs("GET", "POST")]
    [Route("addCheckinDraft")]
    public async Task<IActionResult> AddCheckinDraft([FromForm] DraftRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var inactive = await InactiveUser(cancellationToken);
            if (inactive is not null) return inactive;
            if (!request.CheckinId.HasValue || string.IsNullOrWhiteSpace(request.DraftMsg))
            {
                return BadRequest(new { status = "error", message = new { checkin_id = new[] { "The checkin id field is required." }, draft_msg = new[] { "The draft msg field is required." } } });
            }

            var existing = await QueryScalar("SELECT id FROM check_in_drafts WHERE checkin_id = @checkin_id LIMIT 1", cancellationToken, ("@checkin_id", request.CheckinId.Value));
            var now = IndiaNow();
            if (existing is null or DBNull)
            {
                await Execute("INSERT INTO check_in_drafts (checkin_id, draft_msg, created_at, updated_at) VALUES (@checkin_id, @draft_msg, @now, @now)", cancellationToken,
                    ("@checkin_id", request.CheckinId.Value), ("@draft_msg", request.DraftMsg), ("@now", now));
            }
            else
            {
                await Execute("UPDATE check_in_drafts SET draft_msg = @draft_msg, updated_at = @now WHERE id = @id", cancellationToken,
                    ("@draft_msg", request.DraftMsg), ("@now", now), ("@id", existing));
            }

            var draft = (await QueryRows("SELECT id, checkin_id, draft_msg, created_at, updated_at FROM check_in_drafts WHERE checkin_id = @checkin_id LIMIT 1", cancellationToken, ("@checkin_id", request.CheckinId.Value))).FirstOrDefault();
            return Ok(new { status = "success", data = draft, message = "Draft saved successfully" });
        }
        catch (Exception exception)
        {
            return StatusCode(500, new { status = "error", message = exception.Message });
        }
    }

    [AcceptVerbs("GET", "POST")]
    [Route("getCheckinDraft")]
    public async Task<IActionResult> GetCheckinDraft(CancellationToken cancellationToken)
    {
        try
        {
            var inactive = await InactiveUser(cancellationToken);
            if (inactive is not null) return inactive;
            var checkinId = ULongValue("checkin_id");
            if (!checkinId.HasValue) return BadRequest(new { status = "error", message = new { checkin_id = new[] { "The checkin id field is required." } } });
            var draft = (await QueryRows("SELECT id, checkin_id, draft_msg, created_at, updated_at FROM check_in_drafts WHERE checkin_id = @checkin_id LIMIT 1", cancellationToken, ("@checkin_id", checkinId.Value))).FirstOrDefault();
            return Ok(new { status = "success", data = draft, message = "Draft retrieved successfully" });
        }
        catch (Exception exception)
        {
            return StatusCode(500, new { status = "error", message = exception.Message });
        }
    }

    /// <summary>Visit history for one customer, for the Activity screen in the SFA app.
    /// Every check-in on the customer, by any employee - not just the caller - with the
    /// value this customer ordered on that date and the note left at checkout.</summary>
    [AcceptVerbs("GET", "POST")]
    [Route("getCustomerCheckinActivity")]
    public async Task<IActionResult> GetCustomerCheckinActivity(CancellationToken cancellationToken)
    {
        try
        {
            var inactive = await InactiveUser(cancellationToken);
            if (inactive is not null) return inactive;

            var entityType = NormalizeEntityType(FirstNonEmpty(RequestValue("entity_type"), "secondary_customer"));
            var entityId = ULongValue("entity_id") ?? ULongValue("customer_id");
            if (!IsValidEntityType(entityType) || !entityId.HasValue || entityId.Value < 1)
            {
                return BadRequest(new { status = "error", message = new { entity_id = new[] { "The entity id field is required." } } });
            }

            var page = Math.Max(1, (int)(ULongValue("page") ?? 1));
            var pageSize = Math.Clamp((int)(ULongValue("per_page") ?? ULongValue("page_size") ?? 20), 1, 200);
            var offset = (page - 1) * pageSize;

            const string scope = @"FROM check_in ci
WHERE ci.deleted_at IS NULL
  AND ci.entity_type = @entity_type
  AND COALESCE(ci.entity_id, ci.customer_id) = @entity_id";

            var total = await QueryScalarLong($"SELECT COUNT(*) {scope}", cancellationToken,
                ("@entity_type", entityType), ("@entity_id", entityId.Value));

            // Sums cover the whole history, not the page, so the header total does not
            // change as more pages are pulled in.
            var totalOrderValue = await QueryScalar(@"SELECT COALESCE(SUM(day_total), 0) FROM (
    SELECT DISTINCT ci.checkin_date,
        (SELECT COALESCE(SUM(o.grand_total), 0) FROM orders o
          WHERE o.deleted_at IS NULL AND o.buyer_id = @entity_id
            AND CAST(o.order_date AS date) = ci.checkin_date) AS day_total
    FROM check_in ci
    WHERE ci.deleted_at IS NULL
      AND ci.entity_type = @entity_type
      AND COALESCE(ci.entity_id, ci.customer_id) = @entity_id
) days", cancellationToken, ("@entity_type", entityType), ("@entity_id", entityId.Value));

            var rows = await QueryRows($@"SELECT ci.id, ci.user_id, ci.checkin_date, ci.checkin_time, ci.checkout_date, ci.checkout_time,
    u.name AS employee_name, u.employee_codes, d.designation_name,
    (SELECT TOP 1 vr.description FROM visit_reports vr
      WHERE vr.checkin_id = ci.id AND vr.deleted_at IS NULL
      ORDER BY vr.id DESC) AS note,
    (SELECT COALESCE(SUM(o.grand_total), 0) FROM orders o
      WHERE o.deleted_at IS NULL AND o.buyer_id = @entity_id
        AND CAST(o.order_date AS date) = ci.checkin_date) AS order_value
FROM check_in ci
LEFT JOIN users u ON u.id = ci.user_id
LEFT JOIN designations d ON d.id = u.designation_id
WHERE ci.deleted_at IS NULL
  AND ci.entity_type = @entity_type
  AND COALESCE(ci.entity_id, ci.customer_id) = @entity_id
ORDER BY ci.checkin_date DESC, ci.checkin_time DESC, ci.id DESC
OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY", cancellationToken,
                ("@entity_type", entityType), ("@entity_id", entityId.Value));

            var entity = await EntityLabel(entityType!, entityId.Value, cancellationToken);
            var data = rows.Select(row => new
            {
                checkin_id = ULong(row, "id"),
                user_id = ULong(row, "user_id"),
                employee_name = FirstNonEmpty(Str(row, "employee_name"), "-"),
                employee_code = Str(row, "employee_codes"),
                designation = Str(row, "designation_name"),
                checkin_date = DateString(row, "checkin_date"),
                checkin_time = TimeString(row, "checkin_time"),
                checkout_date = DateString(row, "checkout_date"),
                checkout_time = TimeString(row, "checkout_time"),
                order_value = Convert.ToDecimal(Obj(row, "order_value") ?? 0m, CultureInfo.InvariantCulture),
                note = Str(row, "note")
            }).ToList();

            return Ok(new
            {
                status = "success",
                message = total == 0 ? "No check-in found for this customer." : "Check-in activity retrieved successfully.",
                entity_id = entityId.Value,
                entity_type = entityType,
                entity_name = entity.Name,
                total_checkins = total,
                total_order_value = Convert.ToDecimal(totalOrderValue is null or DBNull ? 0m : totalOrderValue, CultureInfo.InvariantCulture),
                pagination = new
                {
                    page,
                    page_size = pageSize,
                    total,
                    total_pages = (int)Math.Ceiling(total / (double)pageSize),
                    has_more = offset + data.Count < total
                },
                data
            });
        }
        catch (Exception exception)
        {
            return StatusCode(500, new { status = "error", message = exception.Message });
        }
    }

    private async Task<IActionResult?> InactiveUser(CancellationToken cancellationToken, int inactiveStatusCode = 401, string inactiveMessage = "User Inactive")
    {
        var active = await QueryScalar("SELECT active FROM users WHERE id = @id AND deleted_at IS NULL LIMIT 1", cancellationToken, ("@id", CurrentUserId()));
        if (active is null or DBNull) return Unauthorized(new { status = "error", message = "Unauthenticated. Please login again." });
        return Convert.ToString(active, CultureInfo.InvariantCulture) == "N" ? StatusCode(inactiveStatusCode, new { status = "error", message = inactiveMessage }) : null;
    }

    private static object? ValidateCheckin(CheckinRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!IsValidEntityType(request.EntityType)) errors["entity_type"] = ["The entity type field is required."];
        if (!request.EntityId.HasValue || request.EntityId.Value < 1) errors["entity_id"] = ["The entity id field is required."];
        if (!ValidCoordinate(request.CheckinLatitude, 90)) errors["checkin_latitude"] = ["A valid latitude between -90 and 90 is required."];
        if (!ValidCoordinate(request.CheckinLongitude, 180)) errors["checkin_longitude"] = ["A valid longitude between -180 and 180 is required."];
        return errors.Count == 0 ? null : new { status = false, message = "Validation failed", errors };
    }

    private static object? ValidateCheckout(CheckoutRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!request.CheckinId.HasValue) errors["checkin_id"] = ["The checkin id field is required."];
        if (!IsValidEntityType(request.EntityType)) errors["entity_type"] = ["The entity type field is required."];
        if (!request.EntityId.HasValue || request.EntityId.Value < 1) errors["entity_id"] = ["The entity id field is required."];
        if (!ValidCoordinate(request.CheckoutLatitude, 90)) errors["checkout_latitude"] = ["A valid latitude between -90 and 90 is required."];
        if (!ValidCoordinate(request.CheckoutLongitude, 180)) errors["checkout_longitude"] = ["A valid longitude between -180 and 180 is required."];
        if (string.IsNullOrWhiteSpace(request.Description)) errors["description"] = ["The description field is required."];
        if (!request.VisitTypeId.HasValue || request.VisitTypeId.Value < 1) errors["visit_type_id"] = ["The visit type id field is required."];
        return errors.Count == 0 ? null : new { status = false, message = "Validation failed", errors };
    }

    private static bool ValidCoordinate(string? value, decimal limit) =>
        !string.IsNullOrWhiteSpace(value) &&
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var coordinate) &&
        coordinate >= -limit && coordinate <= limit;

    private async Task<bool> EntityExists(string entityType, ulong entityId, CancellationToken cancellationToken)
    {
        var table = EntityTable(entityType);
        if (table is null) return false;
        var deletedFilter = entityType == "customer" ? " AND deleted_at IS NULL" : string.Empty;
        if (await QueryScalarLong($"SELECT COUNT(*) FROM {table} WHERE id = @id{deletedFilter}", cancellationToken, ("@id", entityId)) > 0) return true;
        return entityType is "distributor" or "secondary_customer"
            && await QueryScalarLong("SELECT COUNT(*) FROM customers WHERE id = @id AND deleted_at IS NULL", cancellationToken, ("@id", entityId)) > 0;
    }

    private async Task<(string Name, string Type)> EntityLabel(string entityType, ulong entityId, CancellationToken cancellationToken)
    {
        var row = (await EntityDetails(entityType, entityId, cancellationToken)) ?? [];
        return entityType switch
        {
            "distributor" => (FirstNonEmpty(Str(row, "trade_name"), Str(row, "legal_name"), "Unknown Distributor")!, FirstNonEmpty(Str(row, "category"), "Distributor")!),
            "secondary_customer" => (FirstNonEmpty(Str(row, "shop_name"), "Unknown Shop")!, FirstNonEmpty(Str(row, "sub_type"), "Secondary Customer")!),
            _ => (FirstNonEmpty(Str(row, "name"), "Unknown")!, FirstNonEmpty(Str(row, "customertype_name"), "Customer")!)
        };
    }

    private async Task<bool> HasOpenAttendance(CancellationToken cancellationToken)
    {
        var today = IndiaNow().Date;
        return await QueryScalarLong(@"SELECT COUNT(*)
FROM attendances
WHERE user_id = @user_id
AND punchin_date = @today
AND punchin_time IS NOT NULL
AND punchout_time IS NULL
AND deleted_at IS NULL", cancellationToken, ("@user_id", CurrentUserId()), ("@today", today)) > 0;
    }

    private async Task<string?> EntityBlockedReason(string entityType, ulong entityId, CancellationToken cancellationToken)
    {
        var row = await EntityDetails(entityType, entityId, cancellationToken);
        if (row is null) return "Entity not found";
        if (!string.Equals(Str(row, "active"), "Y", StringComparison.OrdinalIgnoreCase)) return "Customer is inactive";
        // Approval controls ordering only. Active customers of every type,
        // including pending/rejected retailers, remain available for visits.
        return null;
    }

    private async Task<object?> OpenEntityDetails(string entityType, ulong entityId, CancellationToken cancellationToken)
    {
        var row = await EntityDetails(entityType, entityId, cancellationToken);
        if (row is null) return null;
        var status = FirstNonEmpty(Str(row, "visit_status"), Str(row, "status"), "APPROVED");
        return entityType switch
        {
            "distributor" => new
            {
                id = entityId,
                legal_name = FirstNonEmpty(Str(row, "legal_name"), Str(row, "trade_name"), Str(row, "name")),
                shipping_address = FirstNonEmpty(Str(row, "shipping_address"), Str(row, "address_line")),
                mobile = Str(row, "mobile"),
                status
            },
            "secondary_customer" => new
            {
                id = entityId,
                shop_name = FirstNonEmpty(Str(row, "shop_name"), Str(row, "name")),
                address_line = Str(row, "address_line"),
                mobile_number = FirstNonEmpty(Str(row, "mobile_number"), Str(row, "mobile")),
                status,
                type = FirstNonEmpty(Str(row, "type_name"), Str(row, "customertype_name"), "Retailer"),
                distributor_name = Obj(row, "distributor_name")
            },
            _ => row
        };
    }

    private async Task<Dictionary<string, object?>?> EntityDetails(string entityType, ulong entityId, CancellationToken cancellationToken)
    {
        var sql = entityType switch
        {
            "distributor" => @"SELECT c.*, c.name AS legal_name, c.name AS trade_name, c.name AS shop_name, ct.customertype_name, ct.type_name,
a.address1 AS address_line, a.address1 AS shipping_address, c.latitude, c.longitude, cd.visit_status, cd.visit_status AS status
FROM customers c
LEFT JOIN customer_types ct ON ct.id = c.customertype
LEFT JOIN addresses a ON a.customer_id = c.id AND a.deleted_at IS NULL
LEFT JOIN customer_details cd ON cd.customer_id = c.id AND cd.deleted_at IS NULL
WHERE c.id = @id AND c.deleted_at IS NULL
AND (c.customertype IN (1,3) OR ct.customertype_name LIKE '%Distributor%' OR ct.type_name LIKE '%Distributor%' OR ct.type_name = 'Dealer')
LIMIT 1",
            "secondary_customer" => @"SELECT c.*, c.name AS legal_name, c.name AS trade_name, c.name AS shop_name, ct.customertype_name, ct.type_name,
a.address1 AS address_line, c.mobile AS mobile_number, c.latitude, c.longitude,
COALESCE(cd.visit_status, JSON_VALUE(c.custom_fields, '$.status')) AS visit_status,
COALESCE(cd.visit_status, JSON_VALUE(c.custom_fields, '$.status')) AS status,
JSON_UNQUOTE(JSON_EXTRACT(c.custom_fields, '$.distributor_name')) AS distributor_name
FROM customers c
LEFT JOIN customer_types ct ON ct.id = c.customertype
LEFT JOIN addresses a ON a.customer_id = c.id AND a.deleted_at IS NULL
LEFT JOIN customer_details cd ON cd.customer_id = c.id AND cd.deleted_at IS NULL
WHERE c.id = @id AND c.deleted_at IS NULL
AND (ct.customertype_name LIKE '%Retailer%' OR ct.type_name LIKE '%Retailer%' OR (c.customertype NOT IN (1,3) AND COALESCE(ct.customertype_name, '') NOT LIKE '%Distributor%' AND COALESCE(ct.type_name, '') NOT LIKE '%Distributor%'))
LIMIT 1",
            "customer" => @"SELECT c.*, ct.customertype_name FROM customers c LEFT JOIN customer_types ct ON ct.id = c.customertype WHERE c.id = @id LIMIT 1",
            _ => null
        };
        if (sql is null) return null;
        var row = (await QueryRows(sql, cancellationToken, ("@id", entityId))).FirstOrDefault();
        if (row is not null || entityType == "customer") return row;
        return (await QueryRows(@"SELECT c.*, c.name AS legal_name, c.name AS trade_name, c.name AS shop_name, ct.customertype_name, ct.type_name,
a.address1 AS address_line, a.address1 AS shipping_address, c.mobile AS mobile_number, c.latitude, c.longitude,
COALESCE(cd.visit_status, JSON_VALUE(c.custom_fields, '$.status')) AS visit_status,
COALESCE(cd.visit_status, JSON_VALUE(c.custom_fields, '$.status')) AS status,
JSON_UNQUOTE(JSON_EXTRACT(c.custom_fields, '$.distributor_name')) AS distributor_name
FROM customers c
LEFT JOIN customer_types ct ON ct.id = c.customertype
LEFT JOIN addresses a ON a.customer_id = c.id AND a.deleted_at IS NULL
LEFT JOIN customer_details cd ON cd.customer_id = c.id AND cd.deleted_at IS NULL
WHERE c.id = @id AND c.deleted_at IS NULL
LIMIT 1", cancellationToken, ("@id", entityId))).FirstOrDefault();
    }

    private async Task<ulong?> DetectBeatSchedule(string entityType, ulong entityId, DateTime today, CancellationToken cancellationToken)
    {
        if (entityType is not ("customer" or "secondary_customer")) return null;
        var result = await QueryScalar(@"SELECT bs.id FROM beat_schedules bs
INNER JOIN beat_customers bc ON bc.beat_id = bs.beat_id
WHERE bs.user_id = @user_id AND CAST(bs.beat_date AS date) = @today AND bc.customer_id = @entity_id
ORDER BY bs.id DESC LIMIT 1", cancellationToken, ("@user_id", CurrentUserId()), ("@today", today), ("@entity_id", entityId));
        return result is null or DBNull ? null : Convert.ToUInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task<string> Distance(string entityType, ulong entityId, string latitude, string longitude, CancellationToken cancellationToken)
    {
        var row = await EntityDetails(entityType, entityId, cancellationToken);
        if (row is null) return string.Empty;
        var entityLatitude = entityType == "secondary_customer" && !string.IsNullOrWhiteSpace(Str(row, "gps_location")) ? GpsPart(Str(row, "gps_location"), 0) : Str(row, "latitude");
        var entityLongitude = entityType == "secondary_customer" && !string.IsNullOrWhiteSpace(Str(row, "gps_location")) ? GpsPart(Str(row, "gps_location"), 1) : Str(row, "longitude");
        if (!double.TryParse(latitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var lat1) ||
            !double.TryParse(longitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var lon1) ||
            !double.TryParse(entityLatitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var lat2) ||
            !double.TryParse(entityLongitude, NumberStyles.Any, CultureInfo.InvariantCulture, out var lon2))
        {
            return string.Empty;
        }

        const double radiusKm = 6371d;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return (radiusKm * c).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private async Task<object?> QueryScalar(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SqlServerSql.Normalize(sql);
        AddParameters(command, parameters);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<long> QueryScalarLong(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        var value = await QueryScalar(sql, cancellationToken, parameters);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task<int> Execute(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SqlServerSql.Normalize(sql);
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> QueryRows(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SqlServerSql.Normalize(sql);
        AddParameters(command, parameters);
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static void AddParameters(IDbCommand command, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var parameter in parameters)
        {
            var dbParameter = command.CreateParameter();
            dbParameter.ParameterName = parameter.Name;
            dbParameter.Value = SqlServerSql.ParameterValue(parameter.Value);
            command.Parameters.Add(dbParameter);
        }
    }

    private static string? EntityTable(string entityType) => entityType switch
    {
        "customer" => "customers",
        "distributor" => "customers",
        "secondary_customer" => "customers",
        _ => null
    };

    private static string? NormalizeEntityType(string? entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType)) return null;
        var normalized = entityType.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return normalized switch
        {
            "master_distributor" or "dealer" => "distributor",
            "retailer" or "secondarycustomer" => "secondary_customer",
            _ => normalized
        };
    }
    private static bool IsValidEntityType(string? entityType) => NormalizeEntityType(entityType) is "customer" or "distributor" or "secondary_customer";
    private static string? GpsPart(string gps, int index) => gps.Split(',', StringSplitOptions.TrimEntries).ElementAtOrDefault(index);
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private async Task<string> ReverseGeocodeAsync(string latitudeValue, string longitudeValue, CancellationToken cancellationToken)
    {
        if (!double.TryParse(latitudeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
            !double.TryParse(longitudeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            return string.Empty;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var client = _httpClientFactory.CreateClient();

        try
        {
            var apiKey = FirstNonEmpty(
                _configuration["ThirdParty:GoogleMaps:ApiKey"],
                Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY"));
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var googleUrl = $"https://maps.googleapis.com/maps/api/geocode/json?latlng={latitude.ToString(CultureInfo.InvariantCulture)},{longitude.ToString(CultureInfo.InvariantCulture)}&key={Uri.EscapeDataString(apiKey)}";
                using var googleResponse = await client.GetAsync(googleUrl, timeout.Token);
                if (googleResponse.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await googleResponse.Content.ReadAsStreamAsync(timeout.Token));
                    if (document.RootElement.TryGetProperty("results", out var results) &&
                        results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0 &&
                        results[0].TryGetProperty("formatted_address", out var formattedAddress))
                    {
                        var address = formattedAddress.GetString();
                        if (!string.IsNullOrWhiteSpace(address)) return address.Trim();
                    }
                }
            }

            // Key-free fallback keeps check-in compatible with the already-published app.
            var nominatimUrl = $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={latitude.ToString(CultureInfo.InvariantCulture)}&lon={longitude.ToString(CultureInfo.InvariantCulture)}&zoom=18&addressdetails=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, nominatimUrl);
            request.Headers.UserAgent.ParseAdd("FieldKonnect/1.0 (support@ksbindia.co.in)");
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return string.Empty;
            using var fallbackDocument = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            return fallbackDocument.RootElement.TryGetProperty("display_name", out var displayName)
                ? displayName.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static object? Obj(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not DBNull ? value : null;
    private static string Str(Dictionary<string, object?> row, string key) => Convert.ToString(Obj(row, key), CultureInfo.InvariantCulture) ?? string.Empty;
    private static ulong ULong(Dictionary<string, object?> row, string key) => Obj(row, key) is null ? 0 : Convert.ToUInt64(Obj(row, key), CultureInfo.InvariantCulture);
    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private static DateTime IndiaNow() => DateTime.UtcNow.AddHours(5).AddMinutes(30);
    private static DateTime? ParseDateTime(string? value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date) ? date : null;
    private static DateTime? CombineDateTime(object? date, object? time)
    {
        if (date is null or DBNull || time is null or DBNull) return null;
        var datePart = Convert.ToDateTime(date, CultureInfo.InvariantCulture).Date;
        var timePart = time is TimeSpan span ? span : TimeSpan.Parse(Convert.ToString(time, CultureInfo.InvariantCulture) ?? "00:00:00", CultureInfo.InvariantCulture);
        return datePart.Add(timePart);
    }

    private static string DateString(Dictionary<string, object?> row, string key) => Obj(row, key) is null ? string.Empty : Convert.ToDateTime(Obj(row, key), CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string TimeString(Dictionary<string, object?> row, string key) => Obj(row, key) switch
    {
        null => string.Empty,
        TimeSpan span => span.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
        DateTime date => date.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        var value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
    private static string? DateTimeString(Dictionary<string, object?> row, string dateKey, string timeKey)
    {
        var date = DateString(row, dateKey);
        if (string.IsNullOrWhiteSpace(date)) return null;
        var time = TimeString(row, timeKey);
        return string.IsNullOrWhiteSpace(time) ? date : $"{date} {time}";
    }

    private ulong CurrentUserId() => ulong.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : throw new InvalidOperationException("Unauthenticated.");

    private string? RequestValue(string key)
    {
        if (Request.Query.TryGetValue(key, out var queryValue) && !string.IsNullOrWhiteSpace(queryValue)) return queryValue.ToString();
        if (Request.HasFormContentType && Request.Form.TryGetValue(key, out var formValue) && !string.IsNullOrWhiteSpace(formValue)) return formValue.ToString();
        return null;
    }

    private ulong? ULongValue(string key) => ulong.TryParse(RequestValue(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private async Task<CheckinRequest> ReadCheckinRequest(CheckinRequest formRequest, CancellationToken cancellationToken)
    {
        if (Request.HasFormContentType || Request.ContentLength is null or 0) return formRequest;
        if (Request.ContentType is null || !Request.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return formRequest;

        using var document = await JsonDocument.ParseAsync(Request.Body, cancellationToken: cancellationToken);
        var root = document.RootElement;
        return new CheckinRequest
        {
            EntityType = JsonString(root, "entity_type") ?? JsonString(root, "entityType") ?? formRequest.EntityType,
            EntityId = JsonULong(root, "entity_id") ?? JsonULong(root, "entityId") ?? formRequest.EntityId,
            CheckinLatitude = JsonString(root, "checkin_latitude") ?? JsonString(root, "checkinLatitude") ?? formRequest.CheckinLatitude,
            CheckinLongitude = JsonString(root, "checkin_longitude") ?? JsonString(root, "checkinLongitude") ?? formRequest.CheckinLongitude,
            CheckinAddress = JsonString(root, "checkin_address") ?? JsonString(root, "checkinAddress") ?? formRequest.CheckinAddress,
            Address = JsonString(root, "address") ?? formRequest.Address,
            BeatScheduleId = JsonULong(root, "beatScheduleId") ?? JsonULong(root, "beat_schedule_id") ?? formRequest.BeatScheduleId
        };
    }

    private async Task<CheckoutRequest> ReadCheckoutRequest(CheckoutRequest formRequest, CancellationToken cancellationToken)
    {
        if (Request.HasFormContentType || Request.ContentLength is null or 0) return formRequest;
        if (Request.ContentType is null || !Request.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return formRequest;

        using var document = await JsonDocument.ParseAsync(Request.Body, cancellationToken: cancellationToken);
        var root = document.RootElement;
        return new CheckoutRequest
        {
            CheckinId = JsonULong(root, "checkin_id") ?? JsonULong(root, "checkinId") ?? formRequest.CheckinId,
            EntityType = JsonString(root, "entity_type") ?? JsonString(root, "entityType") ?? formRequest.EntityType,
            EntityId = JsonULong(root, "entity_id") ?? JsonULong(root, "entityId") ?? formRequest.EntityId,
            CheckoutLatitude = JsonString(root, "checkout_latitude") ?? JsonString(root, "checkoutLatitude") ?? formRequest.CheckoutLatitude,
            CheckoutLongitude = JsonString(root, "checkout_longitude") ?? JsonString(root, "checkoutLongitude") ?? formRequest.CheckoutLongitude,
            CheckoutAddress = JsonString(root, "checkout_address") ?? JsonString(root, "checkoutAddress") ?? formRequest.CheckoutAddress,
            Address = JsonString(root, "address") ?? formRequest.Address,
            Description = JsonString(root, "description") ?? formRequest.Description,
            VisitTypeId = JsonULong(root, "visit_type_id") ?? JsonULong(root, "visitTypeId") ?? formRequest.VisitTypeId,
            NextVisit = JsonString(root, "next_visit") ?? JsonString(root, "nextVisit") ?? formRequest.NextVisit
        };
    }

    private static string? JsonString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static ulong? JsonULong(JsonElement root, string key)
    {
        var value = JsonString(root, key);
        return ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static Dictionary<string, object?> CleanRow(Dictionary<string, object?> row) =>
        row.ToDictionary(pair => pair.Key, pair => pair.Value is DateTime date ? date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : pair.Value, StringComparer.OrdinalIgnoreCase);

    public sealed record CheckinRequest
    {
        [FromForm(Name = "entity_type")] public string? EntityType { get; init; }
        [FromForm(Name = "entity_id")] public ulong? EntityId { get; init; }
        [FromForm(Name = "checkin_latitude")] public string? CheckinLatitude { get; init; }
        [FromForm(Name = "checkin_longitude")] public string? CheckinLongitude { get; init; }
        [FromForm(Name = "checkin_address")] public string? CheckinAddress { get; init; }
        public string? Address { get; init; }
        [FromForm(Name = "beatScheduleId")] public ulong? BeatScheduleId { get; init; }
    }

    public sealed record CheckoutRequest
    {
        [FromForm(Name = "checkin_id")] public ulong? CheckinId { get; init; }
        [FromForm(Name = "checkout_latitude")] public string? CheckoutLatitude { get; init; }
        [FromForm(Name = "checkout_longitude")] public string? CheckoutLongitude { get; init; }
        [FromForm(Name = "checkout_address")] public string? CheckoutAddress { get; init; }
        public string? Address { get; init; }
        public string? Description { get; init; }
        [FromForm(Name = "entity_id")] public ulong? EntityId { get; init; }
        [FromForm(Name = "entity_type")] public string? EntityType { get; init; }
        [FromForm(Name = "visit_type_id")] public ulong? VisitTypeId { get; init; }
        [FromForm(Name = "next_visit")] public string? NextVisit { get; init; }
    }

    public sealed class DraftRequest
    {
        [FromForm(Name = "checkin_id")] public ulong? CheckinId { get; init; }
        [FromForm(Name = "draft_msg")] public string? DraftMsg { get; init; }
    }
}
