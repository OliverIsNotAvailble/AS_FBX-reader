using System.Drawing.Drawing2D;

namespace AS.FBXReader.Rendering;

/// <summary>
/// Blender-like orientation sphere. Drag to orbit or click an axis tip to snap
/// to Front/Back/Right/Left/Top/Bottom. Does not edit geometry or skeleton.
/// </summary>
public sealed class OrbitSphereControl : Control
{
    private float _yaw = 25f;
    private float _pitch = 12f;
    private Point _down;
    private float _downYaw;
    private float _downPitch;
    private bool _dragging;
    private readonly List<(PointF Position, float Yaw, float Pitch)> _tips = new();

    public event Action<float, float>? AnglesChanged;
    public OrbitSphereControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(34, 38, 46);
        ForeColor = Color.White;
        MinimumSize = new Size(180, 180);
        Size = new Size(218, 218);
        Cursor = Cursors.Hand;
    }

    public void SetAngles(float yaw, float pitch)
    {
        _yaw = yaw;
        _pitch = pitch;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var center = new PointF(Width / 2f, Height / 2f);
        var radius = Math.Min(Width, Height) * 0.32f;

        using (var fill = new LinearGradientBrush(
            new RectangleF(center.X - radius, center.Y - radius, 2 * radius, 2 * radius),
            Color.FromArgb(77, 89, 109),
            Color.FromArgb(23, 28, 40), 90f))
            g.FillEllipse(fill, center.X - radius, center.Y - radius, 2 * radius, 2 * radius);
        using (var border = new Pen(Color.FromArgb(115, 133, 154), 2))
            g.DrawEllipse(border, center.X - radius, center.Y - radius, 2 * radius, 2 * radius);

        var yaw = _yaw * MathF.PI / 180f;
        var pitch = _pitch * MathF.PI / 180f;
        var right = new float[] { MathF.Cos(yaw), 0, -MathF.Sin(yaw) };
        var up = new float[] { -MathF.Sin(yaw) * MathF.Sin(pitch),
                               MathF.Cos(pitch),
                              -MathF.Cos(yaw) * MathF.Sin(pitch) };
        var direction = new float[] { MathF.Sin(yaw) * MathF.Cos(pitch),
                                      MathF.Sin(pitch),
                                      MathF.Cos(yaw) * MathF.Cos(pitch) };
        var axes = new (float X, float Y, float Z, Color Color, string Label, float SnapYaw, float SnapPitch)[]
        {
            ( 1, 0, 0, Color.FromArgb(244, 113, 117), "+X",  90,  0),
            (-1, 0, 0, Color.FromArgb(185,  75,  78), "-X", -90,  0),
            ( 0, 1, 0, Color.FromArgb(126, 232, 156), "+Y",   0, 85),
            ( 0,-1, 0, Color.FromArgb( 72, 161,  99), "-Y",   0,-85),
            ( 0, 0, 1, Color.FromArgb(102, 171, 255), "+Z",   0,  0),
            ( 0, 0,-1, Color.FromArgb( 59, 111, 185), "-Z", 180,  0)
        };

        _tips.Clear();
        // Draw rear points first, front points on top.
        foreach (var axis in axes.OrderBy(a =>
                     a.X * direction[0] + a.Y * direction[1] + a.Z * direction[2]))
        {
            var px = axis.X * right[0] + axis.Y * right[1] + axis.Z * right[2];
            var py = axis.X * up[0] + axis.Y * up[1] + axis.Z * up[2];
            var p = new PointF(center.X + px * radius * .89f,
                               center.Y - py * radius * .89f);
            using var pen = new Pen(Color.FromArgb(155, axis.Color), 2.4f);
            g.DrawLine(pen, center, p);
            using var brush = new SolidBrush(axis.Color);
            g.FillEllipse(brush, p.X - 13, p.Y - 13, 26, 26);
            TextRenderer.DrawText(g, axis.Label, Font,
                new Rectangle((int)p.X - 14, (int)p.Y - 10, 28, 20),
                Color.White, TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            _tips.Add((p, axis.SnapYaw, axis.SnapPitch));
        }

        using var infoFont = new Font(Font.FontFamily, 9f);
        TextRenderer.DrawText(g, "Drag sphere / click X Y Z", infoFont,
            new Rectangle(0, Height - 27, Width, 23),
            Color.LightGray, TextFormatFlags.HorizontalCenter);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        foreach (var tip in _tips.AsEnumerable().Reverse())
        {
            var dx = tip.Position.X - e.X;
            var dy = tip.Position.Y - e.Y;
            if (dx * dx + dy * dy < 14 * 14)
            {
                AnglesChanged?.Invoke(tip.Yaw, tip.Pitch);
                return;
            }
        }
        _dragging = true;
        _down = e.Location;
        _downYaw = _yaw;
        _downPitch = _pitch;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || (e.Button & MouseButtons.Left) == 0) return;
        AnglesChanged?.Invoke(
            _downYaw + (e.X - _down.X) * .55f,
            Math.Clamp(_downPitch - (e.Y - _down.Y) * .55f, -85, 85));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }
}
