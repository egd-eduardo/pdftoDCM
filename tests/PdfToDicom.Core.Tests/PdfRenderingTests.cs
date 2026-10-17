using PdfToDicom.Core.Imaging;
using PdfToDicom.Core.Models;
using Xunit;

namespace PdfToDicom.Core.Tests;

/// <summary>
/// Covers rendering a PDF to page images, which is what lets a PACS viewer that will only
/// open a study containing an image still show the report.
/// </summary>
public sealed class PdfRenderingTests
{
    private readonly RecordingLogger _logger = new();
    private readonly PdfPageRenderer _renderer;

    public PdfRenderingTests() => _renderer = new PdfPageRenderer(_logger);

    [Fact]
    public void TheRendererLoadsOnThisMachine()
    {
        // Guards the native dependency itself. If PDFium cannot load, everything below
        // degrades silently to an empty list, so this asserts the capability separately
        // rather than letting the other tests pass vacuously.
        Assert.True(_renderer.IsAvailable, "The PDF rendering library did not load.");
    }

    [Fact]
    public void EveryPageOfTheDocumentIsRendered()
    {
        IReadOnlyList<RenderedPdfPage> pages = _renderer.Render(TestSupport.SamplePdfPath, 150);

        // The sample report is two pages. Rendering only the first would leave a reader
        // who opened the study in the image viewer believing they had seen all of it.
        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { 1, 2 }, pages.Select(p => p.PageNumber));

