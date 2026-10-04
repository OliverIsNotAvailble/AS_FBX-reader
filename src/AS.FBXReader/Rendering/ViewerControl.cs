using System.ComponentModel;
using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using AS.FBXReader.Core;
using AS.FBXReader.Models;

namespace AS.FBXReader.Rendering;

public readonly record struct CapturedFrame(int Width, int Height, bool Transparent, byte[] Pixels);

public sealed class ViewerControl : UserControl
{
    private readonly GLControl _gl;
    private readonly Label _overlay;
    private FbxSession? _session;
    private AnimationPlayer? _player;
    private readonly SceneRenderer _renderer = new();
    private ScenePart? _movingPart;
    private Point _dragStart;
    private float _startOffsetX;
    private float _startOffsetY;
    private float _unitsPerPixelX;
    private float _unitsPerPixelY;
    private bool _dragged;
    private bool _suppressNextClick;
    private bool _freeCameraEnabled;
    private bool _cameraDragging;
    private Point _cameraDragStart;
    private FramingState? _cameraStart;

    public event EventHandler<ScenePart>? PartPicked;
    public event EventHandler<ScenePart>? PartMoved;
    public event EventHandler<FramingState>? CameraChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool FreeCameraEnabled
    {
        get => _freeCameraEnabled;
        set
        {
            _freeCameraEnabled = value;
            if (value && _session != null && !_renderer.GetFraming().Enabled)
                _renderer.CaptureFramingReference();
            if (!value)
            {
                _cameraDragging = false;
                _cameraStart = null;
                _gl.Capture = false;
            }
            _gl.Cursor = value ? Cursors.Hand : Cursors.Default;
        }
    }

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
            if (_suppressNextClick)
            {
                _suppressNextClick = false;
                return;
            }
            if (e.Button != MouseButtons.Left || _movingPart != null ||
                _session is null || _player is null)
                return;

            var part = _renderer.PickPart(
                e.X,
                e.Y,
                Math.Max(1, _gl.ClientSize.Width),
                Math.Max(1, _gl.ClientSize.Height));

