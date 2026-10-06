using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using Application.Common;
using Application.DTOs.LoyaltySchemes;
using Application.DTOs.MasterData;
using Application.Interfaces.Repositories;
using Application.Interfaces.Services;
using Domain.Entities;
using Shared.Exceptions;
using Shared.Responses;
using Domain.Services;

namespace Application.Services;

public sealed class LoyaltySchemeService : ILoyaltySchemeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SchemeTags = ["Regular", "Booster"];
    private static readonly string[] CustomerTypes = ["Dealer", "Retailer", "Influencer"];
    private static readonly string[] AreaScopes = ["All", "Branch", "Zone", "State", "Customer"];
    private static readonly string[] BasedOnOptions = [SchemeReward.Value, SchemeReward.Percentage, SchemeReward.Mixed];
    /// <summary>What the scheme is read on. Invoice weighs the whole bill and carries
    /// slabs; Product and Quantity are read on the goods and carry product lines instead.</summary>
    private static readonly string[] SchemeTypes = [SchemeKinds.Invoice, SchemeKinds.Product, SchemeKinds.Quantity];

    private const int SchemeNoteMaxLength = 500;
    private readonly ILoyaltySchemeRepository _repository;

    public LoyaltySchemeService(ILoyaltySchemeRepository repository)
    {
        _repository = repository;
    }

    public async Task<LaravelApiResponse> GetSchemesAsync(LoyaltySchemeFilterDto filter, CancellationToken cancellationToken) =>
        LaravelApiResponse.Success("schemes", await _repository.GetSchemesAsync(filter, cancellationToken));

    /// <summary>
    /// The scheme listing as a workbook, carrying the whole trail: who wrote the scheme,
    /// who sent it on, who approved or rejected it, and who published it, each with the
    /// moment it happened. Published by and at are blank on anything published before
    /// those columns existed; that moment was never recorded and is not guessed at.
    /// </summary>
    public async Task<MasterDataFileDto> ExportSchemesAsync(LoyaltySchemeFilterDto filter, CancellationToken cancellationToken)
    {
        var schemes = await _repository.GetSchemesAsync(filter, cancellationToken);
        var fileName = $"loyalty-schemes-{IndiaToday():yyyy-MM-dd}.xlsx";

        return ExportWorkbook.Create(fileName,
            ["scheme_name", "scheme_code", "scheme_tag", "customer_type", "area", "scheme_type", "based_on",
             "start_date", "end_date", "status", "slabs", "note",
             "created_by", "created_at", "submitted_by", "submitted_at",
             "approved_by", "approved_at", "approval_remark",
             "published_by", "published_at",
             "rejected_by", "rejected_at", "rejection_remark"],
            schemes.Select(x => new object?[]
            {
                x.SchemeName,
                x.SchemeCode,
                x.SchemeTag,
                x.CustomerType,
                x.AreaDisplay,
                x.SchemeType,
                x.BasedOn,
                x.StartDate,
                x.EndDate,
                x.Status,
                x.Slabs.Count,
                x.SchemeNote,
                x.CreatedByName,
                x.CreatedAt,
                x.SubmittedByName,
                x.SubmittedAt,
                x.ApprovedByName,
                x.ApprovedAt,
                x.ApprovalRemark,
                x.PublishedByName,
                x.PublishedAt,
                x.RejectedByName,
                x.RejectedAt,
                x.RejectionRemark
            }));
    }

    public async Task<LaravelApiResponse> GetSchemeAsync(ulong id, CancellationToken cancellationToken) =>
        LaravelApiResponse.Success("scheme", await GetOrThrowAsync(id, cancellationToken));

    public async Task<LaravelApiResponse> GetOptionsAsync(CancellationToken cancellationToken) =>
        LaravelApiResponse.Success("options", await _repository.GetOptionsAsync(cancellationToken));

    public async Task<LaravelApiResponse> GetDealerOptionsAsync(CancellationToken cancellationToken) =>
        LaravelApiResponse.Success("dealers", await _repository.GetDealerOptionsAsync(cancellationToken));

    public async Task<LaravelApiResponse> GenerateSchemeCodeAsync(string? schemeName, string? schemeTag, string? basedOn, string? schemeType, CancellationToken cancellationToken)
    {
        var prefix = BuildSchemeCodePrefix(schemeName, schemeTag, basedOn, schemeType, DateTime.UtcNow.Year);
        var lastCode = await _repository.GetLastSchemeCodeAsync(prefix, cancellationToken);
        var nextSequence = LastSequence(lastCode, prefix) + 1;

        if (nextSequence <= 1 && string.IsNullOrWhiteSpace(lastCode))
        {
            nextSequence = Random.Shared.Next(1, 100);
        }

        var code = $"{prefix}-{nextSequence:00}";
        while (await _repository.SchemeCodeExistsAsync(code, null, cancellationToken))
        {
            nextSequence++;
            code = $"{prefix}-{nextSequence:00}";
        }

        return LaravelApiResponse.Success("scheme_code", code);
    }

    public async Task<LaravelApiResponse> CreateSchemeAsync(LoyaltySchemeRequestDto request, ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) throw Http(LaravelStatusCodes.Unauthorized, "Unauthenticated.");
        await ValidateRequestAsync(request, null, true, cancellationToken);
        var schemeCode = string.IsNullOrWhiteSpace(request.SchemeCode)
            ? await GenerateUniqueSchemeCodeAsync(request.SchemeName, request.SchemeTag, request.BasedOn, request.SchemeType, cancellationToken)
            : request.SchemeCode.Trim().ToUpperInvariant();

        var now = DateTime.UtcNow;
        var scheme = new LoyaltyScheme
        {
            Active = NormalizeActive(request.Active),
            SchemeName = request.SchemeName!.Trim(),
            SchemeCode = schemeCode,
            SchemeDescription = NormalizeText(request.SchemeDescription),
            SchemeNote = NormalizeText(request.SchemeNote),
            SchemeTag = NormalizeChoice(request.SchemeTag, "Regular", SchemeTags),
            CustomerType = NormalizeChoice(request.CustomerType, string.Empty, CustomerTypes),
            AreaScope = NormalizeChoice(request.AreaScope, "All", AreaScopes),
            AreaValues = SerializeAreaValues(request.AreaScope, request.AreaValues),
            ExcludedDealerIds = SerializeExcludedDealerIds(request.ExcludedDealerIds),
            StartDate = request.StartDate!.Value,
            EndDate = request.EndDate!.Value,
            SchemeType = NormalizeChoice(request.SchemeType, SchemeKinds.Invoice, SchemeTypes),
            BasedOn = NormalizeChoice(request.BasedOn, SchemeReward.Value, BasedOnOptions),
            RedemptionEnabled = request.RedemptionEnabled,
            Status = "Draft",
            CreatedBy = actorUserId,
            UpdatedBy = actorUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // A scheme keeps only what its own type is read on, so a type switched during
        // editing cannot leave slabs behind that still pay.
        var createBasedOn = NormalizeChoice(request.BasedOn, SchemeReward.Value, BasedOnOptions);
        if (SchemeKinds.ReadsProducts(scheme.SchemeType))
            scheme.Products = MapProductLines(request.Products, createBasedOn, now);
        else
            scheme.Slabs = MapSlabs(request.Slabs, createBasedOn, now);

        var created = await _repository.CreateSchemeAsync(scheme, cancellationToken);
        return LaravelApiResponse.Success("scheme", created, "Scheme created successfully");
    }

    public async Task<LaravelApiResponse> UpdateSchemeAsync(ulong id, LoyaltySchemeRequestDto request, ulong? actorUserId, bool isSuperAdmin, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) throw Http(LaravelStatusCodes.Unauthorized, "Unauthenticated.");
        var scheme = await FindOrThrowAsync(id, cancellationToken);
        if (!isSuperAdmin && string.Equals(scheme.Status, "Published", StringComparison.OrdinalIgnoreCase))
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "A published scheme can only be edited by superadmin.");
        await ValidateRequestAsync(request, id, true, cancellationToken);

        var now = DateTime.UtcNow;
        scheme.Active = NormalizeActive(request.Active);
        scheme.SchemeName = request.SchemeName!.Trim();
        scheme.SchemeDescription = NormalizeText(request.SchemeDescription);
        scheme.SchemeNote = NormalizeText(request.SchemeNote);
        scheme.SchemeTag = NormalizeChoice(request.SchemeTag, "Regular", SchemeTags);
        scheme.CustomerType = NormalizeChoice(request.CustomerType, string.Empty, CustomerTypes);
        scheme.AreaScope = NormalizeChoice(request.AreaScope, "All", AreaScopes);
        scheme.AreaValues = SerializeAreaValues(request.AreaScope, request.AreaValues);
        scheme.ExcludedDealerIds = SerializeExcludedDealerIds(request.ExcludedDealerIds);
        scheme.StartDate = request.StartDate!.Value;
        scheme.EndDate = request.EndDate!.Value;
        scheme.SchemeType = NormalizeChoice(request.SchemeType, SchemeKinds.Invoice, SchemeTypes);
        scheme.BasedOn = NormalizeChoice(request.BasedOn, SchemeReward.Value, BasedOnOptions);
        scheme.RedemptionEnabled = request.RedemptionEnabled;
        // Approval is a separate permission-gated action. Editing must never
        // publish or demote a scheme through a client-supplied status.
        scheme.UpdatedBy = actorUserId;
        scheme.UpdatedAt = now;
        // Both sides are retired first. A scheme switched from Invoice to Product must not
        // keep slabs that would still be read, and the other way round.
        foreach (var existingSlab in scheme.Slabs.Where(slab => slab.DeletedAt == null))
        {
            existingSlab.DeletedAt = now;
            existingSlab.UpdatedAt = now;
        }
        foreach (var existingLine in scheme.Products.Where(line => line.DeletedAt == null))
        {
            existingLine.DeletedAt = now;
            existingLine.UpdatedAt = now;
        }

        if (SchemeKinds.ReadsProducts(scheme.SchemeType))
        {
            foreach (var line in MapProductLines(request.Products, scheme.BasedOn, now)) scheme.Products.Add(line);
        }
        else
        {
            foreach (var slab in MapSlabs(request.Slabs, scheme.BasedOn, now)) scheme.Slabs.Add(slab);
        }

        var updated = await _repository.SaveSchemeAsync(scheme, cancellationToken);
        return LaravelApiResponse.Success("scheme", updated, "Scheme updated successfully");
    }

    public async Task<LaravelApiResponse> SendToDraftAsync(ulong id, ulong? actorUserId, bool isSuperAdmin, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) throw Http(LaravelStatusCodes.Unauthorized, "Unauthenticated.");
        var scheme = await FindOrThrowAsync(id, cancellationToken);
        if (string.Equals(scheme.Status, "Draft", StringComparison.OrdinalIgnoreCase))
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "Scheme is already in draft.");
        if (!isSuperAdmin && string.Equals(scheme.Status, "Published", StringComparison.OrdinalIgnoreCase))
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "A published scheme can only be returned to draft by superadmin.");

        scheme.Status = "Draft";
        scheme.SubmittedAt = null;
        scheme.SubmittedBy = null;
        scheme.ApprovedAt = null;
        scheme.ApprovedBy = null;
        scheme.ApprovalRemark = null;
        scheme.RejectedAt = null;
        scheme.RejectedBy = null;
        scheme.RejectionRemark = null;
        scheme.UpdatedBy = actorUserId;
        scheme.UpdatedAt = DateTime.UtcNow;
        var updated = await _repository.SaveSchemeAsync(scheme, cancellationToken);
        return LaravelApiResponse.Success("scheme", updated, "Scheme returned to draft successfully");
    }

    public async Task<LaravelApiResponse> ApproveSchemeAsync(ulong id, string? remark, ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) throw Http(LaravelStatusCodes.Unauthorized, "Unauthenticated.");
        var scheme = await FindOrThrowAsync(id, cancellationToken);
        if (!string.Equals(scheme.Status, "Pending Approval", StringComparison.OrdinalIgnoreCase))
        {
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "Only schemes pending approval can be approved.");
        }
        if (scheme.EndDate < IndiaToday())
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "An expired scheme cannot be approved.");

        scheme.Status = "Approved";
        scheme.ApprovedAt = DateTime.UtcNow;
        scheme.ApprovedBy = actorUserId;
        scheme.ApprovalRemark = NormalizeText(remark);
        scheme.UpdatedBy = actorUserId;
        scheme.UpdatedAt = DateTime.UtcNow;
        var updated = await _repository.SaveSchemeAsync(scheme, cancellationToken);
        return LaravelApiResponse.Success("scheme", updated, "Scheme approved successfully");
    }

    public async Task<LaravelApiResponse> SubmitSchemeAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) throw Http(LaravelStatusCodes.Unauthorized, "Unauthenticated.");
        var scheme = await FindOrThrowAsync(id, cancellationToken);
        if (scheme.Status is not ("Draft" or "Rejected"))
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "Only draft or rejected schemes can be submitted.");
        if (scheme.EndDate < IndiaToday())
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "An expired scheme cannot be submitted.");

        scheme.Status = "Pending Approval";
        scheme.SubmittedAt = DateTime.UtcNow;
        scheme.SubmittedBy = actorUserId;
        scheme.RejectedAt = null;
        scheme.RejectedBy = null;
        scheme.RejectionRemark = null;
        scheme.UpdatedBy = actorUserId;
        var updated = await _repository.SaveSchemeAsync(scheme, cancellationToken);
        return LaravelApiResponse.Success("scheme", updated, "Scheme submitted for approval");
    }

    public async Task<LaravelApiResponse> RejectSchemeAsync(ulong id, string? remark, ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) throw Http(LaravelStatusCodes.Unauthorized, "Unauthenticated.");
        if (string.IsNullOrWhiteSpace(remark))
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "Rejection remark is required.");
        var scheme = await FindOrThrowAsync(id, cancellationToken);
        if (!string.Equals(scheme.Status, "Pending Approval", StringComparison.OrdinalIgnoreCase))
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "Only schemes pending approval can be rejected.");

        scheme.Status = "Rejected";
        scheme.RejectedAt = DateTime.UtcNow;
        scheme.RejectedBy = actorUserId;
        scheme.RejectionRemark = remark.Trim();
        scheme.UpdatedBy = actorUserId;
        var updated = await _repository.SaveSchemeAsync(scheme, cancellationToken);
        return LaravelApiResponse.Success("scheme", updated, "Scheme rejected");
    }

    public async Task<LaravelApiResponse> PublishSchemeAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) throw Http(LaravelStatusCodes.Unauthorized, "Unauthenticated.");
        var scheme = await FindOrThrowAsync(id, cancellationToken);
        if (!string.Equals(scheme.Status, "Approved", StringComparison.OrdinalIgnoreCase))
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "Only approved schemes can be published.");
        if (scheme.EndDate < IndiaToday())
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "An expired scheme cannot be published.");

        scheme.Status = "Published";
        scheme.PublishedAt = DateTime.UtcNow;
        scheme.PublishedBy = actorUserId;
        scheme.UpdatedBy = actorUserId;
        var updated = await _repository.SaveSchemeAsync(scheme, cancellationToken);
        return LaravelApiResponse.Success("scheme", updated, "Scheme published successfully");
    }

    public async Task<LaravelApiResponse> SetBrochureAsync(ulong id, string brochurePath, ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) throw Http(LaravelStatusCodes.Unauthorized, "Unauthenticated.");
        var scheme = await FindOrThrowAsync(id, cancellationToken);
        scheme.BrochurePath = brochurePath;
        scheme.UpdatedBy = actorUserId;
        var updated = await _repository.SaveSchemeAsync(scheme, cancellationToken);
        return LaravelApiResponse.Success("scheme", updated, "Scheme brochure uploaded successfully");
    }

    public async Task<LaravelApiResponse> DeleteSchemeAsync(ulong id, ulong? actorUserId, bool isSuperAdmin, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) throw Http(LaravelStatusCodes.Unauthorized, "Unauthenticated.");
        var scheme = await FindOrThrowAsync(id, cancellationToken);
        if (!isSuperAdmin && scheme.Status is not ("Draft" or "Rejected"))
            throw Http(LaravelStatusCodes.NoContentLikeValidation, "Only draft or rejected schemes can be deleted.");
        scheme.UpdatedBy = actorUserId;
        await _repository.DeleteSchemeAsync(scheme, cancellationToken);
        return LaravelApiResponse.MessageOnly("success", "Scheme and related configuration soft-deleted successfully");
    }

    private async Task ValidateRequestAsync(LoyaltySchemeRequestDto request, ulong? exceptId, bool allowBlankCode, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.SchemeName)) errors["scheme_name"] = ["Scheme name is required."];
        if (!allowBlankCode && string.IsNullOrWhiteSpace(request.SchemeCode)) errors["scheme_code"] = ["Scheme code is required."];
        if (!request.StartDate.HasValue) errors["start_date"] = ["Start date is required."];
        if (!request.EndDate.HasValue) errors["end_date"] = ["End date is required."];
        if (request.StartDate.HasValue && request.EndDate.HasValue && request.EndDate.Value < request.StartDate.Value) errors["end_date"] = ["End date must be after start date."];
        AddChoiceError(errors, "scheme_tag", request.SchemeTag, "Regular", SchemeTags, "Invalid scheme tag.");
        AddChoiceError(errors, "customer_type", request.CustomerType, string.Empty, CustomerTypes, "Invalid customer type.");
        AddChoiceError(errors, "area_scope", request.AreaScope, "All", AreaScopes, "Invalid area scope.");
        AddChoiceError(errors, "based_on", request.BasedOn, SchemeReward.Value, BasedOnOptions, "Invalid based on value.");
        AddChoiceError(errors, "scheme_type", request.SchemeType, SchemeKinds.Invoice, SchemeTypes, "Invalid scheme type.");

        var schemeType = NormalizeChoice(request.SchemeType, SchemeKinds.Invoice, SchemeTypes);
        // Quantity counts units, and a percentage of a count is not a reward anyone can
        // settle. Such a scheme pays a rate per unit, so only a flat value is allowed.
        if (SchemeKinds.IsQuantity(schemeType) && !string.Equals(
                NormalizeChoice(request.BasedOn, SchemeReward.Value, BasedOnOptions), SchemeReward.Value, StringComparison.OrdinalIgnoreCase))
        {
            errors["based_on"] = ["A Quantity scheme pays a rate per unit, so Based On must be Value."];
        }

        // The note is meant to be a couple of lines under the scheme dates, and the
        // column holds 500 characters. Saying so beats a truncation error from SQL.
        if (request.SchemeNote is { Length: > SchemeNoteMaxLength })
        {
            errors["scheme_note"] = [$"Note cannot be longer than {SchemeNoteMaxLength} characters."];
        }

        var areaScope = NormalizeChoice(request.AreaScope, "All", AreaScopes);
        if (!string.Equals(areaScope, "All", StringComparison.OrdinalIgnoreCase) && (request.AreaValues is null || request.AreaValues.Length == 0))
        {
            errors["area_values"] = ["Select at least one area value."];
        }

        if (!SchemeKinds.IsInvoice(schemeType))
        {
            await ValidateProductLinesAsync(request, schemeType, errors, cancellationToken);
        }
        else if (request.Slabs.Count == 0)
        {
            errors["slabs"] = ["At least one slab is required."];
        }
        else
        {
            decimal? previousTo = null;
            for (var index = 0; index < request.Slabs.Count; index++)
            {
                var slab = request.Slabs[index];
                var prefix = $"slabs.{index}";
                if (string.IsNullOrWhiteSpace(slab.TierName)) errors[$"{prefix}.tier_name"] = ["Tier name is required."];
                if (!slab.ValueFrom.HasValue || slab.ValueFrom.Value < 0) errors[$"{prefix}.value_from"] = ["Value from is required and cannot be negative."];
                if (slab.ValueTo.HasValue && slab.ValueFrom.HasValue && slab.ValueTo.Value < slab.ValueFrom.Value) errors[$"{prefix}.value_to"] = ["Value to must be greater than or equal to value from."];
                if (!slab.RewardValue.HasValue || slab.RewardValue.Value < 0) errors[$"{prefix}.reward_value"] = ["Reward value is required and cannot be negative."];
                // A mixed scheme is checked slab by slab, since the one below it may be
                // a flat amount and the one above a percentage.
                var slabIsPercentage = SchemeReward.IsMixedScheme(request.BasedOn)
                    ? SchemeReward.IsPercentage(slab.RewardType)
                    : SchemeReward.IsPercentage(request.BasedOn);

                if (SchemeReward.IsMixedScheme(request.BasedOn)
                    && !string.IsNullOrWhiteSpace(slab.RewardType)
                    && !SchemeReward.IsPercentage(slab.RewardType)
                    && !string.Equals(slab.RewardType.Trim(), SchemeReward.Value, StringComparison.OrdinalIgnoreCase))
                {
                    errors[$"{prefix}.reward_type"] = ["Reward type must be Value or Percentage."];
                }

                if (slabIsPercentage && slab.RewardValue.HasValue && slab.RewardValue.Value > 99.9m)
                {
                    errors[$"{prefix}.reward_value"] = ["Reward percentage can contain a maximum of four characters including the decimal (maximum 99.9)."];
                }
                if (!slabIsPercentage && slab.RewardValue.HasValue && slab.RewardValue.Value > 10000000)
                {
                    errors[$"{prefix}.reward_value"] = ["Reward amount cannot be greater than 1,00,00,000."];
                }
                if (index > 0 && previousTo is null)
                    errors[$"{prefix}.value_from"] = ["The previous slab must have a value to before another slab is added."];
                else if (index > 0 && slab.ValueFrom.HasValue && slab.ValueFrom.Value != previousTo!.Value + 1)
                    errors[$"{prefix}.value_from"] = [$"Value from must be {previousTo.Value + 1} to avoid overlaps or gaps."];
                previousTo = slab.ValueTo;
            }
        }

        if (errors.Count > 0) throw Http(LaravelStatusCodes.NoContentLikeValidation, errors);

        if (!string.IsNullOrWhiteSpace(request.SchemeCode)
            && await _repository.SchemeCodeExistsAsync(request.SchemeCode.Trim().ToUpperInvariant(), exceptId, cancellationToken))
        {
            throw Http(LaravelStatusCodes.NoContentLikeValidation, new { scheme_code = new[] { "This scheme code already exists." } });
        }
    }

    /// <summary>The columns the product lines are written and read in. Names travel with
    /// the ids so the sheet can be read by a person; on the way back in the ids decide, and
    /// a renamed product cannot break an import.</summary>
    private static readonly string[] ProductLineColumns =
        ["segment_ids", "segment_names", "family_ids", "family_names", "product_ids", "product_names", "reward"];

    /// <summary>
    /// The blank sheet the scheme form hands out, with a second tab listing every segment
    /// and family by id. There are three segments and a hundred families; nobody should
    /// have to guess an id, and the alternative is a hundred lookups by hand.
    /// </summary>
    public async Task<MasterDataFileDto> ProductLineTemplateAsync(CancellationToken cancellationToken)
    {
        var segments = await _repository.GetProductSegmentsAsync(cancellationToken);
        var families = await _repository.GetProductFamiliesAsync(cancellationToken);

        var file = ExportWorkbook.Create($"scheme-product-lines-template-{IndiaToday():yyyy-MM-dd}.xlsx",
            ProductLineColumns, []);

        using var stream = new MemoryStream(file.Content);
        using var workbook = new XLWorkbook(stream);
        var reference = workbook.AddWorksheet("Segments and Families");
        reference.Style.Font.FontName = "Calibri";
        reference.Style.Font.FontSize = 9;
        reference.Cell(1, 1).Value = "Segment Id";
        reference.Cell(1, 2).Value = "Segment Name";
        reference.Cell(1, 4).Value = "Family Id";
        reference.Cell(1, 5).Value = "Family Name";
        reference.Cell(1, 6).Value = "Belongs To Segment";
        reference.Range(1, 1, 1, 6).Style.Font.Bold = true;

        var row = 2;
        foreach (var segment in segments)
        {
            reference.Cell(row, 1).Value = segment.Id;
            reference.Cell(row, 2).Value = segment.Name;
            row++;
        }
        row = 2;
        foreach (var family in families)
        {
            reference.Cell(row, 4).Value = family.Id;
            reference.Cell(row, 5).Value = family.Name;
            reference.Cell(row, 6).Value = family.ParentName;
            row++;
        }
        reference.Columns().AdjustToContents();

        using var saved = new MemoryStream();
        workbook.SaveAs(saved);
        return new MasterDataFileDto { FileName = file.FileName, Content = saved.ToArray() };
    }

    /// <summary>One scheme's lines, in the same columns the import reads - so a scheme can
    /// be exported, edited in Excel and sent straight back.</summary>
    public async Task<MasterDataFileDto> ExportProductLinesAsync(ulong schemeId, CancellationToken cancellationToken)
    {
        var scheme = await _repository.GetSchemeAsync(schemeId, cancellationToken)
            ?? throw Http(LaravelStatusCodes.NotFound, "Scheme not found.");

        return ExportWorkbook.Create(
            $"{scheme.SchemeCode}-product-lines-{IndiaToday():yyyy-MM-dd}.xlsx",
            ProductLineColumns,
            scheme.Products.Select(line => new object?[]
            {
                string.Join(',', line.SegmentIds),
                string.Join(", ", line.SegmentNames),
                string.Join(',', line.FamilyIds),
                string.Join(", ", line.FamilyNames),
                string.Join(',', line.ProductIds),
                string.Join(", ", line.ProductNames),
                line.RewardValue
            }));
    }

    /// <summary>
    /// Reads an edited sheet back into lines the form can show.
    ///
    /// Nothing is saved here - the form merges what comes back with what is already on it
    /// and the scheme is written when the form is saved. That is what lets the same import
    /// serve a scheme that does not exist yet.
    ///
    /// The ids decide; the name columns are there to be read and are ignored.
    /// </summary>
    public async Task<LaravelApiResponse> ImportProductLinesAsync(Stream stream, CancellationToken cancellationToken)
    {
        XLWorkbook workbook;
        try { workbook = new XLWorkbook(stream); }
        catch (Exception) { throw Http(LaravelStatusCodes.BadRequest, "That file could not be read as an Excel workbook."); }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault()
                ?? throw Http(LaravelStatusCodes.BadRequest, "The workbook has no sheet in it.");
            var headerRow = sheet.FirstRowUsed()
                ?? throw Http(LaravelStatusCodes.BadRequest, "The sheet is empty.");
            var headings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in headerRow.CellsUsed())
            {
                var key = cell.GetString().Trim().ToLowerInvariant().Replace(" ", "_");
                if (key.Length > 0) headings.TryAdd(key, cell.Address.ColumnNumber);
            }

            var master = await _repository.GetProductMasterIdsAsync(cancellationToken);
            var names = await _repository.GetProductNamesAsync(cancellationToken);
            var lines = new List<LoyaltySchemeProductDto>();
            var problems = new List<string>();

            foreach (var row in sheet.RowsUsed().Where(x => x.RowNumber() > headerRow.RowNumber()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string Text(params string[] keys)
                {
                    foreach (var key in keys)
                    {
                        if (headings.TryGetValue(key, out var column)) return row.Cell(column).GetString().Trim();
                    }
                    return string.Empty;
                }

                var segments = ParseIds(Text("segment_ids", "segment_id"));
                var families = ParseIds(Text("family_ids", "family_id"));
                var products = ParseIds(Text("product_ids", "product_id"));
                var rewardText = Text("reward", "reward_value");
                if (segments.Count == 0 && families.Count == 0 && products.Count == 0 && rewardText.Length == 0) continue;

                if (segments.Count == 0 && families.Count == 0 && products.Count == 0)
                {
                    problems.Add($"Row {row.RowNumber()}: name a segment, a family or a product.");
                    continue;
                }
                var unknown = segments.FirstOrDefault(id => !master.Segments.Contains(id));
                if (unknown != 0) { problems.Add($"Row {row.RowNumber()}: segment {unknown} does not exist."); continue; }
                unknown = families.FirstOrDefault(id => !master.Families.Contains(id));
                if (unknown != 0) { problems.Add($"Row {row.RowNumber()}: family {unknown} does not exist."); continue; }
                unknown = products.FirstOrDefault(id => !master.Products.Contains(id));
                if (unknown != 0) { problems.Add($"Row {row.RowNumber()}: product {unknown} does not exist."); continue; }

                if (!decimal.TryParse(rewardText, NumberStyles.Any, CultureInfo.InvariantCulture, out var reward) || reward <= 0)
                {
                    problems.Add($"Row {row.RowNumber()}: reward must be a number greater than 0.");
                    continue;
                }

                lines.Add(new LoyaltySchemeProductDto
                {
                    SegmentIds = [.. segments],
                    SegmentNames = [.. segments.Select(id => names.Segments.GetValueOrDefault(id, string.Empty)).Where(x => x.Length > 0)],
                    FamilyIds = [.. families],
                    FamilyNames = [.. families.Select(id => names.Families.GetValueOrDefault(id, string.Empty)).Where(x => x.Length > 0)],
                    ProductIds = [.. products],
                    ProductNames = [.. products.Select(id => names.Products.GetValueOrDefault(id, string.Empty)).Where(x => x.Length > 0)],
                    RewardValue = reward,
                    // Value or Percentage is the scheme's own choice on the form, not the
                    // sheet's, so the import never carries one.
                    RewardType = null,
                    SortOrder = lines.Count + 1
                });
            }

            return LaravelApiResponse.Success("lines", new
            {
                lines,
                problems,
                message = $"{lines.Count} line(s) read." + (problems.Count > 0 ? $" {problems.Count} row(s) skipped." : string.Empty)
            });
        }
    }

    private static List<ulong> ParseIds(string? value) =>
        (value ?? string.Empty).Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => ulong.TryParse(part, out var id) ? id : 0)
            .Where(id => id > 0).Distinct().ToList();

    /// <summary>
    /// The lines of a Product or Quantity scheme.
    ///
    /// A line has to name something - a segment, a family or a product - and say what it
    /// pays. The narrowest naming wins when the reward is worked out, so a line may name a
    /// whole family and no product at all; what it may not do is name nothing.
    /// </summary>
    private async Task ValidateProductLinesAsync(
        LoyaltySchemeRequestDto request, string schemeType, Dictionary<string, string[]> errors, CancellationToken cancellationToken)
    {
        if (request.Products.Count == 0)
        {
            errors["products"] = [$"A {schemeType} scheme needs at least one product line."];
            return;
        }

        var master = await _repository.GetProductMasterIdsAsync(cancellationToken);
        var basedOn = NormalizeChoice(request.BasedOn, SchemeReward.Value, BasedOnOptions);

        for (var index = 0; index < request.Products.Count; index++)
        {
            var line = request.Products[index];
            var prefix = $"products.{index}";
            var segments = Ids(line.SegmentIds);
            var families = Ids(line.FamilyIds);
            var products = Ids(line.ProductIds);

            if (segments.Count == 0 && families.Count == 0 && products.Count == 0)
            {
                errors[$"{prefix}.product_ids"] = ["Select a segment, a family or a product for this line."];
            }

            var unknownSegment = segments.FirstOrDefault(id => !master.Segments.Contains(id));
            if (unknownSegment != 0) errors[$"{prefix}.segment_ids"] = [$"Segment {unknownSegment} does not exist."];
            var unknownFamily = families.FirstOrDefault(id => !master.Families.Contains(id));
            if (unknownFamily != 0) errors[$"{prefix}.family_ids"] = [$"Family {unknownFamily} does not exist."];
            var unknownProduct = products.FirstOrDefault(id => !master.Products.Contains(id));
            if (unknownProduct != 0) errors[$"{prefix}.product_ids"] = [$"Product {unknownProduct} does not exist."];

            if (!line.RewardValue.HasValue || line.RewardValue.Value <= 0)
            {
                errors[$"{prefix}.reward_value"] = ["Reward is required and must be greater than 0."];
                continue;
            }

            // A mixed scheme is read line by line, the way it is read slab by slab.
            var isPercentage = SchemeReward.IsMixedScheme(basedOn)
                ? SchemeReward.IsPercentage(line.RewardType)
                : SchemeReward.IsPercentage(basedOn);

            if (SchemeReward.IsMixedScheme(basedOn)
                && !string.IsNullOrWhiteSpace(line.RewardType)
                && !SchemeReward.IsPercentage(line.RewardType)
                && !string.Equals(line.RewardType.Trim(), SchemeReward.Value, StringComparison.OrdinalIgnoreCase))
            {
                errors[$"{prefix}.reward_type"] = ["Reward type must be Value or Percentage."];
            }

            if (isPercentage && line.RewardValue.Value > 99.9m)
            {
                errors[$"{prefix}.reward_value"] = ["Reward percentage cannot be more than 99.9."];
            }
            if (!isPercentage && line.RewardValue.Value > 10000000)
            {
                errors[$"{prefix}.reward_value"] = ["Reward amount cannot be greater than 1,00,00,000."];
            }
        }
    }

    private static List<ulong> Ids(ulong[]? values) =>
        (values ?? []).Where(id => id > 0).Distinct().ToList();

    /// <summary>A line only carries its own reward type when the scheme is mixed, exactly
    /// as a slab does.</summary>
    private static List<LoyaltySchemeProduct> MapProductLines(
        IEnumerable<LoyaltySchemeProductRequestDto> lines, string? basedOn, DateTime now)
    {
        var mixed = SchemeReward.IsMixedScheme(basedOn);
        return lines.Select((line, index) => new LoyaltySchemeProduct
        {
            SegmentIds = string.Join(',', Ids(line.SegmentIds)),
            FamilyIds = string.Join(',', Ids(line.FamilyIds)),
            ProductIds = string.Join(',', Ids(line.ProductIds)),
            RewardValue = line.RewardValue ?? 0,
            RewardType = mixed
                ? (SchemeReward.IsPercentage(line.RewardType) ? SchemeReward.Percentage : SchemeReward.Value)
                : null,
            SortOrder = index + 1,
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();
    }

    /// <summary>A slab only carries its own reward type when the scheme is mixed. Any
    /// other scheme stores nothing, so a type left over from an edit cannot change how
    /// that scheme pays.</summary>
    private static List<LoyaltySchemeSlab> MapSlabs(IEnumerable<LoyaltySchemeSlabRequestDto> slabs, string? basedOn, DateTime now)
    {
        var mixed = SchemeReward.IsMixedScheme(basedOn);
        return slabs.Select((slab, index) => new LoyaltySchemeSlab
        {
            TierName = slab.TierName!.Trim(),
            ValueFrom = slab.ValueFrom!.Value,
            ValueTo = slab.ValueTo,
            RewardValue = slab.RewardValue!.Value,
            RewardType = mixed
                ? (SchemeReward.IsPercentage(slab.RewardType) ? SchemeReward.Percentage : SchemeReward.Value)
                : null,
            SortOrder = index + 1,
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();
    }

    private async Task<LoyaltySchemeDto> GetOrThrowAsync(ulong id, CancellationToken cancellationToken) =>
        await _repository.GetSchemeAsync(id, cancellationToken) ?? throw Http(LaravelStatusCodes.NotFound, "Scheme not found");

    private async Task<LoyaltyScheme> FindOrThrowAsync(ulong id, CancellationToken cancellationToken) =>
        await _repository.FindSchemeEntityAsync(id, cancellationToken) ?? throw Http(LaravelStatusCodes.NotFound, "Scheme not found");

    /// <summary>The excluded dealers, as their customer ids. Ids rather than names because a
    /// dealer can be renamed and the scheme must still leave out the same dealers.</summary>
    private static string SerializeExcludedDealerIds(ulong[]? dealerValues)
    {
        var ids = (dealerValues ?? []).Where(x => x > 0).Distinct().OrderBy(x => x).ToArray();
        return ids.Length == 0 ? "[]" : JsonSerializer.Serialize(ids);
    }

    private static string SerializeAreaValues(string? areaScope, string[]? areaValues)
    {
        var scope = NormalizeChoice(areaScope, "All", AreaScopes);
        if (string.Equals(scope, "All", StringComparison.OrdinalIgnoreCase)) return "[]";

        var values = (areaValues ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return JsonSerializer.Serialize(values, JsonOptions);
    }

    private async Task<string> GenerateUniqueSchemeCodeAsync(string? schemeName, string? schemeTag, string? basedOn, string? schemeType, CancellationToken cancellationToken)
    {
        var prefix = BuildSchemeCodePrefix(schemeName, schemeTag, basedOn, schemeType, DateTime.UtcNow.Year);
        var lastCode = await _repository.GetLastSchemeCodeAsync(prefix, cancellationToken);
        var nextSequence = LastSequence(lastCode, prefix) + 1;

        if (nextSequence <= 1 && string.IsNullOrWhiteSpace(lastCode))
        {
            nextSequence = Random.Shared.Next(1, 100);
        }

        var code = $"{prefix}-{nextSequence:00}";
        while (await _repository.SchemeCodeExistsAsync(code, null, cancellationToken))
        {
            nextSequence++;
            code = $"{prefix}-{nextSequence:00}";
        }

        return code;
    }

    private static string BuildSchemeCodePrefix(string? schemeName, string? schemeTag, string? basedOn, string? schemeType, int year)
    {
        var tagPart = string.Equals(schemeTag, "Booster", StringComparison.OrdinalIgnoreCase) ? "BST" : "REG";
        var namePart = Abbr(schemeName);
        // The code says what the scheme is read on, so the three types never share a
        // sequence and a code can be placed without opening the scheme.
        var typePart = SchemeKinds.IsProduct(schemeType) ? "PRD"
            : SchemeKinds.IsQuantity(schemeType) ? "QTY" : "INV";
        var basisPart = SchemeReward.IsMixedScheme(basedOn) ? "MIX"
            : SchemeReward.IsPercentage(basedOn) ? "PCT" : "VAL";
        return $"{tagPart}-{namePart}-{typePart}-{basisPart}-{year}".ToUpperInvariant();
    }

    private static string Abbr(string? value)
    {
        var clean = new string((value ?? "Scheme").Select(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) ? ch : ' ').ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(clean)) return "SCH";

        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3).ToArray();
        var abbreviation = string.Concat(words.Select(word => word[0]));
        if (abbreviation.Length == 0) abbreviation = clean[0].ToString();
        return abbreviation.PadRight(3, abbreviation[0]).Length > 5 ? abbreviation[..5] : abbreviation.PadRight(3, abbreviation[0]);
    }

    private static int LastSequence(string? code, string prefix)
    {
        if (string.IsNullOrWhiteSpace(code) || !code.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)) return 0;
        var suffix = code[(prefix.Length + 1)..];
        return int.TryParse(suffix, out var sequence) ? sequence : 0;
    }

    private static void AddChoiceError(IDictionary<string, string[]> errors, string key, string? value, string fallback, string[] allowed, string message)
    {
        var normalized = NormalizeChoice(value, fallback, allowed);
        if (string.IsNullOrWhiteSpace(normalized)) errors[key] = [message];
    }

    private static string NormalizeChoice(string? value, string fallback, string[] allowed)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return allowed.FirstOrDefault(x => string.Equals(x, candidate, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    }

    private static string NormalizeActive(string? value) =>
        string.Equals(value, "N", StringComparison.OrdinalIgnoreCase) ? "N" : "Y";

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static LaravelHttpException Http(int statusCode, object message) => new(statusCode, message);
    private static DateOnly IndiaToday() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
}