        foreach (RenderedPdfPage page in pages)
        {
            Assert.True(page.Width > 0 && page.Height > 0);

            long expected = (long)page.Width * page.Height * (page.IsColour ? 3 : 1);
            Assert.Equal(expected, page.Pixels.LongLength);
        }
    }

    [Fact]
    public void APageOfBlackTextOnWhiteIsStoredAsGreyscale()
    {
        // The sample report is black text on white. Storing it as RGB would triple every
        // object for no gain, so this asserts the saving is actually taken.
        RenderedPdfPage page = _renderer.Render(TestSupport.SamplePdfPath, 150)[0];

        Assert.False(page.IsColour);
        Assert.Equal((long)page.Width * page.Height, page.Pixels.LongLength);
    }

    [Fact]
    public void TheRenderedPageIsNotBlank()
    {
        // A renderer that silently produced white pages would satisfy every other
        // assertion here and be worse than useless: the viewer would launch and show
        // nothing. Ink on the page is therefore checked directly.
        RenderedPdfPage page = _renderer.Render(TestSupport.SamplePdfPath, 150)[0];

        int dark = page.Pixels.Count(sample => sample < 128);

        Assert.True(dark > 1000, $"Only {dark} dark samples were found, so the page appears blank.");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(200)]
    public void HigherResolutionProducesProportionallyLargerPages(int dpi)
    {
        RenderedPdfPage page = _renderer.Render(TestSupport.SamplePdfPath, dpi)[0];

        // A4 is 8.27 inches wide, so the width should track the requested resolution to
        // within a pixel of rounding.
        int expectedWidth = (int)Math.Round(8.27 * dpi);

        Assert.InRange(page.Width, expectedWidth - 3, expectedWidth + 3);
    }

    [Fact]
    public void SomethingThatIsNotAPdfRendersNothingRatherThanThrowing()
    {
        // Rendering is an enhancement. A failure must never stop the encapsulated PDF
        // being produced, so the contract is an empty list rather than an exception.
        string path = Path.Combine(Path.GetTempPath(), $"not-a-pdf-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, "This is not a PDF.");

        try
        {
            Assert.Empty(_renderer.Render(path, 150));
            Assert.Contains(_logger.Messages, m => m.StartsWith("WARN", StringComparison.Ordinal) && m.Contains("could not be rendered", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFileRendersNothingRatherThanThrowing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.pdf");

        Assert.Empty(_renderer.Render(path, 150));
    }

    // ------------------------------------------------- The pixel data ceiling

    [Fact]
    public void AnOrdinaryDocumentIsNotTouchedByTheCeiling()
    {
        IReadOnlyList<RenderedPdfPage> unlimited = _renderer.Render(TestSupport.SamplePdfPath, 150);
        IReadOnlyList<RenderedPdfPage> limited = _renderer.Render(TestSupport.SamplePdfPath, 150, 512);

        Assert.Equal(unlimited.Count, limited.Count);
        Assert.Equal(unlimited[0].Width, limited[0].Width);
        Assert.Equal(unlimited[0].Pixels.LongLength, limited[0].Pixels.LongLength);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("more than the", StringComparison.Ordinal));
    }

    [Fact]
    public void ADocumentOverTheCeilingIsRenderedSmallerRatherThanInPart()
    {
        // The whole report or none of it. A reader shown some of the pages has no way of
        // knowing that the rest are missing, so the resolution gives way instead.
        IReadOnlyList<RenderedPdfPage> full = _renderer.Render(TestSupport.SamplePdfPath, 300);
        IReadOnlyList<RenderedPdfPage> squeezed = _renderer.Render(TestSupport.SamplePdfPath, 300, 1);

        Assert.Equal(full.Count, squeezed.Count);
        Assert.True(squeezed[0].Width < full[0].Width,
            $"The pages should have been rendered smaller but came out {squeezed[0].Width} wide against {full[0].Width}.");
        Assert.All(squeezed, page => Assert.Equal(squeezed[0].Width, page.Width));
        Assert.Contains(_logger.Messages, m =>
            m.StartsWith("WARN", StringComparison.Ordinal) &&
            m.Contains("Every page is rendered", StringComparison.Ordinal));
    }

    [Fact]
    public void ACeilingTooSmallForAnyReadableImageProducesNoneAndSaysSo()
    {
        // A page of 2000 by 2000 points occupies about 4 MB at the 72 DPI floor, so one
        // megabyte cannot hold it however far the resolution is lowered. Nothing is
        // rendered rather than something nobody could read: the encapsulated document is
        // always there to open.
        //
        // The resolution that fits a byte budget depends on the page area and the budget,
        // not on what was asked for, which is why this needs a large page rather than a
        // large DPI.
        string path = Path.Combine(Path.GetTempPath(), $"huge-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, OneVeryLargePage());

        try
        {
            Assert.NotEmpty(_renderer.Render(path, 150));
            Assert.Empty(_renderer.Render(path, 150, 1));

            Assert.Contains(_logger.Messages, m =>
                m.StartsWith("WARN", StringComparison.Ordinal) &&
                m.Contains("no page images were produced", StringComparison.Ordinal) &&
                m.Contains("encapsulated PDF is complete", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A one page PDF whose page is far larger than any real report.</summary>
    private static byte[] OneVeryLargePage()
    {
        const string content = "0.5 g 0 0 2000 2000 re f\n";

        string pdf =
            "%PDF-1.4\n" +
            "1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n" +
            "2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
            "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 2000 2000]/Contents 4 0 R>>endobj\n" +
            $"4 0 obj<</Length {System.Text.Encoding.ASCII.GetByteCount(content)}>>stream\n" +
            content +
            "endstream endobj\n" +
            "trailer<</Root 1 0 R>>\n";

        return System.Text.Encoding.ASCII.GetBytes(pdf);
    }

    [Fact]
    public void ZeroMeansNoCeiling()
    {
        IReadOnlyList<RenderedPdfPage> unlimited = _renderer.Render(TestSupport.SamplePdfPath, 300, 0);
        IReadOnlyList<RenderedPdfPage> same = _renderer.Render(TestSupport.SamplePdfPath, 300);

        Assert.Equal(same[0].Width, unlimited[0].Width);
        Assert.DoesNotContain(_logger.Messages, m => m.Contains("more than the", StringComparison.Ordinal));
    }

    [Fact]
    public void RedStaysRedAndBlueStaysBlue()
    {
        // The renderer reads the rasterizer's own pixel layout rather than converting
        // every page to one layout first, which saves a whole extra page of memory per
        // page. The cost of that is caring which end of each pixel red lives at, and a
        // mistake there is silent: the page still renders, the sizes still match, and a
        // stamp that was red arrives blue. This is the test that would catch it.
        string path = Path.Combine(Path.GetTempPath(), $"colour-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, TwoColouredHalves());

        try
        {
            RenderedPdfPage page = Assert.Single(_renderer.Render(path, 72));

            Assert.True(page.IsColour, "A page of solid red and solid blue was stored as greyscale.");

            // PDF coordinates start at the bottom, so the rectangle drawn from y=50 up is
            // the top half of the rendered image.
            (byte topRed, byte topGreen, byte topBlue) = SampleAt(page, page.Width / 2, page.Height / 4);
            (byte lowRed, byte lowGreen, byte lowBlue) = SampleAt(page, page.Width / 2, page.Height * 3 / 4);

            Assert.True(topRed > 200 && topGreen < 60 && topBlue < 60,
                $"The top half should be red but came out R{topRed} G{topGreen} B{topBlue}.");
            Assert.True(lowBlue > 200 && lowGreen < 60 && lowRed < 60,
                $"The bottom half should be blue but came out R{lowRed} G{lowGreen} B{lowBlue}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Reads one pixel out of the interleaved RGB buffer.</summary>
    private static (byte Red, byte Green, byte Blue) SampleAt(RenderedPdfPage page, int x, int y)
    {
        int offset = ((y * page.Width) + x) * 3;
        return (page.Pixels[offset], page.Pixels[offset + 1], page.Pixels[offset + 2]);
    }

    /// <summary>
    /// A one page PDF, red over blue, written by hand.
    /// </summary>
    /// <remarks>
    /// By hand because the alternative is committing a binary fixture for two rectangles,
    /// and because a reader can see from this exactly which half is meant to be which
    /// colour. No cross reference table: the rasterizer rebuilds one, as it does for the
    /// renderer's own availability probe.
    /// </remarks>
    private static byte[] TwoColouredHalves()
    {
        const string content = "1 0 0 rg 0 50 100 50 re f\n0 0 1 rg 0 0 100 50 re f\n";

        string pdf =
            "%PDF-1.4\n" +
            "1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n" +
            "2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
            "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/Contents 4 0 R>>endobj\n" +
            $"4 0 obj<</Length {System.Text.Encoding.ASCII.GetByteCount(content)}>>stream\n" +
            content +
            "endstream endobj\n" +
            "trailer<</Root 1 0 R>>\n";

        return System.Text.Encoding.ASCII.GetBytes(pdf);
    }
}
