namespace Application.Common;

public sealed record PagedResult<T>(
    IReadOnlyCollection<T> Items,
    long Total,
    int Page,
    int PageSize);

public static class Pagination
{
    public static int Page(int value) => Math.Max(1, value);
    public static int PageSize(int value) => Math.Clamp(value, 1, 200);
}
