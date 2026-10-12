namespace Domain.Entities;

/// <summary>One feedback note a dealer leaves from the CRM dealer portal.
///
/// The dealer who writes it is a Distributor-role <see cref="User"/> linked to a
/// <see cref="Customer"/> through <c>users.customer_id</c>; that customer id is stored here
/// so internal users see the note reporting-wise - the same scope their customer listing
/// uses. It is read-only for internal users: they view and export, they do not reply.</summary>
public sealed class DealerFeedback : BaseEntity
{
    /// <summary>The dealer's <c>customers.id</c> - what the reporting scope is resolved from.</summary>
    public ulong CustomerId { get; set; }

    /// <summary>The Distributor <c>users.id</c> that submitted it, kept for reference.</summary>
    public ulong? CreatedByUserId { get; set; }

    public string Category { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    /// <summary>Overall satisfaction, 1-5 stars. Defaults to 3 when none is sent.</summary>
    public int Rating { get; set; } = 3;
}
