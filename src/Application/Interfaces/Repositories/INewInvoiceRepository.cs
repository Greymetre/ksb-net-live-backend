using Application.DTOs.NewInvoices;
using Application.Common;
using Domain.Entities;

namespace Application.Interfaces.Repositories;

public interface INewInvoiceRepository
{
    Task<PagedResult<NewInvoiceDto>> GetInvoicesAsync(NewInvoiceFilterDto filter, ulong? actorUserId, CancellationToken cancellationToken);
    Task<NewInvoiceSummaryDto> GetInvoiceSummaryAsync(NewInvoiceFilterDto filter, ulong? actorUserId, CancellationToken cancellationToken);
    Task<NewInvoiceDto?> GetInvoiceAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<RetailerOptionDto>> GetRetailerOptionsAsync(string? search, ulong? actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<DealerOptionDto>> GetDealerOptionsAsync(ulong? actorUserId, CancellationToken cancellationToken);
    Task<Customer?> GetRetailerAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<InvoiceSchemeOptionDto>> GetEligibleSchemeOptionsAsync(ulong customerId, DateTime invoiceDate, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<InvoiceSchemeOptionDto>> GetInvoiceSchemeFilterOptionsAsync(CancellationToken cancellationToken);
    Task<bool> InvoiceNumberExistsAsync(string invoiceNumber, ulong secondaryCustomerId, ulong? exceptId, CancellationToken cancellationToken);
    Task<NewInvoiceDto> CreateInvoiceAsync(NewInvoice invoice, CancellationToken cancellationToken);
    Task<NewInvoice?> FindInvoiceEntityAsync(ulong id, CancellationToken cancellationToken);
    Task<NewInvoiceDto> SaveInvoiceAsync(NewInvoice invoice, string statusType, int? fromStatus, int toStatus, ulong actorUserId, string? remark, decimal? approvedAmount, CancellationToken cancellationToken);
    /// <summary>
    /// Removes the invoice with everything hanging off it - approval log, attachment
    /// rows and the retailer's loyalty points for it. Returns the stored file paths
    /// so the caller can clear them off disk.
    /// </summary>
    Task<IReadOnlyCollection<string>> DeleteInvoiceAsync(NewInvoice invoice, CancellationToken cancellationToken);
}
