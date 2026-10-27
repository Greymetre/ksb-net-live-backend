using Application.Common;
using Application.DTOs.Customers;
using Application.DTOs.MasterData;
using Application.DTOs.NewInvoices;
using Application.DTOs.Redemptions;
using Application.Interfaces.Repositories;
using Application.Interfaces.Services;
using ClosedXML.Excel;
using Domain.Entities;
using Shared.Exceptions;
using Shared.Responses;

namespace Application.Services;

public sealed class RedemptionService : IRedemptionService
{
    private const decimal MinimumRedemptionPoints = 500;
    private readonly IRedemptionRepository _repository;
    private readonly ICustomerRepository _customerRepository;
    private readonly INewInvoiceRepository _newInvoiceRepository;

    public RedemptionService(IRedemptionRepository repository, ICustomerRepository customerRepository, INewInvoiceRepository newInvoiceRepository)
    {
        _repository = repository;
        _customerRepository = customerRepository;
        _newInvoiceRepository = newInvoiceRepository;
    }

    public async Task<LaravelApiResponse> GetRedemptionsAsync(RedemptionFilterDto filter, CancellationToken cancellationToken)
    {
        var redemptions = await _repository.GetRedemptionsAsync(filter, cancellationToken);
        return LaravelApiResponse.Success("redemptions", new RedemptionListResultDto
        {
            Redemptions = redemptions,
            Summary = new RedemptionSummaryDto
            {
                TotalRequests = redemptions.Count,
                Approved = redemptions.Count(x => x.Status == LoyaltyRedemption.StatusApproved),
                Pending = redemptions.Count(x => x.Status == LoyaltyRedemption.StatusPending),
                RejectedOrHold = redemptions.Count(x => x.Status is LoyaltyRedemption.StatusRejected or LoyaltyRedemption.StatusHold)
            }
        });
    }

    public async Task<MasterDataFileDto> ExportRedemptionsAsync(RedemptionFilterDto filter, CancellationToken cancellationToken)
    {
        var redemptions = await _repository.GetRedemptionsAsync(filter, cancellationToken);
        return CreateWorkbook("Redemption Report.xlsx",
            ["Zone", "Branch", "Dealer Code", "Dealer Name", "Customer Code", "Customer Shop Name", "Type", "City", "Mobile",
             "GST No", "Aadhaar Number", "Pan Number", "Bank Account Type", "Bank Account Number", "IFSC Code", "Account Holder Name",
             "Bank Name", "Mode", "Redeem Point", "Status", "Transaction Id", "Scheme Type", "Redeem Date", "Transaction Date", "Created By"],
            redemptions.Select(x => new object?[]
            {
                x.Zone,
                x.Branch,
                x.DealerCode,
                x.DistributorName,
                x.CustomerCode,
                x.ShopName,
                x.CustomerTypeName,
                x.CityName,
                x.MobileNumber,
                x.GstNo,
                x.AadhaarNumber,
                x.PanNumber,
                x.BankAccountType,
                x.AccountNumber,
                x.IfscCode,
                x.AccountHolder,
                x.BankName,
                x.RedeemMode,
                x.Points,
                x.StatusLabel,
                x.TransactionNo,
                x.WalletType,
                x.CreatedAt,
                x.ApprovedAt ?? x.CreatedAt,
                x.CreatedByName
            }));
    }

    public async Task<LaravelApiResponse> GetCustomerOptionsAsync(string? search, ulong? actorUserId, CancellationToken cancellationToken)
    {
        // The dropdown searches server-side, so a search returns only its matches and the cap is
        // rarely hit; it just bounds the unsearched first view. Invoices are then loaded only for
        // the customers actually shown, not the whole table.
        var customers = (await _customerRepository.GetCustomersAsync(new CustomerListFilterDto
        {
            Active = "Y",
            Search = search,
            Unpaged = true
        }, cancellationToken)).Items
            .Where(customer => customer.CustomerType is 2 or 3)
            .Take(100)
            .ToList();

        var customerIds = customers.Select(customer => customer.Id).ToList();
        var invoices = customerIds.Count == 0
            ? []
            : (await _newInvoiceRepository.GetInvoicesAsync(new NewInvoiceFilterDto { Unpaged = true, SecondaryCustomerIds = customerIds }, actorUserId, cancellationToken)).Items;
        var options = new List<RedemptionCustomerOptionDto>();
        foreach (var customer in customers)
        {
            options.Add(await BuildCustomerOptionAsync(customer, invoices.Where(x => x.SecondaryCustomerId == customer.Id).ToList(), cancellationToken));
        }

        return LaravelApiResponse.Success("customers", options);
    }

