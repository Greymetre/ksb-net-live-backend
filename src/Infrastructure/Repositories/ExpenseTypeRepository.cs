using System.Globalization;
using Application.DTOs.Expenses;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public sealed class ExpenseTypeRepository : IExpenseTypeRepository
{
    private readonly AppDbContext _dbContext;

    public ExpenseTypeRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyCollection<ExpenseTypeDto>> GetExpenseTypesAsync(string? search, CancellationToken cancellationToken)
    {
        var query = _dbContext.ExpenseTypes.AsNoTracking();

        var rows = await query
            .OrderByDescending(x => x.Id)
            .Select(x => new ExpenseTypeDto
            {
                Id = x.Id,
                Name = x.Name,
                Rate = x.Rate,
                IsActive = x.IsActive,
                Active = x.IsActive == 1 ? "Y" : "N",
                AllowanceTypeId = x.AllowanceTypeId,
                PayrollId = x.PayrollId,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            row.AllowanceTypeName = ExpenseTypeLookups.AllowanceTypeName(row.AllowanceTypeId);
            row.PayrollName = ExpenseTypeLookups.PayrollName(row.PayrollId);
        }

        // Allowance type and payroll are resolved from lookups after projection, so
        // searching them has to happen here rather than in the SQL query.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            rows = rows.Where(x => x.Name.ToLowerInvariant().Contains(term)
                || (x.AllowanceTypeName ?? string.Empty).ToLowerInvariant().Contains(term)
                || (x.PayrollName ?? string.Empty).ToLowerInvariant().Contains(term)
                || x.Rate.ToString(CultureInfo.InvariantCulture).Contains(term)).ToList();
        }

        return rows;
    }

    public async Task<ExpenseTypeDto?> GetExpenseTypeDtoAsync(ulong id, CancellationToken cancellationToken) =>
        (await GetExpenseTypesAsync(null, cancellationToken)).FirstOrDefault(x => x.Id == id);

    public Task<ExpenseType?> GetExpenseTypeAsync(ulong id, CancellationToken cancellationToken) =>
        _dbContext.ExpenseTypes.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task AddExpenseTypeAsync(ExpenseType expenseType, CancellationToken cancellationToken) =>
        await _dbContext.ExpenseTypes.AddAsync(expenseType, cancellationToken);

    public async Task<bool> DeleteExpenseTypeAsync(ulong id, CancellationToken cancellationToken)
    {
        var expenseType = await _dbContext.ExpenseTypes.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (expenseType is null) return false;
        _dbContext.ExpenseTypes.Remove(expenseType);
        return true;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
