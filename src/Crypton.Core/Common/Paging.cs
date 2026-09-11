namespace Crypton.Core.Common;

public sealed record PageRequest(int Page = 1, int PageSize = 25)
{
    public int SafePage => Page < 1 ? 1 : Page;

    public int SafePageSize => PageSize switch
    {
        < 1 => 25,
        > 200 => 200,
        _ => PageSize,
    };

    public int Skip => (SafePage - 1) * SafePageSize;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
