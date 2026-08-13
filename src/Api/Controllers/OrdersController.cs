using Api.Filters;
using Application.DTOs.Orders;
using Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;

namespace Api.Controllers;

[ApiController]
[Route("api")]
public sealed class OrdersController : ControllerBase
{
    private readonly IOrderService _service;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public OrdersController(IOrderService service, IWebHostEnvironment environment, IConfiguration configuration)
    {
        _service = service;
        _environment = environment;
        _configuration = configuration;
    }

    [Authorize]
    [RequirePermission("order_access")]
    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders(
        [FromQuery(Name = "retailers_id")] ulong? retailersId,
        [FromQuery(Name = "distributor_id")] ulong? distributorId,
        [FromQuery(Name = "user_id")] ulong? userId,
        [FromQuery(Name = "division_id")] ulong? divisionId,
        [FromQuery(Name = "designation_id")] ulong[] designationIds,
        [FromQuery(Name = "pending_status")] int? pendingStatus,
        [FromQuery(Name = "startdate")] DateTime? startDate,
        [FromQuery(Name = "enddate")] DateTime? endDate,
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var filter = new OrderFilterDto
        {
            RetailersId = retailersId,
            DistributorId = distributorId,
            UserId = userId,
            DivisionId = divisionId,
            DesignationIds = designationIds,
            PendingStatus = pendingStatus,
            StartDate = startDate,
            EndDate = endDate,
            Search = search,
            ActorUserId = CurrentUserId()
        };
        return Ok(await _service.GetOrdersAsync(filter, cancellationToken));
    }

    [Authorize]
    [HttpGet("orders/options")]
    public async Task<IActionResult> GetOptions(CancellationToken cancellationToken) =>
        Ok(await _service.GetOptionsAsync(CurrentUserId(), cancellationToken));

    [Authorize]
    [HttpGet("orders/products")]
    public async Task<IActionResult> GetProductsByFamily([FromQuery(Name = "subcategory_id")] ulong subcategoryId, CancellationToken cancellationToken) =>
        Ok(await _service.GetProductsByFamilyAsync(subcategoryId, cancellationToken));

    [Authorize]
    [RequirePermission("order_show")]
    [HttpGet("orders/{id}")]
    public async Task<IActionResult> GetOrder(ulong id, CancellationToken cancellationToken) =>
        Ok(await _service.GetOrderAsync(id, CurrentUserId(), cancellationToken));

    [Authorize]
    [RequirePermission("order_create")]
    [HttpPost("orders")]
    public async Task<IActionResult> CreateOrder([FromBody] OrderRequestDto request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await _service.CreateOrderAsync(request, CurrentUserId(), cancellationToken));

    [Authorize]
    [RequirePermission("order_edit")]
    [HttpPut("orders/{id}")]
    public async Task<IActionResult> UpdateOrder(ulong id, [FromBody] OrderRequestDto request, CancellationToken cancellationToken) =>
        Ok(await _service.UpdateOrderAsync(id, request, CurrentUserId(), cancellationToken));

    [Authorize]
    [RequirePermission("order_delete")]
    [HttpDelete("orders/{id}")]
    public async Task<IActionResult> DeleteOrder(ulong id, CancellationToken cancellationToken) =>
        Ok(await _service.DeleteOrderAsync(id, cancellationToken));

    [Authorize]
    [RequirePermission("order_active")]
    [HttpPost("orders/{id}/active")]
    public async Task<IActionResult> SetActive(ulong id, [FromBody] OrderActiveRequestDto request, CancellationToken cancellationToken) =>
        Ok(await _service.SetActiveAsync(id, request, cancellationToken));

    [Authorize]
    [RequirePermission("order_edit")]
    [HttpPost("orders/{id}/status")]
    public async Task<IActionResult> SetStatus(ulong id, [FromBody] OrderStatusRequestDto request, CancellationToken cancellationToken) =>
        Ok(await _service.SetStatusAsync(id, request, cancellationToken));

