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
    Task<Application.Common.PagedResult<RetailerOptionDto>> GetRetailerOptionPageAsync(string? search, ulong? actorUserId, int page, int pageSize, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<DealerOptionDto>> GetDealerOptionsAsync(ulong? actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<RetailerDealerOptionDto>> GetRetailerDealerOptionsAsync(ulong customerId, CancellationToken cancellationToken);
    /// <summary>Whether this user may raise an invoice from the field app: an ASR, or a superadmin.</summary>
    Task<bool> CanCreateFieldInvoiceAsync(ulong? actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FieldSchemeDto>> GetFieldSchemesAsync(ulong? actorUserId, DateOnly today, CancellationToken cancellationToken);
    Task<FieldSchemeDetailDto?> GetFieldSchemeAsync(ulong id, ulong? actorUserId, DateOnly today, CancellationToken cancellationToken);
    Task<Customer?> GetRetailerAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<InvoiceSchemeOptionDto>> GetEligibleSchemeOptionsAsync(ulong customerId, DateTime invoiceDate, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<InvoiceSchemeOptionDto>> GetInvoiceSchemeFilterOptionsAsync(CancellationToken cancellationToken);
    Task<bool> InvoiceNumberExistsAsync(string invoiceNumber, ulong secondaryCustomerId, ulong? dealerCustomerId, ulong? exceptId, CancellationToken cancellationToken);
    Task<NewInvoiceDto> CreateInvoiceAsync(NewInvoice invoice, CancellationToken cancellationToken);
    /// <summary>Adds files to an invoice and removes the ones the caller took away.
    /// Returns the stored paths of everything removed, so the caller can clear them off
    /// disk once the database change has stuck.</summary>
    Task<IReadOnlyCollection<string>> SaveAttachmentsAsync(ulong invoiceId, IReadOnlyList<InvoiceAttachmentInput> added, IReadOnlyList<long> removedIds, CancellationToken cancellationToken);
    /// <summary>How many files the invoice currently holds.</summary>
    Task<int> CountAttachmentsAsync(ulong invoiceId, CancellationToken cancellationToken);
    Task<NewInvoice?> FindInvoiceEntityAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken);
    Task<NewInvoiceDto> SaveInvoiceAsync(NewInvoice invoice, string statusType, int? fromStatus, int toStatus, ulong actorUserId, string? remark, decimal? approvedAmount, CancellationToken cancellationToken);
    /// <summary>
    /// Removes the invoice with everything hanging off it - approval log, attachment
    /// rows and the retailer's loyalty points for it. Returns the stored file paths
    /// so the caller can clear them off disk.
    /// </summary>
    Task<IReadOnlyCollection<string>> DeleteInvoiceAsync(NewInvoice invoice, CancellationToken cancellationToken);
}
