using Application.Common;
using Application.DTOs.Customers;
using Application.DTOs.MasterData;
using Application.Interfaces.Repositories;
using Application.Interfaces.Services;
using ClosedXML.Excel;
using Shared.Exceptions;
using Shared.Responses;

namespace Application.Services;

public sealed class CustomerService : ICustomerService
{
    private static readonly string[] AssignmentFieldKeys = ["employee_id", "sales_executive_id", "supervisor_id"];

    private static readonly (string Key, string Heading)[] DistributorExportDefinition =
    [
        ("id", "ID"), ("distributor_code", "Distributor Code"), ("legal_name", "Legal Name"),
        ("trade_name", "Trade Name"), ("business_status", "Business Status"),
        ("business_start_date", "Business Start Date"), ("contact_person", "Contact Person"),
        ("mobile", "Mobile"), ("alternate_mobile", "Alternate Mobile"), ("email", "Email"),
        ("billing_address", "Billing Address"), ("billing_city_name", "Billing City"),
        ("billing_city", "Billing City ID"), ("billing_district_name", "Billing District"),
        ("billing_district", "Billing District ID"), ("billing_state_name", "Billing State"),
        ("billing_state", "Billing State ID"), ("billing_country_name", "Billing Country"),
        ("billing_country", "Billing Country ID"), ("billing_pincode_name", "Billing Pincode"),
        ("billing_pincode", "Billing Pincode ID"), ("shipping_address", "Shipping Address"),
        ("beat_route", "Beat Route"), ("beat_id", "Beat ID"), ("gst_number", "GST Number"),
        ("pan_number", "PAN Number"), ("registration_type", "Registration Type"),
        ("sales_executive_id", "Sales Executive ID (JSON)"), ("supervisor_id", "Supervisor ID"),
        ("customer_segment", "Customer Segment"), ("employee_id_name", "Employee Names"),
        ("employee_codes", "Employee Codes"), ("reporting_managers", "Reporting Managers"),
        ("created_at_datetime", "Created At"), ("updated_at_datetime", "Updated At")
    ];

    private static readonly string[] DistributorExportColumns = DistributorExportDefinition.Select(x => x.Key).ToArray();

    // Same layout as Laravel SecondaryCustomersExport. The unified customer model
    // stores two phone numbers, therefore old Mobile Number-3..5 are omitted.
    private static readonly (string Key, string Heading)[] RetailerExportDefinition =
    [
        ("type", "Type"), ("status", "Approval Status"), ("employee_id_name", "Employee Names"), ("branch_name", "Branch Name"),
        ("shop_name", "Shop Name"), ("owner_name", "Owner Name"), ("mobile_1", "Mobile Number-1"), ("mobile_2", "Mobile Number-2"),
        ("email", "Email"),
        ("distributor_name_name", "Domestic Distributor Name"), ("distributor_code", "Domestic Distributor Code"),
        ("agri_distributor_name", "Agri Distributor"), ("agri_distributor_code", "Agri Distributor Code"),
        ("address_line", "Address"), ("belt_area_market_name", "Belt/Area/Market Name"),
        ("country_name", "Country"), ("country_id", "Country ID"), ("state_name", "State"), ("state_id", "State ID"),
        ("district_name", "District"), ("district_id", "District ID"), ("city_name", "City"), ("city_id", "City ID"),
        ("pincode", "Pincode"), ("pincode_id", "Pincode ID"), ("beat_name", "Beat"), ("beat_id", "Beat ID"),
        ("gst_number", "Gst Number"), ("pan_number", "Pan Number"), ("bank_account_type", "Bank Account Type"),
        ("bank_account_number", "Bank Account Number"), ("bank_name", "Bank Name"), ("ifsc_code", "IFSC Code"),
        ("account_holder_name", "Account Holder Name"), ("active", "Active Status"), ("gps_location", "GPS Location"),
        ("gmap", "Google Map"), ("created_at", "Created Date"), ("employee_designations", "Employee Designations"),
        ("created_by_name", "Created By"), ("approve_reject_by_name", "Approved/Rejected By"), ("remark", "Rejected Reason"),
        ("id", "Retailer ID"), ("distributor_id", "Domestic Distributor ID"), ("agri_distributor_id", "Agri Distributor ID"),
        ("employee_codes", "Employee Codes"), ("reporting_managers", "Reporting Managers"), ("owner_photo", "Owner Photo"),
        ("shop_photo", "Shop Photo"), ("gst_attachment", "GST Attachment"), ("pan_attachment", "PAN Attachment"), ("zone", "Zone")
    ];

