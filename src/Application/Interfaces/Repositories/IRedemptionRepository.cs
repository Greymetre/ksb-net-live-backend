using Application.DTOs.Redemptions;
using Domain.Entities;

namespace Application.Interfaces.Repositories;

public interface IRedemptionRepository
{
    Task<IReadOnlyCollection<RedemptionDto>> GetRedemptionsAsync(RedemptionFilterDto filter, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<LoyaltyRedemption>> GetCustomerRedemptionsAsync(ulong customerId, CancellationToken cancellationToken);
    Task<LoyaltyRedemption?> GetByIdAsync(ulong id, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, LoyaltyRedemption>> GetByTransactionNosAsync(IReadOnlyCollection<string> transactionNos, CancellationToken cancellationToken);
    Task<LoyaltyRedemption> CreateAsync(LoyaltyRedemption redemption, CancellationToken cancellationToken);
    Task UpdateAsync(LoyaltyRedemption redemption, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
    Task<bool> TransactionNoExistsAsync(string transactionNo, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, (ulong Id, string Tag)>> GetSchemesByCodeAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<ulong, (string Name, bool RedemptionEnabled, bool RedemptionCrmEnabled)>> GetSchemeFlagsAsync(IReadOnlyCollection<ulong> schemeIds, CancellationToken cancellationToken);
}