    public async Task<LaravelApiResponse> CreateRedemptionAsync(RedemptionCreateRequestDto request, ulong? actorUserId, CancellationToken cancellationToken, bool scopeInvoicesToActor = true)
    {
        if (!actorUserId.HasValue) throw Http(LaravelStatusCodes.Unauthorized, "Unauthenticated.");
        if (request.CustomerId == 0) throw Http(LaravelStatusCodes.NoContentLikeValidation, "Customer is required.");
        if (!request.LoyaltySchemeId.HasValue) throw Http(LaravelStatusCodes.NoContentLikeValidation, "Loyalty scheme is required.");
        if (!request.BankConfirmed) throw Http(LaravelStatusCodes.NoContentLikeValidation, "Bank details confirmation is required.");
        if (request.Points < MinimumRedemptionPoints) throw Http(LaravelStatusCodes.NoContentLikeValidation, $"Minimum redemption is {MinimumRedemptionPoints:0} points.");

        var walletType = NormalizeWalletType(request.WalletType);
        var redeemMode = NormalizeRedeemMode(request.RedeemMode);
        var customer = await _customerRepository.GetCustomerAsync(request.CustomerId, null, cancellationToken) ?? throw Http(LaravelStatusCodes.NotFound, "Customer not found.");

        // The scheme must have redemption turned on (the on/off toggle from scheme creation).
        // Enforced here so the CRM form, the import and the Vriddhi app - all three go through this
        // method - are blocked the same way, not only the app's display.
        var schemeFlags = await _repository.GetSchemeFlagsAsync(new[] { request.LoyaltySchemeId!.Value }, cancellationToken);
        if (schemeFlags.TryGetValue(request.LoyaltySchemeId.Value, out var requestedScheme) && !requestedScheme.RedemptionEnabled)
            throw Http(LaravelStatusCodes.NoContentLikeValidation, $"Redemption is turned off for the scheme \"{requestedScheme.Name}\".");
        var invoiceActorUserId = scopeInvoicesToActor ? actorUserId : null;
        var invoices = (await _newInvoiceRepository.GetInvoicesAsync(new NewInvoiceFilterDto { Unpaged = true, SecondaryCustomerIds = new[] { customer.Id } }, invoiceActorUserId, cancellationToken)).Items;
        var option = await BuildCustomerOptionAsync(customer, invoices.Where(x => x.SecondaryCustomerId == customer.Id).ToList(), cancellationToken);

        if (!option.KycApproved) throw Http(LaravelStatusCodes.NoContentLikeValidation, option.KycMessage);
        if (string.IsNullOrWhiteSpace(option.AccountHolder) || string.IsNullOrWhiteSpace(option.AccountNumber) || string.IsNullOrWhiteSpace(option.BankName) || string.IsNullOrWhiteSpace(option.IfscCode))
        {
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "Bank details are required before redemption.");
        }

        var scheme = (walletType == "Booster" ? option.BoosterSchemes : option.RegularSchemes)
            .FirstOrDefault(x => x.SchemeId == request.LoyaltySchemeId);
        if (scheme is null) throw Http(LaravelStatusCodes.NoContentLikeValidation, "Selected scheme is not available in this wallet.");
        if (request.Points > scheme.AvailablePoints) throw Http(LaravelStatusCodes.NoContentLikeValidation, $"Redeem points ({request.Points:0}) cannot be more than the available {scheme.AvailablePoints:0} points for {scheme.SchemeName} ({walletType}).");

