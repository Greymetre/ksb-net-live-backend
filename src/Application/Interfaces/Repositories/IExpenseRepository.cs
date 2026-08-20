using Application.DTOs.Expenses;
using Domain.Entities;

namespace Application.Interfaces.Repositories;

public interface IExpenseRepository
{
    Task<IReadOnlyCollection<ExpenseDto>> GetExpensesAsync(ExpenseFilterDto filter, CancellationToken cancellationToken);
    Task<ExpenseDto?> GetExpenseDtoAsync(ulong id, CancellationToken cancellationToken);
    Task<Expense?> GetExpenseAsync(ulong id, CancellationToken cancellationToken);
    Task<User?> GetUserAsync(ulong id, CancellationToken cancellationToken);
    Task<ExpenseType?> GetExpenseTypeAsync(ulong id, CancellationToken cancellationToken);
    Task<ExpenseOptionsDto> GetOptionsAsync(ulong? actorUserId, CancellationToken cancellationToken);

    /// <summary>Whether the signed-in user may report on - and file expenses for - the given employee.</summary>
    Task<bool> CanActorReportOnAsync(ulong? actorUserId, ulong userId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ExpenseLogDto>> GetLogsAsync(ulong expenseId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ExpenseSummaryRow>> GetSummaryRowsAsync(ExpenseFilterDto filter, CancellationToken cancellationToken);
    Task AddExpenseAsync(Expense expense, CancellationToken cancellationToken);
    Task AddLogAsync(ExpenseLog log, CancellationToken cancellationToken);
    Task AddMediaAsync(Media media, CancellationToken cancellationToken);

    /// <summary>One attachment of an expense, or null when it belongs to another record.</summary>
    Task<Media?> GetAttachmentAsync(ulong expenseId, ulong attachmentId, CancellationToken cancellationToken);

    void RemoveMedia(Media media);
    Task<bool> DeleteExpenseAsync(ulong id, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>The few columns a summary needs, kept out of the full expense row.</summary>
public readonly record struct ExpenseSummaryRow(int CheckerStatus, decimal ClaimAmount, decimal ApproveAmount, string? Date);
