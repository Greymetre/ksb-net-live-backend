using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public sealed class PromotionalActivityConfiguration : IEntityTypeConfiguration<PromotionalActivity>
{
    public void Configure(EntityTypeBuilder<PromotionalActivity> b)
    {
        b.ToTable("promotional_activities");
        b.HasKey(x => x.Id);
        ActivityEntityMapping.MapBase(b);
        b.Property(x => x.ActivityCode).HasColumnName("activity_code").HasMaxLength(40);
        b.Property(x => x.ActivityType).HasColumnName("activity_type").HasMaxLength(20);
        b.Property(x => x.ActivityName).HasColumnName("activity_name").HasMaxLength(150);
        b.Property(x => x.ActivityDate).HasColumnName("activity_date").HasColumnType("date");
        b.Property(x => x.UserId).HasColumnName("user_id").HasColumnType("bigint");
        b.Property(x => x.CreatedById).HasColumnName("created_by_id").HasColumnType("bigint");
        b.Property(x => x.BranchId).HasColumnName("branch_id").HasColumnType("bigint");
        b.Property(x => x.Zone).HasColumnName("zone").HasMaxLength(100);
        b.Property(x => x.ReportingManagerId).HasColumnName("reporting_manager_id").HasColumnType("bigint");
        b.Property(x => x.DistributorId).HasColumnName("distributor_id").HasColumnType("bigint");
        b.Property(x => x.DistributorName).HasColumnName("distributor_name").HasMaxLength(255);
        b.Property(x => x.DealerName).HasColumnName("dealer_name").HasMaxLength(255);
        b.Property(x => x.HotelName).HasColumnName("hotel_name").HasMaxLength(255);
        b.Property(x => x.LocationLat).HasColumnName("location_lat").HasPrecision(10, 7);
        b.Property(x => x.LocationLng).HasColumnName("location_lng").HasPrecision(10, 7);
        b.Property(x => x.LocationText).HasColumnName("location_text").HasMaxLength(500);
        b.Property(x => x.GiftCount).HasColumnName("gift_count");
        b.Property(x => x.TotalExpense).HasColumnName("total_expense").HasPrecision(18, 2);
        b.Property(x => x.DealerShareAmount).HasColumnName("dealer_share_amount").HasPrecision(18, 2);
        b.Property(x => x.Feedback).HasColumnName("feedback");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        b.HasMany(x => x.Participants).WithOne().HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Expenses).WithOne().HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Photos).WithOne().HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PromotionalActivityParticipantConfiguration : IEntityTypeConfiguration<PromotionalActivityParticipant>
{
    public void Configure(EntityTypeBuilder<PromotionalActivityParticipant> b)
    {
        b.ToTable("promotional_activity_participants"); b.HasKey(x => x.Id); ActivityEntityMapping.MapBase(b);
        b.Property(x => x.ActivityId).HasColumnName("activity_id").HasColumnType("bigint");
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(255); b.Property(x => x.ShopName).HasColumnName("shop_name").HasMaxLength(255);
        b.Property(x => x.ProprietorName).HasColumnName("proprietor_name").HasMaxLength(255); b.Property(x => x.ParticipantType).HasColumnName("participant_type").HasMaxLength(100);
        b.Property(x => x.Profession).HasColumnName("profession").HasMaxLength(100);
        b.Property(x => x.Mobile).HasColumnName("mobile").HasMaxLength(20); b.Property(x => x.GiftName).HasColumnName("gift_name").HasMaxLength(255);
        b.Property(x => x.Remarks).HasColumnName("remarks"); b.Property(x => x.IsInfluencer).HasColumnName("is_influencer");
        b.Property(x => x.SocialType).HasColumnName("social_type").HasMaxLength(50); b.Property(x => x.SocialLink).HasColumnName("social_link").HasMaxLength(500);
    }
}

public sealed class PromotionalActivityExpenseConfiguration : IEntityTypeConfiguration<PromotionalActivityExpense>
{
    public void Configure(EntityTypeBuilder<PromotionalActivityExpense> b)
    {
        b.ToTable("promotional_activity_expenses"); b.HasKey(x => x.Id); ActivityEntityMapping.MapBase(b);
        b.Property(x => x.ActivityId).HasColumnName("activity_id").HasColumnType("bigint"); b.Property(x => x.ExpenseType).HasColumnName("expense_type").HasMaxLength(50);
        b.Property(x => x.TotalAmount).HasColumnName("total_amount").HasPrecision(18, 2); b.Property(x => x.DealerShareAmount).HasColumnName("dealer_share_amount").HasPrecision(18, 2);
        b.Property(x => x.DealerSharePct).HasColumnName("dealer_share_pct").HasPrecision(7, 2); b.Property(x => x.Remarks).HasColumnName("remarks");
        b.Property(x => x.InvoiceUrl).HasColumnName("invoice_url").HasMaxLength(500);
    }
}

public sealed class PromotionalActivityPhotoConfiguration : IEntityTypeConfiguration<PromotionalActivityPhoto>
{
    public void Configure(EntityTypeBuilder<PromotionalActivityPhoto> b)
    {
        b.ToTable("promotional_activity_photos"); b.HasKey(x => x.Id); ActivityEntityMapping.MapBase(b);
        b.Property(x => x.ActivityId).HasColumnName("activity_id").HasColumnType("bigint"); b.Property(x => x.PhotoUrl).HasColumnName("photo_url").HasMaxLength(500);
        b.Property(x => x.Latitude).HasColumnName("latitude").HasPrecision(10, 7); b.Property(x => x.Longitude).HasColumnName("longitude").HasPrecision(10, 7);
        b.Property(x => x.TakenAt).HasColumnName("taken_at");
    }
}

internal static class ActivityEntityMapping
{
    internal static void MapBase<T>(EntityTypeBuilder<T> b) where T : class
    {
        b.Property<long>("Id").HasColumnName("id").HasColumnType("bigint");
        b.Property<DateTime?>("CreatedAt").HasColumnName("created_at"); b.Property<DateTime?>("UpdatedAt").HasColumnName("updated_at");
        b.Property<DateTime?>("DeletedAt").HasColumnName("deleted_at");
    }
}
