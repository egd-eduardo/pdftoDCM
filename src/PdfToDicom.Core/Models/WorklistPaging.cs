namespace PdfToDicom.Core.Models;

/// <summary>
/// The arithmetic of showing a long worklist result one page at a time.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in the view model on purpose. The view models have no automated
/// coverage, because WPF will not run on a build agent, and version 3.3.0 shipped a dialog
/// that would not open at all for exactly that reason. Paging is arithmetic, it is where an
/// off by one hides a patient, and arithmetic can be tested on any machine. What is left in
/// the view model is the binding, which a test could not have checked anyway.
/// </para>
/// <para>
/// Everything here is a pure function of a count and a page size. Nothing holds entries,
/// so a page cannot disagree with the list it came from.
/// </para>
/// </remarks>
public static class WorklistPaging
{
    /// <summary>How many entries a page holds when nothing sensible was configured.</summary>
    public const int DefaultPageSize = 500;

    /// <summary>
    /// The page size to use, substituting the default for anything useless.
    /// </summary>
    /// <remarks>
    /// Zero or a negative number would mean no page can hold anything, which shows an empty
    /// window to somebody whose search found hundreds. A configuration file is editable by
    /// hand, so this is reachable without anybody intending it.
    /// </remarks>
    public static int SanePageSize(int pageSize) => pageSize > 0 ? pageSize : DefaultPageSize;

    /// <summary>
    /// How many pages a result of this size makes.
    /// </summary>
    /// <remarks>
    /// One when nothing was found, rather than zero: an empty list is one empty page, and
    /// "page 1 of 0" is not something anybody can read.
    /// </remarks>
    public static int PageCount(int totalCount, int pageSize)
    {
        int size = SanePageSize(pageSize);

        if (totalCount <= 0)
        {
            return 1;
        }

        return (totalCount + size - 1) / size;
    }

    /// <summary>
    /// The nearest page that exists to the one asked for, counted from zero.
    /// </summary>
    /// <remarks>
    /// Clamped rather than rejected. A page number can arrive from a button that stayed
    /// enabled a moment too long or from a second search that found fewer entries than the
    /// first, and a window showing nothing with no way back is a worse answer than the last
    /// page.
    /// </remarks>
    public static int ClampPageIndex(int pageIndex, int totalCount, int pageSize) =>
        Math.Clamp(pageIndex, 0, PageCount(totalCount, pageSize) - 1);

    /// <summary>
    /// Which entries one page covers, numbered from one for a person to read.
    /// </summary>
    /// <returns>
    /// The first and last entry numbers on the page, or (0, 0) when there is nothing to
    /// show. Both ends are inclusive.
    /// </returns>
    /// <remarks>
    /// The last number is the end of the result rather than the end of the page on the
    /// final page, so a result of 1001 entries ends with "showing 1001 to 1001 of 1001" and
    /// never "1001 to 1500".
    /// </remarks>
    public static (int First, int Last) PageWindow(int pageIndex, int totalCount, int pageSize)
    {
        if (totalCount <= 0)
        {
            return (0, 0);
        }

        int size = SanePageSize(pageSize);
        int index = ClampPageIndex(pageIndex, totalCount, size);
        int first = (index * size) + 1;

        return (first, Math.Min(first + size - 1, totalCount));
    }

    /// <summary>How many entries to skip to reach a page.</summary>
    public static int Skipped(int pageIndex, int totalCount, int pageSize)
    {
        int size = SanePageSize(pageSize);

        return ClampPageIndex(pageIndex, totalCount, size) * size;
    }
}
