using System.Drawing;

namespace AS.FBXReader.Rendering;

/// <summary>
/// Fits an export resolution into the existing editor viewport. The returned
/// outer rectangle INCLUDES the thin border; its interior has the aspect
/// ratio used by the OpenGL renderer and offscreen FFmpeg frame capture.
/// Pixel resolution itself is not limited by the preview's display size.
/// </summary>
public static class OutputAspectLayout
{
    public static Rectangle Fit(Size host, int outputWidth, int outputHeight,
                                int margin = 12, int border = 2)
    {
        if (outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputWidth),
                "Output width/height must be positive.");

        if (host.Width <= 0 || host.Height <= 0)
            return Rectangle.Empty;

        // Cannot show margins/border in a tiny transient resize: fill safely.
        var smallest = 2 * (margin + border) + 1;
        if (host.Width < smallest || host.Height < smallest)
            return new Rectangle(Point.Empty, host);

        var maxW = host.Width - 2 * margin - 2 * border;
        var maxH = host.Height - 2 * margin - 2 * border;
        var aspect = outputWidth / (double)outputHeight;

        int w, h;
        if (maxW / (double)maxH > aspect)
        {
            h = maxH;
            w = Math.Max(1, (int)Math.Floor(h * aspect));
        }
        else
        {
            w = maxW;
            h = Math.Max(1, (int)Math.Floor(w / aspect));
        }

        w = Math.Clamp(w, 1, maxW);
        h = Math.Clamp(h, 1, maxH);
        var outerW = w + 2 * border;
        var outerH = h + 2 * border;

        return new Rectangle(
            (host.Width - outerW) / 2,
            (host.Height - outerH) / 2,
            outerW,
            outerH);
    }
}
