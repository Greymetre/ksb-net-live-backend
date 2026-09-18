using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public sealed class AppDocumentConfiguration : IEntityTypeConfiguration<AppDocument>
{
    public void Configure(EntityTypeBuilder<AppDocument> builder)
    {
        builder.ToTable("app_documents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.DocumentName).HasColumnName("document_name").HasMaxLength(200);
        builder.Property(x => x.FilePath).HasColumnName("file_path").HasMaxLength(1000);
        builder.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(500);
        builder.Property(x => x.FileSize).HasColumnName("file_size");
        builder.Property(x => x.ShowInSfa).HasColumnName("show_in_sfa");
        builder.Property(x => x.ShowInVriddhi).HasColumnName("show_in_vriddhi");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        builder.Property(x => x.DeletedBy).HasColumnName("deleted_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        builder.HasQueryFilter(x => x.DeletedAt == null);
    }
}
