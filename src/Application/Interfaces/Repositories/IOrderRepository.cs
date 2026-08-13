using Application.DTOs.Orders;
using Domain.Entities;

namespace Application.Interfaces.Repositories;

public interface IOrderRepository
{
    Task<IReadOnlyCollection<OrderDto>> GetOrdersAsync(OrderFilterDto filter, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<OrderExportRowDto>> GetOrderExportRowsAsync(OrderFilterDto filter, CancellationToken cancellationToken);
    Task<OrderDto?> GetOrderAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<OrderDetailDto>> GetOrderDetailsAsync(ulong orderId, CancellationToken cancellationToken);
    Task<bool> DeleteOrderAsync(ulong id, CancellationToken cancellationToken);
    Task<OrderOptionsDto> GetOptionsAsync(ulong? actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<OrderProductOptionDto>> GetProductsByFamilyAsync(ulong familyId, CancellationToken cancellationToken);
    Task<User?> GetUserAsync(ulong id, CancellationToken cancellationToken);
    Task<bool> IsApprovedRetailerAsync(ulong id, CancellationToken cancellationToken);
    Task<Order?> GetOrderEntityAsync(ulong id, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<OrderDetail>> GetOrderDetailEntitiesAsync(ulong orderId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<OrderDispatchDto>> GetDispatchesAsync(string? mode, ulong? actorUserId, CancellationToken cancellationToken);
    Task<OrderDispatchDetailDto?> GetDispatchDetailAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken);
    Task DispatchAsync(Order order, IReadOnlyCollection<OrderDetail> details, OrderDispatchRequestDto request, ulong? actorUserId, CancellationToken cancellationToken);
    Task AddOrderAsync(Order order, CancellationToken cancellationToken);
    Task AddOrderDetailsAsync(IReadOnlyCollection<OrderDetail> details, CancellationToken cancellationToken);
    void RemoveOrder(Order order);
    void RemoveOrderDetails(IReadOnlyCollection<OrderDetail> details);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
