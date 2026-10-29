using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public sealed class NewInvoiceItemConfiguration : IEntityTypeConfiguration<NewInvoiceItem>
{
    public void Configure(EntityTypeBuilder<NewInvoiceItem> builder)
    {
        builder.ToTable("new_invoice_items");
        builder.HasKey(x => x.Id);
        // This table is not in any EF migration snapshot (the project ships schema as SQL
        // scripts, not migrations), so EF only sees the model. A ulong would otherwise be modelled
        // as decimal(20,0) and clash with the bigint columns on read, so the ids are mapped to
        // bigint explicitly.
        builder.Property(x => x.Id).HasColumnName("id").HasConversion<long>();
        builder.Property(x => x.NewInvoiceId).HasColumnName("new_invoice_id").HasConversion<long>();
        builder.Property(x => x.ProductId).HasColumnName("product_id").HasConversion<long>();
        builder.Property(x => x.Quantity).HasColumnName("quantity").HasPrecision(15, 2);
        builder.Property(x => x.Price).HasColumnName("price").HasPrecision(15, 2);
        builder.Property(x => x.SortOrder).HasColumnName("sort_order").HasDefaultValue(0);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        // The parent new_invoices row has no soft delete, so neither does an item.
        builder.Ignore(x => x.DeletedAt);
        builder.HasIndex(x => x.NewInvoiceId);
        builder.HasIndex(x => x.ProductId);
    }
}
