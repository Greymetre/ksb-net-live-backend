using Application.DTOs.Orders;
using Application.DTOs.Users;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Data;

namespace Infrastructure.Repositories;

public sealed class OrderRepository : IOrderRepository
{
    private const ulong DistributorCustomerType = 1;
    private const ulong RetailerCustomerType = 2;
    private const ulong InfluencerCustomerType = 3;
    private const int MaxRows = 50000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _dbContext;

    public OrderRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> IsApprovedRetailerAsync(ulong id, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers.AsNoTracking()
            .Where(x => x.Id == id && x.DeletedAt == null && x.Active == "Y" && x.CustomerType == RetailerCustomerType)
            .Select(x => x.CustomFields)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(customer)) return false;

        try
        {
            using var document = JsonDocument.Parse(customer);
            if (!document.RootElement.TryGetProperty("status", out var element)) return false;
            var status = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
            return string.Equals(status?.Trim(), "APPROVED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status?.Trim(), "1", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyCollection<OrderDto>> GetOrdersAsync(OrderFilterDto filter, CancellationToken cancellationToken)
    {
        var query = _dbContext.Orders.AsNoTracking();
        query = await ApplyVisibilityAsync(query, filter.ActorUserId, cancellationToken);
        query = ApplyFilters(query, filter);

        var rows = await (
            from order in query
            join executiveRow in _dbContext.Users.AsNoTracking() on order.ExecutiveId equals executiveRow.Id into executives
            from executive in executives.DefaultIfEmpty()
            join creatorRow in _dbContext.Users.AsNoTracking() on order.CreatedBy equals creatorRow.Id into creators
            from creator in creators.DefaultIfEmpty()
            join buyerRow in _dbContext.Customers.AsNoTracking() on order.BuyerId equals buyerRow.Id into buyers
            from buyer in buyers.DefaultIfEmpty()
            join sellerRow in _dbContext.Customers.AsNoTracking() on order.SellerId equals sellerRow.Id into sellers
            from seller in sellers.DefaultIfEmpty()
            orderby order.CreatedAt descending, order.Id descending
            select new OrderProjection(
                order.Id,
                order.Active,
                order.OrderDate,
                order.CompletedDate,
                order.OrderNo,
                order.BuyerId,
                buyer.Name,
                buyer.CustomFields,
                order.SellerId,
                seller.Name,
                seller.CustomFields,
                order.ExecutiveId,
                executive.Name,
                executive.BranchId,
                order.TotalQty,
                order.ShippedQty,
                order.SubTotal,
                order.GrandTotal,
                order.StatusId,
                order.CreatedBy,
                creator.Name,
                order.CreatedAt,
                order.OrderType,
                order.OrderRemark))
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim().ToLowerInvariant();
            rows = rows.Where(row =>
                row.OrderNo.ToLowerInvariant().Contains(search)
                || (row.BuyerName ?? string.Empty).ToLowerInvariant().Contains(search)
                || (row.SellerName ?? string.Empty).ToLowerInvariant().Contains(search)
                || (row.ExecutiveName ?? string.Empty).ToLowerInvariant().Contains(search)
                || (row.CreatedByName ?? string.Empty).ToLowerInvariant().Contains(search)).ToList();
        }

        var branchIds = rows.Select(row => FirstBranchId(row.BranchId)).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var branches = await _dbContext.Branches.AsNoTracking()
            .Where(x => branchIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.BranchName, cancellationToken);

        return rows.Select(row => new OrderDto
        {
            Id = row.Id,
            Active = row.Active,
            OrderDate = row.OrderDate,
            CompletedDate = row.CompletedDate,
            OrderNo = row.OrderNo,
            BuyerId = row.BuyerId,
            BuyerName = CustomerDisplayName(row.BuyerName, row.BuyerFields, "shop_name", "owner_name"),
            SellerId = row.SellerId,
            SellerName = CustomerDisplayName(row.SellerName, row.SellerFields, "legal_name", "shop_name", "distributor_code"),
            ExecutiveId = row.ExecutiveId,
            ExecutiveName = row.ExecutiveName,
            BranchName = FirstBranchId(row.BranchId) is { } branchId && branches.TryGetValue(branchId, out var branchName) ? branchName : null,
            TotalQty = row.TotalQty,
            ShippedQty = row.ShippedQty,
            SubTotal = row.SubTotal,
            GrandTotal = row.GrandTotal,
            StatusId = row.StatusId,
            StatusName = StatusName(row.StatusId),
            CreatedBy = row.CreatedBy,
            CreatedByName = row.CreatedByName,
            CreatedAt = row.CreatedAt,
            OrderType = row.OrderType,
            OrderRemark = row.OrderRemark
        }).ToArray();
    }

    public async Task<OrderDto?> GetOrderAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken) =>
        (await GetOrdersAsync(new OrderFilterDto { ActorUserId = actorUserId }, cancellationToken)).FirstOrDefault(x => x.Id == id);

