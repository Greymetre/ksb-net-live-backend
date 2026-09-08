using Domain.Entities;

namespace Domain.Services;

/// <summary>
/// How a slab pays: a flat amount, or a percentage of the invoice.
///
/// Most schemes are one or the other for every slab, and the scheme's own BasedOn
/// says which. A mixed scheme is the exception a real KSB scheme needed: thirteen
/// slabs paying a fixed gift, and a final open-ended slab paying 2.8% of whatever
/// the retailer actually bills, because 1,40,000 is only the value that rate reaches
/// at the 50 lakh floor. In that case each slab carries its own type.
///
/// Every reward figure, and every label showing one, resolves through here. The
/// percentage rule used to be written out at each call site, which is how the same
/// slab can start meaning one thing on a screen and another in a total.
/// </summary>
public static class SchemeReward
{
    public const string Value = "Value";
    public const string Percentage = "Percentage";

    /// <summary>The scheme-level choice that hands the decision to each slab.</summary>
    public const string Mixed = "Value + Percentage";

    public static bool IsMixedScheme(string? basedOn) =>
        string.Equals(basedOn?.Trim(), Mixed, StringComparison.OrdinalIgnoreCase);

    public static bool IsPercentage(string? rewardType) =>
        string.Equals(rewardType?.Trim(), Percentage, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The type that actually applies to one slab. A mixed scheme reads the slab's own
    /// type and treats a blank as a flat amount; any other scheme ignores the slab and
    /// follows the scheme, so a stray slab type can never change an existing scheme.
    /// </summary>
    public static string TypeFor(LoyaltyScheme scheme, LoyaltySchemeSlab slab)
    {
        if (!IsMixedScheme(scheme.BasedOn)) return IsPercentage(scheme.BasedOn) ? Percentage : Value;
        return IsPercentage(slab.RewardType) ? Percentage : Value;
    }

    /// <summary>
    /// What one invoice earns under the slab the period's business reached.
    ///
    /// A percentage slab pays on the invoice itself, so every invoice earns its own
    /// share and the total grows as more are uploaded. A flat slab is the opposite:
    /// the figure in the table is what the retailer earns for the period, once. It was
    /// being paid on every invoice, so two invoices reaching the 6 lakh slab returned
    /// 10,800 each and the retailer appeared to have earned 21,600. Each invoice now
    /// carries the share of that one reward its own value represents, and the invoices
    /// add back up to the slab amount.
    /// </summary>
    public static decimal PointsFor(decimal invoiceAmount, decimal periodAmount, LoyaltyScheme scheme, LoyaltySchemeSlab slab)
    {
        if (IsPercentage(TypeFor(scheme, slab)))
        {
            return Math.Round(invoiceAmount * slab.RewardValue / 100, 2);
        }

        // Before any business is recorded there is nothing to divide, and one invoice
        // holding the whole period simply earns the whole reward.
        if (periodAmount <= 0) return 0;
        if (invoiceAmount >= periodAmount) return slab.RewardValue;

        return Math.Round(slab.RewardValue * invoiceAmount / periodAmount, 2);
    }

    /// <summary>"2.8%" or "Rs. 140000", whichever this slab pays.</summary>
    public static string Label(LoyaltyScheme scheme, LoyaltySchemeSlab slab) =>
        Format(slab.RewardValue, TypeFor(scheme, slab));

    public static string Format(decimal rewardValue, string? rewardType) =>
        IsPercentage(rewardType) ? $"{rewardValue:0.##}%" : $"Rs. {rewardValue:0.##}";
}
