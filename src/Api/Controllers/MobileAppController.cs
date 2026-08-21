using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Security.Claims;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.DTOs.NewInvoices;
using Application.Interfaces.Repositories;
using Application.Interfaces.Services;
using Api.Services;
using Domain.Entities;
using Domain.Services;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Responses;

namespace Api.Controllers;

[ApiController]
[Route("api")]
public sealed class MobileAppController : ControllerBase
{
    private const ulong DealerType = 1;
    private const ulong RetailerType = 2;
    private const ulong InfluencerType = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _dbContext;
    private readonly IMasterDataService _masterDataService;
    private readonly INewInvoiceRepository _invoiceRepository;
    private readonly INewInvoiceService _newInvoiceService;
    private readonly ITokenService _tokenService;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ISmtpEmailSender _emailSender;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public MobileAppController(AppDbContext dbContext, IMasterDataService masterDataService, INewInvoiceRepository invoiceRepository, INewInvoiceService newInvoiceService, ITokenService tokenService, IPasswordHasher passwordHasher, ISmtpEmailSender emailSender, IWebHostEnvironment environment, IConfiguration configuration)
    {
        _dbContext = dbContext;
        _masterDataService = masterDataService;
        _invoiceRepository = invoiceRepository;
        _newInvoiceService = newInvoiceService;
        _tokenService = tokenService;
        _passwordHasher = passwordHasher;
        _emailSender = emailSender;
        _environment = environment;
        _configuration = configuration;
    }

    [AllowAnonymous]
    [HttpPost("auth/customer-lookup")]
    public async Task<IActionResult> CustomerLookup([FromBody] CustomerLookupRequest request, CancellationToken cancellationToken)
    {
        var mobile = NormalizeMobile(request.Mobile);
        if (string.IsNullOrWhiteSpace(mobile)) return BadRequest(new { status = "error", message = "A valid mobile number is required." });

        var customer = await FindMobileCustomer(mobile).FirstOrDefaultAsync(cancellationToken);
        if (customer is null)
        {
            if (string.IsNullOrWhiteSpace(request.Email))
                return Ok(new { status = "success", next_action = "email_required", customer_exists = false, mobile });

            var email = NormalizeEmail(request.Email);
            if (email is null) return BadRequest(new { status = "error", message = "A valid email address is required." });
            if (await EmailInUseAsync(email, null, cancellationToken))
                return Conflict(new { status = "error", message = "This email address is already registered with another customer." });

            return Ok(new { status = "success", next_action = "register", customer_exists = false, mobile, email });
        }

        if (string.IsNullOrWhiteSpace(customer.Email))
        {
            if (string.IsNullOrWhiteSpace(request.Email))
                return Ok(new { status = "success", next_action = "email_required", customer_exists = true, mobile });

            var email = NormalizeEmail(request.Email);
            if (email is null) return BadRequest(new { status = "error", message = "A valid email address is required." });
            if (await EmailInUseAsync(email, customer.Id, cancellationToken))
                return Conflict(new { status = "error", message = "This email address is already registered with another customer." });
            customer.Email = email;
            customer.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(customer.Password))
        {
            var setup = await SendPasswordCodeAsync(customer, "Create your KSB Loyalty password", cancellationToken);
            return Ok(new
            {
                status = "success",
                next_action = "set_password",
                customer_exists = true,
                mobile,
                email = customer.Email,
                masked_email = MaskEmail(customer.Email),
                mail_bypassed = setup.Bypassed,
                testing_code = setup.Bypassed ? setup.Code : null,
                message = setup.Bypassed
                    ? "Email is unavailable on this testing server. Use the password setup link shown in the app."
                    : "A password setup code has been sent to your email."
            });
        }

        return Ok(new { status = "success", next_action = "password", customer_exists = true, mobile, email = customer.Email, masked_email = MaskEmail(customer.Email) });
    }