    public async Task<IReadOnlyCollection<OrderDispatchDto>> GetDispatchesAsync(string? mode, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var orders = _dbContext.Orders.AsNoTracking();
        orders = await ApplyVisibilityAsync(orders, actorUserId, cancellationToken);
        if (string.Equals(mode, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return await (from order in orders
                          join detail in _dbContext.OrderDetails.AsNoTracking() on order.Id equals detail.OrderId
                          join productRow in _dbContext.Products.AsNoTracking() on detail.ProductId equals productRow.Id into products
                          from product in products.DefaultIfEmpty()
                          where order.StatusId == 4 && detail.Quantity > detail.ShippedQty
                          orderby order.UpdatedAt descending, order.Id descending, detail.Id
                          select new OrderDispatchDto
                          {
                              Id = detail.Id,
                              OrderId = order.Id,
                              OrderNo = order.OrderNo,
                              ProductCode = product.ProductCode,
                              ProductName = product.ProductName,
                              OrderQty = detail.Quantity,
                              ShippedQty = detail.ShippedQty,
                              CancelledQty = detail.Quantity - detail.ShippedQty,
                              StatusId = 4,
                              StatusName = detail.ShippedQty > 0 ? "Partially Cancelled" : "Cancelled",
                              CreatedAt = order.UpdatedAt
                          }).Take(MaxRows).ToArrayAsync(cancellationToken);
        }
        var query = from sale in _dbContext.Sales.AsNoTracking()
                    join order in orders on sale.OrderId equals order.Id
                    where sale.DeletedAt == null
                    orderby sale.DispatchDate descending, sale.Id descending
                    select new OrderDispatchDto
                    {
                        Id = sale.Id, OrderId = sale.OrderId, OrderNo = sale.OrderNo, InvoiceNo = sale.InvoiceNo,
                        InvoiceDate = sale.InvoiceDate, DispatchDate = sale.DispatchDate, LrNo = sale.LrNo,
                        TransportDetails = sale.TransportDetails, ShippedQty = sale.ShippedQty,
                        GrandTotal = sale.GrandTotal, StatusId = sale.StatusId,
                        StatusName = sale.StatusId == 1 ? "Fully Dispatched" : "Partially Dispatched", CreatedAt = sale.CreatedAt,
                        InvoiceAttachment = sale.InvoiceAttachment
                    };
        if (string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase)) query = query.Where(x => x.StatusId == 1);
        if (string.Equals(mode, "partial", StringComparison.OrdinalIgnoreCase)) query = query.Where(x => x.StatusId == 2);
        return await query.Take(MaxRows).ToArrayAsync(cancellationToken);
    }

    public async Task<OrderDispatchDetailDto?> GetDispatchDetailAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var visibleOrders = await ApplyVisibilityAsync(_dbContext.Orders.AsNoTracking(), actorUserId, cancellationToken);
        var header = await (
            from sale in _dbContext.Sales.AsNoTracking()
            join order in visibleOrders on sale.OrderId equals order.Id
            join buyerRow in _dbContext.Customers.AsNoTracking() on sale.BuyerId equals buyerRow.Id into buyers
            from buyer in buyers.DefaultIfEmpty()
            join sellerRow in _dbContext.Customers.AsNoTracking() on sale.SellerId equals sellerRow.Id into sellers
            from seller in sellers.DefaultIfEmpty()
            join creatorRow in _dbContext.Users.AsNoTracking() on sale.CreatedBy equals creatorRow.Id into creators
            from creator in creators.DefaultIfEmpty()
            join schemeRow in _dbContext.LoyaltySchemes.AsNoTracking() on sale.LoyaltySchemeId equals schemeRow.Id into schemes
            from scheme in schemes.DefaultIfEmpty()
            where sale.Id == id && sale.DeletedAt == null
            select new
            {
                Sale = sale,
                BuyerName = buyer.Name,
                BuyerFields = buyer.CustomFields,
                SellerName = seller.Name,
                SellerFields = seller.CustomFields,
                CreatedByName = creator.Name,
                SchemeName = scheme.SchemeName,
                SchemeCode = scheme.SchemeCode
            }).FirstOrDefaultAsync(cancellationToken);

        if (header is null) return null;

        var products = await (
            from detail in _dbContext.SaleDetails.AsNoTracking()
            join productRow in _dbContext.Products.AsNoTracking() on detail.ProductId equals productRow.Id into productRows
            from product in productRows.DefaultIfEmpty()
            join segmentRow in _dbContext.ProductCategories.AsNoTracking() on product.CategoryId equals segmentRow.Id into segmentRows
            from segment in segmentRows.DefaultIfEmpty()
            join familyRow in _dbContext.ProductFamilies.AsNoTracking() on product.SubcategoryId equals familyRow.Id into familyRows
            from family in familyRows.DefaultIfEmpty()
            where detail.SalesId == id
            orderby detail.Id
            select new OrderDispatchProductDto
            {
                Id = detail.Id,
                ProductId = detail.ProductId,
                ProductCode = product.PartNo,
                ProductName = product.ProductName,
                SegmentName = segment.CategoryName,
                FamilyName = family.SubcategoryName,
                Quantity = detail.ShippedQty ?? detail.Quantity ?? 0,
                Price = detail.Price ?? 0,
                TaxAmount = detail.TaxAmount ?? 0,
                LineTotal = detail.LineTotal ?? 0
            }).ToArrayAsync(cancellationToken);