    [Authorize]
    [RequirePermission("order_dispatch")]
    [HttpPost("orders/{id}/dispatch")]
    public async Task<IActionResult> Dispatch(ulong id, [FromForm] OrderDispatchFormRequest form, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<OrderDispatchItemDto> items;
        try { items = JsonSerializer.Deserialize<List<OrderDispatchItemDto>>(form.Items ?? "[]", new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []; }
        catch (JsonException) { throw new Shared.Exceptions.LaravelHttpException(422, "Invalid dispatch product quantities."); }
        var request = new OrderDispatchRequestDto
        {
            Mode = form.Mode ?? "full", InvoiceNo = form.InvoiceNo, InvoiceDate = form.InvoiceDate, LrNo = form.LrNo,
            DispatchDate = form.DispatchDate, TransportDetails = form.TransportDetails, Remark = form.Remark, Items = items,
            LoyaltySchemeId = form.LoyaltySchemeId,
            RemovedOrderDetailIds = ParseIds(form.RemovedOrderDetailIds),
            InvoiceAttachment = await SaveInvoiceAttachmentAsync(form.InvoiceAttachment, cancellationToken)
        };
        return Ok(await _service.DispatchAsync(id, request, CurrentUserId(), cancellationToken));
    }

    private static IReadOnlyCollection<ulong> ParseIds(string? json)
    {
        try { return JsonSerializer.Deserialize<List<ulong>>(json ?? "[]", new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []; }
        catch (JsonException) { throw new Shared.Exceptions.LaravelHttpException(422, "Invalid removed product rows."); }
    }

    [Authorize]
    [RequirePermission("sale_access")]
    [HttpGet("order-dispatches")]
    public async Task<IActionResult> GetDispatches([FromQuery] string? mode, CancellationToken cancellationToken) =>
        Ok(await _service.GetDispatchesAsync(mode, CurrentUserId(), cancellationToken));

    [Authorize]
    [RequirePermission("sale_show")]
    [HttpGet("order-dispatches/{id}")]
    public async Task<IActionResult> GetDispatchDetail(ulong id, CancellationToken cancellationToken) =>
        Ok(await _service.GetDispatchDetailAsync(id, CurrentUserId(), cancellationToken));

    [Authorize]
    [RequirePermission("order_download")]
    [HttpGet("orders/export")]
    public async Task<IActionResult> ExportOrders(
        [FromQuery(Name = "retailers_id")] ulong? retailersId,
        [FromQuery(Name = "distributor_id")] ulong? distributorId,
        [FromQuery(Name = "user_id")] ulong? userId,
        [FromQuery(Name = "division_id")] ulong? divisionId,
        [FromQuery(Name = "designation_id")] ulong[] designationIds,
        [FromQuery(Name = "pending_status")] int? pendingStatus,
        [FromQuery(Name = "startdate")] DateTime? startDate,
        [FromQuery(Name = "enddate")] DateTime? endDate,
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var file = await _service.ExportOrdersAsync(new OrderFilterDto
        {
            RetailersId = retailersId,
            DistributorId = distributorId,
            UserId = userId,
            DivisionId = divisionId,
            DesignationIds = designationIds,
            PendingStatus = pendingStatus,
            StartDate = startDate,
            EndDate = endDate,
            Search = search,
            ActorUserId = CurrentUserId()
        }, cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }

    private ulong? CurrentUserId()
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return ulong.TryParse(subject, out var userId) ? userId : null;
    }

    private async Task<string?> SaveInvoiceAttachmentAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return null;
        if (file.Length > 10 * 1024 * 1024) throw new Shared.Exceptions.LaravelHttpException(422, "Invoice attachment must not exceed 10 MB.");
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".pdf" or ".jpg" or ".jpeg" or ".png" or ".webp"))
            throw new Shared.Exceptions.LaravelHttpException(422, "Only PDF and image invoice attachments are allowed.");
        var legacyRoot = _configuration["FileUploads:LegacyFilesRoot"];
        var root = !string.IsNullOrWhiteSpace(legacyRoot)
            ? Path.Combine(legacyRoot, "uploads", "order-dispatch")
            : Path.Combine(_environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"), "uploads", "order-dispatch");
        Directory.CreateDirectory(root);
        var fileName = $"{Guid.NewGuid():N}{extension}";
        await using var stream = System.IO.File.Create(Path.Combine(root, fileName));
        await file.CopyToAsync(stream, cancellationToken);
        return $"/uploads/order-dispatch/{fileName}";
    }
}

public sealed class OrderDispatchFormRequest
{
    [FromForm(Name = "mode")] public string? Mode { get; set; }
    [FromForm(Name = "invoice_no")] public string? InvoiceNo { get; set; }
    [FromForm(Name = "invoice_date")] public DateTime? InvoiceDate { get; set; }
    [FromForm(Name = "lr_no")] public string? LrNo { get; set; }
    [FromForm(Name = "dispatch_date")] public DateTime? DispatchDate { get; set; }
    [FromForm(Name = "transport_details")] public string? TransportDetails { get; set; }
    [FromForm(Name = "remark")] public string? Remark { get; set; }
    [FromForm(Name = "items")] public string? Items { get; set; }
    [FromForm(Name = "loyalty_scheme_id")] public ulong? LoyaltySchemeId { get; set; }
    [FromForm(Name = "removed_order_detail_ids")] public string? RemovedOrderDetailIds { get; set; }
    [FromForm(Name = "invoice_attachment")] public IFormFile? InvoiceAttachment { get; set; }
}
