using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public sealed class DealerFeedbackConfiguration : IEntityTypeConfiguration<DealerFeedback>
{
    public void Configure(EntityTypeBuilder<DealerFeedback> builder)
    {
        builder.ToTable("dealer_feedback");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.CustomerId).HasColumnName("customer_id");
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(x => x.Category).HasColumnName("category").HasMaxLength(100);
        builder.Property(x => x.Message).HasColumnName("message").HasColumnType("nvarchar(max)");
        builder.Property(x => x.Rating).HasColumnName("rating");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.DeletedAt).HasColumnName("deleted_at");

        builder.HasIndex(x => x.CustomerId);
        builder.HasQueryFilter(x => x.DeletedAt == null);
    }
}