    [AllowAnonymous]
    [HttpPost("auth/customer-login")]
    public async Task<IActionResult> CustomerPasswordLogin([FromBody] CustomerPasswordLoginRequest request, CancellationToken cancellationToken)
    {
        var mobile = NormalizeMobile(request.Mobile);
        var customer = await FindMobileCustomer(mobile).FirstOrDefaultAsync(cancellationToken);
        if (customer is null || string.IsNullOrWhiteSpace(customer.Password) || string.IsNullOrWhiteSpace(request.Password)
            || !_passwordHasher.Verify(request.Password, customer.Password))
            return Unauthorized(new { status = "error", message = "Incorrect mobile number or password." });
        if (!string.Equals(customer.Active, "Y", StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, new { status = "error", message = "Account deactivated. Contact admin." });

        var token = _tokenService.CreateAccessToken("customers", customer.Id, DisplayName(customer), [], out var tokenId);
        await StoreCustomerTokenAndLogin(customer.Id, tokenId, request, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { status = "success", message = "Login successful.", token, access_token = token, user = ToProfile(customer) });
    }

    [AllowAnonymous]
    [HttpPost("auth/forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] PasswordCodeRequest request, CancellationToken cancellationToken)
    {
        var customer = await FindMobileCustomer(NormalizeMobile(request.Mobile)).FirstOrDefaultAsync(cancellationToken);
        if (customer is null || string.IsNullOrWhiteSpace(customer.Email))
            return NotFound(new { status = "error", message = "No customer account with an email address was found for this mobile number." });

        var setup = await SendPasswordCodeAsync(customer, "Reset your KSB Loyalty password", cancellationToken);
        return Ok(new
        {
            status = "success",
            next_action = "set_password",
            masked_email = MaskEmail(customer.Email),
            mail_bypassed = setup.Bypassed,
            testing_code = setup.Bypassed ? setup.Code : null,
            message = setup.Bypassed
                ? "Email is unavailable on this testing server. Use the password reset link shown in the app."
                : "A password reset code has been sent to your email."
        });
    }

    [AllowAnonymous]
    [HttpPost("auth/set-password")]
    public async Task<IActionResult> SetPassword([FromBody] SetPasswordRequest request, CancellationToken cancellationToken)
    {
        var customer = await FindMobileCustomer(NormalizeMobile(request.Mobile)).FirstOrDefaultAsync(cancellationToken);
        if (customer is null) return NotFound(new { status = "error", message = "Customer not found." });
        if (string.IsNullOrWhiteSpace(request.Code) || customer.Otp != request.Code || customer.UpdatedAt < DateTime.UtcNow.AddMinutes(-15))
            return BadRequest(new { status = "error", message = "The password code is invalid or has expired." });
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
            return BadRequest(new { status = "error", message = "Password must be at least 6 characters." });

        customer.Password = _passwordHasher.Hash(request.Password);
        customer.Otp = null;
        customer.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { status = "success", next_action = "password", message = "Password created successfully. You can now log in." });
    }

    [AllowAnonymous]
    [HttpPost("retailer/register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        var mobile = NormalizeMobile(request.Mobile ?? GetString(request.Extra, "mobile_number"));
        var ownerName = FirstNonEmpty(request.OwnerName, request.Name, GetString(request.Extra, "owner_name"), GetString(request.Extra, "full_name"));
        var shopName = FirstNonEmpty(request.ShopName, request.FirmName, GetString(request.Extra, "shop_name"), ownerName);
        var email = NormalizeEmail(request.Email);
        if (string.IsNullOrWhiteSpace(mobile) || string.IsNullOrWhiteSpace(ownerName) || email is null || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { status = "error", message = "Owner name, mobile number, email, and password are required." });
        }
        if (request.Password.Length < 6) return BadRequest(new { status = "error", message = "Password must be at least 6 characters." });

        var existing = await FindMobileCustomer(mobile).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null) return Conflict(new { status = "error", message = "Mobile number is already registered.", user = ToProfile(existing) });
        if (await EmailInUseAsync(email, null, cancellationToken)) return Conflict(new { status = "error", message = "Email address is already registered." });

        var customerType = ResolveCustomerType(request.AppType, request.CustomerType, GetString(request.Extra, "customer_type"));
        var fields = ToFieldDictionary(request.Extra);
        fields["owner_name"] = ownerName;
        fields["shop_name"] = shopName ?? ownerName;
        fields["mobile_numbers"] = mobile;
        fields["customer_type"] = customerType.ToString();
        await ApplyMobileCustomerAddressFields(fields, request, cancellationToken);
        SetIfPresent(fields, "distributor_name", request.DealerId?.ToString());
        var assignedUserIds = await MobileAssignedUserIds(request, cancellationToken);
        var assignedUserId = assignedUserIds.FirstOrDefault();
        SetMobileAssignmentFields(fields, assignedUserIds);

        var now = DateTime.UtcNow;
        var customer = new Customer
        {
            Active = "Y",
            Name = shopName ?? ownerName,
            FirstName = ownerName,
            Mobile = mobile,
            ContactNumber = mobile,
            Email = email,
            Password = _passwordHasher.Hash(request.Password),
            CustomerType = customerType,
            CustomerCode = $"{CustomerTypePrefix(customerType)}-{now:yyMMddHHmmss}",
            ExecutiveId = assignedUserId > 0 ? assignedUserId : null,
            CreatedBy = assignedUserId > 0 ? assignedUserId : null,
            UpdatedBy = assignedUserId > 0 ? assignedUserId : null,
            CustomFields = JsonSerializer.Serialize(fields, JsonOptions),
            CreatedAt = now,
            UpdatedAt = now
        };

        await _dbContext.Customers.AddAsync(customer, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await SyncMobileCustomerAddressAsync(customer.Id, fields, cancellationToken);
        await SyncMobileEmployeeDetailsAsync(customer.Id, assignedUserIds, cancellationToken);

        var token = _tokenService.CreateAccessToken("customers", customer.Id, DisplayName(customer), [], out var tokenId);
        await StoreCustomerTokenAndLogin(customer.Id, tokenId, request, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return StatusCode(StatusCodes.Status201Created, new
        {
            status = "success",
            message = "Registration completed successfully.",
            token,
            access_token = token,
            user = ToProfile(customer)
        });
    }

    [Authorize]
    [HttpGet("retailer/kyc")]
    public async Task<IActionResult> GetKyc(CancellationToken cancellationToken)
    {
        var customer = await CurrentCustomer(cancellationToken);
        if (customer is null) return Unauthorized(new { status = "error", message = "Unauthenticated." });

        return Ok(new { status = "success", data = BuildMobileKyc(customer, ReadFields(customer)) });
    }

    [Authorize]
    [HttpPost("retailer/kyc")]
    [HttpPut("retailer/kyc")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadKyc(CancellationToken cancellationToken)
    {
        var customer = await CurrentCustomer(cancellationToken);
        if (customer is null) return Unauthorized(new { status = "error", message = "Unauthenticated." });

        var fields = ReadFields(customer);
        var changedDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var formField in Request.Form)
        {
            var key = formField.Key;
            var value = formField.Value.ToString();
            if (string.Equals(Field(fields, key), value, StringComparison.Ordinal)) continue;

            fields[key] = value;
            var documentKey = KycDocumentKeyForDetail(key);
            if (!string.IsNullOrWhiteSpace(documentKey)) changedDocuments.Add(documentKey);
        }

        foreach (var file in Request.Form.Files)
        {
            if (file.Length == 0) continue;

            var key = KycAttachmentKey(file.Name);
            fields[key] = await SaveFileAsync(file, "customer-kyc", cancellationToken);
            changedDocuments.Add(KycDocumentKey(key));
        }

        foreach (var documentKey in changedDocuments)
        {
            ResetKycStatus(fields, documentKey);
        }

        await CanonicalizeMobileCustomerAddressFields(fields, cancellationToken);
        customer.CustomFields = JsonSerializer.Serialize(fields, JsonOptions);
        customer.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await SyncMobileCustomerAddressAsync(customer.Id, fields, cancellationToken);
        return Ok(new
        {
            status = "success",
            message = "KYC submitted successfully.",
            kyc = KycState(fields),
            data = BuildMobileKyc(customer, fields)
        });
    }

    [AllowAnonymous]
    [HttpGet("masters/customer-types")]
    public IActionResult CustomerTypes() => Ok(new
    {
        status = "success",
        data = new[]
        {
            new { id = DealerType, name = "Dealer", type_name = "Dealer" },
            new { id = RetailerType, name = "Retailer", type_name = "Retailer" },
            new { id = InfluencerType, name = "Influencer", type_name = "Influencer" }
        }
    });

    [AllowAnonymous]
    [HttpGet("masters/states")]
    public async Task<IActionResult> States([FromQuery] string? search, CancellationToken cancellationToken)
    {
        var query = _dbContext.States.AsNoTracking().Where(x => x.Active == "Y");
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.StateName.Contains(search));
        return Ok(new { status = "success", data = await query.OrderBy(x => x.StateName).Select(x => new { x.Id, name = x.StateName, x.CountryId }).ToListAsync(cancellationToken) });
    }

    [AllowAnonymous]
    [HttpGet("masters/location-lookup")]
    [HttpGet("masters/locations")]
    public async Task<IActionResult> LocationLookup(
        [FromQuery] string? pincode,
        [FromQuery(Name = "state_id")] ulong? stateId,
        [FromQuery(Name = "city_id")] ulong? cityId,
        [FromQuery] string? city,
        CancellationToken cancellationToken)
    {
        var response = await _masterDataService.GetLocationDetailsAsync(pincode, stateId, cityId, city, cancellationToken);
        return Ok(response);
    }

    [AllowAnonymous]
    [HttpGet("dealers")]
    public async Task<IActionResult> Dealers([FromQuery] string? search, CancellationToken cancellationToken)
    {
        var query = _dbContext.Customers.AsNoTracking().Where(x => x.Active == "Y" && x.CustomerType == 1);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(x => x.Name.Contains(search) || x.CustomerCode.Contains(search) || (x.Mobile != null && x.Mobile.Contains(search)));
        var dealers = await query.OrderBy(x => x.Name).Take(100).Select(x => new { x.Id, name = x.Name, code = x.CustomerCode, mobile = x.Mobile }).ToListAsync(cancellationToken);
        return Ok(new { status = "success", data = dealers });
    }

    [Authorize]
    [HttpGet("retailer/dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken cancellationToken)
    {
        var customer = await CurrentCustomer(cancellationToken);
        if (customer is null) return Unauthorized(new { status = "error", message = "Unauthenticated." });
        var invoices = await CustomerInvoices(customer.Id, cancellationToken);
        var wallet = await BuildWallet(customer.Id, invoices, cancellationToken);
        var walletCards = await BuildDashboardWalletCards(customer, wallet, invoices, cancellationToken);
        var currentSchemes = await CurrentRunningSchemes(null, cancellationToken, invoices, customer);
        // Customer-facing status intentionally exposes only three states:
        // HO approval is Approved, an explicit rejection is Rejected, and all
        // intermediate workflow states (Pending/SS/Sales) remain Pending.
        var pendingInvoices = invoices.Count(x => x.ApprovalStatus is not NewInvoice.StatusApprovedHo and not NewInvoice.StatusRejected);

        return Ok(new
        {
            status = "success",
            data = new
            {
                customer_id = customer.Id,
                profile = ToProfile(customer),
                total_invoices = invoices.Select(x => x.Id).Distinct().Count(),
                approved_invoices = invoices.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo).Select(x => x.Id).Distinct().Count(),
                pending_invoices = pendingInvoices,
                rejected_invoices = invoices.Where(x => x.ApprovalStatus == NewInvoice.StatusRejected).Select(x => x.Id).Distinct().Count(),
                total_invoice_value = invoices.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo).GroupBy(x => x.Id).Sum(x => x.First().Amount),
                slab_wallet = wallet.Regular.AvailablePoints,
                booster_wallet = wallet.Booster.AvailablePoints,
                active_wallets = walletCards.Count(x => x.IsActive),
                wallets = walletCards,
                current_schemes = currentSchemes,
                recent_invoices = BuildInvoiceListItems(invoices).Take(5)
            }
        });
    }

    [Authorize]
    [HttpGet("dealer/dashboard")]
    public async Task<IActionResult> DealerDashboard(CancellationToken cancellationToken)
    {
        var dealer = await CurrentCustomer(cancellationToken);
        if (dealer is null) return Unauthorized(new { status = "error", message = "Unauthenticated." });
        if (dealer.CustomerType != DealerType)
            return StatusCode(StatusCodes.Status403Forbidden, new { status = "error", message = "Dealer dashboard is available only for dealer/distributor accounts." });

        var assignedRetailers = await DealerAssignedRetailers(dealer.Id).ToListAsync(cancellationToken);
        var assignedRetailerIds = assignedRetailers.Select(x => x.Id).ToHashSet();

        var invoices = (await _invoiceRepository.GetInvoicesAsync(new NewInvoiceFilterDto
        {
            DistributorCustomerId = dealer.Id,
            Unpaged = true
        }, null, cancellationToken)).Items;

        var invoiceItems = BuildInvoiceListItems(invoices, showIntermediateAsInProcess: true);
        var distinctInvoices = invoices.GroupBy(x => x.Id).Select(x => x.First()).ToList();
        var earnedReward = invoiceItems.Where(x => x.Status == "approved").Sum(x => x.RewardAmount);
        var expectedReward = invoiceItems.Where(x => x.Status is "pending" or "in_process").Sum(x => x.ExpectedRewardAmount);
        var totalInvoiceAmount = distinctInvoices.Sum(x => x.Amount);
        var approvedInvoiceAmount = distinctInvoices
            .Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo)
            .Sum(x => x.HoApprovedAmount ?? x.Amount);
        var expectedInvoiceAmount = distinctInvoices
            .Where(x => x.ApprovalStatus is not NewInvoice.StatusApprovedHo and not NewInvoice.StatusRejected)
            .Sum(x => x.SalesApprovedAmount ?? x.SsApprovedAmount ?? x.Amount);
        var activeRetailerIds = distinctInvoices
            .Select(x => x.SecondaryCustomerId)
            .Where(assignedRetailerIds.Contains)
            .ToHashSet();
        var activeRetailers = assignedRetailers.Where(x => activeRetailerIds.Contains(x.Id)).ToList();
        // Keep this in sync with the retailer redemption/profile KYC status. A retailer is
        // pending KYC until every required document is approved.
        var pendingKycRetailers = activeRetailers.Count(x => !string.Equals(KycStatusValue(ReadFields(x)), "approved", StringComparison.OrdinalIgnoreCase));

        return Ok(new
        {
            status = "success",
            data = new
            {
                dealer_id = dealer.Id,
                profile = ToProfile(dealer),
                assigned_retailers = assignedRetailers.Count,
                active_retailers = activeRetailers.Count,
                pending_kyc_retailers = pendingKycRetailers,
                total_invoices = distinctInvoices.Count,
                total_invoice_amount = totalInvoiceAmount,
                approved_invoice_amount = approvedInvoiceAmount,
                expected_invoice_amount = expectedInvoiceAmount,
                total_reward_earned = earnedReward,
                total_expected_reward = expectedReward,
                recent_invoices = invoiceItems.Take(5)
            }
        });
    }

    [Authorize]
    [HttpGet("dealer/schemes")]
    public async Task<IActionResult> DealerSchemes(CancellationToken cancellationToken)
    {
        var dealer = await CurrentDealer(cancellationToken);
        if (dealer.Result is not null) return dealer.Result;

        var today = CurrentBusinessDate();
        // Published schemes only, expired ones included so the dealer can still see
        // what has just ended. Date filtering happens per scheme below, not here.
        var schemes = await _dbContext.LoyaltySchemes.AsNoTracking().Include(x => x.Slabs)
            .Where(x => x.DeletedAt == null
                && x.Active == "Y"
                && (x.Status == "Published" || x.Status == "Live")
                && x.SchemeType == "Invoice")
            .ToListAsync(cancellationToken);
        if (schemes.Count == 0) return Ok(new { status = "success", data = Array.Empty<object>() });

        // A dealer should see anything relevant to its own account plus anything
        // relevant to the retailers assigned to it, so the scheme is matched against
        // every one of those audiences and kept if any of them qualifies.
        var audiences = await DealerSchemeAudiencesAsync(dealer.Customer!, cancellationToken);

        var data = schemes
            .Where(scheme => audiences.Any(audience => SchemeEligibility.Matches(scheme, EffectiveMatchDate(scheme, today), audience)))
            .Select(scheme =>
            {
                var expired = scheme.EndDate < today;
                var upcoming = scheme.StartDate > today;
                return new
                {
                    id = scheme.Id,
                    scheme_name = scheme.SchemeName,
                    scheme_code = scheme.SchemeCode,
                    scheme_tag = scheme.SchemeTag,
                    wallet_type = IsBooster(scheme.SchemeTag) ? "Booster" : "Regular",
                    based_on = scheme.BasedOn,
                    start_date = scheme.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    end_date = scheme.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    status = expired ? "expired" : upcoming ? "upcoming" : "live",
                    status_label = expired ? "Expired" : upcoming ? "Upcoming" : "Live",
                    is_live = !expired && !upcoming,
                    days_remaining = expired || upcoming ? 0 : scheme.EndDate.DayNumber - today.DayNumber,
                    area_scope = scheme.AreaScope,
                    customer_type = scheme.CustomerType
                };
            })
            .OrderByDescending(x => x.is_live)
            .ThenByDescending(x => x.end_date)
            .ToList();

        return Ok(new { status = "success", data });
    }

    /// <summary>
    /// A scheme's own period is used when checking area/type targeting, so an expired
    /// scheme is still matched against the audience instead of being dropped on date.
    /// </summary>
    private static DateOnly EffectiveMatchDate(LoyaltyScheme scheme, DateOnly today) =>
        today < scheme.StartDate ? scheme.StartDate : today > scheme.EndDate ? scheme.EndDate : today;

    /// <summary>
    /// The dealer's own audience plus one per assigned retailer. Branch, zone and
    /// state lookups are batched because a dealer can have hundreds of retailers.
    /// </summary>
    private async Task<IReadOnlyList<SchemeAudience>> DealerSchemeAudiencesAsync(Customer dealer, CancellationToken cancellationToken)
    {
        var retailers = await DealerAssignedRetailers(dealer.Id).ToListAsync(cancellationToken);
        var customers = new List<Customer> { dealer };
        customers.AddRange(retailers);

        var employeeIds = customers
            .Select(customer =>
            {
                var fields = ReadFields(customer);
                return FirstAssignedId(Field(fields, "employee_id"))
                    ?? FirstAssignedId(Field(fields, "sales_executive_id"))
                    ?? customer.ExecutiveId;
            })
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();

        var employees = employeeIds.Length == 0
            ? []
            : await _dbContext.Users.AsNoTracking().Where(x => employeeIds.Contains(x.Id))
                .Select(x => new { x.Id, x.PrimaryBranchId, x.BranchId, x.DivisionId })
                .ToListAsync(cancellationToken);

        var branchIds = employees.Select(x => x.PrimaryBranchId ?? FirstAssignedId(x.BranchId)).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var divisionIds = employees.Where(x => x.DivisionId.HasValue).Select(x => x.DivisionId!.Value).Distinct().ToArray();
        var stateIds = customers.Select(SchemeEligibility.ReadStateId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();

        var branches = branchIds.Length == 0 ? [] : await _dbContext.Branches.AsNoTracking()
            .Where(x => branchIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.BranchName, cancellationToken);
        var divisions = divisionIds.Length == 0 ? [] : await _dbContext.Divisions.AsNoTracking()
            .Where(x => divisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DivisionName, cancellationToken);
        var states = stateIds.Length == 0 ? [] : await _dbContext.States.AsNoTracking()
            .Where(x => stateIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.StateName, cancellationToken);
        var employeeById = employees.ToDictionary(x => x.Id);

        return customers.Select(customer =>
        {
            var fields = ReadFields(customer);
            var employeeId = FirstAssignedId(Field(fields, "employee_id"))
                ?? FirstAssignedId(Field(fields, "sales_executive_id"))
                ?? customer.ExecutiveId;

            string? branchName = null;
            string? zoneName = null;
            if (employeeId.HasValue && employeeById.TryGetValue(employeeId.Value, out var employee))
            {
                var branchId = employee.PrimaryBranchId ?? FirstAssignedId(employee.BranchId);
                if (branchId.HasValue) branchName = branches.GetValueOrDefault(branchId.Value);
                if (employee.DivisionId.HasValue) zoneName = divisions.GetValueOrDefault(employee.DivisionId.Value);
            }

            var stateId = SchemeEligibility.ReadStateId(customer);
            var stateName = stateId.HasValue ? states.GetValueOrDefault(stateId.Value) : null;
            return new SchemeAudience(customer.CustomerType, customer.Name, customer.CustomerCode, branchName, zoneName, stateName);
        }).ToList();
    }

    [Authorize]
    [HttpGet("dealer/schemes/{id}")]
    public async Task<IActionResult> DealerSchemeDetail(ulong id, CancellationToken cancellationToken)
    {
        var dealer = await CurrentDealer(cancellationToken);
        if (dealer.Result is not null) return dealer.Result;

        var scheme = await _dbContext.LoyaltySchemes.AsNoTracking().Include(x => x.Slabs)
            .FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null && x.Active == "Y"
                && (x.Status == "Published" || x.Status == "Live") && x.SchemeType == "Invoice", cancellationToken);
        if (scheme is null) return NotFound(new { status = "error", message = "Scheme not found." });

        var today = CurrentBusinessDate();
        var audiences = await DealerSchemeAudiencesAsync(dealer.Customer!, cancellationToken);
        if (!audiences.Any(audience => SchemeEligibility.Matches(scheme, EffectiveMatchDate(scheme, today), audience)))
        {
            return NotFound(new { status = "error", message = "Scheme not found." });
        }

        // Only this dealer's invoices under this scheme. Reward figures follow the same
        // rules as the dealer dashboard: points are real only once HO has approved,
        // anything still moving through approval counts as expected.
        var invoices = (await _invoiceRepository.GetInvoicesAsync(new NewInvoiceFilterDto
        {
            DistributorCustomerId = dealer.Customer!.Id,
            SchemeId = scheme.Id,
            Unpaged = true
        }, null, cancellationToken)).Items;

        var items = BuildInvoiceListItems(invoices, showIntermediateAsInProcess: true);
        var distinctInvoices = invoices.GroupBy(x => x.Id).Select(x => x.First()).ToList();

        var pointsEarned = items.Where(x => x.Status == "approved").Sum(x => x.RewardAmount);
        var pointsExpected = items.Where(x => x.Status is "pending" or "in_process").Sum(x => x.ExpectedRewardAmount);
        var approvedAmount = distinctInvoices.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo)
            .Sum(x => x.HoApprovedAmount ?? x.Amount);
        var pendingAmount = distinctInvoices
            .Where(x => x.ApprovalStatus is not NewInvoice.StatusApprovedHo and not NewInvoice.StatusRejected)
            .Sum(x => x.SalesApprovedAmount ?? x.SsApprovedAmount ?? x.Amount);

        // "Scheme retailers" = the dealer's retailers that actually pushed an invoice
        // under this scheme, not every retailer assigned to the dealer.
        var retailerGroups = items.GroupBy(x => x.RetailerId).ToList();
        var expired = scheme.EndDate < today;
        var upcoming = scheme.StartDate > today;

        return Ok(new
        {
            status = "success",
            data = new
            {
                id = scheme.Id,
                scheme_name = scheme.SchemeName,
                scheme_code = scheme.SchemeCode,
                scheme_description = scheme.SchemeDescription,
                scheme_tag = scheme.SchemeTag,
                wallet_type = IsBooster(scheme.SchemeTag) ? "Booster" : "Regular",
                based_on = scheme.BasedOn,
                area_scope = scheme.AreaScope,
                customer_type = scheme.CustomerType,
                start_date = scheme.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                end_date = scheme.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                status = expired ? "expired" : upcoming ? "upcoming" : "live",
                status_label = expired ? "Expired" : upcoming ? "Upcoming" : "Live",
                is_live = !expired && !upcoming,
                days_remaining = expired || upcoming ? 0 : scheme.EndDate.DayNumber - today.DayNumber,
                summary = new
                {
                    scheme_retailers = retailerGroups.Count,
                    total_invoices = distinctInvoices.Count,
                    approved_invoices = items.Count(x => x.Status == "approved"),
                    pending_invoices = items.Count(x => x.Status is "pending" or "in_process"),
                    rejected_invoices = items.Count(x => x.Status == "rejected"),
                    total_invoice_amount = distinctInvoices.Sum(x => x.Amount),
                    approved_invoice_amount = approvedAmount,
                    expected_invoice_amount = pendingAmount,
                    points_earned = pointsEarned,
                    points_expected = pointsExpected
                },
                slabs = scheme.Slabs.Where(x => x.DeletedAt == null).OrderBy(x => x.ValueFrom).ThenBy(x => x.SortOrder)
                    .Select(x => new { tier_name = x.TierName, value_from = x.ValueFrom, value_to = x.ValueTo, reward_value = x.RewardValue }),
                retailers = retailerGroups
                    .Select(group => new
                    {
                        retailer_id = group.Key,
                        retailer_name = group.First().RetailerName,
                        shop_name = group.First().ShopName,
                        invoice_count = group.Count(),
                        invoice_amount = group.Sum(x => x.Amount),
                        points_earned = group.Where(x => x.Status == "approved").Sum(x => x.RewardAmount),
                        points_expected = group.Where(x => x.Status is "pending" or "in_process").Sum(x => x.ExpectedRewardAmount)
                    })
                    .OrderByDescending(x => x.invoice_amount)
                    .ToList(),
                recent_invoices = items.Take(10)
            }
        });
    }

    [Authorize]
    [HttpGet("dealer/retailers")]
    public async Task<IActionResult> DealerRetailers(
        [FromQuery] string? search,
        [FromQuery] int? page,
        [FromQuery(Name = "page_size")] int? pageSize,
        [FromQuery(Name = "include_metrics")] bool? includeMetrics,
        CancellationToken cancellationToken)
    {
        var dealer = await CurrentDealer(cancellationToken);
        if (dealer.Result is not null) return dealer.Result;

        // Customer-to-distributor assignments are stored in the legacy JSON
        // column. Materialize that expensive scope once, then perform search,
        // sorting and pagination in memory. Previously this endpoint executed
        // the same JSON LIKE scan four or five times per request.
        var assignedRetailers = await DealerAssignedRetailers(dealer.Customer!.Id)
            .ToListAsync(cancellationToken);
        var shouldIncludeMetrics = includeMetrics ?? (page.HasValue || pageSize.HasValue);
        var totalRetailers = assignedRetailers.Count;
        // The Retailers screen shows KYC coverage for every assigned retailer,
        // including retailers who have not uploaded an invoice yet.
        var pendingKycRetailers = shouldIncludeMetrics
            ? assignedRetailers.Count(retailer =>
                !string.Equals(KycStatusValue(ReadFields(retailer)), "approved", StringComparison.OrdinalIgnoreCase))
            : 0;
        var activeRetailers = shouldIncludeMetrics
            ? await CountActiveRetailers(assignedRetailers.Select(x => x.Id), cancellationToken)
            : 0;

        IEnumerable<Customer> filteredRetailers = assignedRetailers;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            filteredRetailers = filteredRetailers.Where(x => RetailerMatchesSearch(x, term));
        }

