using System.Security.Claims;
using Api.Filters;
using Application.DTOs.Customers;
using Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>The KYC menu under Customers Management. It reads the same customers the
/// Master screen does - and through the same data scope - but reports where each one's
/// paperwork has reached rather than their details.</summary>
[ApiController]
[Authorize]
[Route("api/customer-kyc")]
public sealed class CustomerKycController : ControllerBase
{
    private readonly ICustomerService _customerService;

    public CustomerKycController(ICustomerService customerService)
    {
        _customerService = customerService;
    }

    [RequirePermission("customer_kyc.view")]
    [HttpGet]
    public async Task<IActionResult> GetKycList(
        [FromQuery] CustomerKycFilterDto filter,
        [FromQuery(Name = "customer_type")] ulong? customerType,
        [FromQuery(Name = "kyc_status")] string? kycStatus,
        [FromQuery(Name = "page_size")] int? pageSize,
        [FromQuery(Name = "dealer_id")] ulong? dealerId,
        CancellationToken cancellationToken)
    {
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

    private ulong? CurrentUserId()
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return ulong.TryParse(subject, out var userId) ? userId : null;
    }
}
