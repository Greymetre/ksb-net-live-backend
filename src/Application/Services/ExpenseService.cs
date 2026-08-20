using Application.Common;
using Application.DTOs.Expenses;
using Application.Interfaces.Repositories;
using Application.Interfaces.Services;
using Domain.Entities;
using Shared.Exceptions;
using Shared.Responses;
using System.Globalization;

namespace Application.Services;

public sealed class ExpenseService : IExpenseService
{
    private readonly IExpenseRepository _repository;

    public ExpenseService(IExpenseRepository repository)
    {
        _repository = repository;
    }

    public async Task<LaravelApiResponse> GetExpensesAsync(ExpenseFilterDto filter, CancellationToken cancellationToken) =>
        LaravelApiResponse.Success("expenses", await _repository.GetExpensesAsync(filter, cancellationToken));

    public async Task<LaravelApiResponse> GetExpenseAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var rows = await _repository.GetExpensesAsync(new ExpenseFilterDto { ExpenseId = id, ActorUserId = actorUserId }, cancellationToken);
        return LaravelApiResponse.Success("expense", rows.FirstOrDefault() ?? throw NotFound("Expense not found"));
    }

    public async Task<LaravelApiResponse> GetOptionsAsync(ulong? actorUserId, CancellationToken cancellationToken) =>
        LaravelApiResponse.Success("options", await _repository.GetOptionsAsync(actorUserId, cancellationToken));

    public async Task<LaravelApiResponse> CreateExpenseAsync(ExpenseRequestDto request, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var expense = await BuildExpenseAsync(new Expense { CreatedAt = DateTime.Now }, request, actorUserId, cancellationToken);
        expense.UpdatedAt = DateTime.Now;
        await _repository.AddExpenseAsync(expense, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
        await AddLogAsync(expense.Id, actorUserId, "generated", cancellationToken);
        return LaravelApiResponse.Success("expense", await _repository.GetExpenseDtoAsync(expense.Id, cancellationToken), "expense added successfully");
    }

    public async Task<LaravelApiResponse> UpdateExpenseAsync(ulong id, ExpenseRequestDto request, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var expense = await GetOrThrowAsync(_repository.GetExpenseAsync(id, cancellationToken), "Expense not found");
        await RequireReportingAccessAsync(actorUserId, expense.UserId, cancellationToken);
        RequirePending(expense, "edited");
        await BuildExpenseAsync(expense, request, actorUserId, cancellationToken);
        expense.UpdatedAt = DateTime.Now;
        await _repository.SaveChangesAsync(cancellationToken);
        await AddLogAsync(expense.Id, actorUserId, "updated", cancellationToken);
        return LaravelApiResponse.Success("expense", await _repository.GetExpenseDtoAsync(id, cancellationToken), "expense updated successfully");
    }

    public async Task<LaravelApiResponse> AddAttachmentsAsync(ulong id, IReadOnlyCollection<ExpenseUploadDto> uploads, CancellationToken cancellationToken)
    {
        if (uploads.Count == 0) return LaravelApiResponse.Success("expense", await _repository.GetExpenseDtoAsync(id, cancellationToken));
        _ = await GetOrThrowAsync(_repository.GetExpenseAsync(id, cancellationToken), "Expense not found");

        uint order = 1;
        foreach (var upload in uploads)
        {
            await _repository.AddMediaAsync(new Media
            {
                ModelType = "App\\Models\\Expenses",
                ModelId = id,
                Uuid = Guid.NewGuid().ToString(),
                CollectionName = "expense_file",
                Name = Path.GetFileNameWithoutExtension(upload.OriginalName),
                FileName = upload.FileName,
                MimeType = upload.MimeType,
                Disk = "public",
                ConversionsDisk = "public",
                Size = upload.Size,
                Manipulations = "[]",
                CustomProperties = "[]",
                GeneratedConversions = "[]",
                ResponsiveImages = "[]",
                OrderColumn = order++,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            }, cancellationToken);
        }

        await _repository.SaveChangesAsync(cancellationToken);
        return LaravelApiResponse.Success("expense", await _repository.GetExpenseDtoAsync(id, cancellationToken), "attachment uploaded successfully");
    }

    /// <summary>
    /// Drops one attachment while the expense is still being corrected. The stored
    /// file name comes back so the caller can clear the file off disk too.
    /// </summary>
    public async Task<LaravelApiResponse> RemoveAttachmentAsync(ulong id, ulong attachmentId, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var expense = await GetOrThrowAsync(_repository.GetExpenseAsync(id, cancellationToken), "Expense not found");
        await RequireReportingAccessAsync(actorUserId, expense.UserId, cancellationToken);
        RequirePending(expense, "edited");

        var attachment = await _repository.GetAttachmentAsync(id, attachmentId, cancellationToken)
            ?? throw NotFound("Attachment not found");
        var fileName = attachment.FileName;

        _repository.RemoveMedia(attachment);
        await _repository.SaveChangesAsync(cancellationToken);

        var response = LaravelApiResponse.Success("expense", await _repository.GetExpenseDtoAsync(id, cancellationToken), "attachment removed successfully");
        response.Extra["removed_file"] = fileName;
        return response;
    }

    public async Task<LaravelApiResponse> DeleteExpenseAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var existing = await GetOrThrowAsync(_repository.GetExpenseAsync(id, cancellationToken), "Expense not found");
        await RequireReportingAccessAsync(actorUserId, existing.UserId, cancellationToken);
        RequirePending(existing, "deleted");

        if (!await _repository.DeleteExpenseAsync(id, cancellationToken)) throw NotFound("Expense not found");
        await _repository.SaveChangesAsync(cancellationToken);
        return LaravelApiResponse.MessageOnly("success", "Expense deleted successfully!");
    }

    public async Task<LaravelApiResponse> SetStatusAsync(ulong id, ExpenseStatusRequestDto request, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var expense = await GetOrThrowAsync(_repository.GetExpenseAsync(id, cancellationToken), "Expense not found");
        await RequireReportingAccessAsync(actorUserId, expense.UserId, cancellationToken);
        var status = request.Status ?? throw BadRequest("Status is required.");
        if (!ExpenseStatusLookups.Statuses.ContainsKey(status)) throw BadRequest("Status is invalid.");

        if (status == 1)
        {
            var approveAmount = request.ApproveAmount ?? expense.ClaimAmount ?? 0;
            if (approveAmount > (expense.ClaimAmount ?? 0)) throw BadRequest("Approve amount greater than to claim amount");
            expense.ApproveAmount = approveAmount;
        }
        else if (status == 2)
        {
            RequireValue(request.Reason, "Please add reason if you want reject the expens.");
            expense.ApproveAmount = null;
        }
        else if (status == 0)
        {
            expense.ApproveAmount = null;
        }

        expense.CheckerStatus = status;
        expense.Reason = request.Reason;
        expense.ApproveRejectBy = actorUserId;
        expense.UpdatedAt = DateTime.Now;
        await _repository.SaveChangesAsync(cancellationToken);
        await AddLogAsync(expense.Id, actorUserId, StatusLogName(status), cancellationToken);
        return LaravelApiResponse.Success("expense", await _repository.GetExpenseDtoAsync(id, cancellationToken), StatusMessage(status));
    }

    /// <summary>
    /// The one approval action the mobile app offers. A reporting manager marks an
    /// expense as seen - whether the back office has checked it or not - and the final
    /// approve or reject still happens in the CRM.
    /// </summary>
    public async Task<LaravelApiResponse> CheckByReportingAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var expense = await GetOrThrowAsync(_repository.GetExpenseAsync(id, cancellationToken), "Expense not found");
        await RequireReportingAccessAsync(actorUserId, expense.UserId, cancellationToken);

        if (expense.CheckerStatus is not (0 or 3))
        {
            throw BadRequest($"Only a pending or checked expense can be marked checked by reporting. This expense is already {ExpenseStatusLookups.StatusName(expense.CheckerStatus)}.");
        }

        expense.CheckerStatus = 4;
        expense.ApproveRejectBy = actorUserId;
        expense.UpdatedAt = DateTime.Now;
        await _repository.SaveChangesAsync(cancellationToken);
        await AddLogAsync(expense.Id, actorUserId, StatusLogName(4), cancellationToken);
        return LaravelApiResponse.Success("expense", await _repository.GetExpenseDtoAsync(id, cancellationToken), "Expense marked checked by reporting.");
    }

    public async Task<LaravelApiResponse> GetLogsAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var expense = await GetOrThrowAsync(_repository.GetExpenseAsync(id, cancellationToken), "Expense not found");
        await RequireReportingAccessAsync(actorUserId, expense.UserId, cancellationToken);
        return LaravelApiResponse.Success("logs", await _repository.GetLogsAsync(id, cancellationToken));
    }

    public async Task<LaravelApiResponse> GetSummaryAsync(ExpenseFilterDto filter, CancellationToken cancellationToken)
    {
        var rows = await _repository.GetSummaryRowsAsync(filter, cancellationToken);
        // expenses.date is stored as text, so the running month is matched by prefix.
        var today = DateTime.UtcNow.AddHours(5).AddMinutes(30);
        var monthPrefix = today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var monthRows = rows.Where(x => x.Date is not null && x.Date.StartsWith(monthPrefix, StringComparison.Ordinal)).ToArray();

        var summary = new ExpenseSummaryDto
        {
            Total = rows.Count,
            Pending = rows.Count(x => x.CheckerStatus == 0),
            Approved = rows.Count(x => x.CheckerStatus == 1),
            Rejected = rows.Count(x => x.CheckerStatus == 2),
            Checked = rows.Count(x => x.CheckerStatus == 3),
            CheckedByReporting = rows.Count(x => x.CheckerStatus == 4),
            Hold = rows.Count(x => x.CheckerStatus == 5),
            ClaimAmount = Math.Round(rows.Sum(x => x.ClaimAmount), 2),
            ApproveAmount = Math.Round(rows.Sum(x => x.ApproveAmount), 2),
            Month = new ExpenseMonthSummaryDto
            {
                Label = today.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                Total = monthRows.Length,
                Pending = monthRows.Count(x => x.CheckerStatus == 0),
                ClaimAmount = Math.Round(monthRows.Sum(x => x.ClaimAmount), 2),
                ApproveAmount = Math.Round(monthRows.Sum(x => x.ApproveAmount), 2)
            }
        };

        return LaravelApiResponse.Success("summary", summary);
    }

    private async Task<Expense> BuildExpenseAsync(Expense expense, ExpenseRequestDto request, ulong? actorUserId, CancellationToken cancellationToken)
    {
        RequireId(request.ExpensesType, "Expense Type is required.");
        // A field user files for themselves, so an omitted employee means the actor.
        request.UserId ??= actorUserId;
        RequireId(request.UserId, "Employee is required.");
        RequireValue(request.Date, "Date is required.");
        await RequireReportingAccessAsync(actorUserId, request.UserId, cancellationToken);

        var user = await _repository.GetUserAsync(request.UserId!.Value, cancellationToken) ?? throw NotFound("User not found");
        var expenseType = await _repository.GetExpenseTypeAsync(request.ExpensesType!.Value, cancellationToken) ?? throw NotFound("Expense Type not found");
        RequireGradeMatch(user, expenseType);
        var claimAmount = ResolveClaimAmount(request, expenseType);

        expense.ExpensesType = request.ExpensesType;
        expense.UserId = request.UserId;
        expense.Date = request.Date;
        expense.ClaimAmount = claimAmount;
        expense.Note = request.Note;
        expense.Reason = request.Reason;
        expense.CreatedBy = actorUserId ?? expense.CreatedBy;

        if (expenseType.AllowanceTypeId == 1)
        {
            expense.StartKm = request.StartKm;
            expense.StopKm = request.StopKm;
            expense.TotalKm = CalculateTotalKm(request.StartKm, request.StopKm).ToString("0.##", CultureInfo.InvariantCulture);
        }
        else
        {
            expense.StartKm = null;
            expense.StopKm = null;
            expense.TotalKm = null;
        }

        if (request.ApproveAmount.HasValue) expense.ApproveAmount = request.ApproveAmount;
        return expense;
    }

    /// <summary>An expense may only be filed against, or acted on for, an employee the
    /// signed-in user reports on. For a field user that set is just themselves.</summary>
    private async Task RequireReportingAccessAsync(ulong? actorUserId, ulong? userId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue || userId is null or 0) return;
        if (!await _repository.CanActorReportOnAsync(actorUserId, userId.Value, cancellationToken))
        {
            throw new LaravelHttpException(LaravelStatusCodes.Forbidden, "You can only work on expenses of the users you report on.");
        }
    }

    /// <summary>Once an expense leaves Pending it is part of an approval trail, so the
    /// row is locked and only its status may change.</summary>
    private static void RequirePending(Expense expense, string action)
    {
        if (expense.CheckerStatus != 0)
        {
            throw BadRequest($"Only a pending expense can be {action}. This expense is already {ExpenseStatusLookups.StatusName(expense.CheckerStatus)}.");
        }
    }

    /// <summary>
    /// Rates are graded: the same "Bike" carries a different rate per grade, so an
    /// employee may only claim the expense types tagged with their own grade. A type
    /// without a grade stays open to everyone.
    /// </summary>
    private static void RequireGradeMatch(User user, ExpenseType expenseType)
    {
        if (expenseType.PayrollId is null or 0) return;

        if (!ulong.TryParse(user.Payroll, out var grade) || grade == 0)
        {
            throw BadRequest("Grade is not set for this employee. Please assign a grade before adding an expense.");
        }

        if (grade != expenseType.PayrollId.Value)
        {
            throw BadRequest("This expense type is not available for the employee's grade.");
        }
    }

    private static decimal ResolveClaimAmount(ExpenseRequestDto request, ExpenseType expenseType)
    {
        if (expenseType.AllowanceTypeId == 1)
        {
            var totalKm = CalculateTotalKm(request.StartKm, request.StopKm);
            return Math.Round(totalKm * expenseType.Rate, 2, MidpointRounding.AwayFromZero);
        }

        return expenseType.Rate > 0
            ? expenseType.Rate
            : request.ClaimAmount ?? throw BadRequest("Claim Amount is required.");
    }

    private static decimal CalculateTotalKm(string? startKm, string? stopKm)
    {
        if (!decimal.TryParse(startKm, NumberStyles.Number, CultureInfo.InvariantCulture, out var start))
        {
            throw BadRequest("Start Km is required.");
        }

        if (!decimal.TryParse(stopKm, NumberStyles.Number, CultureInfo.InvariantCulture, out var stop))
        {
            throw BadRequest("End Km is required.");
        }

        if (stop < start) throw BadRequest("End Km must be greater than or equal to Start Km.");
        return stop - start;
    }

    private async Task AddLogAsync(ulong expenseId, ulong? actorUserId, string statusType, CancellationToken cancellationToken)
    {
        await _repository.AddLogAsync(new ExpenseLog
        {
            ExpenseId = expenseId,
            CreatedBy = actorUserId,
            LogDate = DateOnly.FromDateTime(DateTime.Today),
            StatusType = statusType,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        }, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
    }

    private static string StatusLogName(int status) => status switch
    {
        1 => "approved",
        2 => "rejected",
        3 => "checked",
        4 => "Checked By Reporting",
        5 => "Hold",
        _ => "unchecked"
    };

    private static string StatusMessage(int status) => status switch
    {
        1 => "Approve amount.",
        2 => "Expense reject successfully.",
        0 => "Status unchecked successfully",
        _ => "Status checked successfully"
    };

    private static async Task<T> GetOrThrowAsync<T>(Task<T?> task, string message)
    {
        var value = await task;
        return value ?? throw NotFound(message);
    }

    private static void RequireValue(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) throw BadRequest(message);
    }

    private static void RequireId(ulong? value, string message)
    {
        if (value is null or 0) throw BadRequest(message);
    }

    private static LaravelHttpException BadRequest(string message) =>
        new(LaravelStatusCodes.BadRequest, message);

    private static LaravelHttpException NotFound(string message) =>
        new(LaravelStatusCodes.NotFound, message);
}
