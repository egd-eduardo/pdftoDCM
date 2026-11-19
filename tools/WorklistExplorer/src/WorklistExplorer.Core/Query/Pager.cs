namespace WorklistExplorer.Core.Query;

/// <summary>Page arithmetic, kept out of the window so it can be tested without one.</summary>
public readonly record struct PageWindow(int Page, int PageCount, int Skip, int Take, int Total)
{
    public static PageWindow For(int total, int pageSize, int requestedPage)
    {
        if (pageSize < 1) pageSize = 1;
        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var page = Math.Clamp(requestedPage, 1, pageCount);
        var skip = (page - 1) * pageSize;
        var take = Math.Max(0, Math.Min(pageSize, total - skip));
        return new PageWindow(page, pageCount, skip, take, total);
    }

    /// <summary>"Showing 501 to 1000 of 12 000", or a plain statement when there is nothing.</summary>
    public string Describe() => Total == 0
        ? "page 1 of 1, nothing to show"
        : $"page {Page} of {PageCount}, rows {Skip + 1:N0} to {Skip + Take:N0} of {Total:N0}";
}
