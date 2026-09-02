using Application.DTOs.Customers;

namespace Application.Interfaces.Repositories;

public interface ICustomerRepository
{
    Task<Application.Common.PagedResult<CustomerDto>> GetCustomersAsync(CustomerListFilterDto filter, CancellationToken cancellationToken);
    Task<CustomerKycListResultDto> GetKycListAsync(CustomerKycFilterDto filter, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<CustomerKycDealerOptionDto>> GetKycDealerOptionsAsync(ulong? actorUserId, CancellationToken cancellationToken);
    /// <summary>Of the given customers, the ones this user is allowed to see. The caller
    /// supplies a small candidate set - the customers that have actually signed in on the
    /// app - so the whole customer table is never materialised to answer it.</summary>
    Task<IReadOnlyCollection<ulong>> FilterVisibleCustomerIdsAsync(ulong? actorUserId, IReadOnlyCollection<ulong> candidateIds, CancellationToken cancellationToken);
    Task<CustomerDto?> GetCustomerAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken);
    Task<CustomerDto> CreateCustomerAsync(CustomerRequestDto request, ulong? actorUserId, CancellationToken cancellationToken);
    Task<CustomerDto?> UpdateCustomerAsync(ulong id, CustomerRequestDto request, ulong? actorUserId, CancellationToken cancellationToken);
    Task<CustomerDto?> UpdateKycStatusAsync(ulong id, string documentKey, string status, string? remark, ulong actorUserId, CancellationToken cancellationToken);
    Task<CustomerDto?> SetRetailerApprovalStatusAsync(ulong id, string status, string? remark, ulong actorUserId, CancellationToken cancellationToken);
    Task<CustomerDto?> SetCustomerActiveAsync(ulong id, string? active, ulong? actorUserId, CancellationToken cancellationToken);
    Task<bool> DeleteCustomerAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken);
    Task EnsureDistributorLoginUserAsync(ulong customerId, ulong? actorUserId, CancellationToken cancellationToken);
    Task<bool> MobileExistsAsync(string mobile, ulong? exceptId, CancellationToken cancellationToken);
    Task<bool> EmailExistsAsync(string email, ulong? exceptId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, ulong>> GetUserIdsByEmployeeCodesAsync(IEnumerable<string> employeeCodes, CancellationToken cancellationToken);
}
