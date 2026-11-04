namespace AS.FBXReader.Rendering;

/// <summary>
/// Center-crop a still background image to the output canvas, without
/// distortion or letterboxing. UV coordinates refer to the image's visible
/// rectangle; OpenGL rendering maps its top row to the top of the canvas.
/// </summary>
public static class BackgroundImageLayout
{
    public readonly record struct UvRect(float MinU, float MaxU, float MinV, float MaxV);

    public static UvRect Cover(int imageWidth, int imageHeight,
        int outputWidth, int outputHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Dimensions must be positive.");

        var imageAspect = (double)imageWidth / imageHeight;
        var outputAspect = (double)outputWidth / outputHeight;

        if (imageAspect > outputAspect)
        {
            var width = (float)(outputAspect / imageAspect);
            return new UvRect((1f - width) * 0.5f, (1f + width) * 0.5f, 0f, 1f);
        }

        var height = (float)(imageAspect / outputAspect);
        return new UvRect(0f, 1f, (1f - height) * 0.5f, (1f + height) * 0.5f);
    }
}