        var orderedRetailers = filteredRetailers
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Id)
            .ToList();
        var filteredTotal = orderedRetailers.Count;
        var requestedPage = Math.Max(1, page ?? 1);
        var requestedPageSize = Math.Clamp(pageSize ?? 200, 1, 200);
        var retailers = page.HasValue || pageSize.HasValue
            ? orderedRetailers.Skip((requestedPage - 1) * requestedPageSize).Take(requestedPageSize).ToList()
            : orderedRetailers.Take(requestedPageSize).ToList();
        var retailerIds = retailers.Select(x => x.Id).ToArray();
        var pageInvoices = shouldIncludeMetrics && retailerIds.Length > 0
            ? (await _invoiceRepository.GetInvoicesAsync(new NewInvoiceFilterDto
            {
                SecondaryCustomerIds = retailerIds,
                Unpaged = true
            }, null, cancellationToken)).Items
            : [];
        var retailerInvoiceSummary = BuildInvoiceListItems(pageInvoices, showIntermediateAsInProcess: true)
            .GroupBy(x => x.RetailerId)
            .ToDictionary(
                x => x.Key,
                x => new
                {
                    InvoiceCount = x.Count(),
                    RewardPoints = x.Where(i => i.Status == "approved").Sum(i => i.RewardAmount)
                });
        return Ok(new
        {
            status = "success",
            data = retailers.Select(x =>
            {
                var fields = ReadFields(x);
                var kycStatus = KycStatusValue(fields);
                var invoiceSummary = retailerInvoiceSummary.GetValueOrDefault(x.Id);
                return new
                {
                    id = x.Id,
                    code = x.CustomerCode,
                    name = DisplayName(x),
                    owner_name = FirstField(fields, "owner_name", "contact_person", "proprietor_name") ?? x.Name,
                    shop_name = FirstField(fields, "shop_name", "firm_name") ?? x.Name,
                    mobile = x.Mobile ?? x.ContactNumber ?? FirstField(fields, "mobile_number", "mobile_numbers"),
                    beat_name = FirstField(fields, "beat_name", "beat_route", "beat") ?? string.Empty,
                    kyc_status = kycStatus,
                    kyc_status_label = string.Equals(kycStatus, "approved", StringComparison.OrdinalIgnoreCase) ? "Verified" : "Pending",
                    reward_points = invoiceSummary?.RewardPoints ?? 0,
                    invoice_count = invoiceSummary?.InvoiceCount ?? 0,
                    is_active = invoiceSummary is not null
                };
            }),
            summary = new
            {
                total_retailers = totalRetailers,
                active_retailers = activeRetailers,
                pending_kyc_retailers = pendingKycRetailers
            },
            pagination = new
            {
                page = requestedPage,
                page_size = requestedPageSize,
                total = filteredTotal
            }
        });
    }

    [Authorize]
    [HttpGet("dealer/invoices")]
    public async Task<IActionResult> DealerInvoices([FromQuery] MobileInvoiceFilter filter, CancellationToken cancellationToken)
    {
        var dealer = await CurrentDealer(cancellationToken);
        if (dealer.Result is not null) return dealer.Result;
        var (fromDate, toDate) = DateRange(filter.FromDate, filter.ToDate);
        var invoices = (await _invoiceRepository.GetInvoicesAsync(new NewInvoiceFilterDto
        {
            DistributorCustomerId = dealer.Customer!.Id,
            Search = filter.Search,
            FromDate = fromDate,
            ToDate = toDate,
            Unpaged = true
        }, null, cancellationToken)).Items;
        if (!string.IsNullOrWhiteSpace(filter.Status)) invoices = invoices.Where(x => DealerInvoiceStatusMatches(x, filter.Status)).ToList();

        var allItems = BuildInvoiceListItems(invoices, showIntermediateAsInProcess: true);
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 50);
        var items = allItems.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Ok(new
        {
            status = "success",
            summary = BuildInvoiceListSummary(allItems),
            items,
            groups = BuildInvoiceMonthGroups(items),
            pagination = new { page, page_size = pageSize, total = allItems.Count }
        });
    }

    [Authorize]
    [HttpGet("dealer/invoices/{id:long}")]
    public async Task<IActionResult> DealerInvoice(ulong id, CancellationToken cancellationToken)
    {
        var dealer = await CurrentDealer(cancellationToken);
        if (dealer.Result is not null) return dealer.Result;

        var invoice = await _invoiceRepository.GetInvoiceAsync(id, null, cancellationToken);
        if (invoice is null || !await DealerAssignedRetailers(dealer.Customer!.Id).AnyAsync(x => x.Id == invoice.SecondaryCustomerId, cancellationToken))
            return NotFound(new { status = "error", message = "Invoice not found." });

        return Ok(new { status = "success", data = BuildInvoiceListItems([invoice], showIntermediateAsInProcess: true).Single() });
    }

    [Authorize]
    [HttpGet("dealer/invoice-schemes")]
    public async Task<IActionResult> DealerInvoiceSchemes([FromQuery(Name = "retailer_id")] ulong retailerId, [FromQuery(Name = "invoice_date")] DateTime? invoiceDate, CancellationToken cancellationToken)
    {
        var dealer = await CurrentDealer(cancellationToken);
        if (dealer.Result is not null) return dealer.Result;
        if (!await DealerAssignedRetailers(dealer.Customer!.Id).AnyAsync(x => x.Id == retailerId, cancellationToken))
            return NotFound(new { status = "error", message = "Assigned retailer not found." });
        IReadOnlyCollection<InvoiceSchemeOptionDto> schemes = !invoiceDate.HasValue
            ? Array.Empty<InvoiceSchemeOptionDto>()
            : await _invoiceRepository.GetEligibleSchemeOptionsAsync(retailerId, invoiceDate.Value, cancellationToken);
        return Ok(new { status = "success", data = schemes });
    }

    [Authorize]
    [HttpPost("dealer/invoices")]
    [RequestSizeLimit(15_000_000)]
    public async Task<IActionResult> CreateDealerInvoice([FromForm] DealerInvoiceForm form, CancellationToken cancellationToken)
    {
        var dealer = await CurrentDealer(cancellationToken);
        if (dealer.Result is not null) return dealer.Result;
        var retailer = await DealerAssignedRetailers(dealer.Customer!.Id).FirstOrDefaultAsync(x => x.Id == form.RetailerId, cancellationToken);
        if (retailer is null) return UnprocessableEntity(new { status = "error", message = "Only a retailer assigned to this dealer can be selected." });
        if (form.Attachment is null || form.Attachment.Length == 0)
            return UnprocessableEntity(new { status = "error", message = "Invoice attachment is required." });

        var creatorUserId = await _dbContext.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(x => x.CustomerId == dealer.Customer.Id && x.DeletedAt == null)
            .Select(x => (ulong?)x.Id).FirstOrDefaultAsync(cancellationToken);
        if (!creatorUserId.HasValue)
        {
            var fields = ReadFields(retailer);
            var employeeValue = FirstField(fields, "employee_id", "sales_executive_id")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (ulong.TryParse(employeeValue, out var employeeId)) creatorUserId = employeeId;
            creatorUserId ??= retailer.ExecutiveId;
        }
        if (!creatorUserId.HasValue || !await _dbContext.Users.AsNoTracking().IgnoreQueryFilters().AnyAsync(x => x.Id == creatorUserId.Value, cancellationToken))
            return UnprocessableEntity(new { status = "error", message = "No valid sales employee is linked with this dealer/retailer. Please contact admin." });

        var attachment = await SaveFileAsync(form.Attachment, "new-invoices", cancellationToken);
        var response = await WithoutOrphanUploadAsync(attachment, () => _newInvoiceService.CreateInvoiceAsync(new NewInvoiceRequestDto
        {
            SecondaryCustomerId = retailer.Id,
            SchemeId = form.SchemeId,
            InvoiceNumber = form.InvoiceNumber,
            InvoiceDate = form.InvoiceDate,
            Amount = form.Amount,
            Points = 0,
            Attachment = attachment
        }, creatorUserId, cancellationToken));
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [Authorize]
    [HttpPost("dealer/invoices/{id:long}")]
    [HttpPut("dealer/invoices/{id:long}")]
    [RequestSizeLimit(15_000_000)]
    public async Task<IActionResult> UpdateDealerInvoice(ulong id, [FromForm] DealerInvoiceForm form, CancellationToken cancellationToken)
    {
        var dealer = await CurrentDealer(cancellationToken);
        if (dealer.Result is not null) return dealer.Result;

        var existing = await _invoiceRepository.GetInvoiceAsync(id, null, cancellationToken);
        if (existing is null || !await DealerAssignedRetailers(dealer.Customer!.Id).AnyAsync(x => x.Id == existing.SecondaryCustomerId, cancellationToken))
            return NotFound(new { status = "error", message = "Invoice not found." });
        if (existing.ApprovalStatus is not (NewInvoice.StatusPending or NewInvoice.StatusHold))
            return StatusCode(StatusCodes.Status403Forbidden, new { status = "error", message = "Only a pending or held invoice can be edited." });

        var retailer = await DealerAssignedRetailers(dealer.Customer.Id).FirstOrDefaultAsync(x => x.Id == form.RetailerId, cancellationToken);
        if (retailer is null) return UnprocessableEntity(new { status = "error", message = "Only a retailer assigned to this dealer can be selected." });

        var uploaded = form.Attachment is { Length: > 0 }
            ? await SaveFileAsync(form.Attachment, "new-invoices", cancellationToken)
            : null;
        var attachment = uploaded ?? existing.Attachment;
        var response = await WithoutOrphanUploadAsync(uploaded, () => _newInvoiceService.UpdateInvoiceAsync(id, new NewInvoiceRequestDto
        {
            SecondaryCustomerId = retailer.Id,
            SchemeId = form.SchemeId,
            InvoiceNumber = form.InvoiceNumber,
            InvoiceDate = form.InvoiceDate,
            Amount = form.Amount,
            Points = 0,
            Attachment = attachment
        }, existing.CreatedBy, cancellationToken));
        return Ok(response);
    }

    [Authorize]
    [HttpDelete("dealer/invoices/{id:long}")]
    public async Task<IActionResult> DeleteDealerInvoice(ulong id, CancellationToken cancellationToken)
    {
        var dealer = await CurrentDealer(cancellationToken);
        if (dealer.Result is not null) return dealer.Result;

        var existing = await _invoiceRepository.GetInvoiceAsync(id, null, cancellationToken);
        if (existing is null || !await DealerAssignedRetailers(dealer.Customer!.Id).AnyAsync(x => x.Id == existing.SecondaryCustomerId, cancellationToken))
            return NotFound(new { status = "error", message = "Invoice not found." });
        if (existing.ApprovalStatus != NewInvoice.StatusPending)
            return StatusCode(StatusCodes.Status403Forbidden, new { status = "error", message = "Only pending invoices can be deleted." });

        // A dealer never deletes past pending, and the stored file paths are of no
        // use to the app, so they are dropped from the reply.
        var response = await _newInvoiceService.DeleteInvoiceAsync(id, false, cancellationToken);
        response.Extra.Remove("removed_files");
        return Ok(response);
    }

    [Authorize]
    [HttpGet("wallets/slab")]
    public async Task<IActionResult> SlabWallet(CancellationToken cancellationToken) => Ok(await WalletResponse("Regular", cancellationToken));

    [Authorize]
    [HttpGet("wallets/booster")]
    public async Task<IActionResult> BoosterWallet(CancellationToken cancellationToken) => Ok(await WalletResponse("Booster", cancellationToken));

    [Authorize]
    [HttpGet("invoices")]
    public async Task<IActionResult> Invoices([FromQuery] MobileInvoiceFilter filter, CancellationToken cancellationToken)
    {
        var customer = await CurrentCustomer(cancellationToken);
        if (customer is null) return Unauthorized(new { status = "error", message = "Unauthenticated." });
        var invoices = await CustomerInvoices(customer.Id, cancellationToken);
        var (fromDate, toDate) = DateRange(filter.FromDate, filter.ToDate);

        if (!string.IsNullOrWhiteSpace(filter.Search)) invoices = invoices.Where(x => x.InvoiceNumber.Contains(filter.Search) || x.ShopName.Contains(filter.Search)).ToList();
        if (!string.IsNullOrWhiteSpace(filter.Status)) invoices = invoices.Where(x => InvoiceStatusMatches(x, filter.Status)).ToList();
        if (fromDate.HasValue) invoices = invoices.Where(x => x.InvoiceDate.Date >= fromDate.Value.Date).ToList();
        if (toDate.HasValue) invoices = invoices.Where(x => x.InvoiceDate.Date <= toDate.Value.Date).ToList();

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var invoiceItems = BuildInvoiceListItems(invoices);
        var total = invoiceItems.Count;
        var data = invoices.OrderByDescending(x => x.InvoiceDate).ThenByDescending(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var items = invoiceItems.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Ok(new
        {
            status = "success",
            summary = BuildInvoiceListSummary(invoiceItems),
            filter_options = new
            {
                search_placeholder = "Search invoice number",
                statuses = new[]
                {
                    new { key = "all", label = "All" },
                    new { key = "pending", label = "Pending" },
                    new { key = "hold", label = "Hold" },
                    new { key = "in_process", label = "In Process" },
                    new { key = "approved", label = "Approved" },
                    new { key = "rejected", label = "Rejected" }
                }
            },
            groups = BuildInvoiceMonthGroups(items),
            items,
            data,
            pagination = new { page, page_size = pageSize, total }
        });
    }

    [Authorize]
    [HttpGet("invoices/{id:long}")]
    public async Task<IActionResult> InvoiceDetails(ulong id, CancellationToken cancellationToken)
    {
        var customer = await CurrentCustomer(cancellationToken);
        if (customer is null) return Unauthorized(new { status = "error", message = "Unauthenticated." });

        var invoice = await _invoiceRepository.GetInvoiceAsync(id, null, cancellationToken);
        if (invoice is null || invoice.SecondaryCustomerId != customer.Id)
            return NotFound(new { status = "error", message = "Invoice not found." });

        return Ok(new
        {
            status = "success",
            data = invoice,
            status_key = InvoiceStatusKey(invoice.ApprovalStatus),
            is_pending = InvoiceStatusKey(invoice.ApprovalStatus) == "pending",
            is_approved = invoice.ApprovalStatus == NewInvoice.StatusApprovedHo,
            is_rejected = InvoiceStatusKey(invoice.ApprovalStatus) == "rejected"
        });
    }

    [Authorize]
    [HttpPost("redemptions/preview")]
    public async Task<IActionResult> RedemptionPreview([FromBody] MobileRedemptionRequest request, CancellationToken cancellationToken)
    {
        var customer = await CurrentCustomer(cancellationToken);
        if (customer is null) return Unauthorized(new { status = "error", message = "Unauthenticated." });
        if (!request.LoyaltySchemeId.HasValue) return BadRequest(new { status = "error", message = "Loyalty scheme is required." });

        var wallet = await BuildWallet(customer.Id, await CustomerInvoices(customer.Id, cancellationToken), cancellationToken);
        var selected = string.Equals(request.WalletType, "Booster", StringComparison.OrdinalIgnoreCase) ? wallet.Booster : wallet.Regular;
        var scheme = selected.Schemes.FirstOrDefault(x => x.SchemeId == request.LoyaltySchemeId);
        if (scheme is null)
        {
            return BadRequest(new { status = "error", message = "Selected scheme is not available in this wallet." });
        }

        var points = request.Points <= 0 ? scheme.AvailablePoints : request.Points;
        return Ok(new
        {
            status = "success",
            data = new
            {
                loyalty_scheme_id = scheme.SchemeId,
                scheme_name = scheme.SchemeName,
                redemption_enabled = scheme.RedemptionEnabled,
                wallet_type = selected.WalletType,
                available_points = scheme.AvailablePoints,
                requested_points = points,
                eligible = scheme.RedemptionEnabled && points > 0 && points <= scheme.AvailablePoints,
                bank_account = BankAccount(ReadFields(customer))
            }
        });
    }

    [Authorize]
    [HttpGet("redemptions/history")]
    public async Task<IActionResult> RedemptionHistory([FromQuery] MobileRedemptionHistoryFilter filter, CancellationToken cancellationToken)
    {
        var customer = await CurrentCustomer(cancellationToken);
        if (customer is null) return Unauthorized(new { status = "error", message = "Unauthenticated." });

        var query = _dbContext.LoyaltyRedemptions.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.CustomerId == customer.Id);
        var (fromDate, toDate) = DateRange(filter.FromDate, filter.ToDate);

        if (!string.IsNullOrWhiteSpace(filter.WalletType) && !string.Equals(filter.WalletType, "all", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(x => x.WalletType == NormalizeWalletType(filter.WalletType));
        }

        if (!string.IsNullOrWhiteSpace(filter.RedeemMode) && !string.Equals(filter.RedeemMode, "all", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(x => x.RedeemMode == NormalizeRedeemMode(filter.RedeemMode));
        }

        if (TryRedemptionStatus(filter.Status, out var status))
        {
            query = query.Where(x => x.Status == status);
        }

        if (fromDate.HasValue) query = query.Where(x => x.CreatedAt.HasValue && x.CreatedAt.Value.Date >= fromDate.Value.Date);
        if (toDate.HasValue) query = query.Where(x => x.CreatedAt.HasValue && x.CreatedAt.Value.Date <= toDate.Value.Date);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(x => x.TransactionNo.Contains(search)
                || x.SchemeName.Contains(search)
                || x.Points.ToString().Contains(search));
        }

        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        var items = rows.Select(ToMobileRedemptionHistoryItem).ToList();
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var total = items.Count;
        var pagedItems = items.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return Ok(new
        {
            status = "success",
            summary = BuildRedemptionHistorySummary(items),
            filter_options = new
            {
                search_placeholder = "Search transaction or scheme",
                wallets = new[]
                {
                    new { key = "all", label = "All Wallets" },
                    new { key = "Regular", label = "Regular Wallet" },
                    new { key = "Booster", label = "Booster Wallet" }
                },
                statuses = new[]
                {
                    new { key = "all", label = "All Status" },
                    new { key = "pending", label = "Pending" },
                    new { key = "approved", label = "Approved" },
                    new { key = "rejected", label = "Rejected" },
                    new { key = "hold", label = "Hold" }
                },
                modes = new[]
                {
                    new { key = "all", label = "All Modes" },
                    new { key = "NEFT", label = "NEFT" },
                    new { key = "IMPS", label = "IMPS" }
                }
            },
            groups = BuildRedemptionMonthGroups(pagedItems),
            items = pagedItems,
            data = pagedItems,
            pagination = new
            {
                page,
                page_size = pageSize,
                total,
                total_pages = total == 0 ? 0 : (int)Math.Ceiling(total / (decimal)pageSize),
                has_next = page * pageSize < total,
                has_previous = page > 1
            }
        });
    }

    [AllowAnonymous]
    [HttpGet("scheme/current")]
    public async Task<IActionResult> CurrentSchemes(CancellationToken cancellationToken) => Ok(new { status = "success", data = await LiveSchemes(null, cancellationToken) });

    [AllowAnonymous]
    [HttpGet("scheme/slabs")]
    public async Task<IActionResult> SlabSchemes(CancellationToken cancellationToken) => Ok(new { status = "success", data = await LiveSchemes("Regular", cancellationToken) });

    [AllowAnonymous]
    [HttpGet("scheme/boosters")]
    public async Task<IActionResult> BoosterSchemes(CancellationToken cancellationToken) => Ok(new { status = "success", data = await LiveSchemes("Booster", cancellationToken) });

    [Authorize]
    [HttpGet("profile")]
    public async Task<IActionResult> Profile(CancellationToken cancellationToken)
    {
        var customer = await CurrentCustomer(cancellationToken);
        return customer is null ? Unauthorized(new { status = "error", message = "Unauthenticated." }) : Ok(await ToMobileProfile(customer, cancellationToken));
    }

    [Authorize]
    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        var customer = await CurrentCustomer(cancellationToken);
        if (customer is null) return Unauthorized(new { status = "error", message = "Unauthenticated." });
        var fields = ReadFields(customer);
        var ownerName = FirstNonEmpty(request.OwnerName, request.Name, GetString(request.Extra, "owner_name"));
        var shopName = FirstNonEmpty(request.ShopName, request.FirmName, GetString(request.Extra, "shop_name"));
        SetIfPresent(fields, "owner_name", ownerName);
        SetIfPresent(fields, "shop_name", shopName);
        SetIfPresent(fields, "gst_number", GetString(request.Extra, "gst_number"));
        await ApplyMobileCustomerAddressFields(fields, request, cancellationToken);
        var assignedUserIds = await MobileAssignedUserIds(request, cancellationToken);
        SetMobileAssignmentFields(fields, assignedUserIds);
        customer.FirstName = ownerName ?? customer.FirstName;
        customer.Name = shopName ?? customer.Name;
        customer.Email = request.Email?.Trim().ToLowerInvariant() ?? customer.Email;
        if (assignedUserIds.Count > 0) customer.ExecutiveId = assignedUserIds.First();
        customer.CustomFields = JsonSerializer.Serialize(fields, JsonOptions);
        customer.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await SyncMobileCustomerAddressAsync(customer.Id, fields, cancellationToken);
        await SyncMobileEmployeeDetailsAsync(customer.Id, assignedUserIds, cancellationToken);
        return Ok(await ToMobileProfile(customer, cancellationToken));
    }

    [Authorize]
    [HttpGet("bank-accounts")]
    public async Task<IActionResult> BankAccounts(CancellationToken cancellationToken)
    {
        var customer = await CurrentCustomer(cancellationToken);
        return customer is null ? Unauthorized(new { status = "error", message = "Unauthenticated." }) : Ok(new { status = "success", data = new[] { BankAccount(ReadFields(customer)) } });
    }

    [Authorize]
    [HttpPost("bank-accounts")]
    public async Task<IActionResult> SaveBankAccount([FromBody] BankAccountRequest request, CancellationToken cancellationToken)
    {
        var customer = await CurrentCustomer(cancellationToken);
        if (customer is null) return Unauthorized(new { status = "error", message = "Unauthenticated." });
        var fields = ReadFields(customer);
        fields["account_holder_name"] = request.AccountHolderName ?? string.Empty;
        fields["bank_account_number"] = request.AccountNumber ?? string.Empty;
        fields["bank_name"] = request.BankName ?? string.Empty;
        fields["ifsc_code"] = request.IfscCode ?? string.Empty;
        customer.CustomFields = JsonSerializer.Serialize(fields, JsonOptions);
        customer.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { status = "success", message = "Bank account saved successfully.", data = BankAccount(fields) });
    }

    private IQueryable<Customer> FindMobileCustomer(string mobile) =>
        _dbContext.Customers.Where(x => x.Active == "Y" && (x.CustomerType == DealerType || x.CustomerType == RetailerType || x.CustomerType == InfluencerType) && (x.Mobile == mobile || x.ContactNumber == mobile || (x.CustomFields != null && x.CustomFields.Contains(mobile))));

    private Task<bool> EmailInUseAsync(string email, ulong? exceptCustomerId, CancellationToken cancellationToken) =>
        _dbContext.Customers.IgnoreQueryFilters().AnyAsync(
            x => x.Email != null && x.Email.ToLower() == email && (!exceptCustomerId.HasValue || x.Id != exceptCustomerId.Value),
            cancellationToken);

    private async Task<PasswordCodeDelivery> SendPasswordCodeAsync(Customer customer, string subject, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customer.Email)) throw new InvalidOperationException("Customer email is required.");

        var code = Random.Shared.Next(100000, 999999).ToString(CultureInfo.InvariantCulture);
        customer.Otp = code;
        customer.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (MailBypassEnabled())
        {
            return new PasswordCodeDelivery(code, true);
        }

        await _emailSender.SendAsync(customer.Email, subject, $"Your KSB Loyalty password verification code is {code}. This code expires in 15 minutes.", cancellationToken);
        return new PasswordCodeDelivery(code, false);
    }

    private bool MailBypassEnabled()
    {
        var value = Environment.GetEnvironmentVariable("MAIL_BYPASS_ENABLED")
            ?? _configuration["Mail:BypassEnabled"]
            ?? "true";
        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PasswordCodeDelivery(string Code, bool Bypassed);

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var normalized = email.Trim().ToLowerInvariant();
        try
        {
            var parsed = new MailAddress(normalized);
            return parsed.Address == normalized ? normalized : null;
        }
        catch
        {
            return null;
        }
    }

    private static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return string.Empty;
        var parts = email.Split('@', 2);
        if (parts.Length != 2) return email;
        var visible = parts[0].Length <= 2 ? parts[0][..1] : parts[0][..2];
        return $"{visible}{new string('*', Math.Max(3, parts[0].Length - visible.Length))}@{parts[1]}";
    }

    private async Task<Customer?> CurrentCustomer(CancellationToken cancellationToken)
    {
        if (!string.Equals(User.FindFirstValue("provider"), "customers", StringComparison.OrdinalIgnoreCase)) return null;
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return ulong.TryParse(subject, out var id) ? await _dbContext.Customers.FirstOrDefaultAsync(x => x.Id == id, cancellationToken) : null;
    }

    private async Task<(Customer? Customer, IActionResult? Result)> CurrentDealer(CancellationToken cancellationToken)
    {
        var customer = await CurrentCustomer(cancellationToken);
        if (customer is null) return (null, Unauthorized(new { status = "error", message = "Unauthenticated." }));
        if (customer.CustomerType != DealerType)
            return (null, StatusCode(StatusCodes.Status403Forbidden, new { status = "error", message = "This feature is available only for dealer/distributor accounts." }));
        return (customer, null);
    }

    private IQueryable<Customer> DealerAssignedRetailers(ulong dealerId)
    {
        // The legacy implementation used six leading-wildcard LIKE predicates
        // against the JSON document. Besides scanning the entire customer table,
        // it parsed the same assignment in several textual formats. JSON_VALUE
        // handles both JSON strings and numbers and reduces the predicate to the
        // two actual assignment fields. FromSqlInterpolated keeps dealerId safely
        // parameterized; EF still applies the Customer soft-delete query filter.
        var dealerValue = dealerId.ToString(CultureInfo.InvariantCulture);
        return _dbContext.Customers
            .FromSqlInterpolated($@"
                SELECT *
                FROM customers
                WHERE active = 'Y'
                  AND customertype = {RetailerType}
                  AND ISJSON(custom_fields) = 1
                  AND (
                       JSON_VALUE(custom_fields, '$.distributor_name') = {dealerValue}
                    OR JSON_VALUE(custom_fields, '$.agri_distributor') = {dealerValue}
                  )")
            .AsNoTracking();
    }

    private async Task<int> CountActiveRetailers(IEnumerable<ulong> retailerIds, CancellationToken cancellationToken)
    {
        // SQL Server has a 2,100 parameter limit. Chunking keeps this safe even
        // for distributors with unusually large retailer networks, while the
        // secondary_customer_id index makes every lookup inexpensive.
        var activeRetailerIds = new HashSet<ulong>();
        foreach (var idChunk in retailerIds.Distinct().Chunk(1000))
        {
            var ids = idChunk.ToArray();
            var activeIds = await _dbContext.NewInvoices.AsNoTracking()
                .Where(invoice => ids.Contains(invoice.SecondaryCustomerId))
                .Select(invoice => invoice.SecondaryCustomerId)
                .Distinct()
                .ToListAsync(cancellationToken);
            activeRetailerIds.UnionWith(activeIds);
        }

        return activeRetailerIds.Count;
    }

    private static bool RetailerMatchesSearch(Customer retailer, string term) =>
        ContainsIgnoreCase(retailer.Name, term)
        || ContainsIgnoreCase(retailer.CustomerCode, term)
        || ContainsIgnoreCase(retailer.Mobile, term)
        || ContainsIgnoreCase(retailer.CustomFields, term);

    private static bool ContainsIgnoreCase(string? value, string term) =>
        !string.IsNullOrEmpty(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyCollection<NewInvoiceDto>> CustomerInvoices(ulong customerId, CancellationToken cancellationToken)
    {
        var invoices = (await _invoiceRepository.GetInvoicesAsync(new NewInvoiceFilterDto { Unpaged = true }, null, cancellationToken)).Items;
        return invoices.Where(x => x.SecondaryCustomerId == customerId).ToList();
    }

    private (DateTime? FromDate, DateTime? ToDate) DateRange(DateTime? fromDate, DateTime? toDate) =>
        (fromDate ?? QueryDate("from_date", "date_from", "start_date"),
            toDate ?? QueryDate("to_date", "date_to", "end_date"));

    private DateTime? QueryDate(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!Request.Query.TryGetValue(key, out var values)) continue;
            var value = values.FirstOrDefault();
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return parsed;
        }

        return null;
    }

    private static IReadOnlyCollection<MobileInvoiceListItemDto> BuildInvoiceListItems(
        IReadOnlyCollection<NewInvoiceDto> invoices,
        bool showIntermediateAsInProcess = false)
    {
        return invoices
            .GroupBy(x => x.Id)
            .Select(group =>
            {
                var invoice = group.OrderByDescending(x => x.SchemePoints).First();
                var rewardAmount = invoice.ApprovalStatus == NewInvoice.StatusApprovedHo ? group.Sum(x => x.SchemePoints) : 0;
                var expectedRewardAmount = invoice.ApprovalStatus == NewInvoice.StatusApprovedHo
                    ? rewardAmount
                    : invoice.ApprovalStatus == NewInvoice.StatusRejected
                        ? 0
                        : group.Sum(x => x.ExpectedSchemePoints);
                var displayDate = invoice.InvoiceDate;
                var statusKey = showIntermediateAsInProcess
                    ? DealerInvoiceStatusKey(invoice.ApprovalStatus)
                    : InvoiceStatusKey(invoice.ApprovalStatus);
                return new MobileInvoiceListItemDto
                {
                    Id = invoice.Id,
                    RetailerName = !string.IsNullOrWhiteSpace(invoice.ShopName) ? invoice.ShopName : invoice.CustomerName,
                    OwnerName = invoice.CustomerName,
                    ShopName = invoice.ShopName,
                    RetailerCode = invoice.RetailerCode,
                    MobileNumber = invoice.MobileNumber,
                    InvoiceNumber = invoice.InvoiceNumber,
                    InvoiceNumberDisplay = $"#{invoice.InvoiceNumber.TrimStart('#')}",
                    InvoiceDate = invoice.InvoiceDate,
                    // An invoice carries a date only, so the stored time is always midnight.
                    // Formatting it with a clock produced a meaningless "12:00 AM" on every row.
                    DisplayDate = displayDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
                    MonthKey = invoice.InvoiceDate.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    MonthLabel = invoice.InvoiceDate.ToString("MMMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant(),
                    Amount = invoice.Amount,
                    AmountDisplay = FormatIndianCurrency(invoice.Amount),
                    RewardAmount = rewardAmount,
                    RewardDisplay = rewardAmount > 0 ? $"+{FormatIndianCurrency(rewardAmount)}" : null,
                    ExpectedRewardAmount = expectedRewardAmount,
                    ExpectedRewardDisplay = expectedRewardAmount > 0 ? $"+{FormatIndianCurrency(expectedRewardAmount)}" : null,
                    RewardLabel = rewardAmount > 0
                        ? "Reward credited"
                        : statusKey switch
                        {
                            "pending" => "Awaiting Approval",
                            "hold" => "On hold, correction needed",
                            "in_process" => "Approval In Process",
                            _ => "No reward earned"
                        },
                    Status = statusKey,
                    StatusLabel = statusKey switch
                    {
                        "approved" => "Approved",
                        "rejected" => "Rejected",
                        "in_process" => "In Process",
                        "hold" => "Hold",
                        _ => "Pending"
                    },
                    IsRewardCredited = rewardAmount > 0,
                    IsPending = statusKey == "pending",
                    CanEdit = invoice.ApprovalStatus is NewInvoice.StatusPending or NewInvoice.StatusHold,
                    CanDelete = invoice.ApprovalStatus == NewInvoice.StatusPending,
                    RetailerId = invoice.SecondaryCustomerId,
                    SchemeId = invoice.SchemeId,
                    Attachment = invoice.Attachment,
                    SchemeName = invoice.SchemeName,
                    SchemeNames = group.Where(x => !string.IsNullOrWhiteSpace(x.SchemeName)).Select(x => x.SchemeName!).Distinct().ToArray()
                };
            })
            .OrderByDescending(x => x.InvoiceDate)
            .ThenByDescending(x => x.Id)
            .ToList();
    }

    private static object BuildInvoiceListSummary(IReadOnlyCollection<MobileInvoiceListItemDto> items) => new
    {
        total_invoices = items.Count,
        rewards_credited = items.Sum(x => x.RewardAmount),
        rewards_credited_display = FormatIndianCurrency(items.Sum(x => x.RewardAmount)),
        approved_invoices = items.Count(x => x.Status == "approved"),
        pending_invoices = items.Count(x => x.Status == "pending"),
        rejected_invoices = items.Count(x => x.Status == "rejected"),
        total_turnover = items.Sum(x => x.Amount),
        total_turnover_display = FormatIndianCurrency(items.Sum(x => x.Amount))
    };

    private static IReadOnlyCollection<MobileInvoiceMonthGroupDto> BuildInvoiceMonthGroups(IReadOnlyCollection<MobileInvoiceListItemDto> items)
    {
        return items
            .GroupBy(x => new { x.MonthKey, x.MonthLabel })
            .Select(group => new MobileInvoiceMonthGroupDto
            {
                MonthKey = group.Key.MonthKey,
                MonthLabel = group.Key.MonthLabel,
                Count = group.Count(),
                Turnover = group.Sum(x => x.Amount),
                TurnoverDisplay = FormatIndianCurrency(group.Sum(x => x.Amount)),
                RewardAmount = group.Sum(x => x.RewardAmount),
                RewardDisplay = group.Sum(x => x.RewardAmount) > 0 ? $"+{FormatIndianCurrency(group.Sum(x => x.RewardAmount))}" : null,
                Items = group.ToList()
            })
            .OrderByDescending(x => x.MonthKey)
            .ToList();
    }

    private static MobileRedemptionHistoryItemDto ToMobileRedemptionHistoryItem(LoyaltyRedemption redemption)
    {
        var createdAt = redemption.CreatedAt ?? DateTime.UtcNow;
        return new MobileRedemptionHistoryItemDto
        {
            Id = redemption.Id,
            TransactionNo = redemption.TransactionNo,
            TransactionNoDisplay = string.IsNullOrWhiteSpace(redemption.TransactionNo) ? $"#{redemption.Id}" : redemption.TransactionNo,
            LoyaltySchemeId = redemption.LoyaltySchemeId,
            SchemeName = redemption.SchemeName,
            WalletType = NormalizeWalletType(redemption.WalletType),
            RedeemMode = NormalizeRedeemMode(redemption.RedeemMode),
            Points = redemption.Points,
            PointsDisplay = $"{redemption.Points:0.##}",
            AccountHolder = redemption.AccountHolder,
            MaskedAccountNumber = Mask(redemption.AccountNumber),
            BankName = redemption.BankName,
            IfscCode = redemption.IfscCode,
            Status = RedemptionStatusKey(redemption.Status),
            StatusLabel = RedemptionStatusLabel(redemption.Status),
            Remark = redemption.Remark,
            CreatedAt = redemption.CreatedAt,
            DisplayDate = createdAt.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            MonthKey = createdAt.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            MonthLabel = createdAt.ToString("MMMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant()
        };
    }

    private static object BuildRedemptionHistorySummary(IReadOnlyCollection<MobileRedemptionHistoryItemDto> items) => new
    {
        total_redemptions = items.Count,
        total_points = items.Sum(x => x.Points),
        pending_points = items.Where(x => x.Status == "pending").Sum(x => x.Points),
        approved_points = items.Where(x => x.Status == "approved").Sum(x => x.Points),
        rejected_points = items.Where(x => x.Status == "rejected").Sum(x => x.Points),
        hold_points = items.Where(x => x.Status == "hold").Sum(x => x.Points),
        regular_points = items.Where(x => x.WalletType == "Regular").Sum(x => x.Points),
        booster_points = items.Where(x => x.WalletType == "Booster").Sum(x => x.Points),
        pending_count = items.Count(x => x.Status == "pending"),
        approved_count = items.Count(x => x.Status == "approved"),
        rejected_count = items.Count(x => x.Status == "rejected"),
        hold_count = items.Count(x => x.Status == "hold")
    };

    private static IReadOnlyCollection<MobileRedemptionMonthGroupDto> BuildRedemptionMonthGroups(IReadOnlyCollection<MobileRedemptionHistoryItemDto> items)
    {
        return items
            .GroupBy(x => new { x.MonthKey, x.MonthLabel })
            .Select(group => new MobileRedemptionMonthGroupDto
            {
                MonthKey = group.Key.MonthKey,
                MonthLabel = group.Key.MonthLabel,
                Count = group.Count(),
                TotalPoints = group.Sum(x => x.Points),
                Items = group.ToList()
            })
            .OrderByDescending(x => x.MonthKey)
            .ToList();
    }

    private static IReadOnlyCollection<MobileWalletSchemeBalanceDto> ToMobileSchemeBalances(WalletDto wallet) =>
        wallet.Schemes
            .Where(x => x.AvailablePoints > 0)
            .Select(x => new MobileWalletSchemeBalanceDto
            {
                LoyaltySchemeId = x.SchemeId,
                SchemeName = x.SchemeName,
                RedemptionEnabled = x.RedemptionEnabled,
                AvailablePoints = x.AvailablePoints,
                WalletType = wallet.WalletType
            })
            .ToList();

    private async Task<object> WalletResponse(string walletType, CancellationToken cancellationToken)
    {
        var customer = await CurrentCustomer(cancellationToken);
        if (customer is null) return new { status = "error", message = "Unauthenticated." };
        var wallet = await BuildWallet(customer.Id, await CustomerInvoices(customer.Id, cancellationToken), cancellationToken);
        var selected = walletType == "Booster" ? wallet.Booster : wallet.Regular;
        return new { status = "success", data = selected };
    }

    private async Task<WalletPair> BuildWallet(ulong customerId, IReadOnlyCollection<NewInvoiceDto> invoices, CancellationToken cancellationToken)
    {
        var redemptions = await _dbContext.LoyaltyRedemptions.AsNoTracking()
            .Where(x => x.CustomerId == customerId && x.DeletedAt == null && (x.Status == LoyaltyRedemption.StatusPending || x.Status == LoyaltyRedemption.StatusApproved))
            .ToListAsync(cancellationToken);
        var schemeIds = invoices.Where(x => x.SchemeId.HasValue).Select(x => x.SchemeId!.Value).Distinct().ToArray();
        // A scheme that was deleted, deactivated or pulled back from Published must not
        // keep redemption switched on for points already earned under it.
        var redemptionSettings = await _dbContext.LoyaltySchemes.AsNoTracking()
            .Where(x => schemeIds.Contains(x.Id)
                && x.DeletedAt == null
                && x.Active == "Y"
                && (x.Status == "Published" || x.Status == "Live"))
            .ToDictionaryAsync(x => x.Id, x => x.RedemptionEnabled, cancellationToken);

        return new WalletPair(
            BuildWallet("Regular", invoices, redemptions, redemptionSettings),
            BuildWallet("Booster", invoices, redemptions, redemptionSettings));
    }

    private static WalletDto BuildWallet(
        string walletType,
        IReadOnlyCollection<NewInvoiceDto> invoices,
        IReadOnlyCollection<LoyaltyRedemption> redemptions,
        IReadOnlyDictionary<ulong, bool> redemptionSettings)
    {
        var schemeRows = invoices
            .Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo && x.SchemeId.HasValue && x.SchemePoints > 0)
            .Where(x => walletType == "Booster" ? IsBooster(x.SchemeTag) : !IsBooster(x.SchemeTag))
            .GroupBy(x => new { x.SchemeId, x.SchemeName })
            .Select(x =>
            {
                var redeemed = redemptions.Where(r => r.LoyaltySchemeId == x.Key.SchemeId && string.Equals(r.WalletType, walletType, StringComparison.OrdinalIgnoreCase)).Sum(r => r.Points);
                var earned = x.Sum(i => i.SchemePoints);
                var redemptionEnabled = x.Key.SchemeId.HasValue
                    && redemptionSettings.GetValueOrDefault(x.Key.SchemeId.Value, false);
                return new WalletSchemeDto(x.Key.SchemeId, x.Key.SchemeName ?? "Scheme", redemptionEnabled, earned, redeemed, Math.Max(0, earned - redeemed));
            })
            .ToList();

        return new WalletDto(walletType, schemeRows.Sum(x => x.EarnedPoints), schemeRows.Sum(x => x.RedeemedPoints), schemeRows.Sum(x => x.AvailablePoints), schemeRows);
    }

    private async Task<IReadOnlyCollection<DashboardWalletCardDto>> BuildDashboardWalletCards(Customer customer, WalletPair wallet, IReadOnlyCollection<NewInvoiceDto> invoices, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var schemes = await _dbContext.LoyaltySchemes.AsNoTracking()
            .Include(x => x.Slabs)
            .Where(x => x.DeletedAt == null
                && x.Active == "Y"
                && (x.Status == "Published" || x.Status == "Live")
                && x.SchemeType == "Invoice"
                && x.StartDate <= today
                && x.EndDate >= today)
            .ToListAsync(cancellationToken);
        var dashboardAudience = await BuildSchemeAudienceAsync(customer, cancellationToken);

        var regularScheme = schemes
            .Where(x => !IsBooster(x.SchemeTag) && SchemeEligibility.Matches(x, today, dashboardAudience))
            .OrderBy(x => x.EndDate)
            .ThenBy(x => x.SchemeName)
            .FirstOrDefault();

        var boosterScheme = schemes
            .Where(x => IsBooster(x.SchemeTag) && SchemeEligibility.Matches(x, today, dashboardAudience))
            .OrderBy(x => x.EndDate)
            .ThenBy(x => x.SchemeName)
            .FirstOrDefault();

        return
        [
            BuildRegularWalletCard(regularScheme, wallet.Regular, invoices, today),
            BuildBoosterWalletCard(boosterScheme, wallet.Booster, invoices, today)
        ];
    }

    private static DashboardWalletCardDto BuildRegularWalletCard(LoyaltyScheme? scheme, WalletDto wallet, IReadOnlyCollection<NewInvoiceDto> invoices, DateOnly today)
    {
        var approvedInvoices = invoices
            .Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .ToList();

        if (scheme is null)
        {
            return new DashboardWalletCardDto
            {
                Key = "slab",
                Title = "Slab Wallet",
                WalletType = "Regular",
                Points = wallet.AvailablePoints,
                AvailablePoints = wallet.AvailablePoints,
                Schemes = ToMobileSchemeBalances(wallet),
                IsActive = false,
                ExpiryLabel = "No active scheme",
                NextMessage = "No active slab scheme available.",
                DaysLeftMessage = "No active slab scheme available."
            };
        }

        var start = scheme.StartDate.ToDateTime(TimeOnly.MinValue);
        var end = scheme.EndDate.ToDateTime(TimeOnly.MaxValue);
        var invoiceAmount = approvedInvoices
            .Where(x => x.InvoiceDate >= start && x.InvoiceDate <= end)
            .Sum(x => x.HoApprovedAmount ?? x.Amount);
        var slabs = scheme.Slabs
            .OrderBy(x => x.ValueFrom)
            .ThenBy(x => x.SortOrder)
            .ToList();
        var achieved = slabs.LastOrDefault(x => invoiceAmount >= x.ValueFrom && (!x.ValueTo.HasValue || invoiceAmount <= x.ValueTo.Value))
            ?? slabs.LastOrDefault(x => invoiceAmount >= x.ValueFrom);
        var next = slabs.FirstOrDefault(x => invoiceAmount < x.ValueFrom);
        var daysLeft = Math.Max(0, (scheme.EndDate.ToDateTime(TimeOnly.MinValue).Date - DateTime.UtcNow.Date).Days);
        var amountMore = next is null ? 0 : Math.Max(0, next.ValueFrom - invoiceAmount);

        return new DashboardWalletCardDto
        {
            Key = "slab",
            Title = "Slab Wallet",
            WalletType = "Regular",
            SchemeId = scheme.Id,
            SchemeName = scheme.SchemeName,
            SchemeCode = scheme.SchemeCode,
            SchemeTag = scheme.SchemeTag,
            BasedOn = scheme.BasedOn,
            RedemptionEnabled = scheme.RedemptionEnabled,
            Points = wallet.AvailablePoints,
            AvailablePoints = wallet.AvailablePoints,
            EarnedPoints = wallet.EarnedPoints,
            RedeemedPoints = wallet.RedeemedPoints,
            Schemes = ToMobileSchemeBalances(wallet),
            InvoiceAmount = invoiceAmount,
            InvoiceAmountShort = FormatIndianShortAmount(invoiceAmount),
            AchievedReward = achieved?.RewardValue ?? 0,
            AchievedLabel = achieved is null ? "0" : FormatReward(achieved.RewardValue, scheme.BasedOn),
            AchievedTierName = achieved?.TierName,
            NextReward = next?.RewardValue,
            NextRewardLabel = next is null ? null : FormatReward(next.RewardValue, scheme.BasedOn),
            NextTierName = next?.TierName,
            AmountMoreForNextSlab = amountMore,
            NextMessage = next is null
                ? "Highest slab achieved."
                : $"{FormatIndianCurrency(amountMore)} more for {FormatReward(next.RewardValue, scheme.BasedOn)} slab",
            DaysLeft = daysLeft,
            DaysLeftMessage = daysLeft == 1 ? "You have 1 day left to reach it" : $"You have {daysLeft} days left to reach it",
            StartDate = scheme.StartDate,
            EndDate = scheme.EndDate,
            ExpiresOn = scheme.EndDate.ToString("dd MMM"),
            ExpiryLabel = $"Expires {scheme.EndDate:dd MMM} · Quarterly",
            BadgeText = daysLeft == 1 ? "1D LEFT" : $"{daysLeft}D LEFT",
            IsActive = true,
            ProgressSteps = slabs.Select(slab => new DashboardSlabStepDto
            {
                Id = slab.Id,
                TierName = slab.TierName,
                ValueFrom = slab.ValueFrom,
                ValueTo = slab.ValueTo,
                RewardValue = slab.RewardValue,
                RewardLabel = FormatReward(slab.RewardValue, scheme.BasedOn),
                Achieved = invoiceAmount >= slab.ValueFrom,
                Current = achieved?.Id == slab.Id
            }).ToList(),
            ProgressIndex = achieved is null ? -1 : slabs.FindIndex(x => x.Id == achieved.Id),
            ProgressPercent = slabs.Count == 0 ? 0 : Math.Clamp((decimal)(achieved is null ? 0 : slabs.FindIndex(x => x.Id == achieved.Id) + 1) / slabs.Count * 100, 0, 100)
        };
    }

    private static DashboardWalletCardDto BuildBoosterWalletCard(LoyaltyScheme? scheme, WalletDto wallet, IReadOnlyCollection<NewInvoiceDto> invoices, DateOnly today)
    {
        var approvedInvoices = invoices
            .Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo)
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .ToList();
        var invoiceAmount = approvedInvoices.Sum(x => x.HoApprovedAmount ?? x.Amount);

        return new DashboardWalletCardDto
        {
            Key = "booster",
            Title = "Booster Wallet",
            WalletType = "Booster",
            SchemeId = scheme?.Id,
            SchemeName = scheme?.SchemeName,
            SchemeCode = scheme?.SchemeCode,
            SchemeTag = scheme?.SchemeTag ?? "Booster",
            BasedOn = scheme?.BasedOn,
            RedemptionEnabled = scheme?.RedemptionEnabled ?? false,
            Points = wallet.AvailablePoints,
            AvailablePoints = wallet.AvailablePoints,
            EarnedPoints = wallet.EarnedPoints,
            RedeemedPoints = wallet.RedeemedPoints,
            Schemes = ToMobileSchemeBalances(wallet),
            InvoiceAmount = invoiceAmount,
            InvoiceAmountShort = FormatIndianShortAmount(invoiceAmount),
            StartDate = scheme?.StartDate,
            EndDate = scheme?.EndDate,
            ExpiresOn = scheme?.EndDate.ToString("dd MMM"),
            ExpiryLabel = "Lifetime · Never expires",
            BadgeText = "FOREVER",
            IsActive = scheme is not null,
            NextMessage = scheme is null ? "No active booster scheme available." : null,
            DaysLeft = scheme is null ? null : Math.Max(0, (scheme.EndDate.ToDateTime(TimeOnly.MinValue).Date - DateTime.UtcNow.Date).Days),
            ProgressSteps = scheme?.Slabs.OrderBy(x => x.ValueFrom).ThenBy(x => x.SortOrder).Select(slab => new DashboardSlabStepDto
            {
                Id = slab.Id,
                TierName = slab.TierName,
                ValueFrom = slab.ValueFrom,
                ValueTo = slab.ValueTo,
                RewardValue = slab.RewardValue,
                RewardLabel = FormatReward(slab.RewardValue, scheme.BasedOn),
                Achieved = invoiceAmount >= slab.ValueFrom
            }).ToList() ?? []
        };
    }

    // These endpoints allow anonymous access, but the app calls them after login.
    // When the caller can be identified, the same targeting the invoice screen uses
    // is applied so a retailer never sees a scheme aimed at another audience.
    private async Task<IReadOnlyCollection<CurrentSchemeDto>> LiveSchemes(string? walletType, CancellationToken cancellationToken) =>
        await CurrentRunningSchemes(walletType, cancellationToken, audienceCustomer: await CurrentCustomer(cancellationToken));

    private async Task<IReadOnlyCollection<CurrentSchemeDto>> CurrentRunningSchemes(
        string? walletType,
        CancellationToken cancellationToken,
        IReadOnlyCollection<NewInvoiceDto>? customerInvoices = null,
        Customer? audienceCustomer = null)
    {
        var today = CurrentBusinessDate();
        var query = _dbContext.LoyaltySchemes.AsNoTracking().Include(x => x.Slabs)
            .Where(x => x.DeletedAt == null
                && x.Active == "Y"
                && (x.Status == "Published" || x.Status == "Live")
                && x.SchemeType == "Invoice"
                && x.StartDate <= today
                && x.EndDate >= today);
        if (walletType == "Booster") query = query.Where(x => x.SchemeTag == "Booster");
        if (walletType == "Regular") query = query.Where(x => x.SchemeTag != "Booster");

        var schemes = await query.OrderBy(x => x.SchemeTag).ThenBy(x => x.SchemeName).ToListAsync(cancellationToken);
        if (audienceCustomer is not null)
        {
            var audience = await BuildSchemeAudienceAsync(audienceCustomer, cancellationToken);
            schemes = schemes.Where(scheme => SchemeEligibility.Matches(scheme, today, audience)).ToList();
        }

        return schemes.Select(scheme =>
        {
            var schemeInvoices = customerInvoices?
                .Where(invoice => invoice.SchemeId == scheme.Id)
                .GroupBy(invoice => invoice.Id)
                .Select(group => group.First())
                .ToList() ?? [];
            var achievementValue = schemeInvoices
                .Where(invoice => invoice.ApprovalStatus == NewInvoice.StatusApprovedHo)
                .Sum(invoice => invoice.HoApprovedAmount ?? invoice.Amount);
            var pendingInvoices = schemeInvoices
                .Where(invoice => invoice.ApprovalStatus != NewInvoice.StatusApprovedHo)
                .ToList();
            var orderedSlabs = scheme.Slabs.OrderBy(slab => slab.ValueFrom).ThenBy(slab => slab.SortOrder).ToList();
            var currentSlab = orderedSlabs.LastOrDefault(slab => achievementValue >= slab.ValueFrom);
            var nextSlab = orderedSlabs.FirstOrDefault(slab => slab.ValueFrom > achievementValue);
            return new CurrentSchemeDto
            {
            Id = scheme.Id,
            SchemeName = scheme.SchemeName,
            SchemeCode = scheme.SchemeCode,
            SchemeDescription = scheme.SchemeDescription,
            SchemeTag = scheme.SchemeTag,
            WalletType = IsBooster(scheme.SchemeTag) ? "Booster" : "Regular",
            CustomerType = scheme.CustomerType,
            AreaScope = scheme.AreaScope,
            AreaValues = ReadSchemeAreaValues(scheme.AreaValues),
            StartDate = scheme.StartDate,
            EndDate = scheme.EndDate,
            BasedOn = scheme.BasedOn,
            RedemptionEnabled = scheme.RedemptionEnabled,
            Status = scheme.Status,
            BrochurePath = scheme.BrochurePath,
            DaysLeft = Math.Max(0, (scheme.EndDate.ToDateTime(TimeOnly.MinValue).Date - DateTime.UtcNow.AddHours(5.5).Date).Days),
            AchievementValue = achievementValue,
            PendingInvoiceValue = pendingInvoices.Sum(invoice => invoice.Amount),
            ExpectedPendingReward = pendingInvoices.Sum(invoice => invoice.ExpectedSchemePoints),
            CurrentSlab = currentSlab?.TierName,
            NextSlab = nextSlab?.TierName,
            AdditionalValueRequired = nextSlab is null ? 0 : Math.Max(0, nextSlab.ValueFrom - achievementValue),
            Tiers = orderedSlabs
                .Select(slab => new CurrentSchemeTierDto
                {
                    Id = slab.Id,
                    TierName = slab.TierName,
                    ValueFrom = slab.ValueFrom,
                    ValueTo = slab.ValueTo,
                    RewardValue = slab.RewardValue,
                    RewardLabel = FormatReward(slab.RewardValue, scheme.BasedOn),
                    SortOrder = slab.SortOrder
                })
                .ToList()
            };
        }).ToList();
    }

    private async Task StoreCustomerTokenAndLogin(ulong customerId, string tokenId, DeviceRequest request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await _dbContext.OAuthAccessTokens.AddAsync(new OAuthAccessToken
        {
            Id = tokenId,
            UserId = customerId,
            ClientId = 0,
            Name = "retailer-mobile-token",
            Scopes = "[]",
            Revoked = false,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddDays(30)
        }, cancellationToken);

        await _dbContext.MobileUserLoginDetails.AddAsync(new MobileUserLoginDetail
        {
            CustomerId = customerId,
            AppVersion = request.AppVersion ?? "unknown",
            DeviceName = request.DeviceName ?? "unknown",
            DeviceType = request.DeviceType ?? "unknown",
            UniqueId = request.UniqueId,
            FirstLoginDate = now,
            LastLoginDate = now,
            LoginAt = now,
            LoginStatus = "1",
            App = "retailer"
        }, cancellationToken);
    }

    private async Task<List<ulong>> MobileAssignedUserIds(RegisterRequest request, CancellationToken cancellationToken)
    {
        var candidateIds = new[]
        {
            GetULong(request.Extra, "employee_id"),
            GetULong(request.Extra, "sales_executive_id"),
            GetULong(request.Extra, "sales_executive_id[0]"),
            GetULong(request.Extra, "supervisor_id"),
            GetULong(request.Extra, "user_id"),
            GetULong(request.Extra, "created_by")
        }
            .Where(id => id.HasValue && id.Value > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        if (candidateIds.Length == 0) return [];

        return await _dbContext.Users.AsNoTracking()
            .Where(user => candidateIds.Contains(user.Id) && user.DeletedAt == null)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);
    }

    private static void SetMobileAssignmentFields(IDictionary<string, string?> fields, IReadOnlyCollection<ulong> userIds)
    {
        if (userIds.Count == 0) return;
        var ids = string.Join(',', userIds);
        fields["employee_id"] = ids;
        fields["sales_executive_id"] = ids;
    }

    private async Task SyncMobileEmployeeDetailsAsync(ulong customerId, IReadOnlyCollection<ulong> userIds, CancellationToken cancellationToken)
    {
        foreach (var userId in userIds.Where(id => id > 0).Distinct())
        {
            await _dbContext.Database.ExecuteSqlRawAsync(
                @"UPDATE employee_details
SET active = 'Y', deleted_at = NULL, updated_by = {1}, updated_at = SYSUTCDATETIME()
WHERE customer_id = {0} AND user_id = {1} AND deleted_at IS NOT NULL",
                [customerId, userId],
                cancellationToken);

            await _dbContext.Database.ExecuteSqlRawAsync(
                @"INSERT INTO employee_details (active, customer_id, user_id, created_by, created_at, updated_at)
SELECT 'Y', {0}, {1}, {1}, SYSUTCDATETIME(), SYSUTCDATETIME()
WHERE NOT EXISTS (
    SELECT 1 FROM employee_details
    WHERE customer_id = {0} AND user_id = {1} AND deleted_at IS NULL
)",
                [customerId, userId],
                cancellationToken);
        }
    }

    private object ToProfile(Customer customer)
    {
        var fields = ReadFields(customer);
        return new
        {
            customer.Id,
            customer.CustomerCode,
            name = DisplayName(customer),
            owner_name = Field(fields, "owner_name") ?? customer.FirstName,
            shop_name = Field(fields, "shop_name") ?? customer.Name,
            mobile = customer.Mobile,
            email = customer.Email,
            customer_type = customer.CustomerType,
            customer_type_name = CustomerTypeName(customer.CustomerType),
            profile_image = customer.ProfileImage,
            shop_image = customer.ShopImage,
            kyc = KycState(fields),
            bank_account = BankAccount(fields),
            custom_fields = fields
        };
    }

    private async Task<object> ToMobileProfile(Customer customer, CancellationToken cancellationToken)
    {
        var fields = ReadFields(customer);
        var cityId = Field(fields, "city_id");
        var stateId = Field(fields, "state_id");
        var pincodeId = Field(fields, "pincode_id");

        return new
        {
            id = customer.Id,
            owner_name = Field(fields, "owner_name") ?? customer.FirstName ?? string.Empty,
            shop_name = Field(fields, "shop_name") ?? customer.Name ?? string.Empty,
            mobile = customer.Mobile ?? string.Empty,
            email = customer.Email ?? string.Empty,
            customer_type_name = CustomerTypeName(customer.CustomerType),
            kyc = new { status = KycStatusValue(fields) },
            custom_fields = new
            {
                gst_number = FirstField(fields, "gst_number", "gstin_no") ?? string.Empty,
                address_line = FirstField(fields, "address_line", "address", "address1", "shipping_address") ?? string.Empty,
                city_id = cityId ?? string.Empty,
                city_name = Field(fields, "city_name") ?? await CityName(cityId, cancellationToken) ?? string.Empty,
                state_id = stateId ?? string.Empty,
                state_name = Field(fields, "state_name") ?? await StateName(stateId, cancellationToken) ?? string.Empty,
                pincode_id = pincodeId ?? string.Empty,
                pincode = Field(fields, "pincode") ?? await Pincode(pincodeId, cancellationToken) ?? string.Empty,
                zone = FirstField(fields, "zone", "zone_name") ?? string.Empty,
                branch_name = FirstField(fields, "branch_name", "branch") ?? string.Empty,
                distribution_area = FirstField(fields, "distribution_area")
                    ?? string.Join(" · ", new[]
                    {
                        FirstField(fields, "zone", "zone_name"),
                        FirstField(fields, "branch_name", "branch")
                    }.Where(value => !string.IsNullOrWhiteSpace(value)))
            }
        };
    }

    private static string KycStatusValue(IReadOnlyDictionary<string, string?> fields)
    {
        var documents = new[] { "gst", "pan", "aadhar", "bank" };
        var uploaded = documents.Count(x => !string.IsNullOrWhiteSpace(Field(fields, $"{x}_attachment")) || !string.IsNullOrWhiteSpace(Field(fields, x == "bank" ? "bank_proof" : $"{x}_attachment")));
        var approved = documents.Count(x => string.Equals(Field(fields, $"{x}_kyc_status"), "approved", StringComparison.OrdinalIgnoreCase));
        return approved == documents.Length ? "approved" : uploaded > 0 ? "pending" : "missing";
    }

    private async Task<string?> CityName(string? cityId, CancellationToken cancellationToken)
    {
        if (!ulong.TryParse(cityId, out var id)) return null;
        return await _dbContext.Cities.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => x.CityName)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<string?> StateName(string? stateId, CancellationToken cancellationToken)
    {
        if (!ulong.TryParse(stateId, out var id)) return null;
        return await _dbContext.States.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => x.StateName)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<string?> Pincode(string? pincodeId, CancellationToken cancellationToken)
    {
        if (!ulong.TryParse(pincodeId, out var id)) return null;
        return await _dbContext.Pincodes.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => x.PinCode)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task ApplyMobileCustomerAddressFields(Dictionary<string, string?> fields, RegisterRequest request, CancellationToken cancellationToken)
    {
        SetIfPresent(fields, "address_line", FirstNonEmpty(request.Address, GetString(request.Extra, "address_line"), GetString(request.Extra, "address1"), GetString(request.Extra, "address")));
        SetIfPresent(fields, "address1", Field(fields, "address_line"));
        SetIfPresent(fields, "address", Field(fields, "address_line"));
        SetIfPresent(fields, "country_id", GetString(request.Extra, "country_id"));
        SetIfPresent(fields, "state_id", request.StateId?.ToString() ?? GetString(request.Extra, "state_id"));
        SetIfPresent(fields, "district_id", GetString(request.Extra, "district_id"));
        SetIfPresent(fields, "city_id", request.CityId?.ToString() ?? GetString(request.Extra, "city_id"));
        SetIfPresent(fields, "pincode_id", GetString(request.Extra, "pincode_id"));
        SetIfPresent(fields, "pincode", request.Pincode ?? GetString(request.Extra, "pincode"));
        await CanonicalizeMobileCustomerAddressFields(fields, cancellationToken);
    }

    private async Task CanonicalizeMobileCustomerAddressFields(Dictionary<string, string?> fields, CancellationToken cancellationToken)
    {
        SetIfPresent(fields, "address_line", FirstField(fields, "address_line", "address1", "address"));
        SetIfPresent(fields, "address1", Field(fields, "address_line"));
        SetIfPresent(fields, "address", Field(fields, "address_line"));
        if (string.IsNullOrWhiteSpace(Field(fields, "pincode_id")))
        {
            var pincodeId = await ResolvePincodeId(Field(fields, "pincode"), Field(fields, "city_id"), cancellationToken);
            SetIfPresent(fields, "pincode_id", pincodeId?.ToString(CultureInfo.InvariantCulture));
        }
        if (string.IsNullOrWhiteSpace(Field(fields, "pincode")) && Field(fields, "pincode_id") is { } pincodeIdText)
        {
            SetIfPresent(fields, "pincode", await Pincode(pincodeIdText, cancellationToken));
        }
    }

    private async Task<ulong?> ResolvePincodeId(string? pincode, string? cityId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pincode)) return null;
        if (ulong.TryParse(pincode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            var byValue = _dbContext.Pincodes.AsNoTracking().Where(x => x.PinCode == pincode);
            if (ulong.TryParse(cityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCityId))
            {
                byValue = byValue.Where(x => x.CityId == parsedCityId);
            }
            var matched = await byValue.Select(x => (ulong?)x.Id).FirstOrDefaultAsync(cancellationToken);
            if (matched.HasValue) return matched;

            if (await _dbContext.Pincodes.AsNoTracking().AnyAsync(x => x.Id == numeric, cancellationToken)) return numeric;
        }
        return null;
    }

    private async Task SyncMobileCustomerAddressAsync(ulong customerId, IReadOnlyDictionary<string, string?> fields, CancellationToken cancellationToken)
    {
        var address1 = FirstField(fields, "address_line", "address1", "address") ?? string.Empty;
        var countryId = ParseULong(Field(fields, "country_id"));
        var stateId = ParseULong(Field(fields, "state_id"));
        var districtId = ParseULong(Field(fields, "district_id"));
        var cityId = ParseULong(Field(fields, "city_id"));
        var pincodeId = ParseULong(Field(fields, "pincode_id"));
        if (string.IsNullOrWhiteSpace(address1) && !countryId.HasValue && !stateId.HasValue && !districtId.HasValue && !cityId.HasValue && !pincodeId.HasValue) return;

        var existingId = await _dbContext.Database.SqlQueryRaw<ulong>("SELECT COALESCE(MAX(id), 0) AS Value FROM addresses WHERE customer_id = {0} AND deleted_at IS NULL", customerId).FirstAsync(cancellationToken);
        if (existingId > 0)
        {
            await _dbContext.Database.ExecuteSqlRawAsync(@"UPDATE addresses SET address1 = {0}, country_id = {1}, state_id = {2}, district_id = {3}, city_id = {4}, pincode_id = {5}, updated_at = SYSUTCDATETIME()
WHERE id = {6}", [address1, countryId, stateId, districtId, cityId, pincodeId, existingId], cancellationToken);
            return;
        }

        await _dbContext.Database.ExecuteSqlRawAsync(@"INSERT INTO addresses (active, customer_id, address1, country_id, state_id, district_id, city_id, pincode_id, created_at, updated_at)
VALUES ('Y', {0}, {1}, {2}, {3}, {4}, {5}, {6}, SYSUTCDATETIME(), SYSUTCDATETIME())", [customerId, address1, countryId, stateId, districtId, cityId, pincodeId], cancellationToken);
    }

    private object BuildMobileKyc(Customer customer, IReadOnlyDictionary<string, string?> fields)
    {
        var documents = new[]
        {
            BuildMobileKycDocument(fields, "gst", "GST", FirstField(fields, "gst_attachment", "gst_image"), new[]
            {
                new MobileKycDetailDto("GST Number", "gst_number", FirstField(fields, "gst_number", "gstin_no"))
            }),
            BuildMobileKycDocument(fields, "pan", "PAN", FirstField(fields, "pan_attachment", "pan_image"), new[]
            {
                new MobileKycDetailDto("PAN Number", "pan_number", FirstField(fields, "pan_number", "pan_no"))
            }),
            BuildMobileKycDocument(fields, "aadhar", "Aadhaar Card", FirstField(fields, "aadhar_attachment", "aadhaar_attachment", "adharcard"), new[]
            {
                new MobileKycDetailDto("Aadhaar Number", "aadhar_no", FirstField(fields, "aadhar_no", "aadhaar_no", "aadhaar_number", "aadhar_number"))
            }),
            BuildMobileKycDocument(fields, "bank", "Blank Cheque / Passbook", FirstField(fields, "bank_proof", "blank_cheque", "passbook"), new[]
            {
                new MobileKycDetailDto("Bank Account Type", "bank_account_type", Field(fields, "bank_account_type")),
                new MobileKycDetailDto("Bank Name", "bank_name", Field(fields, "bank_name")),
                new MobileKycDetailDto("Account Number", "bank_account_number", Field(fields, "bank_account_number")),
                new MobileKycDetailDto("IFSC Code", "ifsc_code", Field(fields, "ifsc_code")),
                new MobileKycDetailDto("Account Holder Name", "account_holder_name", Field(fields, "account_holder_name"))
            })
        };

        return new
        {
            customer_id = customer.Id,
            summary = KycState(fields),
            bank_account = BankAccount(fields),
            documents,
            fields = new
            {
                gst_number = FirstField(fields, "gst_number", "gstin_no") ?? string.Empty,
                pan_number = FirstField(fields, "pan_number", "pan_no") ?? string.Empty,
                aadhar_no = FirstField(fields, "aadhar_no", "aadhaar_no", "aadhaar_number", "aadhar_number") ?? string.Empty,
                bank_account_type = Field(fields, "bank_account_type") ?? string.Empty,
                bank_name = Field(fields, "bank_name") ?? string.Empty,
                bank_account_number = Field(fields, "bank_account_number") ?? string.Empty,
                ifsc_code = Field(fields, "ifsc_code") ?? string.Empty,
                account_holder_name = Field(fields, "account_holder_name") ?? string.Empty
            }
        };
    }

    private MobileKycDocumentDto BuildMobileKycDocument(IReadOnlyDictionary<string, string?> fields, string key, string label, string? attachment, IReadOnlyCollection<MobileKycDetailDto> details)
    {
        var status = KycStatus(Field(fields, $"{key}_kyc_status"));
        return new MobileKycDocumentDto
        {
            Key = key,
            Label = label,
            Attachment = attachment ?? string.Empty,
            AttachmentUrl = MediaUrl(attachment),
            Status = status,
            StatusLabel = KycStatusLabel(status),
            Remark = Field(fields, $"{key}_kyc_remark") ?? string.Empty,
            ActionBy = Field(fields, $"{key}_kyc_action_by_name") ?? Field(fields, $"{key}_kyc_action_by") ?? string.Empty,
            ActionAt = Field(fields, $"{key}_kyc_action_at") ?? string.Empty,
            Details = details.ToList()
        };
    }

    private static object BankAccount(IReadOnlyDictionary<string, string?> fields)
    {
        var account = Field(fields, "bank_account_number") ?? string.Empty;
        return new
        {
            account_holder_name = Field(fields, "account_holder_name") ?? string.Empty,
            account_number = account,
            masked_account_number = Mask(account),
            bank_name = Field(fields, "bank_name") ?? string.Empty,
            ifsc_code = Field(fields, "ifsc_code") ?? string.Empty,
            is_primary = true
        };
    }

    private static object KycState(IReadOnlyDictionary<string, string?> fields)
    {
        var documents = new[] { "gst", "pan", "aadhar", "bank" };
        var uploaded = documents.Count(x => !string.IsNullOrWhiteSpace(Field(fields, $"{x}_attachment")) || !string.IsNullOrWhiteSpace(Field(fields, x == "bank" ? "bank_proof" : $"{x}_attachment")));
        var approved = documents.Count(x => string.Equals(Field(fields, $"{x}_kyc_status"), "approved", StringComparison.OrdinalIgnoreCase));
        return new { uploaded, approved, status = approved == documents.Length ? "approved" : uploaded > 0 ? "pending" : "missing" };
    }

    private string MediaUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (Uri.TryCreate(path, UriKind.Absolute, out _)) return path;
        var request = Request;
        return $"{request.Scheme}://{request.Host}{path}";
    }

    /// <summary>The attachment lands on disk before the invoice service validates the
    /// request, so a rejected invoice would otherwise leave the upload behind. Only the
    /// file this request wrote is removed - never the one already on the invoice.</summary>
    private async Task<LaravelApiResponse> WithoutOrphanUploadAsync(string? uploadedPath, Func<Task<LaravelApiResponse>> action)
    {
        try
        {
            return await action();
        }
        catch
        {
            DeleteUpload(uploadedPath);
            throw;
        }
    }

    private void DeleteUpload(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath) || !storedPath.StartsWith("/uploads/", StringComparison.Ordinal)) return;

        var root = _environment.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var path = Path.Combine(root, storedPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch (IOException)
        {
            // A file left behind is noise, not a failure - never mask the real error.
        }
    }

    private async Task<string> SaveFileAsync(IFormFile file, string folder, CancellationToken cancellationToken)
    {
        if (!IsPdfOrImageFile(file))
        {
            throw new BadHttpRequestException("Only PDF and image files are allowed.");
        }

        var root = _environment.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var directory = Path.Combine(root, "uploads", folder);
        Directory.CreateDirectory(directory);
        var extension = Path.GetExtension(file.FileName);
        var filename = $"{Guid.NewGuid():N}{extension}";
        var path = Path.Combine(directory, filename);
        await using var stream = System.IO.File.Create(path);
        await file.CopyToAsync(stream, cancellationToken);
        return $"/uploads/{folder}/{filename}";
    }

    private static Dictionary<string, string?> ReadFields(Customer customer)
    {
        if (string.IsNullOrWhiteSpace(customer.CustomFields)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string?>>(customer.CustomFields, JsonOptions) ?? []; }
        catch { return []; }
    }

    private static Dictionary<string, string?> ToFieldDictionary(Dictionary<string, JsonElement> values)
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            fields[key] = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }
        return fields;
    }

    private static string NormalizeMobile(string? mobile)
    {
        if (string.IsNullOrWhiteSpace(mobile)) return string.Empty;
        var digits = new string(mobile.Where(char.IsDigit).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    private static ulong ResolveCustomerType(string? appType, ulong? customerType, string? customerTypeText)
    {
        if (customerType == DealerType || ContainsDealer(appType) || ContainsDealer(customerTypeText)) return DealerType;
        if (customerType == InfluencerType || ContainsInfluencer(appType) || ContainsInfluencer(customerTypeText)) return InfluencerType;
        return RetailerType;
    }

    private static bool ContainsDealer(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.Equals("dealer", StringComparison.OrdinalIgnoreCase)
            || value.Equals("distributor", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsInfluencer(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.Contains("influencer", StringComparison.OrdinalIgnoreCase)
            || value.Contains("plumber", StringComparison.OrdinalIgnoreCase)
            || value.Contains("sub", StringComparison.OrdinalIgnoreCase));

    private static string CustomerTypeName(ulong? customerType) => customerType switch
    {
        DealerType => "Dealer",
        RetailerType => "Retailer",
        InfluencerType => "Influencer",
        _ => "Customer"
    };

    private static string CustomerTypePrefix(ulong customerType) => customerType switch
    {
        DealerType => "DLR",
        InfluencerType => "INF",
        _ => "RET"
    };

    private static string? GetString(Dictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;

    private static ulong? GetULong(Dictionary<string, JsonElement> values, string key) =>
        ulong.TryParse(GetString(values, key), out var id) && id > 0 ? id : null;

    private static void SetIfPresent(IDictionary<string, string?> fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) fields[key] = value;
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string? Field(IReadOnlyDictionary<string, string?> fields, string key) =>
        fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string? FirstField(IReadOnlyDictionary<string, string?> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Field(fields, key);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private static ulong? ParseULong(string? value) => ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string KycStatus(string? value)
    {
        if (string.Equals(value, "approved", StringComparison.OrdinalIgnoreCase)) return "approved";
        if (string.Equals(value, "rejected", StringComparison.OrdinalIgnoreCase)) return "rejected";
        return "pending";
    }

    private static string KycStatusLabel(string status) => status switch
    {
        "approved" => "Approved",
        "rejected" => "Rejected",
        _ => "Pending"
    };

    private static void ResetKycStatus(IDictionary<string, string?> fields, string documentKey)
    {
        if (string.IsNullOrWhiteSpace(documentKey)) return;

        var prefix = $"{documentKey}_kyc";
        fields[$"{prefix}_status"] = "pending";
        fields.Remove($"{prefix}_remark");
        fields.Remove($"{prefix}_action_by");
        fields.Remove($"{prefix}_action_by_name");
        fields.Remove($"{prefix}_action_at");
    }

    private static string? KycDocumentKeyForDetail(string key) => key switch
    {
        "gst_number" or "gstin_no" => "gst",
        "pan_number" or "pan_no" => "pan",
        "aadhar_no" or "aadhaar_no" or "aadhaar_number" or "aadhar_number" => "aadhar",
        "bank_account_type" or "bank_name" or "bank_account_number" or "bank_account_number_confirm" or "ifsc_code" or "account_holder_name" => "bank",
        _ => null
    };

    private static bool IsImageFile(IFormFile file)
    {
        if (file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return true;
        return Path.GetExtension(file.FileName).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp";
    }

    private static bool IsPdfOrImageFile(IFormFile file)
    {
        if (IsImageFile(file)) return true;
        if (string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBooster(string? schemeTag) => string.Equals(schemeTag, "Booster", StringComparison.OrdinalIgnoreCase);

    private static DateOnly CurrentBusinessDate() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

    /// <summary>Customer-facing stages. Dealers and retailers read the same five:
    /// pending, hold, in process, approved, rejected. SS and Sales stay collapsed into
    /// in_process because those are internal steps of one review.</summary>
    private static string CustomerInvoiceStatusKey(int status) => status switch
    {
        NewInvoice.StatusApprovedHo => "approved",
        NewInvoice.StatusRejected => "rejected",
        NewInvoice.StatusHold => "hold",
        NewInvoice.StatusApprovedSs or NewInvoice.StatusApprovedSales => "in_process",
        _ => "pending"
    };

    private static string InvoiceStatusKey(int status) => CustomerInvoiceStatusKey(status);

    private static string DealerInvoiceStatusKey(int status) => CustomerInvoiceStatusKey(status);

    private static bool InvoiceStatusMatches(NewInvoiceDto invoice, string status)
    {
        var normalized = status.Trim();
        if (string.Equals(normalized, "all", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(normalized, InvoiceStatusKey(invoice.ApprovalStatus), StringComparison.OrdinalIgnoreCase);
    }

    private static bool DealerInvoiceStatusMatches(NewInvoiceDto invoice, string status)
    {
        var normalized = status.Trim().Replace('-', '_').Replace(' ', '_');
        if (string.Equals(normalized, "all", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(normalized, "inprocess", StringComparison.OrdinalIgnoreCase)) normalized = "in_process";
        return string.Equals(normalized, DealerInvoiceStatusKey(invoice.ApprovalStatus), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWalletType(string? value) =>
        string.Equals(value, "Booster", StringComparison.OrdinalIgnoreCase) ? "Booster" : "Regular";

    private static string NormalizeRedeemMode(string? value) =>
        string.Equals(value, "IMPS", StringComparison.OrdinalIgnoreCase) ? "IMPS" : "NEFT";

    private static string RedemptionStatusKey(int status) => status switch
    {
        LoyaltyRedemption.StatusApproved => "approved",
        LoyaltyRedemption.StatusRejected => "rejected",
        LoyaltyRedemption.StatusHold => "hold",
        _ => "pending"
    };

    private static string RedemptionStatusLabel(int status) => status switch
    {
        LoyaltyRedemption.StatusApproved => "Approved",
        LoyaltyRedemption.StatusRejected => "Rejected",
        LoyaltyRedemption.StatusHold => "Hold",
        _ => "Pending"
    };

    private static bool TryRedemptionStatus(string? value, out int status)
    {
        status = LoyaltyRedemption.StatusPending;
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)) return false;
        if (int.TryParse(value, out var parsed) && parsed is >= LoyaltyRedemption.StatusPending and <= LoyaltyRedemption.StatusHold)
        {
            status = parsed;
            return true;
        }

        status = value.Trim().ToLowerInvariant() switch
        {
            "approved" => LoyaltyRedemption.StatusApproved,
            "rejected" => LoyaltyRedemption.StatusRejected,
            "hold" => LoyaltyRedemption.StatusHold,
            "pending" => LoyaltyRedemption.StatusPending,
            _ => -1
        };

        return status >= LoyaltyRedemption.StatusPending;
    }

    /// <summary>
    /// Builds the targeting context for a customer: branch and zone come from the
    /// assigned employee, state from the customer's own address.
    /// </summary>
    private async Task<SchemeAudience> BuildSchemeAudienceAsync(Customer customer, CancellationToken cancellationToken)
    {
        var fields = ReadFields(customer);
        var employeeId = FirstAssignedId(Field(fields, "employee_id"))
            ?? FirstAssignedId(Field(fields, "sales_executive_id"))
            ?? customer.ExecutiveId;

        string? branchName = null;
        string? zoneName = null;
        if (employeeId.HasValue)
        {
            var employee = await _dbContext.Users.AsNoTracking()
                .Where(x => x.Id == employeeId.Value)
                .Select(x => new { x.PrimaryBranchId, x.BranchId, x.DivisionId })
                .FirstOrDefaultAsync(cancellationToken);
            if (employee is not null)
            {
                var branchId = employee.PrimaryBranchId ?? FirstAssignedId(employee.BranchId);
                if (branchId.HasValue)
                {
                    branchName = await _dbContext.Branches.AsNoTracking()
                        .Where(x => x.Id == branchId.Value).Select(x => x.BranchName).FirstOrDefaultAsync(cancellationToken);
                }
                if (employee.DivisionId.HasValue)
                {
                    zoneName = await _dbContext.Divisions.AsNoTracking()
                        .Where(x => x.Id == employee.DivisionId.Value).Select(x => x.DivisionName).FirstOrDefaultAsync(cancellationToken);
                }
            }
        }

        var stateId = SchemeEligibility.ReadStateId(customer);
        var stateName = stateId.HasValue
            ? await _dbContext.States.AsNoTracking().Where(x => x.Id == stateId.Value).Select(x => x.StateName).FirstOrDefaultAsync(cancellationToken)
            : null;

        return new SchemeAudience(customer.CustomerType, customer.Name, customer.CustomerCode, branchName, zoneName, stateName);
    }

    private static ulong? FirstAssignedId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var first = value.Trim().Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return ulong.TryParse(first?.Trim('"'), out var parsed) && parsed > 0 ? parsed : null;
    }

    private static string FormatReward(decimal value, string? basedOn) =>
        string.Equals(basedOn, "Percentage", StringComparison.OrdinalIgnoreCase) ? $"{value:0.##}%" : $"Rs. {value:0.##}";

    private static IReadOnlyCollection<string> ReadSchemeAreaValues(string? json) => SchemeEligibility.ReadAreaValues(json);


    private static string FormatIndianCurrency(decimal value) => $"₹{value.ToString("N0", CultureInfo.GetCultureInfo("en-IN"))}";

    private static string FormatIndianShortAmount(decimal value)
    {
        var absolute = Math.Abs(value);
        if (absolute >= 10000000) return $"₹{value / 10000000:0.##}Cr";
        if (absolute >= 100000) return $"₹{value / 100000:0.##}L";
        if (absolute >= 1000) return $"₹{value / 1000:0.##}K";
        return $"₹{value:0.##}";
    }

    private static string DisplayName(Customer customer) => Field(ReadFields(customer), "owner_name") ?? customer.FirstName ?? customer.Name;

    private static string Mask(string value) => value.Length <= 4 ? value : $"{new string('X', Math.Max(0, value.Length - 4))}{value[^4..]}";

    private static string KycAttachmentKey(string fileName)
    {
        var key = fileName.ToLowerInvariant();
        if (key.Contains("bank") || key.Contains("cheque") || key.Contains("passbook")) return "bank_proof";
        if (key.Contains("pan")) return "pan_attachment";
        if (key.Contains("aadhar") || key.Contains("aadhaar")) return "aadhar_attachment";
        return "gst_attachment";
    }

    private static string KycDocumentKey(string attachmentKey) => attachmentKey switch
    {
        "pan_attachment" => "pan",
        "aadhar_attachment" => "aadhar",
        "bank_proof" => "bank",
        _ => "gst"
    };

    public class DeviceRequest
    {
        public string? AppVersion { get; set; }
        public string? DeviceName { get; set; }
        public string? DeviceType { get; set; }
        public string? UniqueId { get; set; }
    }

    public sealed class CustomerLookupRequest
    {
        public string? Mobile { get; set; }
        public string? Email { get; set; }
    }

    public sealed class CustomerPasswordLoginRequest : DeviceRequest
    {
        public string? Mobile { get; set; }
        public string? Password { get; set; }
    }

    public sealed class PasswordCodeRequest
    {
        public string? Mobile { get; set; }
    }

    public sealed class SetPasswordRequest
    {
        public string? Mobile { get; set; }
        public string? Code { get; set; }
        public string? Password { get; set; }
    }

    public sealed class RegisterRequest : DeviceRequest
    {
        public string? AppType { get; set; }
        public ulong? CustomerType { get; set; }
        public string? Name { get; set; }
        public string? OwnerName { get; set; }
        public string? ShopName { get; set; }
        public string? FirmName { get; set; }
        public string? Mobile { get; set; }
        public string? Email { get; set; }
        public string? Password { get; set; }
        public string? Address { get; set; }
        public ulong? StateId { get; set; }
        public ulong? CityId { get; set; }
        public string? Pincode { get; set; }
        public ulong? DealerId { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; } = [];
    }

    public sealed class MobileInvoiceFilter
    {
        public string? Search { get; set; }
        public string? Status { get; set; }
        [FromQuery(Name = "from_date")] public DateTime? FromDate { get; set; }
        [FromQuery(Name = "to_date")] public DateTime? ToDate { get; set; }
        public int Page { get; set; } = 1;
        [FromQuery(Name = "page_size")] public int PageSize { get; set; } = 20;
    }

    public sealed class MobileRedemptionRequest
    {
        public ulong? LoyaltySchemeId { get; set; }
        public string? WalletType { get; set; }
        public decimal Points { get; set; }
    }

    public sealed class MobileRedemptionHistoryFilter
    {
        public string? Search { get; set; }
        public string? Status { get; set; }
        [FromQuery(Name = "wallet_type")] public string? WalletType { get; set; }
        [FromQuery(Name = "redeem_mode")] public string? RedeemMode { get; set; }
        [FromQuery(Name = "from_date")] public DateTime? FromDate { get; set; }
        [FromQuery(Name = "to_date")] public DateTime? ToDate { get; set; }
        public int Page { get; set; } = 1;
        [FromQuery(Name = "page_size")] public int PageSize { get; set; } = 20;
    }

    public sealed class BankAccountRequest
    {
        public string? AccountHolderName { get; set; }
        public string? AccountNumber { get; set; }
        public string? BankName { get; set; }
        public string? IfscCode { get; set; }
    }

    private sealed record WalletPair(WalletDto Regular, WalletDto Booster);
    private sealed record WalletDto(string WalletType, decimal EarnedPoints, decimal RedeemedPoints, decimal AvailablePoints, IReadOnlyCollection<WalletSchemeDto> Schemes);
    private sealed record WalletSchemeDto(ulong? SchemeId, string SchemeName, bool RedemptionEnabled, decimal EarnedPoints, decimal RedeemedPoints, decimal AvailablePoints);

    private sealed record MobileKycDetailDto(string Label, string Key, string? Value);

    private sealed class MobileKycDocumentDto
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Attachment { get; set; } = string.Empty;
        public string AttachmentUrl { get; set; } = string.Empty;
        public string Status { get; set; } = "pending";
        public string StatusLabel { get; set; } = "Pending";
        public string Remark { get; set; } = string.Empty;
        public string ActionBy { get; set; } = string.Empty;
        public string ActionAt { get; set; } = string.Empty;
        public IReadOnlyCollection<MobileKycDetailDto> Details { get; set; } = [];
    }

    private sealed class MobileWalletSchemeBalanceDto
    {
        public ulong? LoyaltySchemeId { get; set; }
        public string SchemeName { get; set; } = string.Empty;
        public bool RedemptionEnabled { get; set; }
        public decimal AvailablePoints { get; set; }
        public string WalletType { get; set; } = string.Empty;
    }

    private sealed class MobileInvoiceListItemDto
    {
        public ulong Id { get; set; }
        public string RetailerName { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
        public string ShopName { get; set; } = string.Empty;
        public string RetailerCode { get; set; } = string.Empty;
        public string MobileNumber { get; set; } = string.Empty;
        public string InvoiceNumber { get; set; } = string.Empty;
        public string InvoiceNumberDisplay { get; set; } = string.Empty;
        public DateTime InvoiceDate { get; set; }
        public string DisplayDate { get; set; } = string.Empty;
        public string MonthKey { get; set; } = string.Empty;
        public string MonthLabel { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string AmountDisplay { get; set; } = string.Empty;
        public decimal RewardAmount { get; set; }
        public string? RewardDisplay { get; set; }
        public decimal ExpectedRewardAmount { get; set; }
        public string? ExpectedRewardDisplay { get; set; }
        public string RewardLabel { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string StatusLabel { get; set; } = string.Empty;
        public bool IsRewardCredited { get; set; }
        public bool IsPending { get; set; }
        public bool CanEdit { get; set; }
        public bool CanDelete { get; set; }
        public ulong RetailerId { get; set; }
        public ulong? SchemeId { get; set; }
        public string? Attachment { get; set; }
        public string? SchemeName { get; set; }
        public IReadOnlyCollection<string> SchemeNames { get; set; } = [];
    }

    private sealed class MobileInvoiceMonthGroupDto
    {
        public string MonthKey { get; set; } = string.Empty;
        public string MonthLabel { get; set; } = string.Empty;
        public int Count { get; set; }
        public decimal Turnover { get; set; }
        public string TurnoverDisplay { get; set; } = string.Empty;
        public decimal RewardAmount { get; set; }
        public string? RewardDisplay { get; set; }
        public IReadOnlyCollection<MobileInvoiceListItemDto> Items { get; set; } = [];
    }

    private sealed class MobileRedemptionHistoryItemDto
    {
        public ulong Id { get; set; }
        public string TransactionNo { get; set; } = string.Empty;
        public string TransactionNoDisplay { get; set; } = string.Empty;
        public ulong? LoyaltySchemeId { get; set; }
        public string SchemeName { get; set; } = string.Empty;
        public string WalletType { get; set; } = string.Empty;
        public string RedeemMode { get; set; } = string.Empty;
        public decimal Points { get; set; }
        public string PointsDisplay { get; set; } = string.Empty;
        public string AccountHolder { get; set; } = string.Empty;
        public string MaskedAccountNumber { get; set; } = string.Empty;
        public string BankName { get; set; } = string.Empty;
        public string IfscCode { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string StatusLabel { get; set; } = string.Empty;
        public string? Remark { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string DisplayDate { get; set; } = string.Empty;
        public string MonthKey { get; set; } = string.Empty;
        public string MonthLabel { get; set; } = string.Empty;
    }

    private sealed class MobileRedemptionMonthGroupDto
    {
        public string MonthKey { get; set; } = string.Empty;
        public string MonthLabel { get; set; } = string.Empty;
        public int Count { get; set; }
        public decimal TotalPoints { get; set; }
        public IReadOnlyCollection<MobileRedemptionHistoryItemDto> Items { get; set; } = [];
    }

    private sealed class DashboardWalletCardDto
    {
        public string Key { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string WalletType { get; set; } = string.Empty;
        public ulong? SchemeId { get; set; }
        public string? SchemeName { get; set; }
        public string? SchemeCode { get; set; }
        public string? SchemeTag { get; set; }
        public string? BasedOn { get; set; }
        public bool RedemptionEnabled { get; set; }
        public decimal Points { get; set; }
        public decimal AvailablePoints { get; set; }
        public decimal EarnedPoints { get; set; }
        public decimal RedeemedPoints { get; set; }
        public IReadOnlyCollection<MobileWalletSchemeBalanceDto> Schemes { get; set; } = [];
        public decimal InvoiceAmount { get; set; }
        public string InvoiceAmountShort { get; set; } = "₹0";
        public decimal AchievedReward { get; set; }
        public string? AchievedLabel { get; set; }
        public string? AchievedTierName { get; set; }
        public decimal? NextReward { get; set; }
        public string? NextRewardLabel { get; set; }
        public string? NextTierName { get; set; }
        public decimal AmountMoreForNextSlab { get; set; }
        public string? NextMessage { get; set; }
        public int? DaysLeft { get; set; }
        public string? DaysLeftMessage { get; set; }
        public DateOnly? StartDate { get; set; }
        public DateOnly? EndDate { get; set; }
        public string? ExpiresOn { get; set; }
        public string ExpiryLabel { get; set; } = string.Empty;
        public string BadgeText { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public int ProgressIndex { get; set; } = -1;
        public decimal ProgressPercent { get; set; }
        public IReadOnlyCollection<DashboardSlabStepDto> ProgressSteps { get; set; } = [];
    }

    private sealed class DashboardSlabStepDto
    {
        public ulong Id { get; set; }
        public string TierName { get; set; } = string.Empty;
        public decimal ValueFrom { get; set; }
        public decimal? ValueTo { get; set; }
        public decimal RewardValue { get; set; }
        public string RewardLabel { get; set; } = string.Empty;
        public bool Achieved { get; set; }
        public bool Current { get; set; }
    }

    private sealed class CurrentSchemeDto
    {
        public ulong Id { get; set; }
        public string SchemeName { get; set; } = string.Empty;
        public string SchemeCode { get; set; } = string.Empty;
        public string? SchemeDescription { get; set; }
        public string SchemeTag { get; set; } = string.Empty;
        public string WalletType { get; set; } = string.Empty;
        public string CustomerType { get; set; } = string.Empty;
        public string AreaScope { get; set; } = string.Empty;
        public IReadOnlyCollection<string> AreaValues { get; set; } = [];
        public DateOnly StartDate { get; set; }
        public DateOnly EndDate { get; set; }
        public string BasedOn { get; set; } = string.Empty;
        public bool RedemptionEnabled { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? BrochurePath { get; set; }
        public int DaysLeft { get; set; }
        public decimal AchievementValue { get; set; }
        public decimal PendingInvoiceValue { get; set; }
        public decimal ExpectedPendingReward { get; set; }
        public string? CurrentSlab { get; set; }
        public string? NextSlab { get; set; }
        public decimal AdditionalValueRequired { get; set; }
        public IReadOnlyCollection<CurrentSchemeTierDto> Tiers { get; set; } = [];
    }

    private sealed class CurrentSchemeTierDto
    {
        public ulong Id { get; set; }
        public string TierName { get; set; } = string.Empty;
        public decimal ValueFrom { get; set; }
        public decimal? ValueTo { get; set; }
        public decimal RewardValue { get; set; }
        public string RewardLabel { get; set; } = string.Empty;
        public int SortOrder { get; set; }
    }
    public sealed class DealerInvoiceForm
    {
        [FromForm(Name = "retailer_id")] public ulong RetailerId { get; set; }
        [FromForm(Name = "scheme_id")] public ulong? SchemeId { get; set; }
        [FromForm(Name = "invoice_number")] public string? InvoiceNumber { get; set; }
        [FromForm(Name = "invoice_date")] public DateTime? InvoiceDate { get; set; }
        [FromForm(Name = "amount")] public decimal? Amount { get; set; }
        [FromForm(Name = "attachment_file")] public IFormFile? Attachment { get; set; }
    }
}
