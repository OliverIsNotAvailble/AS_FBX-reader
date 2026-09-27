using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using AS.FBXReader.Core;
using AS.FBXReader.Models;

namespace AS.FBXReader.Rendering;

public sealed class ViewerControl : UserControl
{
    private readonly GLControl _gl;
    private readonly Label _overlay;
    private FbxSession? _session;
    private AnimationPlayer? _player;
    private readonly SceneRenderer _renderer = new();

    public event EventHandler<ScenePart>? PartPicked;

    public ViewerControl()
    {
        Dock = DockStyle.Fill;

        _gl = new GLControl(new GLControlSettings
        {
            APIVersion = new Version(3, 3, 0, 0),
            Profile = OpenTK.Windowing.Common.ContextProfile.Core,
            NumberOfSamples = 4,
            DepthBits = 24,
            AlphaBits = 8
        })
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(28, 28, 28)
        };

        _overlay = new Label
        {
            AutoSize = true,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(150, 0, 0, 0),
            Padding = new Padding(6),
            Text = "Open an FBX"
        };

        Controls.Add(_gl);
        Controls.Add(_overlay);
        _overlay.BringToFront();

        _gl.Load += (_, _) =>
        {
            _gl.MakeCurrent();
            _renderer.Initialize();
        };
        _gl.Paint += (_, _) => RenderNow();
        _gl.Resize += (_, _) =>
        {
            if (_gl.ClientSize.Height > 0)
                GL.Viewport(0, 0, _gl.ClientSize.Width, _gl.ClientSize.Height);
            _gl.Invalidate();
        };

        _gl.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || _session is null || _player is null)
                return;

            var part = _renderer.PickPart(
                e.X,
                e.Y,
                Math.Max(1, _gl.ClientSize.Width),
                Math.Max(1, _gl.ClientSize.Height));

            if (part != null)
                PartPicked?.Invoke(this, part);
        };
    }

    public void Attach(FbxSession session, AnimationPlayer player)
    {
        _session = session;
        _player = player;
        _renderer.SetScene(session, player);
        _overlay.Text = $"{Path.GetFileName(session.FilePath)}  |  {session.Parts.Count} parts  |  {session.Animations.Count} animations";
        _gl.Invalidate();
    }

    public bool PremultiplyAlpha => _renderer.PremultiplyAlpha;
    public bool TransparentBackground => _renderer.TransparentBackground;
    public Color BackgroundColor => _renderer.BackgroundColor;

    public void SetBackground(bool transparent, Color color)
    {
        _renderer.SetBackground(transparent, color);
        _gl.Invalidate();
    }

    public FramingState ResetFraming()
    {
        var framing = _renderer.CaptureFramingReference();
        _gl.Invalidate();
        return framing;
    }

    public FramingState GetFraming() => _renderer.GetFraming();

    public void SetFraming(FramingState framing)
    {
        _renderer.SetFraming(framing);
        _gl.Invalidate();
    }

    public void SetFramingTransform(float zoom, float offsetX, float offsetY)
    {
        _renderer.SetFramingTransform(zoom, offsetX, offsetY);
        _gl.Invalidate();
    }

    public void SetPremultiplyAlpha(bool enabled)
    {
        if (!_gl.HasValidContext)
            return;

        _gl.MakeCurrent();
        _renderer.SetPremultiplyAlpha(enabled);
        _gl.Invalidate();
    }

    public void InvalidateScene() => _gl.Invalidate();

    public Bitmap CaptureFrame(int width, int height)
    {
        if (!_gl.HasValidContext)
            throw new InvalidOperationException("OpenGL context is not ready.");

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        _gl.MakeCurrent();

        GL.GetInteger(GetPName.FramebufferBinding, out var previousFramebuffer);

        var framebuffer = GL.GenFramebuffer();
        var colorTexture = GL.GenTexture();

        try
        {
            GL.BindTexture(TextureTarget.Texture2D, colorTexture);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba8,
                width,
                height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                IntPtr.Zero);

            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);

            GL.BindFramebuffer(
                FramebufferTarget.Framebuffer,
                framebuffer);

            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                colorTexture,
                0);

            var status = GL.CheckFramebufferStatus(
                FramebufferTarget.Framebuffer);

            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                throw new InvalidOperationException(
                    $"Offscreen framebuffer is incomplete: {status}");
            }

            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);

            _renderer.Render(width, height);

            var bytes = new byte[width * height * 4];
            GL.ReadPixels(
                0,
                0,
                width,
                height,
                PixelFormat.Bgra,
                PixelType.UnsignedByte,
                bytes);

            // OpenGL compositing buffers store premultiplied RGB when alpha is
            // present. PNG/WebP expect straight-alpha pixels.
            if (_renderer.TransparentBackground)
                UnpremultiplyBgra(bytes);

            var bitmap = new Bitmap(
                width,
                height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            var rect = new Rectangle(0, 0, width, height);
            var bits = bitmap.LockBits(
                rect,
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                bitmap.PixelFormat);

            System.Runtime.InteropServices.Marshal.Copy(
                bytes,
                0,
                bits.Scan0,
                bytes.Length);

            bitmap.UnlockBits(bits);
            bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
            return bitmap;
        }
        finally
        {
            GL.BindFramebuffer(
                FramebufferTarget.Framebuffer,
                previousFramebuffer);

            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.DeleteFramebuffer(framebuffer);
            GL.DeleteTexture(colorTexture);

            GL.Viewport(
                0,
                0,
                Math.Max(1, _gl.ClientSize.Width),
                Math.Max(1, _gl.ClientSize.Height));
        }
    }

    private static void UnpremultiplyBgra(byte[] pixels)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];

            if (alpha == 0)
            {
                pixels[i + 0] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                continue;
            }

            if (alpha == 255)
                continue;

            pixels[i + 0] = (byte)Math.Min(255, (pixels[i + 0] * 255 + alpha / 2) / alpha);
            pixels[i + 1] = (byte)Math.Min(255, (pixels[i + 1] * 255 + alpha / 2) / alpha);
            pixels[i + 2] = (byte)Math.Min(255, (pixels[i + 2] * 255 + alpha / 2) / alpha);
        }
    }

    private void RenderNow()
    {
        if (!_gl.HasValidContext)
            return;

        _gl.MakeCurrent();
        _renderer.Render(Math.Max(1, _gl.ClientSize.Width), Math.Max(1, _gl.ClientSize.Height));
        _gl.SwapBuffers();
    }
}