    private static readonly string[] RetailerExportColumns = RetailerExportDefinition.Select(x => x.Key).ToArray();

    private static readonly string[] ImportColumns = DistributorExportColumns.Concat(RetailerExportColumns).Distinct().ToArray();

    private static readonly HashSet<string> PreserveRawColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "mobile", "mobile_numbers", "email", "customer_code", "contact_number", "alternate_mobile", "whatsapp_number",
        "distributor_code", "business_start_date", "country_id", "state_id", "district_id", "city_id", "pincode_id",
        "beat_id", "sales_executive_id", "supervisor_id", "distributor_name", "agri_distributor", "employee_id",
        "shop_image", "profile_image", "documents", "mou_file", "gst_number", "gst_attachment", "pan_number",
        "pan_attachment", "aadhar_no", "aadhar_attachment", "bank_account_number", "ifsc_code", "bank_proof", "shop_photo", "gps_location", "gmap"
    };

    private static readonly HashSet<string> AttachmentColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "shop_image", "profile_image", "documents", "mou_file", "gst_attachment", "pan_attachment",
        "aadhar_attachment", "bank_proof", "shop_photo", "owner_photo"
    };

    private static readonly HashSet<string> KycDocumentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "gst",
        "pan",
        "aadhar",
        "bank"
    };

    private readonly ICustomerRepository _repository;

    public CustomerService(ICustomerRepository repository)
    {
        _repository = repository;
    }

    public async Task<LaravelApiResponse> GetCustomersAsync(CustomerListFilterDto filter, CancellationToken cancellationToken)
    {
        var result = await _repository.GetCustomersAsync(filter, cancellationToken);
        var response = LaravelApiResponse.Success("customers", result.Items);
        response.Extra["total"] = result.Total;
        response.Extra["page"] = result.Page;
        response.Extra["page_size"] = result.PageSize;
        return response;
    }

    public async Task<LaravelApiResponse> GetCustomerAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken) =>
        LaravelApiResponse.Success("customer", await GetOrThrowAsync(_repository.GetCustomerAsync(id, actorUserId, cancellationToken), "Customer not found"));

    public async Task<LaravelApiResponse> CreateCustomerAsync(CustomerRequestDto request, ulong? actorUserId, CancellationToken cancellationToken)
    {
        NormalizeRequest(request);
        ResolveAssignedUsers(request, actorUserId, defaultToActor: true);
        await ValidateAsync(request, null, cancellationToken);
        var customer = await _repository.CreateCustomerAsync(request, actorUserId, cancellationToken);
        await _repository.EnsureDistributorLoginUserAsync(customer.Id, actorUserId, cancellationToken);
        return LaravelApiResponse.Success("customer", customer, "Customer created successfully");
    }

    public async Task<LaravelApiResponse> UpdateCustomerAsync(ulong id, CustomerRequestDto request, ulong? actorUserId, CancellationToken cancellationToken)
    {
        NormalizeRequest(request);
        ResolveAssignedUsers(request, actorUserId, defaultToActor: false);
        await ValidateAsync(request, id, cancellationToken);
        var customer = await _repository.UpdateCustomerAsync(id, request, actorUserId, cancellationToken);
        if (customer is not null) await _repository.EnsureDistributorLoginUserAsync(customer.Id, actorUserId, cancellationToken);
        return LaravelApiResponse.Success("customer", customer ?? throw NotFound("Customer not found"), "Customer updated successfully");
    }

    public async Task<LaravelApiResponse> ApproveKycDocumentAsync(ulong id, string documentKey, string? remark, ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) throw new LaravelHttpException(LaravelStatusCodes.Unauthorized, "Unauthenticated.");
        var key = NormalizeKycDocumentKey(documentKey);
        var customer = await _repository.UpdateKycStatusAsync(id, key, "approved", remark, actorUserId.Value, cancellationToken);
        return LaravelApiResponse.Success("customer", customer ?? throw NotFound("Customer not found"), "KYC document approved successfully");
    }

    public async Task<LaravelApiResponse> RejectKycDocumentAsync(ulong id, string documentKey, string? remark, ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) throw new LaravelHttpException(LaravelStatusCodes.Unauthorized, "Unauthenticated.");
        if (string.IsNullOrWhiteSpace(remark)) throw new LaravelHttpException(LaravelStatusCodes.NoContentLikeValidation, "Remark is required.");

        var key = NormalizeKycDocumentKey(documentKey);
        var customer = await _repository.UpdateKycStatusAsync(id, key, "rejected", remark, actorUserId.Value, cancellationToken);
        return LaravelApiResponse.Success("customer", customer ?? throw NotFound("Customer not found"), "KYC document rejected successfully");
    }

    public async Task<LaravelApiResponse> SetRetailerApprovalStatusAsync(ulong id, string? status, string? remark, ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) throw new LaravelHttpException(LaravelStatusCodes.Unauthorized, "Unauthenticated.");
        var normalizedStatus = NormalizeApprovalStatus(status);
        var customer = await _repository.SetRetailerApprovalStatusAsync(
            id,
            normalizedStatus,
            normalizedStatus.Equals("REJECTED", StringComparison.OrdinalIgnoreCase) ? remark : null,
            actorUserId.Value,
            cancellationToken);
        return LaravelApiResponse.Success("customer", customer ?? throw NotFound("Retailer not found"), "Status updated successfully");
    }

    public async Task<LaravelApiResponse> SetCustomerActiveAsync(ulong id, string? active, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var customer = await _repository.SetCustomerActiveAsync(id, active, actorUserId, cancellationToken);
        return LaravelApiResponse.Success("customer", customer ?? throw NotFound("Customer not found"), "Customer status changed successfully");
    }

    public async Task<LaravelApiResponse> DeleteCustomerAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!await _repository.DeleteCustomerAsync(id, actorUserId, cancellationToken)) throw NotFound("Customer not found");
        return LaravelApiResponse.MessageOnly("success", "Customer deleted successfully!");
    }

    public async Task<MasterDataFileDto> ExportCustomersAsync(CustomerListFilterDto filter, string baseUrl, CancellationToken cancellationToken)
    {
        if (filter.CustomerType is not 1 and not 2 and not 3)
        {
            throw new LaravelHttpException(LaravelStatusCodes.BadRequest, "Customer type filter is required for export.");
        }

        filter.Unpaged = true;
        var rows = (await _repository.GetCustomersAsync(filter, cancellationToken)).Items;
        if (filter.CustomerType == 2)
        {
            return CreateWorkbook(
                "customers-retailer.xlsx",
                RetailerExportDefinition.Select(x => x.Heading).ToArray(),
                rows.Select(customer => RetailerExportDefinition.Select(column => ExportValue(customer, column.Key, baseUrl)).ToArray()),
                preserveHeadings: true);
        }

        if (filter.CustomerType == 1)
        {
            return CreateWorkbook(
                "master-distributors.xlsx",
                DistributorExportDefinition.Select(x => x.Heading).ToArray(),
                rows.Select(customer => DistributorExportDefinition.Select(column => ExportValue(customer, column.Key, baseUrl)).ToArray()),
                preserveHeadings: true);
        }

        var columns = ExportColumnsFor(filter.CustomerType);
        return CreateWorkbook(
            $"customers-{CustomerTypeName(filter.CustomerType).ToLowerInvariant()}.xlsx",
            columns,
            rows.Select(customer => ToExportRow(customer, columns, baseUrl)));
    }

    public Task<MasterDataFileDto> GetCustomerTemplateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(CreateWorkbook("customers-template.xlsx", ImportColumns.Where(x => x != "id").ToArray(), []));

    public async Task<LaravelApiResponse> UploadCustomersAsync(Stream fileStream, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var result = await ImportRowsAsync(fileStream, async row =>
        {
            var customFields = ReadCustomFields(row);
            SetField(customFields, "distributor_name", FirstNonBlank(row.Value("distributor_id"), row.Value("domestic_distributor_id")));
            SetField(customFields, "agri_distributor", row.Value("agri_distributor_id"));
            // Laravel exports both display names and master IDs. Persist the ID
            // columns in the unified customer model; names are lookup-only.
            SetField(customFields, "billing_city", row.Value("billing_city_id"));
            SetField(customFields, "billing_district", row.Value("billing_district_id"));
            SetField(customFields, "billing_state", row.Value("billing_state_id"));
            SetField(customFields, "billing_country", row.Value("billing_country_id"));
            SetField(customFields, "billing_pincode", row.Value("billing_pincode_id"));

            var customerType = row.CustomerType("customer_type")
                ?? row.CustomerType("type")
                ?? (row.HasHeading("distributor_code") ? 1UL : null);

            if (customerType is 1 or 2 && !string.IsNullOrWhiteSpace(row.Value("employee_codes")))
            {
                var employeeCodes = SplitImportValues(row.Value("employee_codes")).ToArray();
                var usersByCode = await _repository.GetUserIdsByEmployeeCodesAsync(employeeCodes, cancellationToken);
                var missingCodes = employeeCodes.Where(code => !usersByCode.ContainsKey(code)).ToArray();
                if (missingCodes.Length > 0)
                {
                    throw new LaravelHttpException(
                        LaravelStatusCodes.BadRequest,
                        $"Employee Codes not found: {string.Join(", ", missingCodes)}.");
                }

                // Employee Codes is the exported, user-editable assignment column.
                // Treat it as authoritative and rebuild the internal ID assignment.
                foreach (var assignmentKey in AssignmentFieldKeys) customFields.Remove(assignmentKey);
                var targetAssignmentKey = customerType == 1 ? "sales_executive_id" : "employee_id";
                SetField(customFields, targetAssignmentKey, string.Join(',', employeeCodes.Select(code => usersByCode[code])));
            }

            var request = new CustomerRequestDto
            {
                CustomerType = customerType,
                Name = FirstNonBlank(row.Value("name"), row.Value("legal_name"), row.Value("trade_name"), row.Value("shop_name"), row.Value("owner_name")),
                Mobile = FirstNonBlank(row.Value("mobile"), row.Value("mobile_number"), row.Value("mobile_number_1"), row.Value("mobile_1")),
                Email = row.Value("email"),
                // On retailer sheets distributor_code is the parent dealer's code,
                // not the retailer's own, so only dealers may fall back to it.
                CustomerCode = FirstNonBlank(row.Value("customer_code"), customerType == 1 ? row.Value("distributor_code") : null),
                ContactNumber = FirstNonBlank(row.Value("contact_number"), row.Value("whatsapp_number"), row.Value("mobile_number_2"), row.Value("mobile_2")),
                Active = FirstNonBlank(row.Value("active"), row.Value("business_status")),
                CustomFields = customFields
            };

            NormalizeRequest(request);
            if ((row.ULong("id") ?? row.ULong("retailer_id")) is { } id)
            {
                var existing = await _repository.GetCustomerAsync(id, actorUserId, cancellationToken)
                    ?? throw NotFound($"Customer ID {id} was not found.");
                request.CustomFields = MergeImportFields(existing.CustomFields, request.CustomFields);
                await UpdateCustomerAsync(id, request, actorUserId, cancellationToken);
                return true;
            }

            await CreateCustomerAsync(request, actorUserId, cancellationToken);
            return false;
        }, cancellationToken);

        return LaravelApiResponse.Success("import", result, "Customer import completed");
    }

    private async Task ValidateAsync(CustomerRequestDto request, ulong? id, CancellationToken cancellationToken)
    {
        RequireId(request.CustomerType, "Customer type is required.");
        RequireValue(request.Name, "Customer name is required.");

        // A dealer code doubles as the dealer login password, so it cannot be blank.
        if (request.CustomerType == 1) RequireValue(request.CustomerCode, "Dealer code is required.");

        if (!string.IsNullOrWhiteSpace(request.Mobile) && await _repository.MobileExistsAsync(request.Mobile.Trim(), id, cancellationToken))
        {
            throw new LaravelHttpException(LaravelStatusCodes.BadRequest, "Mobile already exists.");
        }

        if (!string.IsNullOrWhiteSpace(request.Email) && await _repository.EmailExistsAsync(request.Email.Trim(), id, cancellationToken))
        {
            throw new LaravelHttpException(LaravelStatusCodes.BadRequest, "Email already exists.");
        }
    }

    private static void NormalizeRequest(CustomerRequestDto request)
    {
        request.CustomerType ??= ReadULong(request.CustomFields, "customer_type");
        request.Name = FirstNonBlank(
            request.Name,
            ReadField(request.CustomFields, "legal_name"),
            ReadField(request.CustomFields, "shop_name"),
            ReadField(request.CustomFields, "owner_name"));
        request.Mobile = FirstNonBlank(request.Mobile, request.MobileNumber, ReadField(request.CustomFields, "mobile_number"));
        request.ContactNumber = FirstNonBlank(request.ContactNumber, request.WhatsappNumber, request.AlternateMobile, ReadField(request.CustomFields, "whatsapp_number"));

        request.CustomFields ??= [];
        if (request.CustomerType == 1)
        {
            // The web form and the mobile app both post the dealer code as
            // distributor_code. Keep the column and the custom field in step.
            request.CustomerCode = FirstNonBlank(request.CustomerCode, ReadField(request.CustomFields, "distributor_code"));

            // Dealer/distributor records do not have parent domestic/agri
            // distributor assignments. Those fields belong only to retailers.
            request.CustomFields.Remove("distributor_name");
            request.CustomFields.Remove("agri_distributor");
            request.CustomFields.Remove("distributor_name_name");
            request.CustomFields.Remove("agri_distributor_name");
        }
        else if (request.CustomerType == 2)
        {
            var approvalStatus = ReadField(request.CustomFields, "status");
            if (string.IsNullOrWhiteSpace(approvalStatus) || approvalStatus.Trim() == "-")
            {
                request.CustomFields["status"] = "PENDING";
            }
        }
        SetField(request.CustomFields, "customer_type", request.CustomerType?.ToString());
        SetField(request.CustomFields, "name", request.Name);
        SetField(request.CustomFields, "mobile", request.Mobile);
        SetField(request.CustomFields, "mobile_number", request.Mobile);
        SetField(request.CustomFields, "contact_number", request.ContactNumber);
        SetField(request.CustomFields, "email", request.Email);
        SetField(request.CustomFields, "customer_code", request.CustomerCode);
        SetField(request.CustomFields, "profile_image", request.ProfileImage);
        SetField(request.CustomFields, "shop_image", request.ShopImage);
    }

    private static void ResolveAssignedUsers(CustomerRequestDto request, ulong? actorUserId, bool defaultToActor)
    {
        if (request.CustomFields is null) return;

        var fieldPresent = AssignmentFieldKeys.Any(key => request.CustomFields.ContainsKey(key));
        var ids = AssignmentFieldKeys
            .SelectMany(key => ReadULongs(ReadField(request.CustomFields, key)))
            .Distinct()
            .ToList();

        if (ids.Count == 0 && defaultToActor && actorUserId.HasValue) ids.Add(actorUserId.Value);
        if (ids.Count == 0 && !fieldPresent) return;

        request.AssignedUserIds = ids;

        if (ids.Count == 0) return;
        if (request.CustomerType == 1)
        {
            SetField(request.CustomFields, "sales_executive_id", string.Join(',', ids));
        }
        else
        {
            SetField(request.CustomFields, "employee_id", string.Join(',', ids));
        }
    }

    private static Dictionary<string, string?> ReadCustomFields(ExcelRow row)
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in ImportColumns.Where(x => x != "id" && x != "customer_type" && x != "name" && x != "mobile" && x != "email" && x != "customer_code" && x != "contact_number" && x != "active"))
        {
            SetField(fields, column, row.Value(column));
        }

        return fields;
    }

    private static object?[] ToExportRow(CustomerDto customer, string[] columns, string baseUrl) =>
        columns.Select(column => ExportValue(customer, column, baseUrl)).ToArray();

    private static object? ExportValue(CustomerDto customer, string column, string baseUrl)
    {
        object? value = column switch
        {
            "id" => customer.Id,
            "customer_type" => CustomerTypeName(customer.CustomerType),
            "name" => customer.Name,
            "mobile" => customer.Mobile,
            "email" => customer.Email,
            "customer_code" => customer.CustomerCode,
            "contact_number" => customer.ContactNumber,
            "active" => customer.Active.Equals("Y", StringComparison.OrdinalIgnoreCase) ? "Active" : "Inactive",
            "type" => FirstNonBlank(Field(customer, "type"), customer.CustomerTypeName),
            "status" => RetailerApprovalStatus(customer),
            "mobile_1" => CustomerMobiles(customer).ElementAtOrDefault(0),
            "mobile_2" => CustomerMobiles(customer).ElementAtOrDefault(1),
            "distributor_id" => Field(customer, "distributor_name"),
            "agri_distributor_id" => Field(customer, "agri_distributor"),
            "country_name" => customer.CountryName,
            "state_name" => customer.StateName,
            "district_name" => customer.DistrictName,
            "city_name" => customer.CityName,
            "pincode" => customer.Pincode,
            "gps_location" => CustomerGpsLocation(customer),
            "gmap" => FirstNonBlank(Field(customer, "gmap"), GoogleMapsUrl(CustomerGpsLocation(customer))),
            "created_by_name" => customer.CreatedByName,
            "created_at" => customer.CreatedAt?.ToString("dd-MM-yyyy"),
            "created_at_datetime" => customer.CreatedAt?.ToString("dd-MM-yyyy HH:mm"),
            "updated_at_datetime" => customer.UpdatedAt?.ToString("dd-MM-yyyy HH:mm"),
            "billing_city_name" => customer.CityName,
            "billing_district_name" => customer.DistrictName,
            "billing_state_name" => customer.StateName,
            "billing_country_name" => customer.CountryName,
            "billing_pincode_name" => customer.Pincode,
            "sales_executive_id" => JsonIdArray(Field(customer, "sales_executive_id")),
            "profile_image" => customer.ProfileImage ?? Field(customer, column),
            "shop_image" => customer.ShopImage ?? Field(customer, column),
            "distributor_name" => Field(customer, "distributor_name_name") ?? Field(customer, column),
            "agri_distributor" => Field(customer, "agri_distributor_name") ?? Field(customer, column),
            "employee_id" => Field(customer, "employee_id_name") ?? Field(customer, column),
            _ => Field(customer, column)
        };

        if (AttachmentColumns.Contains(column) && value is string attachment && !string.IsNullOrWhiteSpace(attachment))
        {
            return ExportHyperlinkFactory.Attachment(attachment, baseUrl);
        }

        return value is string text && !PreserveRawColumns.Contains(column) ? TitleCase(text) : value;
    }

    private static string[] ExportColumnsFor(ulong? customerType) =>
        customerType == 1 ? DistributorExportColumns : RetailerExportColumns;

    private static string CustomerTypeName(ulong? type) => type switch
    {
        1 => "Dealer",
        2 => "Retailer",
        3 => "Influencer",
        null => "All",
        _ => $"Type-{type}"
    };

    private static string? Field(CustomerDto customer, string key) =>
        customer.CustomFields.TryGetValue(key, out var value) ? value : null;

    private static string RetailerApprovalStatus(CustomerDto customer)
    {
        var status = Field(customer, "status")?.Trim();
        return string.IsNullOrWhiteSpace(status) || status == "-" ? "PENDING" : status;
    }

    private static string? CustomerGpsLocation(CustomerDto customer)
    {
        var stored = FirstNonBlank(Field(customer, "gps_location"), Field(customer, "gps"));
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        if (string.IsNullOrWhiteSpace(customer.Latitude) || string.IsNullOrWhiteSpace(customer.Longitude)) return null;
        return $"{customer.Latitude.Trim()},{customer.Longitude.Trim()}";
    }

    private static string? GoogleMapsUrl(string? gpsLocation)
    {
        if (string.IsNullOrWhiteSpace(gpsLocation)) return null;
        var coordinates = gpsLocation.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (coordinates.Length != 2
            || !decimal.TryParse(coordinates[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            || !decimal.TryParse(coordinates[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        return $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString($"{coordinates[0]},{coordinates[1]}")}";
    }

    private static string[] CustomerMobiles(CustomerDto customer) =>
        new[]
        {
            customer.Mobile,
            Field(customer, "mobile_number"),
            Field(customer, "mobile_numbers"),
            customer.ContactNumber,
            Field(customer, "whatsapp_number"),
            Field(customer, "alternate_mobile")
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .SelectMany(value => value!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(2)
        .ToArray();

    private static string NormalizeKycDocumentKey(string documentKey)
    {
        var key = NormalizeText(documentKey)?.ToLowerInvariant();
        if (key is null || !KycDocumentKeys.Contains(key))
        {
            throw new LaravelHttpException(LaravelStatusCodes.BadRequest, "Invalid KYC document.");
        }

        return key;
    }

    private static string NormalizeApprovalStatus(string? status)
    {
        var normalized = NormalizeText(status)?.ToUpperInvariant();
        if (normalized is "APPROVED" or "REJECTED" or "PENDING") return normalized;
        throw new LaravelHttpException(LaravelStatusCodes.NoContentLikeValidation, "Status must be APPROVED, REJECTED, or PENDING.");
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static void SetField(IDictionary<string, string?> fields, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) fields[key] = value.Trim();
    }

    private static string? ReadField(IReadOnlyDictionary<string, string?>? fields, string key) =>
        fields is not null && fields.TryGetValue(key, out var value) ? value : null;

    private static ulong? ReadULong(IReadOnlyDictionary<string, string?>? fields, string key) =>
        ulong.TryParse(ReadField(fields, key), out var parsed) ? parsed : null;

    private static IEnumerable<ulong> ReadULongs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (var part in value.Trim().Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ulong.TryParse(part.Trim().Trim('"', '\''), out var parsed) && parsed > 0) yield return parsed;
        }
    }

    private static IEnumerable<string> SplitImportValues(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (var part in value.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var code = part.Trim();
            if (code.Length > 0) yield return code;
        }
    }

    private static Dictionary<string, string?> MergeImportFields(
        IReadOnlyDictionary<string, string?> existing,
        IReadOnlyDictionary<string, string?>? imported)
    {
        var merged = new Dictionary<string, string?>(existing, StringComparer.OrdinalIgnoreCase);
        if (imported is null) return merged;
        foreach (var field in imported.Where(field => !string.IsNullOrWhiteSpace(field.Value)))
        {
            merged[field.Key] = field.Value!.Trim();
        }
        return merged;
    }

    private static string? JsonIdArray(string? value)
    {
        var ids = ReadULongs(value).Distinct().ToArray();
        return ids.Length == 0 ? null : $"[{string.Join(',', ids)}]";
    }

    private static MasterDataFileDto CreateWorkbook(string fileName, string[] headings, IEnumerable<object?[]> rows, bool preserveHeadings = false)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Sheet1");
        worksheet.Style.Font.FontName = "Calibri";
        worksheet.Style.Font.FontSize = 9;
        for (var column = 0; column < headings.Length; column++)
        {
            worksheet.Cell(1, column + 1).Value = preserveHeadings ? headings[column] : TitleCaseHeading(headings[column]);
            worksheet.Cell(1, column + 1).Style.Font.Bold = true;
            if (preserveHeadings)
            {
                worksheet.Cell(1, column + 1).Style.Font.FontColor = XLColor.White;
                worksheet.Cell(1, column + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                worksheet.Cell(1, column + 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }
        }

        var rowNumber = 2;
        foreach (var row in rows)
        {
            for (var column = 0; column < row.Length; column++)
            {
                SetCellValue(worksheet.Cell(rowNumber, column + 1), row[column]);
            }

            rowNumber++;
        }

        worksheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return new MasterDataFileDto { FileName = fileName, Content = stream.ToArray() };
    }

    private static void SetCellValue(IXLCell cell, object? value)
    {
        if (value is ExportHyperlink link)
        {
            cell.Value = link.Text;
            cell.SetHyperlink(new XLHyperlink(new Uri(link.Url)));
            return;
        }

        cell.Value = XLCellValue.FromObject(value);
    }

    private static async Task<MasterDataImportResultDto> ImportRowsAsync(Stream fileStream, Func<ExcelRow, Task<bool>> importRow, CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook(fileStream);
        var worksheet = workbook.Worksheets.First();
        var headerRow = worksheet.FirstRowUsed() ?? throw new LaravelHttpException(LaravelStatusCodes.BadRequest, "Import file is empty.");
        var headings = ReadImportHeadings(headerRow);
        var totalRows = 0;
        var importedRows = 0;
        var updatedRows = 0;
        var errors = new List<string>();

        foreach (var worksheetRow in worksheet.RowsUsed().Where(row => row.RowNumber() > headerRow.RowNumber()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (worksheetRow.CellsUsed().All(cell => string.IsNullOrWhiteSpace(cell.GetString()))) continue;

            totalRows++;
            try
            {
                if (await importRow(new ExcelRow(worksheetRow, headings))) updatedRows++;
                else importedRows++;
            }
            catch (Exception exception) when (exception is LaravelHttpException or FormatException or InvalidOperationException)
            {
                errors.Add($"Row {worksheetRow.RowNumber()}: {exception.Message}");
            }
        }

        return new MasterDataImportResultDto { TotalRows = totalRows, ImportedRows = importedRows, UpdatedRows = updatedRows, FailedRows = errors.Count, Errors = errors };
    }

    private static string NormalizeHeading(string heading) =>
        heading.Trim()
            .ToLowerInvariant()
            .Replace("(json)", "")
            .Replace(" ", "_")
            .TrimEnd('_')
            .Replace("agri_dealer", "agri_distributor")
            .Replace("dealer_name", "distributor_name")
            .Replace("dealer_code", "distributor_code")
            .Replace("domestic_distributor_id", "distributor_id")
            .Replace("domestic_distributor_code", "distributor_code");

    private static IReadOnlyDictionary<string, int> ReadImportHeadings(IXLRow headerRow)
    {
        var headings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.CellsUsed())
        {
            var key = NormalizeHeading(cell.GetString());
            if (string.IsNullOrWhiteSpace(key)) continue;

            if (!headings.TryAdd(key, cell.Address.ColumnNumber))
            {
                // Laravel's retailer export used "Distributor Code" twice: the
                // first is domestic and the second is agri. Keep those existing
                // workbooks importable instead of failing on duplicate headings.
                if (key.Equals("distributor_code", StringComparison.OrdinalIgnoreCase))
                {
                    headings.TryAdd("agri_distributor_code", cell.Address.ColumnNumber);
                    continue;
                }

                var suffix = 2;
                while (!headings.TryAdd($"{key}_{suffix}", cell.Address.ColumnNumber)) suffix++;
            }
        }

        return headings;
    }

    private static string TitleCaseHeading(string heading) =>
        TitleCase(heading
            .Replace("agri_distributor", "agri_dealer")
            .Replace("distributor_name", "dealer_name")
            .Replace("distributor_code", "dealer_code")
            .Replace("_", " "));

    private static string TitleCase(string value)
    {
        var text = NormalizeText(value);
        return text is null ? string.Empty : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
    }

    private static async Task<T> GetOrThrowAsync<T>(Task<T?> task, string message)
    {
        var value = await task;
        return value ?? throw NotFound(message);
    }

    private static void RequireValue(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new LaravelHttpException(LaravelStatusCodes.BadRequest, message);
    }

    private static void RequireId(ulong? value, string message)
    {
        if (value is null or 0) throw new LaravelHttpException(LaravelStatusCodes.BadRequest, message);
    }

    private static LaravelHttpException NotFound(string message) => new(LaravelStatusCodes.NotFound, message);

    private sealed class ExcelRow
    {
        private readonly IXLRow _row;
        private readonly IReadOnlyDictionary<string, int> _headings;

        public ExcelRow(IXLRow row, IReadOnlyDictionary<string, int> headings)
        {
            _row = row;
            _headings = headings;
        }

        public string? Value(string heading)
        {
            return _headings.TryGetValue(NormalizeHeading(heading), out var column)
                ? NormalizeText(_row.Cell(column).GetFormattedString())
                : null;
        }

        public bool HasHeading(string heading) => _headings.ContainsKey(NormalizeHeading(heading));

        public ulong? ULong(string heading)
        {
            var value = Value(heading);
            if (string.IsNullOrWhiteSpace(value)) return null;
            return ulong.TryParse(value, out var parsed) ? parsed : throw new FormatException($"{heading} must be numeric.");
        }

        public ulong? CustomerType(string heading)
        {
            var value = Value(heading);
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (ulong.TryParse(value, out var parsed)) return parsed;

            return value.Trim().ToLowerInvariant() switch
            {
                "dealer" => 1,
                "distributor" => 1,
                "retailer" => 2,
                "influencer" => 3,
                "influencers" => 3,
                _ => throw new FormatException($"{heading} must be Dealer, Retailer, Influencer, 1, 2, or 3.")
            };
        }
    }

    private static string? NormalizeText(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
