using PdfToDicom.Core.Abstractions;
using PdfToDicom.Core.Models;
using PDFtoImage;
using SkiaSharp;

namespace PdfToDicom.Core.Imaging;

/// <summary>
/// Renders PDF pages with PDFium, the PDF engine used by Chrome.
/// </summary>
/// <remarks>
/// <para>
/// PDFium is Apache-2.0 licensed and ships prebuilt for win-x86, win-x64 and win-arm64,
/// which covers every architecture this software targets. It is reached through
/// PDFtoImage, which is MIT.
/// </para>
/// <para>
/// This is the first native dependency in the product, and it is not taken lightly. The
/// alternatives were Ghostscript and MuPDF, both AGPL, which would place conditions on
/// every hospital receiving the software. There is no pure managed PDF rasterizer of
/// production quality under a permissive licence.
/// </para>
/// <para>
/// Every failure path here is non fatal. If the native library will not load, or a
/// document will not render, the encapsulated PDF is still produced and sent. Losing the
/// images degrades the result; losing the report would be a failure.
/// </para>
/// </remarks>
public sealed class PdfPageRenderer : IPdfPageRenderer
{
    // ------------------------------------------------------------------ CA1416
    //
    // PDFtoImage declares its entry points as supported on Android 31+, Linux, macOS and
    // Windows. This assembly targets plain net8.0, which also admits browser, iOS and
    // tvOS, so the analyzer correctly observes that these call sites are "reachable on all
    // platforms" and cannot prove they are not reached on one of those three.
    //
    // They are not. The product is a Windows desktop application; the only other platforms
    // this assembly ever runs on are Linux and macOS, where the build, the tests and the
    // DICOM interoperability check execute, and the native library supports both.
    //
    // More to the point, the concern the analyzer is raising is already handled at run
    // time and better than an attribute could: ProbeNativeLibrary below loads the library
    // and asks it for real work before anything depends on it, IsAvailable reports the
    // answer, and Render returns an empty list with an explanation when it is false. A
    // platform where the library cannot load produces a document without page images
    // rather than a crash.
    //
    // Narrowing the class with [SupportedOSPlatform] for each of the three platforms was
    // the alternative, and it was rejected: the attribute propagates to every caller, so
    // the composition root and the workflow service would each need the same three
    // attributes to stay quiet, which is a lot of ceremony to express something the
    // runtime probe already enforces.
    #pragma warning disable CA1416

    private readonly IAppLogger _logger;
    private readonly Lazy<bool> _available;

    /// <summary>Creates the renderer.</summary>
    public PdfPageRenderer(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Probed once, lazily, because loading the native library costs time and a
        // conversion that never renders should not pay it.
        _available = new Lazy<bool>(ProbeNativeLibrary);
    }

    /// <inheritdoc />
    public bool IsAvailable => _available.Value;

    /// <summary>
    /// The lowest resolution worth producing.
    /// </summary>
    /// <remarks>
    /// 72 DPI renders an A4 page at 595 by 842, which is legible on screen and is what a
    /// PDF point maps to one for one. Below that the images would be of no clinical use,
    /// and sending something unreadable is worse than sending nothing: the encapsulated
    /// document is always there to open.
    /// </remarks>
    private const int MinimumDotsPerInch = 72;

    /// <inheritdoc />
    public IReadOnlyList<RenderedPdfPage> Render(string pdfPath, int dotsPerInch, int budgetMegabytes = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);

        if (!IsAvailable)
        {
            _logger.Warning(
                "The PDF renderer is not available on this machine, so no page images were produced. " +
                "The encapsulated PDF is unaffected.");
            return Array.Empty<RenderedPdfPage>();
        }

