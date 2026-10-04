using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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

    public MeshDeformForm(ScenePart part, Mesh mesh, string texturePath,
        Func<PointF[]> worldPositions, Action previewChanged)
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

        _canvas = new MeshCanvas(part, mesh, texturePath, worldPositions, () =>
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
        private readonly Func<PointF[]> _worldPositions;
        private readonly PointF[] _reference;
        private readonly PointF[] _sourceUv;
        private readonly uint[] _indices;
        private readonly byte[]? _texturePixels;
        private readonly int _textureWidth, _textureHeight;
        private readonly Action _changed;
        private readonly Action<Dictionary<int, VertexOffset>> _beforeEdit;
        private readonly float _minX, _maxY, _spanX, _spanY;
        private int _dragVertex = -1;
        private Point _dragStart;
        private float[]? _startX, _startY, _falloff;
        private PointF[]? _basisX, _basisY;
        private Dictionary<int, VertexOffset>? _dragSnapshot;
        private bool _dragged;

        public int SelectedVertex { get; private set; } = -1;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public float BrushRadius { get; set; }

        public MeshCanvas(ScenePart part, Mesh mesh, string texturePath,
            Func<PointF[]> worldPositions, Action changed,
            Action<Dictionary<int, VertexOffset>> beforeEdit)
        {
            _part = part;
            _changed = changed;
            _beforeEdit = beforeEdit;
            _worldPositions = worldPositions;
            _reference = worldPositions();
            if (_reference.Length != mesh.VertexCount)
                throw new InvalidDataException("The mesh preview has a different vertex count.");
            _indices = mesh.GetUnsignedIndices().Select(x => (uint)x).ToArray();
            _sourceUv = new PointF[_reference.Length];
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.FromArgb(28, 28, 28);

            var minX = _reference.Min(p => p.X);
            var maxX = _reference.Max(p => p.X);
            var minY = _reference.Min(p => p.Y);
            var maxY = _reference.Max(p => p.Y);
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
                using var crop = atlas.Clone(new Rectangle(left, top, right - left, bottom - top), PixelFormat.Format32bppArgb);
                _textureWidth = crop.Width;
                _textureHeight = crop.Height;
                _texturePixels = ReadPixels(crop);
                for (var i = 0; i < count; i++)
                    _sourceUv[i] = new PointF(_sourceUv[i].X - left, _sourceUv[i].Y - top);
            }
        }

        public void NotifyPreview() => _changed();

        private float ViewScale => Math.Max(0.001f,
            Math.Min((ClientSize.Width - 30f) / _spanX,
                     (ClientSize.Height - 30f) / _spanY));

        private PointF WorldToScreen(PointF p)
        {
            var s = ViewScale;
            return new PointF((ClientSize.Width - _spanX * s) / 2f + (p.X - _minX) * s,
                (ClientSize.Height - _spanY * s) / 2f + (_maxY - p.Y) * s);
        }

        private PointF[] CurrentPositions() => _worldPositions();

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
                var sx = WorldToScreen(new PointF(x, 0)).X;
                g.DrawLine(gridPen, sx, 0, sx, ClientSize.Height);
            }
            for (var y = MathF.Ceiling((_maxY - _spanY) / step) * step; y < _maxY; y += step)
            {
                var sy = WorldToScreen(new PointF(0, y)).Y;
                g.DrawLine(gridPen, 0, sy, ClientSize.Width, sy);
            }

            var positions = CurrentPositions();
            var points = positions.Select(WorldToScreen).ToArray();
            if (_texturePixels is not null)
            {
                using var raster = RasterizeTexture(points);
                g.DrawImageUnscaled(raster, 0, 0);
            }
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
                if (_texturePixels is null)
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

        private static byte[] ReadPixels(Bitmap bitmap)
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new byte[bitmap.Width * bitmap.Height * 4];
                for (var y = 0; y < bitmap.Height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels,
                        y * bitmap.Width * 4, bitmap.Width * 4);
                return pixels;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private Bitmap RasterizeTexture(PointF[] points)
        {
            var width = Math.Max(1, ClientSize.Width);
            var height = Math.Max(1, ClientSize.Height);
            var pixels = new byte[width * height * 4];
            for (var j = 0; j + 2 < _indices.Length; j += 3)
            {
                var a = (int)_indices[j];
                var b = (int)_indices[j + 1];
                var c = (int)_indices[j + 2];
                if (a < 0 || b < 0 || c < 0 ||
                    a >= points.Length || b >= points.Length || c >= points.Length)
                    continue;
                RasterizeTriangle(pixels, width, height, points[a], points[b], points[c],
                    _sourceUv[a], _sourceUv[b], _sourceUv[c]);
            }

            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < height; y++)
                    Marshal.Copy(pixels, y * width * 4,
                        IntPtr.Add(data.Scan0, y * data.Stride), width * 4);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }

        private void RasterizeTriangle(byte[] target, int width, int height,
            PointF a, PointF b, PointF c, PointF ua, PointF ub, PointF uc)
        {
            var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (Math.Abs(area) < 0.001f)
                return;

            var left = Math.Clamp((int)MathF.Floor(Math.Min(a.X, Math.Min(b.X, c.X))), 0, width - 1);
            var right = Math.Clamp((int)MathF.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))), 0, width - 1);
            var top = Math.Clamp((int)MathF.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))), 0, height - 1);
            var bottom = Math.Clamp((int)MathF.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))), 0, height - 1);
            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    var px = x + 0.5f - a.X;
                    var py = y + 0.5f - a.Y;
                    var wb = (px * (c.Y - a.Y) - py * (c.X - a.X)) / area;
                    var wc = ((b.X - a.X) * py - (b.Y - a.Y) * px) / area;
                    var wa = 1f - wb - wc;
                    if (wa < -0.00001f || wb < -0.00001f || wc < -0.00001f)
                        continue;

                    var u = Math.Clamp(wa * ua.X + wb * ub.X + wc * uc.X, 0f, _textureWidth - 1f);
                    var v = Math.Clamp(wa * ua.Y + wb * ub.Y + wc * uc.Y, 0f, _textureHeight - 1f);
                    var tx = (int)u;
                    var ty = (int)v;
                    var fx = u - tx;
                    var fy = v - ty;
                    var tx1 = Math.Min(tx + 1, _textureWidth - 1);
                    var ty1 = Math.Min(ty + 1, _textureHeight - 1);
                    var i00 = (ty * _textureWidth + tx) * 4;
                    var i10 = (ty * _textureWidth + tx1) * 4;
                    var i01 = (ty1 * _textureWidth + tx) * 4;
                    var i11 = (ty1 * _textureWidth + tx1) * 4;
                    var dst = (y * width + x) * 4;
                    var source = _texturePixels!;
                    var alpha = Bilinear(source, i00, i10, i01, i11, 3, fx, fy);
                    if (alpha <= 0)
                        continue;

                    var oldAlpha = target[dst + 3];
                    var newAlpha = alpha + oldAlpha * (255 - alpha) / 255;
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var sample = Bilinear(source, i00, i10, i01, i11, channel, fx, fy);
                        target[dst + channel] = (byte)((sample * alpha +
                            target[dst + channel] * oldAlpha * (255 - alpha) / 255) /
                            Math.Max(1, newAlpha));
                    }
                    target[dst + 3] = (byte)newAlpha;
                }
            }
        }

        private static int Bilinear(byte[] pixels, int i00, int i10, int i01,
            int i11, int channel, float fx, float fy)
        {
            var top = pixels[i00 + channel] * (1f - fx) + pixels[i10 + channel] * fx;
            var bottom = pixels[i01 + channel] * (1f - fx) + pixels[i11 + channel] * fx;
            return Math.Clamp((int)(top * (1f - fy) + bottom * fy + 0.5f), 0, 255);
        }

        private int HitVertex(Point location)
        {
            var closest = -1;
            var distance = 14f * 14f;
            var positions = CurrentPositions();
            for (var i = 0; i < positions.Length; i++)
            {
                var p = WorldToScreen(positions[i]);
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
                var positions = CurrentPositions();
                var center = positions[index];
                var removed = false;
                for (var i = 0; i < positions.Length; i++)
                {
                    if (Distance(positions[i], center) < 0.0001f)
                        removed |= _part.VertexOffsets.Remove(i);
                }
                if (removed)
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
            var positionsAtStart = CurrentPositions();
            _startX = new float[positionsAtStart.Length];
            _startY = new float[positionsAtStart.Length];
            _falloff = new float[positionsAtStart.Length];
            _basisX = new PointF[positionsAtStart.Length];
            _basisY = new PointF[positionsAtStart.Length];
            var center = positionsAtStart[index];
            for (var i = 0; i < positionsAtStart.Length; i++)
            {
                _startX[i] = _part.VertexOffsets.TryGetValue(i, out var edit) ? edit.X : 0f;
                _startY[i] = edit?.Y ?? 0f;
                var distance = Distance(positionsAtStart[i], center);
                var influence = BrushRadius > 0f
                    ? Math.Clamp(1f - distance / BrushRadius, 0f, 1f)
                    : distance < 0.0001f ? 1f : 0f;
                _falloff[i] = influence * influence;
            }
            // The editor shows animated world positions. Find the two local
            // vertex axes numerically so dragging a handle moves it in the
            // indicated direction even under rotated/skinned bones.
            for (var i = 0; i < positionsAtStart.Length; i++)
            {
                if (_falloff[i] <= 0f)
                    continue;
                SetOffset(i, _startX[i] + 1f, _startY[i]);
                var xPosition = CurrentPositions()[i];
                SetOffset(i, _startX[i], _startY[i] + 1f);
                var yPosition = CurrentPositions()[i];
                SetOffset(i, _startX[i], _startY[i]);
                _basisX[i] = new PointF(xPosition.X - positionsAtStart[i].X,
                    xPosition.Y - positionsAtStart[i].Y);
                _basisY[i] = new PointF(yPosition.X - positionsAtStart[i].X,
                    yPosition.Y - positionsAtStart[i].Y);
            }
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragVertex < 0 || _startX is null || _startY is null ||
                _falloff is null || _basisX is null || _basisY is null)
                return;
            var dx = (e.X - _dragStart.X) / ViewScale;
            var dy = (_dragStart.Y - e.Y) / ViewScale;
            if (dx == 0f && dy == 0f)
                return;
            _dragged = true;
            for (var i = 0; i < _startX.Length; i++)
            {
                if (_falloff[i] <= 0f)
                    continue;
                var bx = _basisX[i];
                var by = _basisY[i];
                var det = bx.X * by.Y - bx.Y * by.X;
                if (Math.Abs(det) < 0.000001f)
                    continue;
                var localX = (dx * by.Y - dy * by.X) / det;
                var localY = (dy * bx.X - dx * bx.Y) / det;
                SetOffset(i, _startX[i] + localX * _falloff[i],
                    _startY[i] + localY * _falloff[i]);
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
            _basisX = _basisY = null;
            Capture = false;
        }

        private static float Distance(PointF a, PointF b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
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