        var now = DateTime.UtcNow;
        var redemption = new LoyaltyRedemption
        {
            TransactionNo = await GenerateTransactionNoAsync(cancellationToken),
            CustomerId = customer.Id,
            LoyaltySchemeId = scheme.SchemeId,
            WalletType = walletType,
            SchemeName = scheme.SchemeName,
            RedeemMode = redeemMode,
            Points = request.Points,
            AccountHolder = option.AccountHolder,
            AccountNumber = option.AccountNumber,
            BankName = option.BankName,
            IfscCode = option.IfscCode,
            BankConfirmed = true,
            Status = LoyaltyRedemption.StatusPending,
            CreatedBy = actorUserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _repository.CreateAsync(redemption, cancellationToken);
        return LaravelApiResponse.Success("data", new
        {
            redemption_id = redemption.Id,
            request_id = redemption.TransactionNo,
            transaction_no_display = redemption.TransactionNo,
            status = "pending",
            status_label = "Pending",
            message = "Redemption request submitted successfully"
        }, "Redemption request submitted successfully");
    }

    public MasterDataFileDto GetImportTemplate() =>
        CreateWorkbook("Redemption Import Template.xlsx",
            ["Customer ID", "Scheme Code", "Redeem Points", "Redeem Mode"],
            Enumerable.Empty<object?[]>());

    public async Task<RedemptionImportResultDto> ImportRedemptionsAsync(Stream fileStream, ulong? actorUserId, bool scopeInvoicesToActor, CancellationToken cancellationToken)
    {
        var result = new RedemptionImportResultDto();
        using var workbook = new XLWorkbook(fileStream);
        var sheet = workbook.Worksheets.First();
        // Header in row 1; data from row 2. A row whose Customer ID is not a number (a blank line
        // or the template's hint row) is skipped, so the same template file can be filled and sent back.
        var dataRows = sheet.RowsUsed().Skip(1).ToList();

        var codes = dataRows.Select(r => r.Cell(2).GetString().Trim()).Where(x => x.Length > 0).Distinct().ToList();
        var schemesByCode = await _repository.GetSchemesByCodeAsync(codes, cancellationToken);

        foreach (var row in dataRows)
        {
            var customerText = row.Cell(1).GetString().Trim();
            if (!ulong.TryParse(customerText, out var customerId) || customerId == 0) continue;

            result.Total++;
            var schemeCode = row.Cell(2).GetString().Trim();
            var pointsText = row.Cell(3).GetString().Trim();
            var mode = row.Cell(4).GetString().Trim();
            var rowResult = new RedemptionImportRowResultDto { Row = row.RowNumber(), CustomerId = customerId };
            try
            {
                if (schemeCode.Length == 0) throw Http(LaravelStatusCodes.NoContentLikeValidation, "Scheme Code is required.");
                if (!schemesByCode.TryGetValue(schemeCode, out var scheme)) throw Http(LaravelStatusCodes.NoContentLikeValidation, $"Scheme code '{schemeCode}' was not found.");
                if (!decimal.TryParse(pointsText, out var points)) throw Http(LaravelStatusCodes.NoContentLikeValidation, "Redeem Points must be a number.");

                await CreateRedemptionAsync(new RedemptionCreateRequestDto
                {
                    CustomerId = customerId,
                    LoyaltySchemeId = scheme.Id,
                    WalletType = IsBooster(scheme.Tag) ? "Booster" : "Regular",
                    RedeemMode = mode,
                    Points = points,
                    BankConfirmed = true
                }, actorUserId, cancellationToken, scopeInvoicesToActor);

                rowResult.Success = true;
                rowResult.Message = "Redemption created (Pending).";
                result.Created++;
            }
            catch (LaravelHttpException ex)
            {
                rowResult.Success = false;
                rowResult.Message = ex.ResponseMessage?.ToString() ?? "Validation failed.";
                result.Failed++;
            }
            catch (Exception)
            {
                rowResult.Success = false;
                rowResult.Message = "Could not process this row.";
                result.Failed++;
            }
            result.Rows.Add(rowResult);
        }

        return result;
    }

