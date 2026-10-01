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
}
