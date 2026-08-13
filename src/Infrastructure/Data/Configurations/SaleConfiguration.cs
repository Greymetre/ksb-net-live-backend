using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> b)
    {
        b.ToTable("sales"); b.HasKey(x => x.Id); b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        b.Property(x => x.Active).HasColumnName("active"); b.Property(x => x.BuyerId).HasColumnName("buyer_id"); b.Property(x => x.SellerId).HasColumnName("seller_id"); b.Property(x => x.OrderId).HasColumnName("order_id");
        b.Property(x => x.TotalQty).HasColumnName("total_qty"); b.Property(x => x.ShippedQty).HasColumnName("shipped_qty"); b.Property(x => x.OrderNo).HasColumnName("orderno"); b.Property(x => x.FiscalYear).HasColumnName("fiscal_year");
        b.Property(x => x.SalesNo).HasColumnName("sales_no"); b.Property(x => x.InvoiceNo).HasColumnName("invoice_no"); b.Property(x => x.InvoiceDate).HasColumnName("invoice_date"); b.Property(x => x.TransportDetails).HasColumnName("transport_details");
        b.Property(x => x.TotalGst).HasColumnName("total_gst"); b.Property(x => x.TotalDiscount).HasColumnName("total_discount"); b.Property(x => x.ExtraDiscount).HasColumnName("extra_discount"); b.Property(x => x.ExtraDiscountAmount).HasColumnName("extra_discount_amount");
        b.Property(x => x.SubTotal).HasColumnName("sub_total"); b.Property(x => x.GrandTotal).HasColumnName("grand_total"); b.Property(x => x.PaidAmount).HasColumnName("paid_amount"); b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.StatusId).HasColumnName("status_id"); b.Property(x => x.CreatedBy).HasColumnName("created_by"); b.Property(x => x.UpdatedBy).HasColumnName("updated_by"); b.Property(x => x.TransportName).HasColumnName("transport_name");
        b.Property(x => x.LrNo).HasColumnName("lr_no"); b.Property(x => x.DispatchDate).HasColumnName("dispatch_date"); b.Property(x => x.CreatedAt).HasColumnName("created_at"); b.Property(x => x.UpdatedAt).HasColumnName("updated_at"); b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.InvoiceAttachment).HasColumnName("invoice_attachment").HasMaxLength(500);
        b.Property(x => x.LoyaltySchemeId).HasColumnName("loyalty_scheme_id");
        b.HasIndex(x => x.LoyaltySchemeId);
    }
}

public sealed class SaleDetailConfiguration : IEntityTypeConfiguration<SaleDetail>
{
    public void Configure(EntityTypeBuilder<SaleDetail> b)
    {
        b.ToTable("sales_details"); b.HasKey(x => x.Id); b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        b.Property(x => x.Active).HasColumnName("active"); b.Property(x => x.SalesId).HasColumnName("sales_id"); b.Property(x => x.ProductId).HasColumnName("product_id"); b.Property(x => x.ProductDetailId).HasColumnName("product_detail_id");
        b.Property(x => x.Quantity).HasColumnName("quantity"); b.Property(x => x.ShippedQty).HasColumnName("shipped_qty"); b.Property(x => x.Price).HasColumnName("price"); b.Property(x => x.Discount).HasColumnName("discount");
        b.Property(x => x.DiscountAmount).HasColumnName("discount_amount"); b.Property(x => x.TaxAmount).HasColumnName("tax_amount"); b.Property(x => x.LineTotal).HasColumnName("line_total"); b.Property(x => x.StatusId).HasColumnName("status_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at"); b.Property(x => x.UpdatedAt).HasColumnName("updated_at"); b.Ignore(x => x.DeletedAt);
    }
}
