using System.Text.Json;
using Domain.Entities;

namespace Domain.Services;

/// <summary>
/// The audience a scheme is being matched against. Branch and zone come from the
/// customer's assigned employee, state comes from the customer's own address, and
/// the customer identity is used for customer-scoped schemes.
/// </summary>
public sealed record SchemeAudience(
    ulong? CustomerType,
    string? CustomerName,
    string? CustomerCode,
    string? BranchName,
    string? ZoneName,
    string? StateName,
    /// <summary>The dealer this audience belongs to: a dealer's own id, or the dealer a
    /// retailer is mapped to. A scheme that excludes that dealer does not apply to either
    /// of them - see SchemeEligibility.Matches.</summary>
    ulong? DealerId);

/// <summary>
/// Single source of truth for deciding whether a loyalty scheme applies. Every
/// place that offers or applies a scheme must go through this type, otherwise the
/// same scheme is eligible on one screen and invisible on another.
/// </summary>
public static class SchemeEligibility
{
    public const ulong DealerCustomerType = 1;
    public const ulong RetailerCustomerType = 2;
    public const ulong InfluencerCustomerType = 3;

    private const StringComparison Ignore = StringComparison.OrdinalIgnoreCase;

    /// <summary>Scheme is published, active, undeleted and covers <paramref name="date"/>.</summary>
    public static bool IsLiveOn(LoyaltyScheme scheme, DateOnly date) =>
        scheme.DeletedAt is null
        && string.Equals(scheme.Active, "Y", Ignore)
        && (string.Equals(scheme.Status, "Published", Ignore) || string.Equals(scheme.Status, "Live", Ignore))
        && string.Equals(scheme.SchemeType, "Invoice", Ignore)
        && scheme.StartDate <= date
        && scheme.EndDate >= date;

    /// <summary>Full check: period, customer type, area scope, and the excluded dealers.</summary>
    public static bool Matches(LoyaltyScheme scheme, DateOnly date, SchemeAudience audience) =>
        date >= scheme.StartDate
        && date <= scheme.EndDate
        && !IsExcludedDealer(scheme, audience)
        && CustomerTypeMatches(scheme.CustomerType, audience.CustomerType)
        && AreaMatches(scheme, audience);

    /// <summary>
    /// A scheme can name dealers it deliberately leaves out. It then applies neither to
    /// that dealer nor to the retailers mapped to it - so it is not offered on any invoice
    /// form, does not appear on any screen either of them can see, and earns nothing.
    ///
    /// The check sits here rather than at each screen because "earns nothing" and "is not
    /// shown" have to be the same decision: a scheme still quietly paying points while
    /// being invisible is worse than either.
    /// </summary>
    public static bool IsExcludedDealer(LoyaltyScheme scheme, SchemeAudience audience)
    {
        if (!audience.DealerId.HasValue) return false;
        var excluded = ReadExcludedDealerIds(scheme.ExcludedDealerIds);
        return excluded.Count > 0 && excluded.Contains(audience.DealerId.Value);
    }

    public static IReadOnlyCollection<ulong> ReadExcludedDealerIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<ulong[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The dealer an audience belongs to. A dealer is its own; a retailer takes the dealer
    /// it is mapped to, read from the same custom fields the rest of the system uses for
    /// that link. Live data records it under two different keys.
    /// </summary>
    public static ulong? ReadDealerId(Customer customer)
    {
        if (customer.CustomerType == DealerCustomerType) return customer.Id;

        return ReadCustomFieldULong(customer, "distributor_name")
            ?? ReadCustomFieldULong(customer, "agri_distributor")
            ?? ReadCustomFieldULong(customer, "dealer_name");
    }

    /// <summary>
    /// Compares the scheme's target audience against the customer's own type.
    /// Legacy Laravel labels such as "Influencers" and "Retailer + Plumber" are still
    /// present in live data, so matching is done on tokens rather than exact equality.
    /// Influencer aliases are tested first because "Sub-Dealer" also contains "Dealer".
    /// </summary>
    public static bool CustomerTypeMatches(string? schemeCustomerType, ulong? customerType)
    {
        var target = schemeCustomerType?.Trim();
        if (string.IsNullOrWhiteSpace(target) || !customerType.HasValue) return false;

        var isInfluencerTarget = Has(target, "Influencer") || Has(target, "Plumber")
            || Has(target, "Sub-Dealer") || Has(target, "Sub Dealer");

        return customerType.Value switch
        {
            InfluencerCustomerType => isInfluencerTarget,
            RetailerCustomerType => Has(target, "Retailer"),
            DealerCustomerType => !isInfluencerTarget && (Has(target, "Dealer") || Has(target, "Distributor")),
            _ => false
        };
    }

    /// <summary>
    /// Area scope check. An unrecognised scope is treated as unrestricted so that a
    /// future scope value never silently hides every scheme; scopes listed here are
    /// enforced strictly, and a scope with no selected values means "not restricted".
    /// </summary>
    public static bool AreaMatches(LoyaltyScheme scheme, SchemeAudience audience)
    {
        var scope = scheme.AreaScope?.Trim();
        if (string.IsNullOrWhiteSpace(scope) || string.Equals(scope, "All", Ignore)) return true;

        var values = ReadAreaValues(scheme.AreaValues);
        if (values.Count == 0) return true;

        return scope switch
        {
            "Customer" => MatchesAny(values, audience.CustomerName)
                || MatchesAny(values, audience.CustomerCode)
                || MatchesAny(values, CustomerLabel(audience)),
            "Branch" => MatchesAny(values, audience.BranchName),
            "Zone" => MatchesAny(values, audience.ZoneName),
            "State" => MatchesAny(values, audience.StateName),
            _ => true
        };
    }

    /// <summary>Reads the customer's state id from the legacy custom_fields JSON.</summary>
    public static ulong? ReadStateId(Customer customer) =>
        ReadCustomFieldULong(customer, "state_id") ?? ReadCustomFieldULong(customer, "billing_state");

    public static IReadOnlyCollection<string> ReadAreaValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? CustomerLabel(SchemeAudience audience) =>
        string.IsNullOrWhiteSpace(audience.CustomerCode)
            ? audience.CustomerName
            : $"{audience.CustomerCode} - {audience.CustomerName}";

    private static bool MatchesAny(IReadOnlyCollection<string> values, string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate)
        && values.Any(value => string.Equals(value?.Trim(), candidate.Trim(), Ignore));

    private static bool Has(string value, string token) => value.Contains(token, Ignore);

    private static ulong? ReadCustomFieldULong(Customer customer, string key)
    {
        if (string.IsNullOrWhiteSpace(customer.CustomFields)) return null;
        try
        {
            using var document = JsonDocument.Parse(customer.CustomFields);
            if (!document.RootElement.TryGetProperty(key, out var value)) return null;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            return ulong.TryParse(text, out var parsed) && parsed > 0 ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
