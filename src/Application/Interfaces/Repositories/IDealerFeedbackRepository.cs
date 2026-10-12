using Application.DTOs.DealerFeedback;

namespace Application.Interfaces.Repositories;

public interface IDealerFeedbackRepository
{
    /// <summary>Stores a new note. <paramref name="customerId"/> is the dealer's customers.id,
    /// <paramref name="userId"/> the Distributor user that submitted it.</summary>
    Task<DealerFeedbackRow> AddAsync(ulong customerId, ulong? userId, string category, string message, int rating, CancellationToken cancellationToken);

    /// <summary>The dealer's own notes, newest first.</summary>
    Task<IReadOnlyList<DealerFeedbackRow>> ListForDealerAsync(ulong customerId, CancellationToken cancellationToken);

    /// <summary>The internal listing, scoped reporting-wise to the actor and paged.</summary>
    Task<(IReadOnlyList<DealerFeedbackRow> Rows, long Total)> ListAsync(DealerFeedbackListFilter filter, CancellationToken cancellationToken);
}
