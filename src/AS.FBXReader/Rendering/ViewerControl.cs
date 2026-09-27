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
    }

    public void Attach(FbxSession session, AnimationPlayer player)
    {
        _session = session;
        _player = player;
        _renderer.SetScene(session, player);
        _overlay.Text = $"{Path.GetFileName(session.FilePath)}  |  {session.Parts.Count} parts  |  {session.Animations.Count} animations";
        _gl.Invalidate();
    }

    public void InvalidateScene() => _gl.Invalidate();

    public Bitmap CaptureFrame(int width, int height)
    {
        if (!_gl.HasValidContext)
            throw new InvalidOperationException("OpenGL context is not ready.");

        _gl.MakeCurrent();
        _renderer.Render(width, height);

        var bytes = new byte[width * height * 4];
        GL.ReadPixels(0, 0, width, height, PixelFormat.Bgra, PixelType.UnsignedByte, bytes);

        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var bits = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bits.Scan0, bytes.Length);
        bitmap.UnlockBits(bits);
        bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
        return bitmap;
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
