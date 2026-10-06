namespace Domain.Services;

/// <summary>
/// What a scheme is read on.
///
/// Invoice weighs the whole bill in rupees and pays by slab. Product and Quantity are read
/// on the goods instead: the scheme names segments, families and products, and each line
/// says what those goods earn - Product on their value, Quantity on how many were bought.
/// Those two carry no slabs at all.
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

    /// <summary>Product and Quantity - the two read on the goods, so they carry lines
    /// rather than slabs.</summary>
    public static bool ReadsProducts(string? type) => IsProduct(type) || IsQuantity(type);

    private static bool Is(string? value, string name) =>
        string.Equals(value?.Trim(), name, StringComparison.OrdinalIgnoreCase);
}
