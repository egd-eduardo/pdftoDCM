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

    /// <inheritdoc />
    public IReadOnlyList<RenderedPdfPage> Render(string pdfPath, int dotsPerInch)
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

            for (int index = 0; index < pageCount; index++)
            {
                using SKBitmap bitmap = Conversion.ToImage(
                    document,
                    page: new Index(index),
                    options: new RenderOptions(Dpi: dotsPerInch));

                pages.Add(ToRenderedPage(bitmap, index + 1));
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
    private static RenderedPdfPage ToRenderedPage(SKBitmap bitmap, int pageNumber)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        // Normalised so the pixel layout is known regardless of what the renderer chose.
        using SKBitmap rgba = Normalise(bitmap);
        ReadOnlySpan<byte> source = rgba.GetPixelSpan();

        bool isColour = CarriesColour(source, width, height);

        byte[] pixels = isColour
            ? new byte[(long)width * height * 3]
            : new byte[(long)width * height];

        for (int pixel = 0; pixel < width * height; pixel++)
        {
            int offset = pixel * 4;
            byte r = source[offset];
            byte g = source[offset + 1];
            byte b = source[offset + 2];

            if (isColour)
            {
                pixels[pixel * 3] = r;
                pixels[(pixel * 3) + 1] = g;
                pixels[(pixel * 3) + 2] = b;
            }
            else
            {
                // The channels are equal here by definition, so any one of them is the
                // luminance and no weighted conversion is needed or wanted.
                pixels[pixel] = r;
            }
        }

        return new RenderedPdfPage(pageNumber, width, height, isColour, pixels);
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
    private static bool CarriesColour(ReadOnlySpan<byte> rgba, int width, int height)
    {
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            int offset = pixel * 4;

            if (rgba[offset] != rgba[offset + 1] || rgba[offset + 1] != rgba[offset + 2])
            {
                return true;
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