    private async Task<RedemptionCustomerOptionDto> BuildCustomerOptionAsync(CustomerDto customer, IReadOnlyCollection<NewInvoiceDto> invoices, CancellationToken cancellationToken)
    {
        var redemptions = await _repository.GetCustomerRedemptionsAsync(customer.Id, cancellationToken);
        var schemes = invoices
            .Where(x => x.SchemeId.HasValue && x.SchemePoints > 0 && x.ApprovalStatus == 3)
            .GroupBy(x => new { x.SchemeId, x.SchemeName, WalletType = IsBooster(x.SchemeTag) ? "Booster" : "Regular" })
            .Select(group =>
            {
                var redeemed = redemptions
                    .Where(x => x.LoyaltySchemeId == group.Key.SchemeId && x.WalletType == group.Key.WalletType)
                    .Sum(x => x.Points);
                var earned = group.Sum(x => x.SchemePoints);
                return new RedemptionSchemeOptionDto
                {
                    SchemeId = group.Key.SchemeId,
                    SchemeName = group.Key.SchemeName ?? "Scheme",
                    WalletType = group.Key.WalletType,
                    EarnedPoints = earned,
                    RedeemedPoints = redeemed,
                    AvailablePoints = Math.Max(0, earned - redeemed)
                };
            })
            .Where(x => x.AvailablePoints > 0)
            .OrderBy(x => x.SchemeName)
            .ToList();

        // Drop schemes whose redemption toggle is off, so the create form never lists them and
        // their points are not shown as redeemable.
        var schemeIds = schemes.Where(x => x.SchemeId.HasValue).Select(x => x.SchemeId!.Value).Distinct().ToList();
        var schemeFlags = await _repository.GetSchemeFlagsAsync(schemeIds, cancellationToken);
        schemes = schemes.Where(x => x.SchemeId.HasValue && schemeFlags.TryGetValue(x.SchemeId.Value, out var f) && f.RedemptionEnabled).ToList();

        var kyc = KycApproved(customer);
        return new RedemptionCustomerOptionDto
        {
            Id = customer.Id,
            CustomerCode = CustomerCode(customer),
            Name = Field(customer, "owner_name") ?? customer.Name,
            ShopName = Field(customer, "shop_name") ?? customer.Name,
            MobileNumber = Field(customer, "mobile_numbers")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? customer.Mobile ?? string.Empty,
            CustomerTypeName = customer.CustomerTypeName,
            CityName = customer.CityName,
            DistributorName = Field(customer, "distributor_name_name") ?? Field(customer, "distributor_name"),
            KycApproved = kyc.Approved,
            KycState = kyc.State,
            KycMessage = kyc.Message,
            AccountHolder = Field(customer, "account_holder_name") ?? string.Empty,
            AccountNumber = Field(customer, "bank_account_number") ?? string.Empty,
            MaskedAccountNumber = MaskAccount(Field(customer, "bank_account_number") ?? string.Empty),
            BankName = Field(customer, "bank_name") ?? string.Empty,
            IfscCode = Field(customer, "ifsc_code") ?? string.Empty,
            RegularPoints = schemes.Where(x => x.WalletType == "Regular").Sum(x => x.AvailablePoints),
            BoosterPoints = schemes.Where(x => x.WalletType == "Booster").Sum(x => x.AvailablePoints),
            RegularSchemes = schemes.Where(x => x.WalletType == "Regular").ToList(),
            BoosterSchemes = schemes.Where(x => x.WalletType == "Booster").ToList()
        };
    }

