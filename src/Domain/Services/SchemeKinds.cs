namespace Domain.Services;

/// <summary>
/// What a scheme is read on.
///
/// Invoice weighs the whole bill in rupees and pays by slab. Product and Quantity (shown as
/// "Quantity Slab") are read on the goods instead: the scheme names segments, families and
/// products. Product pays a flat rate on each line's value. Quantity Slab pays a per-unit rate
/// that changes with how many of the product were bought in the scheme period - so it carries
/// BOTH the product lines (which goods it covers) AND slabs (the quantity bands and their
/// per-unit reward). Invoice carries slabs only; Product carries lines only.
/// </summary>
public static class SchemeKinds
{
    public const string Invoice = "Invoice";
    public const string Product = "Product";
    public const string Quantity = "Quantity";

    /// <summary>A blank type is an Invoice scheme: that is what every scheme was before
    /// the other two existed, and what the column defaults to.</summary>
    public static bool IsInvoice(string? type) => string.IsNullOrWhiteSpace(type) || Is(type, Invoice);
    public static bool IsProduct(string? type) => Is(type, Product);
    public static bool IsQuantity(string? type) => Is(type, Quantity);

    /// <summary>Product and Quantity Slab - the two read on the goods, so they carry product
    /// lines that name the covered goods.</summary>
    public static bool ReadsProducts(string? type) => IsProduct(type) || IsQuantity(type);

    /// <summary>Invoice and Quantity Slab both carry slabs: Invoice as rupee bands on the bill,
    /// Quantity Slab as quantity bands whose reward is per unit.</summary>
    public static bool ReadsSlabs(string? type) => IsInvoice(type) || IsQuantity(type);

    private static bool Is(string? value, string name) =>
        string.Equals(value?.Trim(), name, StringComparison.OrdinalIgnoreCase);
}
