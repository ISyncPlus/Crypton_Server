using Crypton.Core.Common;

namespace Crypton.Api.Contracts;

public sealed record PageDto<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);

public sealed record NotificationDto(Guid Id, string Type, string Title, string Body, string? Link, bool Read, DateTimeOffset CreatedAt);

public sealed record CountDto(int Count);

public static class PageMappings
{
    public static PageDto<TOut> Map<TIn, TOut>(this PagedResult<TIn> page, Func<TIn, TOut> map) =>
        new(page.Items.Select(map).ToList(), page.Page, page.PageSize, page.TotalCount, page.TotalPages);

    public static PageDto<T> ToPage<T>(this IReadOnlyList<T> items, PageRequest request, int total) =>
        new(items, request.SafePage, request.SafePageSize, total, request.SafePageSize == 0 ? 0 : (int)Math.Ceiling(total / (double)request.SafePageSize));
}
