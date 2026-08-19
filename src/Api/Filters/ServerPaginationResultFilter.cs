using System.Collections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Shared.Responses;

namespace Api.Filters;

public sealed class ServerPaginationResultFilter : IAsyncResultFilter
{
    // Matches the largest option offered by the web "Show N entries" selector.
    private const int MaxPageSize = 500;

    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (!HttpMethods.IsGet(context.HttpContext.Request.Method)
            || context.Result is not ObjectResult { Value: LaravelApiResponse response }
            || response.Extra.ContainsKey("total")
            || !TryPage(context.HttpContext.Request.Query, out var page, out var pageSize))
            return next();

        var entry = response.Extra.FirstOrDefault(pair => IsPageable(pair.Value));
        if (entry.Key is null || entry.Value is not IEnumerable source) return next();

        var values = source.Cast<object?>().ToList();
        response.Extra[entry.Key] = values.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        response.Extra["total"] = values.Count;
        response.Extra["page"] = page;
        response.Extra["page_size"] = pageSize;
        return next();
    }

    private static bool TryPage(IQueryCollection query, out int page, out int pageSize)
    {
        page = int.TryParse(query["page"], out var parsedPage) ? Math.Max(1, parsedPage) : 1;
        var rawSize = query["page_size"].FirstOrDefault() ?? query["pageSize"].FirstOrDefault();
        pageSize = int.TryParse(rawSize, out var parsedSize) ? Math.Clamp(parsedSize, 1, MaxPageSize) : 0;
        return pageSize > 0;
    }

    private static bool IsPageable(object? value) => value is IEnumerable and not string and not IDictionary;
}
