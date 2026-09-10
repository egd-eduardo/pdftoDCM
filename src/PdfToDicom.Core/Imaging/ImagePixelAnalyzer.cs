using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PdfToDicom.Core.Imaging;

/// <summary>
/// Decides how an image should be stored in DICOM by looking at what its pixels actually
/// contain.
/// </summary>
internal static class ImagePixelAnalyzer
{
    /// <summary>
    /// True when any pixel in any frame carries colour, that is, has channels that are
    /// not all equal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The obvious approach, asking the decoder what pixel format the file used, does not
    /// work and is worth explaining. ImageSharp's non generic loader promotes almost
    /// everything to a 32 bit RGBA buffer, so a greyscale PNG reports itself as colour. A
    /// first version of this code did exactly that and would have tripled the size of
    /// every scanned document.
    /// </para>
    /// <para>
    /// Reading the file's own metadata instead would be format specific, and would still
    /// give the wrong answer for the commonest case in this workflow: flatbed scanners
    /// routinely emit an RGB JPEG of a black and white page. Storing that as MONOCHROME2
    /// is not merely smaller by two thirds, it is a more honest description of the
    /// content.
    /// </para>
    /// <para>
    /// Deciding from the content is therefore both simpler and better. It cannot lose
    /// information: if every pixel has red, green and blue equal, there is no colour
    /// present to discard.
    /// </para>
    /// </remarks>
    internal static bool IsColour(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);

        for (int frameIndex = 0; frameIndex < image.Frames.Count; frameIndex++)
        {
            using Image frame = image.Frames.CloneFrame(frameIndex);
            using Image<Rgb24> rgb = frame.CloneAs<Rgb24>();

            if (FrameHasColour(rgb))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FrameHasColour(Image<Rgb24> rgb)
    {
        bool colour = false;

        rgb.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height && !colour; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);

                foreach (Rgb24 pixel in row)
                {
                    if (pixel.R != pixel.G || pixel.G != pixel.B)
                    {
                        colour = true;
                        break;
                    }
                }
            }
        });

        return colour;
    }
}