        try
        {
            byte[] document = File.ReadAllBytes(pdfPath);
            int pageCount = Conversion.GetPageCount(document);

            if (pageCount <= 0)
            {
                _logger.Warning($"'{Path.GetFileName(pdfPath)}' reports no pages, so no page images were produced.");
                return Array.Empty<RenderedPdfPage>();
            }

            var pages = new List<RenderedPdfPage>(pageCount);

            // The first page is rendered at the requested resolution and then measured,
            // because what a page actually costs depends on whether it came out colour,
            // and that is decided from its own pixels. A document that will not fit the
            // budget is rendered again, whole, at a lower resolution.
            RenderedPdfPage first = RenderOne(document, 0, dotsPerInch);
            int effective = ResolveDotsPerInch(
                dotsPerInch, budgetMegabytes, first.Pixels.LongLength, pageCount, Path.GetFileName(pdfPath));

            if (effective == 0)
            {
                return Array.Empty<RenderedPdfPage>();
            }

            if (effective != dotsPerInch)
            {
                // Rendered at the wrong resolution, so it is thrown away rather than left
                // as a page that does not match the rest of the document.
                first = RenderOne(document, 0, effective);
                dotsPerInch = effective;
            }

            pages.Add(first);

            for (int index = 1; index < pageCount; index++)
            {
                pages.Add(RenderOne(document, index, dotsPerInch));
            }

            return pages;
        }
        catch (Exception ex)
        {
            // Deliberately broad. A malformed PDF can fail inside native code in ways that
            // are not worth enumerating, and no failure here may stop the report being
            // sent. An empty list is the signal that rendering did not happen.
            _logger.Warning(
                $"'{Path.GetFileName(pdfPath)}' could not be rendered to images: {ex.Message}. " +
                "The encapsulated PDF is unaffected.");

            return Array.Empty<RenderedPdfPage>();
        }
    }

    /// <summary>Renders a single page and releases the rasterizer's bitmap at once.</summary>
    /// <remarks>
    /// The bitmap's scope is tight on purpose. A rendered A4 page at 150 DPI is about 8 MB
    /// of unmanaged pixels and this runs once per page: holding one page's bitmap while
    /// the next is allocated would double the high water mark for no benefit.
    /// </remarks>
    private static RenderedPdfPage RenderOne(byte[] document, int index, int dotsPerInch)
    {
        using SKBitmap bitmap = Conversion.ToImage(
            document,
            page: new Index(index),
            options: new RenderOptions(Dpi: dotsPerInch));

        return ToRenderedPage(bitmap, index + 1, dotsPerInch);
    }

    /// <summary>
    /// Decides what resolution the document can actually be rendered at.
    /// </summary>
    /// <returns>
    /// The requested resolution when it fits, a lower one when it does not, or zero when
    /// even the lowest useful resolution would not fit.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Every page is held until all the objects have been validated, which is deliberate:
    /// nothing is transmitted until all of it passes, because half a document in an
    /// archive is worse than none. The consequence is that a long report's page images are
    /// all in memory at once, and the product ships a 32 bit build.
    /// </para>
    /// <para>
    /// Resolution is halved in area terms rather than pages being dropped. A reader who is
    /// shown twelve pages of a twenty page report has no way of knowing that eight are
    /// missing; a reader shown twenty slightly softer pages has the whole report. The
    /// encapsulated document, which is the fidelity copy, is unaffected either way.
    /// </para>
    /// <para>
    /// The estimate takes the first page as representative. A document whose pages differ
    /// in size or colour is estimated imperfectly, and the first page of a report is the
    /// one with the letterhead on it, so the error is normally on the safe side.
    /// </para>
    /// </remarks>
    private int ResolveDotsPerInch(
        int requested, int budgetMegabytes, long firstPageBytes, int pageCount, string fileName)
    {
        if (budgetMegabytes <= 0 || firstPageBytes <= 0)
        {
            return requested;
        }

        long budget = (long)budgetMegabytes * 1024 * 1024;
        long estimate = firstPageBytes * pageCount;

        if (estimate <= budget)
        {
            return requested;
        }

        // Pixel count rises with the square of the resolution, so the resolution that fits
        // falls with its square root.
        int scaled = (int)Math.Floor(requested * Math.Sqrt((double)budget / estimate));

        if (scaled < MinimumDotsPerInch)
        {
            _logger.Warning(
                $"'{fileName}' has {pageCount} page(s), which would need about " +
                $"{estimate / (1024 * 1024)} MB of pixel data at {requested} DPI and still more than the " +
                $"{budgetMegabytes} MB allowed at {MinimumDotsPerInch} DPI, so no page images were produced. " +
                "The encapsulated PDF is complete and is sent as usual. Raise the pixel data allowance in " +
                "Settings to render it anyway.");

            return 0;
        }

        _logger.Warning(
            $"'{fileName}' has {pageCount} page(s), which would need about " +
            $"{estimate / (1024 * 1024)} MB of pixel data at {requested} DPI, more than the " +
            $"{budgetMegabytes} MB allowed. Every page is rendered, at {scaled} DPI instead. " +
            "The encapsulated PDF is unaffected.");

        return scaled;
    }

    /// <summary>
    /// Converts one rendered bitmap into the sample layout DICOM wants, deciding colour
    /// from the pixels themselves.
    /// </summary>
    /// <remarks>
    /// A rendered report page is almost always black text on white. Storing that as RGB
    /// would triple the size of every object for no gain at all, so the pixels are
    /// inspected and a page whose three channels are equal everywhere is written as
    /// greyscale. Being wrong in that direction is impossible: equal channels means there
    /// is no colour to discard.
    /// </remarks>
    private static RenderedPdfPage ToRenderedPage(SKBitmap bitmap, int pageNumber, int dotsPerInch)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        // Four byte layouts are read where they lie. Both orders put the three colour
        // channels first and the alpha last, so the only difference is whether red is at
        // offset 0 or offset 2, which one integer expresses. Converting them to a single
        // layout first, which is what this did before, allocated and copied another whole
        // page per page: 8 MB each at 150 DPI, for a document that is about to be read
        // one pixel at a time anyway.
        SKColorType layout = bitmap.ColorType;
        bool readable = layout is SKColorType.Rgba8888 or SKColorType.Bgra8888;

        // Only an unexpected layout pays for a conversion. Nothing is disposed when the
        // bitmap was usable as it stood, which is why this is not a using statement.
        SKBitmap? converted = readable ? null : Normalise(bitmap);
        SKBitmap source = converted ?? bitmap;

        try
        {
            return ToRenderedPage(source, pageNumber, width, height, source.ColorType, dotsPerInch);
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>
    /// Reads a four byte per pixel page into the sample layout DICOM wants.
    /// </summary>
    /// <remarks>
    /// Row by row rather than over one span of the whole image. A bitmap's stride is
    /// allowed to exceed its width, and an image read as though it were contiguous when it
    /// is not comes out sheared, which is the kind of defect that looks like a rendering
    /// bug for weeks.
    /// </remarks>
    private static RenderedPdfPage ToRenderedPage(
        SKBitmap bitmap, int pageNumber, int width, int height, SKColorType layout, int dotsPerInch)
    {
        ReadOnlySpan<byte> source = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;

        // Blue and red swap places between the two layouts; green and alpha do not move.
        int redOffset = layout == SKColorType.Bgra8888 ? 2 : 0;
        int blueOffset = layout == SKColorType.Bgra8888 ? 0 : 2;

        bool isColour = CarriesColour(source, width, height, stride);

        byte[] pixels = isColour
            ? new byte[(long)width * height * 3]
            : new byte[(long)width * height];

        for (int row = 0; row < height; row++)
        {
            ReadOnlySpan<byte> line = source.Slice(row * stride, width * 4);
            int target = row * width;

            for (int column = 0; column < width; column++)
            {
                int offset = column * 4;

                if (isColour)
                {
                    int at = (target + column) * 3;
                    pixels[at] = line[offset + redOffset];
                    pixels[at + 1] = line[offset + 1];
                    pixels[at + 2] = line[offset + blueOffset];
                }
                else
                {
                    // The channels are equal here by definition, so any one of them is the
                    // luminance and no weighted conversion is needed or wanted. Which also
                    // means the byte order does not matter on this path.
                    pixels[target + column] = line[offset];
                }
            }
        }

        return new RenderedPdfPage(pageNumber, width, height, isColour, pixels, dotsPerInch);
    }

    /// <summary>Returns the bitmap as straight RGBA8888, copying only when necessary.</summary>
    private static SKBitmap Normalise(SKBitmap bitmap)
    {
        var wanted = new SKImageInfo(
            bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        var copy = new SKBitmap(wanted);

        if (!bitmap.CopyTo(copy, SKColorType.Rgba8888))
        {
            copy.Dispose();
            throw new InvalidOperationException("The rendered page could not be converted to a known pixel layout.");
        }

        return copy;
    }

    /// <summary>
    /// True when any pixel has channels that are not all equal.
    /// </summary>
    /// <remarks>
    /// Every pixel is examined rather than a sample. A single coloured stamp, signature or
    /// highlighted line on an otherwise black and white page is exactly the content a
    /// sampling stride would miss, and discarding it would be silent.
    /// </remarks>
    private static bool CarriesColour(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        for (int row = 0; row < height; row++)
        {
            ReadOnlySpan<byte> line = pixels.Slice(row * stride, width * 4);

            for (int column = 0; column < width; column++)
            {
                int offset = column * 4;

                if (line[offset] != line[offset + 1] || line[offset + 1] != line[offset + 2])
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Confirms the native library loads, by asking it to do the smallest real piece of
    /// work there is.
    /// </summary>
    private bool ProbeNativeLibrary()
    {
        try
        {
            // A minimal but valid one page PDF. Counting its pages exercises the native
            // load path without touching the file system.
            Conversion.GetPageCount(MinimalPdf);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                $"The PDF rendering library could not be loaded on this machine: {ex.Message}. " +
                "Documents will still be converted and sent; only the page images are unavailable.");

            return false;
        }
    }

    /// <summary>The smallest structurally valid PDF, used only to prove the renderer runs.</summary>
    private static readonly byte[] MinimalPdf = System.Text.Encoding.ASCII.GetBytes(
        "%PDF-1.4\n" +
        "1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n" +
        "2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
        "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 72 72]>>endobj\n" +
        "trailer<</Root 1 0 R>>\n");

    #pragma warning restore CA1416
}