        var dispatchSale = header.Sale;
        return new OrderDispatchDetailDto
        {
            Dispatch = new OrderDispatchDto
            {
                Id = dispatchSale.Id,
                OrderId = dispatchSale.OrderId,
                OrderNo = dispatchSale.OrderNo,
                InvoiceNo = dispatchSale.InvoiceNo,
                InvoiceDate = dispatchSale.InvoiceDate,
                DispatchDate = dispatchSale.DispatchDate,
                LrNo = dispatchSale.LrNo,
                TransportDetails = dispatchSale.TransportDetails,
                ShippedQty = dispatchSale.ShippedQty,
                GrandTotal = dispatchSale.GrandTotal,
                StatusId = dispatchSale.StatusId,
                StatusName = dispatchSale.StatusId == 1 ? "Fully Dispatched" : "Partially Dispatched",
                CreatedAt = dispatchSale.CreatedAt,
                InvoiceAttachment = dispatchSale.InvoiceAttachment
            },
            DealerName = CustomerDisplayName(header.SellerName, header.SellerFields, "legal_name", "shop_name", "distributor_code"),
            RetailerName = CustomerDisplayName(header.BuyerName, header.BuyerFields, "shop_name", "owner_name"),
            CreatedByName = header.CreatedByName,
            Remark = dispatchSale.Description,
            LoyaltySchemeId = dispatchSale.LoyaltySchemeId,
            LoyaltySchemeName = header.SchemeName,
            LoyaltySchemeCode = header.SchemeCode,
            Products = products
        };
    }

    public async Task DispatchAsync(Order order, IReadOnlyCollection<OrderDetail> details, OrderDispatchRequestDto request, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var removedIds = request.RemovedOrderDetailIds.ToHashSet();
        var removed = details.Where(x => removedIds.Contains(x.Id)).ToArray();
        if (removed.Any(x => x.ShippedQty > 0)) throw new InvalidOperationException("A product that was already dispatched cannot be removed from the order.");
        if (removed.Length > 0) _dbContext.OrderDetails.RemoveRange(removed);

        var now = DateTime.Now;
        var newRequests = request.Items.Where(x => !x.OrderDetailId.HasValue).ToArray();
        var productIds = newRequests.Where(x => x.ProductId.HasValue).Select(x => x.ProductId!.Value).Distinct().ToArray();
        var products = await _dbContext.Products.Where(x => productIds.Contains(x.Id) && x.Active == "Y").ToDictionaryAsync(x => x.Id, cancellationToken);
        if (productIds.Length != products.Count) throw new InvalidOperationException("One or more selected products are invalid or inactive.");
        var newDetails = new List<OrderDetail>();
        foreach (var item in newRequests)
        {
            if (!item.ProductId.HasValue || item.Quantity <= 0) throw new InvalidOperationException("Each new product requires a product and quantity greater than zero.");
            var product = products[item.ProductId.Value];
            var price = await _dbContext.ProductDetails.Where(x => x.ProductId == product.Id)
                .OrderBy(x => x.Id).Select(x => x.Mrp ?? x.Price ?? x.SellingPrice ?? 0).FirstOrDefaultAsync(cancellationToken);
            newDetails.Add(new OrderDetail
            {
                Active = "Y", OrderId = order.Id, ProductId = product.Id,
                CategoryId = item.CategoryId ?? product.CategoryId, SubcategoryId = item.SubcategoryId ?? product.SubcategoryId,
                Quantity = item.Quantity, ShippedQty = 0, Price = price, Gst = 0, TaxAmount = 0,
                LineTotal = price * item.Quantity, GstAmount = price * item.Quantity, StatusId = 2,
                CreatedAt = now, UpdatedAt = now
            });
        }
        if (newDetails.Count > 0)
        {
            await _dbContext.OrderDetails.AddRangeAsync(newDetails, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        var activeDetails = details.Where(x => !removedIds.Contains(x.Id)).Concat(newDetails).ToArray();
        if (activeDetails.Length == 0) throw new InvalidOperationException("The order must contain at least one product.");
        var requested = request.Items.Where(x => x.OrderDetailId.HasValue)
            .GroupBy(x => x.OrderDetailId!.Value)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
        var full = request.Mode == "full";
        var selected = new List<(OrderDetail Detail, long Qty)>();
        foreach (var detail in activeDetails)
        {
            var remaining = Math.Max(0, detail.Quantity - detail.ShippedQty);
            var quantity = full ? remaining : requested.GetValueOrDefault(detail.Id);
            if (quantity < 0 || quantity > remaining) throw new InvalidOperationException($"Dispatch quantity for product row {detail.Id} must be between 0 and {remaining}.");
            if (quantity > 0) selected.Add((detail, quantity));
        }
        if (selected.Count == 0) throw new InvalidOperationException("At least one product quantity is required for dispatch.");

        var selectedQuantities = selected.ToDictionary(x => x.Detail.Id, x => x.Qty);
        var completesOrder = activeDetails.All(detail =>
            detail.ShippedQty + selectedQuantities.GetValueOrDefault(detail.Id) >= detail.Quantity);
        var dispatchStatusId = completesOrder ? 1UL : 2UL;

        var shipped = selected.Sum(x => x.Qty);
        var subTotal = selected.Sum(x => x.Detail.Price * x.Qty);
        var gst = selected.Sum(x => x.Detail.Quantity > 0 ? x.Detail.TaxAmount / x.Detail.Quantity * x.Qty : 0);
        var invoiceDate = request.InvoiceDate!.Value.Date;
        var fiscal = invoiceDate.Month > 3 ? $"{invoiceDate.Year}-{invoiceDate.Year + 1}" : $"{invoiceDate.Year - 1}-{invoiceDate.Year}";
        var sale = new Sale
        {
            Active = "Y", BuyerId = order.BuyerId, SellerId = order.SellerId, OrderId = order.Id,
            TotalQty = order.TotalQty, ShippedQty = shipped, OrderNo = order.OrderNo, FiscalYear = fiscal,
            SalesNo = $"{request.InvoiceNo}-{order.SellerId}-{fiscal}", InvoiceNo = request.InvoiceNo!.Trim(), InvoiceDate = invoiceDate,
            TransportDetails = request.TransportDetails, TotalGst = gst, SubTotal = subTotal, GrandTotal = subTotal + gst,
            PaidAmount = 0, Description = request.Remark ?? string.Empty, StatusId = dispatchStatusId,
            CreatedBy = actorUserId, LrNo = request.LrNo?.Trim(), DispatchDate = request.DispatchDate!.Value.Date,
            InvoiceAttachment = request.InvoiceAttachment, CreatedAt = now, UpdatedAt = now
            , LoyaltySchemeId = request.LoyaltySchemeId
        };
        await _dbContext.Sales.AddAsync(sale, cancellationToken);
        // sales.id is an IDENTITY column. Save the header first so SQL Server generates
        // its key before the detail rows reference it.
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (request.LoyaltySchemeId.HasValue)
        {
            if (!order.BuyerId.HasValue || !actorUserId.HasValue || string.IsNullOrWhiteSpace(request.InvoiceAttachment))
                throw new InvalidOperationException("Retailer, logged-in user and invoice attachment are required for a scheme invoice.");

            var loyaltyInvoice = new NewInvoice
            {
                SecondaryCustomerId = order.BuyerId.Value,
                LoyaltySchemeId = request.LoyaltySchemeId.Value,
                InvoiceNumber = request.InvoiceNo!.Trim(),
                InvoiceDate = invoiceDate,
                Amount = sale.GrandTotal,
                Points = 0,
                Attachment = request.InvoiceAttachment,
                ApprovalStatus = NewInvoice.StatusPending,
                CreatedBy = actorUserId.Value,
                CreatedAt = now,
                UpdatedAt = now
            };
            await _dbContext.NewInvoices.AddAsync(loyaltyInvoice, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _dbContext.NewInvoiceApprovalLogs.AddAsync(new NewInvoiceApprovalLog
            {
                LogDate = now.Date,
                NewInvoiceId = loyaltyInvoice.Id,
                CreatedBy = actorUserId.Value,
                StatusType = "generated",
                FromStatus = null,
                ToStatus = NewInvoice.StatusPending,
                CreatedAt = now,
                UpdatedAt = now
            }, cancellationToken);
        }
        foreach (var row in selected)
        {
            var proportionalTax = row.Detail.Quantity > 0 ? row.Detail.TaxAmount / row.Detail.Quantity * row.Qty : 0;
            await _dbContext.SaleDetails.AddAsync(new SaleDetail
            {
                Active = "Y", SalesId = sale.Id, ProductId = row.Detail.ProductId,
                ProductDetailId = row.Detail.ProductDetailId, Quantity = row.Qty, ShippedQty = row.Qty,
                Price = row.Detail.Price, Discount = row.Detail.Discount, TaxAmount = proportionalTax,
                LineTotal = row.Detail.Price * row.Qty,
                StatusId = row.Detail.ShippedQty + row.Qty >= row.Detail.Quantity ? 1UL : 2UL,
                CreatedAt = now, UpdatedAt = now
            }, cancellationToken);
            row.Detail.ShippedQty += row.Qty;
            row.Detail.StatusId = row.Detail.ShippedQty >= row.Detail.Quantity ? 1UL : 2UL;
            row.Detail.UpdatedAt = now;
        }
        order.TotalQty = activeDetails.Sum(x => x.Quantity);
        order.ShippedQty = Math.Min(order.TotalQty, activeDetails.Sum(x => x.ShippedQty));
        order.SubTotal = activeDetails.Sum(x => x.LineTotal);
        order.TotalGst = activeDetails.Sum(x => x.TaxAmount);
        order.GrandTotal = order.SubTotal + order.TotalGst;
        order.StatusId = completesOrder ? 1UL : 2UL;
        order.CompletedDate = order.StatusId == 1 ? request.DispatchDate.Value.Date : null;
        order.OrderRemark = string.IsNullOrWhiteSpace(request.Remark) ? order.OrderRemark : request.Remark;
        order.UpdatedBy = actorUserId; order.UpdatedAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        });
    }

    public async Task<IReadOnlyCollection<OrderExportRowDto>> GetOrderExportRowsAsync(OrderFilterDto filter, CancellationToken cancellationToken)
    {
        var orderQuery = _dbContext.Orders.AsNoTracking();
        orderQuery = await ApplyExportVisibilityAsync(orderQuery, filter.ActorUserId, cancellationToken);
        orderQuery = ApplyFilters(orderQuery, filter);

        var rows = await (
            from detail in _dbContext.OrderDetails.AsNoTracking()
            join order in orderQuery on detail.OrderId equals order.Id
            // Laravel OrderExport uses orders.created_by (createdbyname/getuserdetails)
            // for employee and reporting hierarchy columns.
            join employeeRow in _dbContext.Users.AsNoTracking() on order.CreatedBy equals employeeRow.Id into employees
            from employee in employees.DefaultIfEmpty()
            join reportingRow in _dbContext.Users.AsNoTracking() on employee.ReportingId equals reportingRow.Id into reportings
            from reporting in reportings.DefaultIfEmpty()
            join designationRow in _dbContext.Designations.AsNoTracking() on employee.DesignationId equals designationRow.Id into designations
            from designation in designations.DefaultIfEmpty()
            join divisionRow in _dbContext.Divisions.AsNoTracking() on employee.DivisionId equals divisionRow.Id into divisions
            from division in divisions.DefaultIfEmpty()
            join buyerRow in _dbContext.Customers.AsNoTracking() on order.BuyerId equals buyerRow.Id into buyers
            from buyer in buyers.DefaultIfEmpty()
            join sellerRow in _dbContext.Customers.AsNoTracking() on order.SellerId equals sellerRow.Id into sellers
            from seller in sellers.DefaultIfEmpty()
            join productRow in _dbContext.Products.AsNoTracking() on detail.ProductId equals productRow.Id into products
            from product in products.DefaultIfEmpty()
            join segmentRow in _dbContext.ProductCategories.AsNoTracking() on product.CategoryId equals segmentRow.Id into segments
            from segment in segments.DefaultIfEmpty()
            join familyRow in _dbContext.ProductFamilies.AsNoTracking() on product.SubcategoryId equals familyRow.Id into families
            from family in families.DefaultIfEmpty()
            orderby order.CreatedAt descending, order.Id descending, detail.Id
            select new OrderExportProjection(
                order.OrderDate,
                order.OrderNo,
                employee.Name,
                reporting.Name,
                designation.DesignationName,
                employee.BranchId,
                buyer.Name,
                buyer.CustomFields,
                seller.Name,
                seller.CustomFields,
                seller.CustomerCode,
                seller.SapCode,
                product.ProductCode,
                product.ProductName,
                detail.Quantity,
                detail.ShippedQty,
                order.StatusId,
                detail.Price,
                detail.LineTotal,
                employee.EmployeeCodes,
                order.BuyerId,
                order.SellerId,
                order.OrderRemark,
                segment.CategoryName,
                family.SubcategoryName,
                detail.Id,
                division.DivisionName))
            .ToListAsync(cancellationToken);

        var branchIds = rows.Select(row => FirstBranchId(row.BranchId)).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var branches = await _dbContext.Branches.AsNoTracking()
            .Where(x => branchIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.BranchName, cancellationToken);

        return rows.Select(row => new OrderExportRowDto
        {
            OrderDate = row.OrderDate,
            OrderNo = row.OrderNo,
            EmployeeName = row.EmployeeName,
            ReportingManager = row.ReportingManager,
            Designation = row.Designation,
            Branch = FirstBranchId(row.BranchId) is { } branchId && branches.TryGetValue(branchId, out var branchName) ? branchName : null,
            RetailerName = CustomerDisplayName(row.RetailerName, row.RetailerFields, "shop_name", "owner_name"),
            DistributorName = CustomerDisplayName(row.DistributorName, row.DistributorFields, "legal_name", "shop_name"),
            DistributorCode = FirstNonBlank(
                row.DistributorCustomerCode,
                CustomerField(row.DistributorFields, "distributor_code"),
                row.DistributorSapCode),
            ProductCode = row.ProductCode,
            ProductName = row.ProductName,
            OrderQuantity = row.Quantity,
            ShippedQuantity = row.ShippedQty,
            CancelledQuantity = row.StatusId == 4 ? Math.Max(0, row.Quantity - row.ShippedQty) : 0,
            PendingQuantity = row.StatusId == 4 ? 0 : Math.Max(0, row.Quantity - row.ShippedQty),
            Status = LineItemStatus(row.StatusId, row.Quantity, row.ShippedQty),
            Rate = row.Rate,
            TotalOrderValue = row.TotalOrderValue,
            EmployeeCode = row.EmployeeCode,
            RetailerId = row.RetailerId,
            DistributorId = row.DistributorId,
            OrderRemark = row.OrderRemark,
            Segment = row.Segment,
            Family = row.Family,
            DetailId = row.DetailId,
            Zone = row.Zone
        }).ToArray();
    }

    public async Task<IReadOnlyCollection<OrderDetailDto>> GetOrderDetailsAsync(ulong orderId, CancellationToken cancellationToken)
    {
        var orderStatusId = await _dbContext.Orders.AsNoTracking()
            .Where(order => order.Id == orderId)
            .Select(order => order.StatusId)
            .FirstOrDefaultAsync(cancellationToken);
        var rows = await (
            from detail in _dbContext.OrderDetails.AsNoTracking()
            join productRow in _dbContext.Products.AsNoTracking() on detail.ProductId equals productRow.Id into products
            from product in products.DefaultIfEmpty()
            join segmentRow in _dbContext.ProductCategories.AsNoTracking() on (detail.CategoryId ?? product.CategoryId) equals segmentRow.Id into segments
            from segment in segments.DefaultIfEmpty()
            join familyRow in _dbContext.ProductFamilies.AsNoTracking() on (detail.SubcategoryId ?? product.SubcategoryId) equals familyRow.Id into families
            from family in families.DefaultIfEmpty()
            where detail.OrderId == orderId
            orderby detail.Id
            select new OrderDetailDto
            {
                Id = detail.Id,
                ProductId = detail.ProductId,
                ProductName = product.ProductName,
                CategoryId = detail.CategoryId ?? product.CategoryId,
                CategoryName = segment.CategoryName,
                SubcategoryId = detail.SubcategoryId ?? product.SubcategoryId,
                SubcategoryName = family.SubcategoryName,
                Quantity = detail.Quantity,
                ShippedQty = detail.ShippedQty,
                Price = detail.Price,
                Gst = detail.Gst,
                TaxAmount = detail.TaxAmount,
                LineTotal = detail.LineTotal,
                StatusId = detail.StatusId,
                StatusName = StatusName(detail.StatusId)
            })
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
            row.StatusName = LineItemStatus(orderStatusId, Convert.ToInt64(row.Quantity), Convert.ToInt64(row.ShippedQty));
        return rows;
    }

    public async Task<bool> DeleteOrderAsync(ulong id, CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM order_details WHERE order_id = {id}",
            cancellationToken);

        var deletedOrders = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM orders WHERE id = {id}",
            cancellationToken);

        return deletedOrders > 0;
    }

    public async Task<OrderOptionsDto> GetOptionsAsync(ulong? actorUserId, CancellationToken cancellationToken)
    {
        var visibleUserIds = await ReportingVisibility.GetVisibleUserIdsAsync(_dbContext, actorUserId, cancellationToken);
        var dealerCustomerId = await DealerCustomerIdAsync(actorUserId, cancellationToken);
        return new OrderOptionsDto
        {
            Users = await ReportingVisibility.InternalUsersQuery(_dbContext, _dbContext.Users.AsNoTracking())
                .Where(x => x.Active == "Y" && visibleUserIds.Contains(x.Id))
                .OrderBy(x => x.Name)
                .Select(x => new OptionDto { Id = x.Id, Name = x.Name })
                .ToListAsync(cancellationToken),
            Divisions = await _dbContext.Divisions.AsNoTracking()
                .Where(x => x.Active == "Y")
                .OrderBy(x => x.DivisionName)
                .Select(x => new OptionDto { Id = x.Id, Name = x.DivisionName })
                .ToListAsync(cancellationToken),
            Designations = await _dbContext.Designations.AsNoTracking()
                .Where(x => x.Active == "Y")
                .OrderBy(x => x.DesignationName)
                .Select(x => new OptionDto { Id = x.Id, Name = x.DesignationName })
                .ToListAsync(cancellationToken),
            Retailers = await CustomerOptionsAsync([RetailerCustomerType, InfluencerCustomerType], ["shop_name", "owner_name"], cancellationToken, dealerCustomerId),
            Distributors = await CustomerOptionsAsync([DistributorCustomerType], ["legal_name", "shop_name", "distributor_code"], cancellationToken, dealerCustomerId),
            Families = await _dbContext.ProductFamilies.AsNoTracking()
                .Where(x => x.Active == "Y")
                .OrderBy(x => x.SubcategoryName)
                .Select(x => new OptionDto { Id = x.Id, Name = x.SubcategoryName })
                .ToListAsync(cancellationToken)
        };
    }

    public async Task<IReadOnlyCollection<OrderProductOptionDto>> GetProductsByFamilyAsync(ulong familyId, CancellationToken cancellationToken)
    {
        var products = await _dbContext.Products.AsNoTracking()
            .Where(x => x.Active == "Y" && x.SubcategoryId == familyId)
            .OrderBy(x => x.ProductName)
            .Select(x => new OrderProductOptionDto
            {
                Id = x.Id,
                Name = x.ProductName,
                ProductCode = x.ProductCode,
                HsnSac = x.HsnSac,
                Price = _dbContext.ProductDetails.AsNoTracking()
                    .Where(detail => detail.ProductId == x.Id)
                    .OrderBy(detail => detail.Id)
                    .Select(detail => detail.Mrp ?? detail.Price ?? detail.SellingPrice)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return products;
    }

    public Task<User?> GetUserAsync(ulong id, CancellationToken cancellationToken) =>
        _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<Order?> GetOrderEntityAsync(ulong id, CancellationToken cancellationToken) =>
        _dbContext.Orders.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyCollection<OrderDetail>> GetOrderDetailEntitiesAsync(ulong orderId, CancellationToken cancellationToken) =>
        await _dbContext.OrderDetails.Where(x => x.OrderId == orderId).OrderBy(x => x.Id).ToListAsync(cancellationToken);

    public async Task AddOrderAsync(Order order, CancellationToken cancellationToken) =>
        await _dbContext.Orders.AddAsync(order, cancellationToken);

    public async Task AddOrderDetailsAsync(IReadOnlyCollection<OrderDetail> details, CancellationToken cancellationToken) =>
        await _dbContext.OrderDetails.AddRangeAsync(details, cancellationToken);

    public void RemoveOrder(Order order) =>
        _dbContext.Orders.Remove(order);

    public void RemoveOrderDetails(IReadOnlyCollection<OrderDetail> details) =>
        _dbContext.OrderDetails.RemoveRange(details);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _dbContext.SaveChangesAsync(cancellationToken);

    private async Task<IQueryable<Order>> ApplyVisibilityAsync(IQueryable<Order> query, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var dealerCustomerId = await DealerCustomerIdAsync(actorUserId, cancellationToken);
        if (dealerCustomerId.HasValue)
        {
            return query.Where(x => x.SellerId == dealerCustomerId.Value);
        }

        var visibleUserIds = await ReportingVisibility.GetVisibleUserIdsAsync(_dbContext, actorUserId, cancellationToken);
        return query.Where(x =>
            (x.ExecutiveId.HasValue && visibleUserIds.Contains(x.ExecutiveId.Value))
            || (x.CreatedBy.HasValue && visibleUserIds.Contains(x.CreatedBy.Value)));
    }

    private async Task<IQueryable<Order>> ApplyExportVisibilityAsync(IQueryable<Order> query, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var dealerCustomerId = await DealerCustomerIdAsync(actorUserId, cancellationToken);
        if (dealerCustomerId.HasValue) return query.Where(x => x.SellerId == dealerCustomerId.Value);
        var visibleUserIds = await ReportingVisibility.GetVisibleUserIdsAsync(_dbContext, actorUserId, cancellationToken);
        return query.Where(x => x.CreatedBy.HasValue && visibleUserIds.Contains(x.CreatedBy.Value));
    }

    private IQueryable<Order> ApplyFilters(IQueryable<Order> query, OrderFilterDto filter)
    {
        if (filter.RetailersId.HasValue) query = query.Where(x => x.BuyerId == filter.RetailersId.Value);
        if (filter.DistributorId.HasValue) query = query.Where(x => x.SellerId == filter.DistributorId.Value);
        if (filter.UserId.HasValue) query = query.Where(x => x.CreatedBy == filter.UserId.Value);
        if (filter.PendingStatus.HasValue) query = filter.PendingStatus.Value == 0 ? query.Where(x => x.StatusId == null) : query.Where(x => x.StatusId == (ulong)filter.PendingStatus.Value);
        if (filter.StartDate.HasValue) query = query.Where(x => x.OrderDate >= filter.StartDate.Value.Date);
        if (filter.EndDate.HasValue) query = query.Where(x => x.OrderDate <= filter.EndDate.Value.Date);

        if (filter.DivisionId.HasValue)
        {
            var userIds = _dbContext.Users.AsNoTracking().Where(x => x.DivisionId == filter.DivisionId.Value).Select(x => x.Id);
            query = query.Where(x => x.CreatedBy.HasValue && userIds.Contains(x.CreatedBy.Value));
        }

        if (filter.DesignationIds.Count > 0)
        {
            var userIds = _dbContext.Users.AsNoTracking().Where(x => x.DesignationId.HasValue && filter.DesignationIds.Contains(x.DesignationId.Value)).Select(x => x.Id);
            query = query.Where(x => x.CreatedBy.HasValue && userIds.Contains(x.CreatedBy.Value));
        }

        return query;
    }

    private async Task<IReadOnlyCollection<OptionDto>> CustomerOptionsAsync(IReadOnlyCollection<ulong> customerTypes, string[] preferredFields, CancellationToken cancellationToken, ulong? dealerCustomerId = null)
    {
        var query = _dbContext.Customers.AsNoTracking()
            .Where(x => x.Active == "Y" && x.CustomerType.HasValue && customerTypes.Contains(x.CustomerType.Value))
            .Where(x => !dealerCustomerId.HasValue ||
                (customerTypes.Contains(DistributorCustomerType) && x.Id == dealerCustomerId.Value) ||
                (x.CustomFields != null &&
                 (EF.Functions.Like(x.CustomFields, $"%\"distributor_name\":\"{dealerCustomerId.Value}\"%") ||
                  EF.Functions.Like(x.CustomFields, $"%\"distributor_name\":{dealerCustomerId.Value}%") ||
                  EF.Functions.Like(x.CustomFields, $"%\"agri_distributor\":\"{dealerCustomerId.Value}\"%") ||
                  EF.Functions.Like(x.CustomFields, $"%\"agri_distributor\":{dealerCustomerId.Value}%"))));

        var customers = await query
            .OrderBy(x => x.Name)
            .Take(MaxRows)
            .Select(x => new { x.Id, x.Name, x.CustomFields })
            .ToListAsync(cancellationToken);

        return customers
            .Select(customer => new OptionDto
            {
                Id = customer.Id,
                Name = CustomerDisplayName(customer.Name, customer.CustomFields, preferredFields)
            })
            .Where(option => !string.IsNullOrWhiteSpace(option.Name))
            .OrderBy(option => option.Name)
            .ToArray();
    }

    private async Task<ulong?> DealerCustomerIdAsync(ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) return null;
        return await _dbContext.Users.AsNoTracking()
            .Where(x => x.Id == actorUserId.Value && x.CustomerId.HasValue)
            .Join(_dbContext.Customers.AsNoTracking(), x => x.CustomerId, x => x.Id, (_, customer) => customer)
            .Where(x => x.DeletedAt == null && x.CustomerType == DistributorCustomerType)
            .Select(x => (ulong?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string CustomerDisplayName(string? fallback, string? customFields, params string[] preferredFields)
    {
        var fields = DeserializeFields(customFields);
        foreach (var field in preferredFields)
        {
            if (fields.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return fallback?.Trim() ?? string.Empty;
    }

    private static string? CustomerField(string? customFields, string field)
    {
        var fields = DeserializeFields(customFields);
        return fields.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private static Dictionary<string, string?> DeserializeFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ulong? FirstBranchId(string? branchIds)
    {
        if (string.IsNullOrWhiteSpace(branchIds)) return null;
        var first = branchIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return ulong.TryParse(first, out var id) ? id : null;
    }

    private static string StatusName(ulong? statusId) =>
        statusId switch
        {
            1 => "Dispatch",
            2 => "Partial Dispatch",
            4 => "Canceled",
            _ => "Pending"
        };

    private static string LineItemStatus(ulong? orderStatusId, long orderQuantity, long shippedQuantity)
    {
        if (orderStatusId == 4)
        {
            if (shippedQuantity <= 0) return "Cancelled";
            if (shippedQuantity < orderQuantity) return "Partially Cancelled";
            return "Dispatched";
        }
        if (shippedQuantity >= orderQuantity && orderQuantity > 0) return "Dispatched";
        if (shippedQuantity > 0) return "Partial Dispatch";
        return "Pending";
    }

    private sealed record OrderProjection(
        ulong Id,
        string Active,
        DateTime? OrderDate,
        DateTime? CompletedDate,
        string OrderNo,
        ulong? BuyerId,
        string? BuyerName,
        string? BuyerFields,
        ulong? SellerId,
        string? SellerName,
        string? SellerFields,
        ulong? ExecutiveId,
        string? ExecutiveName,
        string? BranchId,
        decimal TotalQty,
        decimal ShippedQty,
        decimal SubTotal,
        decimal GrandTotal,
        ulong? StatusId,
        ulong? CreatedBy,
        string? CreatedByName,
        DateTime? CreatedAt,
        string? OrderType,
        string? OrderRemark);

    private sealed record OrderExportProjection(
        DateTime? OrderDate,
        string OrderNo,
        string? EmployeeName,
        string? ReportingManager,
        string? Designation,
        string? BranchId,
        string? RetailerName,
        string? RetailerFields,
        string? DistributorName,
        string? DistributorFields,
        string? DistributorCustomerCode,
        string? DistributorSapCode,
        string? ProductCode,
        string? ProductName,
        long Quantity,
        long ShippedQty,
        ulong? StatusId,
        decimal Rate,
        decimal TotalOrderValue,
        string? EmployeeCode,
        ulong? RetailerId,
        ulong? DistributorId,
        string? OrderRemark,
        string? Segment,
        string? Family,
        ulong DetailId,
        string? Zone);
}
