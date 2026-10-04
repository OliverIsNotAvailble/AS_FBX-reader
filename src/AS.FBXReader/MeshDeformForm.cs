using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Assimp;
using AS.FBXReader.Models;

namespace AS.FBXReader;

// Edits the source mesh coordinates. The normal renderer then applies the
// existing skinning, placement and animation to these edited vertices.
public sealed class MeshDeformForm : Form
{
    private readonly ScenePart _part;
    private readonly Dictionary<int, VertexOffset> _initial;
    private readonly Stack<Dictionary<int, VertexOffset>> _undo = new();
    private readonly MeshCanvas _canvas;
    private readonly Label _hint;

    public MeshDeformForm(ScenePart part, Mesh mesh, string texturePath, Action previewChanged)
    {
        _part = part;
        _initial = Snapshot();
        Text = $"Edit mesh shape — {part.DisplayName}";
        StartPosition = FormStartPosition.CenterParent;
        Width = 1000;
        Height = 780;
        MinimumSize = new Size(680, 500);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 10f);

        _hint = new Label
        {
            Text = "Drag a vertex. Increase radius to bend its neighbors smoothly. Right-click a vertex to reset it.",
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };

        _canvas = new MeshCanvas(part, mesh, texturePath, () =>
        {
            previewChanged();
            _hint.Text = $"Vertex {(_canvas?.SelectedVertex ?? -1) + 1}/{mesh.VertexCount}  |  edits: {_part.VertexOffsets.Count}  |  Save profile after Apply";
        }, beforeEdit => _undo.Push(beforeEdit)) { Dock = DockStyle.Fill };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 48,
            WrapContents = false,
            Padding = new Padding(9, 7, 9, 3)
        };
        var radius = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 100,
            DecimalPlaces = 2,
            Increment = 0.25m,
            Width = 75,
            Value = 0
        };
        radius.ValueChanged += (_, _) => _canvas.BrushRadius = (float)radius.Value;
        toolbar.Controls.Add(new Label
        {
            Text = "Neighbor radius (0 = one vertex):",
            AutoSize = true,
            Padding = new Padding(0, 5, 0, 0)
        });
        toolbar.Controls.Add(radius);
        var undo = new Button { Text = "Undo", AutoSize = true };
        undo.Click += (_, _) =>
        {
            if (_undo.TryPop(out var previous))
                Restore(previous);
        };
        toolbar.Controls.Add(undo);
        var reset = new Button { Text = "Reset shape", AutoSize = true };
        reset.Click += (_, _) =>
        {
            _undo.Push(Snapshot());
            _part.VertexOffsets.Clear();
            _canvas.Invalidate();
            previewChanged();
            _hint.Text = "Shape reset. Apply to keep this state, or Cancel to restore the original edits.";
        };
        toolbar.Controls.Add(reset);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            ColumnCount = 2,
            Padding = new Padding(10, 6, 10, 6)
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        var apply = new Button { Text = "Apply", Width = 95, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Width = 95, DialogResult = DialogResult.Cancel };
        actions.Controls.Add(apply);
        actions.Controls.Add(cancel);
        bottom.Controls.Add(_hint, 0, 0);
        bottom.Controls.Add(actions, 1, 0);

        Controls.Add(_canvas);
        Controls.Add(toolbar);
        Controls.Add(bottom);
        AcceptButton = apply;
        CancelButton = cancel;
        FormClosing += (_, _) =>
        {
            if (DialogResult != DialogResult.OK)
                Restore(_initial);
        };
    }

    private Dictionary<int, VertexOffset> Snapshot()
        => _part.VertexOffsets.ToDictionary(x => x.Key,
            x => new VertexOffset { VertexIndex = x.Key, X = x.Value.X, Y = x.Value.Y });

    private void Restore(Dictionary<int, VertexOffset> edits)
    {
        _part.VertexOffsets.Clear();
        foreach (var (index, edit) in edits)
            _part.VertexOffsets[index] = new VertexOffset
            {
                VertexIndex = index, X = edit.X, Y = edit.Y
            };
        _canvas.Invalidate();
        _canvas.NotifyPreview();
    }

    private sealed class MeshCanvas : Control
    {
        private readonly ScenePart _part;
        private readonly PointF[] _original;
        private readonly PointF[] _sourceUv;
        private readonly uint[] _indices;
        private readonly Bitmap? _texture;
        private readonly Action _changed;
        private readonly Action<Dictionary<int, VertexOffset>> _beforeEdit;
        private readonly float _minX, _maxY, _spanX, _spanY;
        private int _dragVertex = -1;
        private Point _dragStart;
        private float[]? _startX, _startY, _falloff;
        private Dictionary<int, VertexOffset>? _dragSnapshot;
        private bool _dragged;

        public int SelectedVertex { get; private set; } = -1;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public float BrushRadius { get; set; }

        public MeshCanvas(ScenePart part, Mesh mesh, string texturePath,
            Action changed, Action<Dictionary<int, VertexOffset>> beforeEdit)
        {
            _part = part;
            _changed = changed;
            _beforeEdit = beforeEdit;
            _original = mesh.Vertices.Select(v => new PointF(v.X, v.Y)).ToArray();
            _indices = mesh.GetUnsignedIndices().Select(x => (uint)x).ToArray();
            _sourceUv = new PointF[_original.Length];
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.FromArgb(28, 28, 28);

            var minX = _original.Min(p => p.X);
            var maxX = _original.Max(p => p.X);
            var minY = _original.Min(p => p.Y);
            var maxY = _original.Max(p => p.Y);
            var pad = Math.Max(0.7f, Math.Max(maxX - minX, maxY - minY) * 0.27f);
            _minX = minX - pad;
            _maxY = maxY + pad;
            _spanX = Math.Max(0.01f, maxX - minX + 2 * pad);
            _spanY = Math.Max(0.01f, maxY - minY + 2 * pad);

            if (mesh.HasTextureCoords(0) &&
                mesh.TextureCoordinateChannels[0].Count > 0 && File.Exists(texturePath))
            {
                using var atlas = new Bitmap(texturePath);
                var uv = mesh.TextureCoordinateChannels[0];
                var count = Math.Min(uv.Count, _sourceUv.Length);
                for (var i = 0; i < count; i++)
                    _sourceUv[i] = new PointF(uv[i].X * atlas.Width,
                        (1f - uv[i].Y) * atlas.Height);

                var left = Math.Clamp((int)MathF.Floor(_sourceUv.Take(count).Min(p => p.X)) - 2, 0, atlas.Width - 1);
                var top = Math.Clamp((int)MathF.Floor(_sourceUv.Take(count).Min(p => p.Y)) - 2, 0, atlas.Height - 1);
                var right = Math.Clamp((int)MathF.Ceiling(_sourceUv.Take(count).Max(p => p.X)) + 2, left + 1, atlas.Width);
                var bottom = Math.Clamp((int)MathF.Ceiling(_sourceUv.Take(count).Max(p => p.Y)) + 2, top + 1, atlas.Height);
                _texture = atlas.Clone(new Rectangle(left, top, right - left, bottom - top), PixelFormat.Format32bppArgb);
                for (var i = 0; i < count; i++)
                    _sourceUv[i] = new PointF(_sourceUv[i].X - left, _sourceUv[i].Y - top);
            }
        }

        public void NotifyPreview() => _changed();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _texture?.Dispose();
            base.Dispose(disposing);
        }

        private float ViewScale => Math.Max(0.001f,
            Math.Min((ClientSize.Width - 30f) / _spanX,
                     (ClientSize.Height - 30f) / _spanY));

        private PointF LocalToScreen(PointF p)
        {
            var s = ViewScale;
            return new PointF((ClientSize.Width - _spanX * s) / 2f + (p.X - _minX) * s,
                (ClientSize.Height - _spanY * s) / 2f + (_maxY - p.Y) * s);
        }

        private PointF EditedPoint(int i)
        {
            var p = _original[i];
            return _part.VertexOffsets.TryGetValue(i, out var offset)
                ? new PointF(p.X + offset.X, p.Y + offset.Y)
                : p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            var step = Math.Max(0.5f, MathF.Ceiling(Math.Max(_spanX, _spanY) / 18f));
            using var gridPen = new Pen(Color.FromArgb(45, 210, 210, 210));
            for (var x = MathF.Ceiling(_minX / step) * step; x < _minX + _spanX; x += step)
            {
                var sx = LocalToScreen(new PointF(x, 0)).X;
                g.DrawLine(gridPen, sx, 0, sx, ClientSize.Height);
            }
            for (var y = MathF.Ceiling((_maxY - _spanY) / step) * step; y < _maxY; y += step)
            {
                var sy = LocalToScreen(new PointF(0, y)).Y;
                g.DrawLine(gridPen, 0, sy, ClientSize.Width, sy);
            }

            var points = Enumerable.Range(0, _original.Length)
                .Select(i => LocalToScreen(EditedPoint(i))).ToArray();
            using var edgePen = new Pen(Color.FromArgb(170, 55, 216, 232), 1f);
            using var fill = new SolidBrush(Color.FromArgb(65, 120, 185, 210));
            for (var j = 0; j + 2 < _indices.Length; j += 3)
            {
                var a = (int)_indices[j];
                var b = (int)_indices[j + 1];
                var c = (int)_indices[j + 2];
                if (a < 0 || b < 0 || c < 0 ||
                    a >= points.Length || b >= points.Length || c >= points.Length)
                    continue;

                var triangle = new[] { points[a], points[b], points[c] };
                if (_texture is not null)
                    DrawTexturedTriangle(g, triangle,
                        [_sourceUv[a], _sourceUv[b], _sourceUv[c]]);
                else
                    g.FillPolygon(fill, triangle);
                g.DrawPolygon(edgePen, triangle);
            }

            using var handle = new SolidBrush(Color.FromArgb(60, 225, 242));
            using var selected = new SolidBrush(Color.FromArgb(255, 224, 75));
            using var outline = new Pen(Color.FromArgb(16, 45, 50), 1f);
            for (var i = 0; i < points.Length; i++)
            {
                var p = points[i];
                var radius = i == SelectedVertex ? 6f : 4f;
                g.FillEllipse(i == SelectedVertex ? selected : handle,
                    p.X - radius, p.Y - radius, radius * 2, radius * 2);
                g.DrawEllipse(outline, p.X - radius, p.Y - radius, radius * 2, radius * 2);
            }
        }

        private void DrawTexturedTriangle(Graphics g, PointF[] dest, PointF[] src)
        {
            var sx1 = src[1].X - src[0].X;
            var sy1 = src[1].Y - src[0].Y;
            var sx2 = src[2].X - src[0].X;
            var sy2 = src[2].Y - src[0].Y;
            var det = sx1 * sy2 - sx2 * sy1;
            if (Math.Abs(det) < 0.00001f)
                return;

            var dx1 = dest[1].X - dest[0].X;
            var dy1 = dest[1].Y - dest[0].Y;
            var dx2 = dest[2].X - dest[0].X;
            var dy2 = dest[2].Y - dest[0].Y;
            if (Math.Abs(dx1 * dy2 - dx2 * dy1) < 0.001f)
                return;
            var xx = (dx1 * sy2 - dx2 * sy1) / det;
            var xy = (dx2 * sx1 - dx1 * sx2) / det;
            var yx = (dy1 * sy2 - dy2 * sy1) / det;
            var yy = (dy2 * sx1 - dy1 * sx2) / det;
            var tx = dest[0].X - xx * src[0].X - xy * src[0].Y;
            var ty = dest[0].Y - yx * src[0].X - yy * src[0].Y;

            var state = g.Save();
            using var clip = new GraphicsPath();
            clip.AddPolygon(dest);
            g.SetClip(clip);
            using var transform = new Matrix(xx, yx, xy, yy, tx, ty);
            g.Transform = transform;
            g.DrawImage(_texture!, 0, 0);
            g.Restore(state);
        }

        private int HitVertex(Point location)
        {
            var closest = -1;
            var distance = 14f * 14f;
            for (var i = 0; i < _original.Length; i++)
            {
                var p = LocalToScreen(EditedPoint(i));
                var d = (p.X - location.X) * (p.X - location.X) +
                        (p.Y - location.Y) * (p.Y - location.Y);
                if (d < distance)
                {
                    distance = d;
                    closest = i;
                }
            }
            return closest;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var index = HitVertex(e.Location);
            if (index < 0)
                return;
            SelectedVertex = index;
            Invalidate();

            if (e.Button == MouseButtons.Right)
            {
                var snapshot = CopyOffsets();
                if (_part.VertexOffsets.Remove(index))
                {
                    _beforeEdit(snapshot);
                    Invalidate();
                    NotifyPreview();
                }
                return;
            }
            if (e.Button != MouseButtons.Left)
                return;

            _dragVertex = index;
            _dragStart = e.Location;
            _dragged = false;
            _dragSnapshot = CopyOffsets();
            _startX = new float[_original.Length];
            _startY = new float[_original.Length];
            _falloff = new float[_original.Length];
            var center = EditedPoint(index);
            for (var i = 0; i < _original.Length; i++)
            {
                _startX[i] = _part.VertexOffsets.TryGetValue(i, out var edit) ? edit.X : 0f;
                _startY[i] = edit?.Y ?? 0f;
                var p = EditedPoint(i);
                var dx = p.X - center.X;
                var dy = p.Y - center.Y;
                var distance = MathF.Sqrt(dx * dx + dy * dy);
                var influence = BrushRadius > 0f
                    ? Math.Clamp(1f - distance / BrushRadius, 0f, 1f)
                    : i == index ? 1f : 0f;
                _falloff[i] = influence * influence;
            }
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragVertex < 0 || _startX is null || _startY is null || _falloff is null)
                return;
            var dx = (e.X - _dragStart.X) / ViewScale;
            var dy = (_dragStart.Y - e.Y) / ViewScale;
            if (dx == 0f && dy == 0f)
                return;
            _dragged = true;
            for (var i = 0; i < _original.Length; i++)
            {
                if (_falloff[i] <= 0f)
                    continue;
                SetOffset(i, _startX[i] + dx * _falloff[i],
                    _startY[i] + dy * _falloff[i]);
            }
            Invalidate();
            NotifyPreview();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || _dragVertex < 0)
                return;
            if (_dragged && _dragSnapshot is not null)
                _beforeEdit(_dragSnapshot);
            _dragVertex = -1;
            _dragSnapshot = null;
            _startX = _startY = _falloff = null;
            Capture = false;
        }

        private void SetOffset(int index, float x, float y)
        {
            if (Math.Abs(x) < 0.00001f && Math.Abs(y) < 0.00001f)
                _part.VertexOffsets.Remove(index);
            else
                _part.VertexOffsets[index] = new VertexOffset
                {
                    VertexIndex = index, X = x, Y = y
                };
        }

        private Dictionary<int, VertexOffset> CopyOffsets()
            => _part.VertexOffsets.ToDictionary(x => x.Key,
                x => new VertexOffset { VertexIndex = x.Key, X = x.Value.X, Y = x.Value.Y });
    }
}
