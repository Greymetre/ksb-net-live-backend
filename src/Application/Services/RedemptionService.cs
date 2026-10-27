using System.Globalization;
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

        // The status cards count the whole view regardless of the clicked status, so they stay
        // meaningful while a status filter is applied. Only re-fetch (status-agnostic) when a status
        // filter is actually set; otherwise the already-loaded list is the full set.
        var summarySet = redemptions;
        if (filter.Status.HasValue)
        {
            var clickedStatus = filter.Status;
            filter.Status = null;
            summarySet = await _repository.GetRedemptionsAsync(filter, cancellationToken);
            filter.Status = clickedStatus;
        }

        return LaravelApiResponse.Success("redemptions", new RedemptionListResultDto
        {
            Redemptions = redemptions,
            Summary = new RedemptionSummaryDto
            {
                TotalRequests = summarySet.Count,
                Approved = summarySet.Count(x => x.Status == LoyaltyRedemption.StatusApproved),
                Pending = summarySet.Count(x => x.Status == LoyaltyRedemption.StatusPending),
                RejectedOrHold = summarySet.Count(x => x.Status == LoyaltyRedemption.StatusRejected),
                Hold = summarySet.Count(x => x.Status == LoyaltyRedemption.StatusHold),
                Success = summarySet.Count(x => x.Status == LoyaltyRedemption.StatusSuccess),
                Failed = summarySet.Count(x => x.Status == LoyaltyRedemption.StatusFailed)
            }
        });
    }

    public async Task<MasterDataFileDto> ExportRedemptionsAsync(RedemptionFilterDto filter, CancellationToken cancellationToken)
    {
        var redemptions = await _repository.GetRedemptionsAsync(filter, cancellationToken);
        return CreateWorkbook("Redemption Report.xlsx",
            ["Zone", "Branch", "Dealer Code", "Dealer Name", "Customer Code", "Customer Shop Name", "Type", "City", "Mobile",
             "GST No", "Aadhaar Number", "Pan Number", "Bank Account Type", "Bank Account Number", "IFSC Code", "Account Holder Name",
             "Bank Name", "Mode", "Redeem Point", "TDS %", "TDS Dedcut Amt", "Final Pay Amount", "Status", "UTR No", "Transaction Id",
             "Scheme Type", "Transaction Date", "Payment Date", "Created By"],
            redemptions.Select(x =>
            {
                // TDS % is stored on the redemption (default 0); the deduction and final pay are
                // always computed from it, so they stay correct after an Update-Redemptions import.
                var tdsPercent = x.TdsPercent ?? 0m;
                var tdsAmount = Math.Round(x.Points * tdsPercent / 100m, 2, MidpointRounding.AwayFromZero);
                var finalPay = x.Points - tdsAmount;
                return new object?[]
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
                    tdsPercent,
                    tdsAmount,
                    finalPay,
                    x.StatusLabel,
                    x.PayoutTransactionNo,       // UTR No - blank until a successful payout
                    x.TransactionNo,             // Transaction Id - the redeem request number
                    x.WalletType,                // Scheme Type
                    x.CreatedAt,                 // Transaction Date - the redeem date
                    x.PaymentDate,               // Payment Date - set via the Update import (date only)
                    x.CreatedByName
                };
            }));
    }

    public async Task<LaravelApiResponse> GetCustomerOptionsAsync(string? search, ulong? actorUserId, CancellationToken cancellationToken)
    {
        // The dropdown searches server-side, so a search returns only its matches and the cap is
        // rarely hit; it just bounds the unsearched first view. Invoices are then loaded only for
        // the customers actually shown, not the whole table.
        var customers = (await _customerRepository.GetCustomersAsync(new CustomerListFilterDto
        {
            // Scope the dropdown to the signed-in user's reporting view (admin = all, branch roles
            // = customers in assigned branches, everyone else = reporting downline). Without
            // ActorUserId the dropdown would list every customer to everybody.
            ActorUserId = actorUserId,
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
            options.Add(await BuildCustomerOptionAsync(customer, invoices.Where(x => x.SecondaryCustomerId == customer.Id).ToList(), cancellationToken, isAppChannel: false));
        }

        return LaravelApiResponse.Success("customers", options);
    }

    public async Task<LaravelApiResponse> CreateRedemptionAsync(RedemptionCreateRequestDto request, ulong? actorUserId, CancellationToken cancellationToken, bool scopeInvoicesToActor = true, bool isAppChannel = false)
    {
        if (!actorUserId.HasValue) throw Http(LaravelStatusCodes.Unauthorized, "Unauthenticated.");
        if (request.CustomerId == 0) throw Http(LaravelStatusCodes.NoContentLikeValidation, "Customer is required.");
        if (!request.LoyaltySchemeId.HasValue) throw Http(LaravelStatusCodes.NoContentLikeValidation, "Loyalty scheme is required.");
        if (!request.BankConfirmed) throw Http(LaravelStatusCodes.NoContentLikeValidation, "Bank details confirmation is required.");
        if (request.Points < MinimumRedemptionPoints) throw Http(LaravelStatusCodes.NoContentLikeValidation, $"Minimum redemption is {MinimumRedemptionPoints:0} points.");

        var walletType = NormalizeWalletType(request.WalletType);
        var redeemMode = NormalizeRedeemMode(request.RedeemMode);
        var customer = await _customerRepository.GetCustomerAsync(request.CustomerId, null, cancellationToken) ?? throw Http(LaravelStatusCodes.NotFound, "Customer not found.");

        // The scheme must have redemption turned on. There are two independent toggles from
        // scheme creation: "Redemption App" (the Vriddhi app / any app channel) and
        // "Redemption CRM" (the create form and the import). The CRM form, the import and the
        // app all go through this method, so the right toggle is checked per channel.
        var schemeFlags = await _repository.GetSchemeFlagsAsync(new[] { request.LoyaltySchemeId!.Value }, cancellationToken);
        if (schemeFlags.TryGetValue(request.LoyaltySchemeId.Value, out var requestedScheme)
            && !(isAppChannel ? requestedScheme.RedemptionEnabled : requestedScheme.RedemptionCrmEnabled))
            throw Http(LaravelStatusCodes.NoContentLikeValidation, $"Redemption is turned off for the scheme \"{requestedScheme.Name}\".");
        var invoiceActorUserId = scopeInvoicesToActor ? actorUserId : null;
        var invoices = (await _newInvoiceRepository.GetInvoicesAsync(new NewInvoiceFilterDto { Unpaged = true, SecondaryCustomerIds = new[] { customer.Id } }, invoiceActorUserId, cancellationToken)).Items;
        var option = await BuildCustomerOptionAsync(customer, invoices.Where(x => x.SecondaryCustomerId == customer.Id).ToList(), cancellationToken, isAppChannel);

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

    /// <summary>Edits a pending redemption. Reuses the exact create validation (customer, scheme
    /// toggle, KYC, bank, minimum + available points) - only the form comes in pre-filled. Everyone
    /// except superadmin may edit a redemption only while it is still Pending.</summary>
    public async Task<LaravelApiResponse> UpdateRedemptionAsync(ulong id, RedemptionCreateRequestDto request, ulong? actorUserId, bool actorIsSuperAdmin, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) throw Http(LaravelStatusCodes.Unauthorized, "Unauthenticated.");

        var existing = await _repository.GetByIdAsync(id, cancellationToken) ?? throw Http(LaravelStatusCodes.NotFound, "Redemption not found.");
        if (!actorIsSuperAdmin && existing.Status != LoyaltyRedemption.StatusPending)
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "Only pending redemptions can be edited.");

        if (request.CustomerId == 0) throw Http(LaravelStatusCodes.NoContentLikeValidation, "Customer is required.");
        if (!request.LoyaltySchemeId.HasValue) throw Http(LaravelStatusCodes.NoContentLikeValidation, "Loyalty scheme is required.");
        if (!request.BankConfirmed) throw Http(LaravelStatusCodes.NoContentLikeValidation, "Bank details confirmation is required.");
        if (request.Points < MinimumRedemptionPoints) throw Http(LaravelStatusCodes.NoContentLikeValidation, $"Minimum redemption is {MinimumRedemptionPoints:0} points.");

        var walletType = NormalizeWalletType(request.WalletType);
        var redeemMode = NormalizeRedeemMode(request.RedeemMode);
        var customer = await _customerRepository.GetCustomerAsync(request.CustomerId, null, cancellationToken) ?? throw Http(LaravelStatusCodes.NotFound, "Customer not found.");

        var schemeFlags = await _repository.GetSchemeFlagsAsync(new[] { request.LoyaltySchemeId!.Value }, cancellationToken);
        if (schemeFlags.TryGetValue(request.LoyaltySchemeId.Value, out var requestedScheme) && !requestedScheme.RedemptionCrmEnabled)
            throw Http(LaravelStatusCodes.NoContentLikeValidation, $"Redemption is turned off for the scheme \"{requestedScheme.Name}\".");

        var invoices = (await _newInvoiceRepository.GetInvoicesAsync(new NewInvoiceFilterDto { Unpaged = true, SecondaryCustomerIds = new[] { customer.Id } }, actorUserId, cancellationToken)).Items;
        var option = await BuildCustomerOptionAsync(customer, invoices.Where(x => x.SecondaryCustomerId == customer.Id).ToList(), cancellationToken, isAppChannel: false);

        if (!option.KycApproved) throw Http(LaravelStatusCodes.NoContentLikeValidation, option.KycMessage);
        if (string.IsNullOrWhiteSpace(option.AccountHolder) || string.IsNullOrWhiteSpace(option.AccountNumber) || string.IsNullOrWhiteSpace(option.BankName) || string.IsNullOrWhiteSpace(option.IfscCode))
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "Bank details are required before redemption.");

        var scheme = (walletType == "Booster" ? option.BoosterSchemes : option.RegularSchemes)
            .FirstOrDefault(x => x.SchemeId == request.LoyaltySchemeId);
        if (scheme is null) throw Http(LaravelStatusCodes.NoContentLikeValidation, "Selected scheme is not available in this wallet.");

        // The available points already have THIS redemption subtracted (it is non-rejected). When
        // the edit stays on the same scheme + wallet, add its current points back so the max is
        // what the user actually has to work with; moving to another scheme uses that scheme's own available.
        var allowance = scheme.AvailablePoints;
        if (existing.LoyaltySchemeId == request.LoyaltySchemeId && string.Equals(existing.WalletType, walletType, StringComparison.OrdinalIgnoreCase))
            allowance += existing.Points;
        if (request.Points > allowance)
            throw Http(LaravelStatusCodes.NoContentLikeValidation, $"Redeem points ({request.Points:0}) cannot be more than the available {allowance:0} points for {scheme.SchemeName} ({walletType}).");

        existing.CustomerId = customer.Id;
        existing.LoyaltySchemeId = scheme.SchemeId;
        existing.WalletType = walletType;
        existing.SchemeName = scheme.SchemeName;
        existing.RedeemMode = redeemMode;
        existing.Points = request.Points;
        existing.AccountHolder = option.AccountHolder;
        existing.AccountNumber = option.AccountNumber;
        existing.BankName = option.BankName;
        existing.IfscCode = option.IfscCode;
        existing.BankConfirmed = true;
        existing.UpdatedAt = DateTime.UtcNow;

        await _repository.UpdateAsync(existing, cancellationToken);
        return LaravelApiResponse.Success("data", new
        {
            redemption_id = existing.Id,
            request_id = existing.TransactionNo,
            transaction_no_display = existing.TransactionNo,
            message = "Redemption updated successfully"
        }, "Redemption updated successfully");
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

    /// <summary>Bulk-updates existing redemptions from the EXPORTED file (xlsx or csv), matched by
    /// Transaction Id. Only TDS %, Status, UTR No and Payment Date are read; TDS Deduct Amt and
    /// Final Pay Amount are recomputed, not imported. Every row reports its own validation message.</summary>
    public async Task<RedemptionImportResultDto> UpdateRedemptionsImportAsync(Stream fileStream, string fileName, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var result = new RedemptionImportResultDto();
        var isCsv = (fileName ?? string.Empty).EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        var rows = isCsv ? ReadCsvRows(fileStream) : ReadXlsxRows(fileStream);
        if (rows.Count == 0) return result;

        if (!rows[0].ContainsKey("transaction id"))
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "The file must have a \"Transaction Id\" column (use the exported redemption file).");

        var txnIds = rows.Select(r => Val(r, "transaction id")).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var byTxn = await _repository.GetByTransactionNosAsync(txnIds, cancellationToken);

        var changed = false;
        var rowNumber = 1; // header is row 1
        foreach (var row in rows)
        {
            rowNumber++;
            result.Total++;
            var txnId = Val(row, "transaction id");
            var rowResult = new RedemptionImportRowResultDto { Row = rowNumber };
            try
            {
                if (txnId.Length == 0) throw Http(LaravelStatusCodes.NoContentLikeValidation, "Transaction Id is required.");
                if (!byTxn.TryGetValue(txnId, out var redemption)) throw Http(LaravelStatusCodes.NoContentLikeValidation, $"Transaction Id '{txnId}' was not found.");
                rowResult.CustomerId = redemption.CustomerId;

                // TDS % (default 0). Blank keeps whatever is there (0 if never set).
                var tdsText = Val(row, "tds %");
                if (tdsText.Length > 0)
                {
                    if (!decimal.TryParse(tdsText, NumberStyles.Any, CultureInfo.InvariantCulture, out var tds)) throw Http(LaravelStatusCodes.NoContentLikeValidation, "TDS % must be a number.");
                    if (tds is < 0m or > 100m) throw Http(LaravelStatusCodes.NoContentLikeValidation, "TDS % must be between 0 and 100.");
                    redemption.TdsPercent = tds;
                }
                redemption.TdsPercent ??= 0m;

                // Status - any status may be set directly (no workflow). Blank keeps the current one.
                var statusText = Val(row, "status");
                if (statusText.Length > 0)
                {
                    var status = ParseStatusText(statusText);
                    if (status is null) throw Http(LaravelStatusCodes.NoContentLikeValidation, $"Invalid Status '{statusText}'. Use Pending, Approved, Rejected, Hold, Success or Failed.");
                    redemption.Status = status.Value;
                }

                // UTR No - set when provided; blank leaves the existing value.
                var utr = Val(row, "utr no");
                if (utr.Length > 0) redemption.PayoutTransactionNo = utr;

                // Payment Date - any Excel/CSV date format, DATE only (time dropped). Blank = no change.
                var paymentText = Val(row, "payment date");
                if (paymentText.Length > 0)
                {
                    var date = ParseFlexibleDate(paymentText);
                    if (date is null) throw Http(LaravelStatusCodes.NoContentLikeValidation, $"Invalid Payment Date '{paymentText}'. Use a date like 31-12-2026.");
                    redemption.PaymentDate = date.Value.Date;
                }

                // Keep the stored paid amount in step with the (possibly new) TDS.
                var tdsPct = redemption.TdsPercent ?? 0m;
                redemption.PayoutAmount = redemption.Points - Math.Round(redemption.Points * tdsPct / 100m, 2, MidpointRounding.AwayFromZero);
                redemption.UpdatedAt = DateTime.UtcNow;

                rowResult.Success = true;
                rowResult.Message = "Updated.";
                result.Created++;
                changed = true;
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

        if (changed) await _repository.SaveChangesAsync(cancellationToken);
        return result;
    }

    private static string Val(IReadOnlyDictionary<string, string> row, string header) =>
        row.TryGetValue(header, out var value) ? value.Trim() : string.Empty;

    private static int? ParseStatusText(string text)
    {
        var value = text.Trim();
        if (int.TryParse(value, out var n) && n is >= LoyaltyRedemption.StatusPending and <= LoyaltyRedemption.StatusFailed) return n;
        return value.ToLowerInvariant() switch
        {
            "pending" => LoyaltyRedemption.StatusPending,
            "approved" => LoyaltyRedemption.StatusApproved,
            "rejected" => LoyaltyRedemption.StatusRejected,
            "hold" => LoyaltyRedemption.StatusHold,
            "success" => LoyaltyRedemption.StatusSuccess,
            "failed" => LoyaltyRedemption.StatusFailed,
            _ => null
        };
    }

    private static readonly string[] DateFormats =
    {
        "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "yyyy/MM/dd",
        "dd-MMM-yyyy", "d-MMM-yyyy", "dd MMM yyyy", "dd.MM.yyyy", "MM/dd/yyyy", "M/d/yyyy",
        "dd-MM-yy", "dd/MM/yy"
    };

    private static DateTime? ParseFlexibleDate(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return null;
        // Explicit day-first formats first (Indian data), then a general invariant parse.
        if (DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)) return exact.Date;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return parsed.Date;
        return null;
    }

    private static List<Dictionary<string, string>> ReadXlsxRows(Stream stream)
    {
        var rows = new List<Dictionary<string, string>>();
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();
        var used = sheet.RangeUsed();
        if (used is null) return rows;
        var headerCells = used.FirstRow().Cells().ToList();
        var headers = headerCells.Select(c => c.GetString().Trim().ToLowerInvariant()).ToList();
        foreach (var row in used.Rows().Skip(1))
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++)
            {
                if (headers[i].Length == 0) continue;
                var cell = row.Cell(i + 1);
                var text = cell.DataType == XLDataType.DateTime && cell.TryGetValue(out DateTime dt) ? dt.ToString("yyyy-MM-dd") : cell.GetString().Trim();
                dict[headers[i]] = text;
            }
            rows.Add(dict);
        }
        return rows;
    }

    private static List<Dictionary<string, string>> ReadCsvRows(Stream stream)
    {
        var rows = new List<Dictionary<string, string>>();
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return rows;
        var headers = SplitCsvLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        foreach (var line in lines.Skip(1))
        {
            var cells = SplitCsvLine(line);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count && i < cells.Count; i++)
            {
                if (headers[i].Length == 0) continue;
                dict[headers[i]] = cells[i].Trim();
            }
            rows.Add(dict);
        }
        return rows;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { cells.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }
        cells.Add(current.ToString());
        return cells;
    }

    private async Task<RedemptionCustomerOptionDto> BuildCustomerOptionAsync(CustomerDto customer, IReadOnlyCollection<NewInvoiceDto> invoices, CancellationToken cancellationToken, bool isAppChannel)
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

        // Drop schemes whose redemption toggle is off for this channel, so the list never shows
        // them and their points are not shown as redeemable. The CRM form reads the "Redemption
        // CRM" toggle; an app channel reads "Redemption App".
        var schemeIds = schemes.Where(x => x.SchemeId.HasValue).Select(x => x.SchemeId!.Value).Distinct().ToList();
        var schemeFlags = await _repository.GetSchemeFlagsAsync(schemeIds, cancellationToken);
        schemes = schemes.Where(x => x.SchemeId.HasValue && schemeFlags.TryGetValue(x.SchemeId.Value, out var f) && (isAppChannel ? f.RedemptionEnabled : f.RedemptionCrmEnabled)).ToList();

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