            if (part != null)
                PartPicked?.Invoke(this, part);
        };

        _gl.MouseDown += (_, e) =>
        {
            if (_movingPart is null)
            {
                _suppressNextClick = false;
                if (_freeCameraEnabled && e.Button == MouseButtons.Left && _session != null)
                {
                    _gl.Focus();
                    _cameraDragStart = e.Location;
                    _cameraStart = _renderer.GetFraming();
                    _cameraDragging = true;
                    _dragged = false;
                    _gl.Capture = true;
                    _gl.Cursor = Cursors.SizeAll;
                }
                return;
            }
            if (e.Button != MouseButtons.Left)
                return;

            _gl.Focus();
            _dragStart = e.Location;
            _startOffsetX = _movingPart.OffsetX;
            _startOffsetY = _movingPart.OffsetY;
            _dragged = false;
            (_unitsPerPixelX, _unitsPerPixelY) = _renderer.GetWorldUnitsPerPixel(
                _gl.ClientSize.Width, _gl.ClientSize.Height);
            _gl.Capture = true;
        };
        _gl.MouseMove += (_, e) =>
        {
            if (_cameraDragging && _cameraStart != null &&
                (e.Button & MouseButtons.Left) != 0)
            {
                var panX = e.X - _cameraDragStart.X;
                var panY = e.Y - _cameraDragStart.Y;
                if (panX != 0 || panY != 0)
                {
                    var zoom = Math.Max(0.1f, _cameraStart.Zoom);
                    _renderer.SetFramingTransform(zoom,
                        _cameraStart.OffsetX + 2f * panX / Math.Max(1, _gl.ClientSize.Width) / zoom,
                        _cameraStart.OffsetY - 2f * panY / Math.Max(1, _gl.ClientSize.Height) / zoom);
                    CameraChanged?.Invoke(this, _renderer.GetFraming());
                    _gl.Invalidate();
                    _dragged = true;
                }
                return;
            }

            if (_movingPart is null || !_gl.Capture ||
                (e.Button & MouseButtons.Left) == 0)
                return;

            var dx = e.X - _dragStart.X;
            var dy = e.Y - _dragStart.Y;
            if (dx == 0 && dy == 0)
                return;

            _dragged = true;
            _movingPart.OffsetX = _startOffsetX + dx * _unitsPerPixelX;
            _movingPart.OffsetY = _startOffsetY - dy * _unitsPerPixelY;
            _gl.Invalidate();
        };
        _gl.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && _cameraDragging)
            {
                _cameraDragging = false;
                _cameraStart = null;
                _gl.Capture = false;
                _gl.Cursor = _freeCameraEnabled ? Cursors.Hand : Cursors.Default;
                _suppressNextClick = _dragged;
                _dragged = false;
                return;
            }

            if (e.Button != MouseButtons.Left || _movingPart is null)
                return;

            _gl.Capture = false;
            if (!_dragged)
                return;

            var moved = _movingPart;
            _suppressNextClick = true;
            EndMovePart();
            PartMoved?.Invoke(this, moved);
        };
        _gl.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape || _movingPart is null)
                return;

            _movingPart.OffsetX = _startOffsetX;
            _movingPart.OffsetY = _startOffsetY;
            EndMovePart();
            _gl.Invalidate();
            e.Handled = true;
        };
        _gl.MouseEnter += (_, _) =>
        {
            if (_freeCameraEnabled)
                _gl.Focus();
        };
        _gl.MouseWheel += (_, e) =>
        {
            if (!_freeCameraEnabled || _session is null || e.Delta == 0)
                return;

            var framing = _renderer.GetFraming();
            if (!framing.Enabled)
                framing = _renderer.CaptureFramingReference();

            // About 12% per wheel notch; independent of window resolution.
            var zoom = Math.Clamp(
                framing.Zoom * (float)Math.Pow(1.12, e.Delta / 120.0),
                0.1f, 10f);
            _renderer.SetFramingTransform(zoom, framing.OffsetX, framing.OffsetY);
            CameraChanged?.Invoke(this, _renderer.GetFraming());
            _gl.Invalidate();
        };
    }

    public void BeginMovePart(ScenePart part)
    {
        EndMovePart();
        _movingPart = part;
        _startOffsetX = part.OffsetX;
        _startOffsetY = part.OffsetY;
        // Freeze automatic framing so the image does not chase the cursor.
        if (!_renderer.GetFraming().Enabled)
            _renderer.CaptureFramingReference();
        _gl.Cursor = Cursors.SizeAll;
        _gl.Focus();
        _gl.Invalidate();
    }

    public void EndMovePart()
    {
        _gl.Capture = false;
        _movingPart = null;
        _gl.Cursor = _freeCameraEnabled ? Cursors.Hand : Cursors.Default;
    }

    public void Attach(FbxSession session, AnimationPlayer player)
    {
        EndMovePart();
        _session = session;
        _player = player;
        _renderer.SetScene(session, player);
        if (_freeCameraEnabled)
            _renderer.CaptureFramingReference();
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

    public Vector3[] GetPartWorldPositions(ScenePart part)
        => _renderer.GetPartWorldPositions(part);

    public Bitmap CaptureFrame(int width, int height)
        => CreateBitmap(CaptureFramePixels(width, height));

    // Only the OpenGL readback happens on the UI thread. Pixel conversion and
    // PNG compression can then run on a worker without touching the GL context.
    public CapturedFrame CaptureFramePixels(int width, int height)
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

            return new CapturedFrame(width, height, _renderer.TransparentBackground, bytes);
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

    public static void SavePngFrame(CapturedFrame frame, string path)
    {
        using var bitmap = CreateBitmap(frame);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static Bitmap CreateBitmap(CapturedFrame frame)
    {
        // OpenGL compositing buffers store premultiplied RGB when alpha is
        // present. PNG/WebP expect straight-alpha pixels.
        if (frame.Transparent)
            UnpremultiplyBgra(frame.Pixels);

        var bitmap = new Bitmap(
            frame.Width,
            frame.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            var rect = new Rectangle(0, 0, frame.Width, frame.Height);
            var bits = bitmap.LockBits(rect,
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                bitmap.PixelFormat);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    frame.Pixels, 0, bits.Scan0, frame.Pixels.Length);
            }
            finally
            {
                bitmap.UnlockBits(bits);
            }
            bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
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
