using Application.DTOs.DealerFeedback;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public sealed class DealerFeedbackRepository : IDealerFeedbackRepository
{
    private readonly AppDbContext _db;
    private readonly ICustomerRepository _customers;

    public DealerFeedbackRepository(AppDbContext db, ICustomerRepository customers)
    {
        _db = db;
        _customers = customers;
    }

    public async Task<DealerFeedbackRow> AddAsync(ulong customerId, ulong? userId, string category, string message, int rating, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var entity = new DealerFeedback
        {
            CustomerId = customerId,
            CreatedByUserId = userId,
            Category = (category ?? string.Empty).Trim(),
            Message = (message ?? string.Empty).Trim(),
            Rating = rating is >= 1 and <= 5 ? rating : 3,
            CreatedAt = now,
            UpdatedAt = now
        };
        await _db.DealerFeedbacks.AddAsync(entity, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        var dealerName = await _db.Customers.AsNoTracking()
            .Where(c => c.Id == customerId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        return new DealerFeedbackRow
        {
            Id = entity.Id,
            CustomerId = entity.CustomerId,
            DealerName = dealerName,
            Category = entity.Category,
            Message = entity.Message,
            Rating = entity.Rating,
            CreatedAt = entity.CreatedAt
        };
    }

    public async Task<IReadOnlyList<DealerFeedbackRow>> ListForDealerAsync(ulong customerId, CancellationToken cancellationToken)
    {
        return await (from f in _db.DealerFeedbacks.AsNoTracking()
                      where f.CustomerId == customerId
                      orderby f.CreatedAt descending, f.Id descending
                      select new DealerFeedbackRow
                      {
                          Id = f.Id,
                          CustomerId = f.CustomerId,
                          Category = f.Category,
                          Message = f.Message,
                          Rating = f.Rating,
                          CreatedAt = f.CreatedAt
                      }).ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<DealerFeedbackRow> Rows, long Total)> ListAsync(DealerFeedbackListFilter filter, CancellationToken cancellationToken)
    {
        var query = from f in _db.DealerFeedbacks.AsNoTracking()
                    join c in _db.Customers.AsNoTracking() on f.CustomerId equals c.Id
                    select new { f, c };

        // Reporting scope: admin/privileged see everything; everyone else is held to the
        // dealers their customer listing would show (a BM -> their branch, others -> downline).
        var unrestricted = await ReportingVisibility.HasUnrestrictedDataScopeAsync(_db, filter.ActorUserId, cancellationToken);
        if (!unrestricted)
        {
            var candidates = await _db.DealerFeedbacks.AsNoTracking()
                .Select(x => x.CustomerId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var visible = await _customers.FilterVisibleCustomerIdsAsync(filter.ActorUserId, candidates, cancellationToken);
            var visibleSet = visible.ToHashSet();
            query = query.Where(x => visibleSet.Contains(x.f.CustomerId));
        }

        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            var category = filter.Category.Trim();
            query = query.Where(x => x.f.Category == category);
        }
        if (filter.FromDate.HasValue)
        {
            query = query.Where(x => x.f.CreatedAt >= filter.FromDate.Value);
        }
        if (filter.ToDate.HasValue)
        {
            query = query.Where(x => x.f.CreatedAt <= filter.ToDate.Value);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(x => x.c.Name.Contains(search) || x.f.Message.Contains(search));
        }

        var total = await query.LongCountAsync(cancellationToken);

        var ordered = query
            .Select(x => new DealerFeedbackRow
            {
                Id = x.f.Id,
                CustomerId = x.f.CustomerId,
                DealerName = x.c.Name,
                DealerMobile = x.c.Mobile,
                Category = x.f.Category,
                Message = x.f.Message,
                Rating = x.f.Rating,
                CreatedAt = x.f.CreatedAt
            })
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id);

        IQueryable<DealerFeedbackRow> paged = ordered;
        if (!filter.All)
        {
            var page = Math.Max(1, filter.Page);
            var pageSize = Math.Clamp(filter.PageSize, 1, 100);
            paged = ordered.Skip((page - 1) * pageSize).Take(pageSize);
        }

        var rows = await paged.ToListAsync(cancellationToken);
        return (rows, total);
    }
}
