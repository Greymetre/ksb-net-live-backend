using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public sealed class NewInvoiceAttachmentConfiguration : IEntityTypeConfiguration<NewInvoiceAttachment>
{
    public void Configure(EntityTypeBuilder<NewInvoiceAttachment> builder)
    {
        builder.ToTable("new_invoice_attachments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.InvoiceId).HasColumnName("invoice_id");
        builder.Property(x => x.FilePath).HasColumnName("file_path").HasMaxLength(1000);
        builder.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(500);
        builder.Property(x => x.MimeType).HasColumnName("mime_type").HasMaxLength(150);
        builder.Property(x => x.FileSize).HasColumnName("file_size");
        builder.Property(x => x.SortOrder).HasColumnName("sort_order");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => new { x.InvoiceId, x.SortOrder, x.Id });
    }
}