    private static (bool Approved, string State, string Message) KycApproved(CustomerDto customer)
    {
        var documents = new[]
        {
            new KycDocument("gst", "GST", "gst_attachment"),
            new KycDocument("pan", "PAN", "pan_attachment"),
            new KycDocument("aadhar", "Aadhaar", "aadhar_attachment"),
            new KycDocument("bank", "Blank Cheque / Passbook", "bank_proof")
        };

        var missing = documents.Where(document => string.IsNullOrWhiteSpace(Field(customer, document.AttachmentKey))).Select(document => document.Label).ToList();
        var rejected = documents.Where(document => string.Equals(Field(customer, $"{document.Key}_kyc_status"), "rejected", StringComparison.OrdinalIgnoreCase)).Select(document => document.Label).ToList();
        var pending = documents
            .Where(document => !missing.Contains(document.Label) && !rejected.Contains(document.Label))
            .Where(document => !string.Equals(Field(customer, $"{document.Key}_kyc_status"), "approved", StringComparison.OrdinalIgnoreCase))
            .Select(document => document.Label)
            .ToList();

        if (missing.Count == 0 && rejected.Count == 0 && pending.Count == 0) return (true, "approved", "KYC approved. You can continue with redemption.");

        var messages = new List<string>();
        if (missing.Count > 0) messages.Add($"Please upload {JoinLabels(missing)} KYC document{Plural(missing)} before creating redemption.");
        if (rejected.Count > 0) messages.Add($"{JoinLabels(rejected)} KYC document{Plural(rejected)} rejected. Please upload corrected document{Plural(rejected)} again.");
        if (pending.Count > 0) messages.Add($"{JoinLabels(pending)} KYC document{Plural(pending)} uploaded and pending for approval.");

        var state = missing.Count > 0 ? "missing" : rejected.Count > 0 ? "rejected" : "pending";
        return (false, state, string.Join(" ", messages));
    }

    private async Task<string> GenerateTransactionNoAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var value = $"KSB-R{Random.Shared.Next(10000, 99999)}";
            if (!await _repository.TransactionNoExistsAsync(value, cancellationToken)) return value;
        }

        return $"KSB-R{DateTime.UtcNow:yyMMddHHmmss}";
    }

    private static string NormalizeWalletType(string? value)
    {
        if (string.Equals(value, "Booster", StringComparison.OrdinalIgnoreCase)) return "Booster";
        return "Regular";
    }

    private static string NormalizeRedeemMode(string? value)
    {
        if (string.Equals(value, "IMPS", StringComparison.OrdinalIgnoreCase)) return "IMPS";
        return "NEFT";
    }

    private static bool IsBooster(string? schemeTag) =>
        string.Equals(schemeTag, "Booster", StringComparison.OrdinalIgnoreCase);

    private static string? Field(CustomerDto customer, string key) =>
        customer.CustomFields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string MaskAccount(string value) =>
        value.Length <= 4 ? value : $"{new string('*', Math.Max(0, value.Length - 4))}{value[^4..]}";

    private static string CustomerCode(CustomerDto customer) =>
        !string.IsNullOrWhiteSpace(customer.CustomerCode)
            ? customer.CustomerCode
            : $"{CustomerPrefix(customer.CustomerType)}-{customer.Id.ToString().PadLeft(4, '0')}";

    private static string CustomerPrefix(ulong? customerType) => customerType switch
    {
        1 => "DIS",
        2 => "RET",
        3 => "INF",
        _ => "CUS"
    };


    private static string JoinLabels(IReadOnlyList<string> values) =>
        values.Count switch
        {
            0 => string.Empty,
            1 => values[0],
            2 => $"{values[0]} and {values[1]}",
            _ => $"{string.Join(", ", values.Take(values.Count - 1))}, and {values[^1]}"
        };

    private static string Plural(IReadOnlyCollection<string> values) => values.Count == 1 ? string.Empty : "s";

    private static LaravelHttpException Http(int statusCode, object message) => new(statusCode, message);

    private sealed record KycDocument(string Key, string Label, string AttachmentKey);

    private static MasterDataFileDto CreateWorkbook(string fileName, string[] headings, IEnumerable<object?[]> rows)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Sheet1");
        worksheet.Style.Font.FontName = "Calibri";
        worksheet.Style.Font.FontSize = 9;

        for (var column = 0; column < headings.Length; column++)
        {
            worksheet.Cell(1, column + 1).Value = headings[column];
            worksheet.Cell(1, column + 1).Style.Font.Bold = true;
        }

        var rowNumber = 2;
        foreach (var row in rows)
        {
            for (var column = 0; column < row.Length; column++)
            {
                worksheet.Cell(rowNumber, column + 1).Value = XLCellValue.FromObject(row[column]);
            }

            rowNumber++;
        }

        worksheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return new MasterDataFileDto { FileName = fileName, Content = stream.ToArray() };
    }
}
