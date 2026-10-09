using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;

namespace AS.FBXReader.Rendering;

public sealed class TextureCache : IDisposable
{
    private readonly Dictionary<string, int> _textures = new(StringComparer.OrdinalIgnoreCase);
    private bool _premultiplyAlpha = true;

    public bool PremultiplyAlpha => _premultiplyAlpha;

    public void SetPremultiplyAlpha(bool enabled)
    {
        if (_premultiplyAlpha == enabled)
            return;

        _premultiplyAlpha = enabled;
        Clear();
    }

    public int GetOrLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return 0;

        if (_textures.TryGetValue(path, out var existing))
            return existing;

        using var source = new Bitmap(path);
        using var bitmap = new Bitmap(
            source.Width,
            source.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.DrawImage(source, 0, 0, source.Width, source.Height);
        }

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bits = bitmap.LockBits(
            rect,
            System.Drawing.Imaging.ImageLockMode.ReadWrite,
            bitmap.PixelFormat);

        try
        {
            if (_premultiplyAlpha)
                PremultiplyBgra(bits, bitmap.Width, bitmap.Height);

            var texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);

            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba,
                bitmap.Width,
                bitmap.Height,
                0,
                PixelFormat.Bgra,
                PixelType.UnsignedByte,
                bits.Scan0);

            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, 0);

            _textures[path] = texture;
            return texture;
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }
    }

    public void Evict(string path)
    {
        if (_textures.Remove(path, out var texture))
            GL.DeleteTexture(texture);
    }

    private static void PremultiplyBgra(
        System.Drawing.Imaging.BitmapData bits,
        int width,
        int height)
    {
        var stride = Math.Abs(bits.Stride);
        var byteCount = stride * height;
        var pixels = new byte[byteCount];

        Marshal.Copy(bits.Scan0, pixels, 0, byteCount);

        for (var y = 0; y < height; y++)
        {
            var row = y * stride;

            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var alpha = pixels[i + 3];

                if (alpha == 255)
                    continue;

                if (alpha == 0)
                {
                    pixels[i + 0] = 0;
                    pixels[i + 1] = 0;
                    pixels[i + 2] = 0;
                    continue;
                }

                pixels[i + 0] = (byte)((pixels[i + 0] * alpha + 127) / 255);
                pixels[i + 1] = (byte)((pixels[i + 1] * alpha + 127) / 255);
                pixels[i + 2] = (byte)((pixels[i + 2] * alpha + 127) / 255);
            }
        }

        Marshal.Copy(pixels, 0, bits.Scan0, byteCount);
    }

    private void Clear()
    {
        foreach (var texture in _textures.Values)
            GL.DeleteTexture(texture);

        _textures.Clear();
    }

    public void Dispose() => Clear();
}
