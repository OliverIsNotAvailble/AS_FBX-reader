using System.Drawing.Drawing2D;
using Assimp;
using AS.FBXReader.Core;
using AS.FBXReader.Models;

namespace AS.FBXReader;

// A temporary rig binding preview. Apply retains the edit in memory; Cancel
// restores the exact original binding. Save profile persists the chosen node.
public sealed class BoneFollowForm : Form
{
    private sealed record Choice(string? Name, string Caption, int Mode)
    {
        public override string ToString() => Caption;
    }

    private readonly ScenePart _part;
    private readonly AnimationPlayer _player;
    private readonly Func<PointF[]> _worldPositions;
    private readonly Action _previewChanged;
    private readonly bool _originalPreserve;
    private readonly string? _originalBone;
    private readonly List<Choice> _choices = new();
    private readonly TextBox _search = new();
    private readonly ListBox _bones = new();
    private readonly Label _selection = new();
    private readonly Label _timeLabel = new();
    private readonly TrackBar _timeline = new();
    private readonly RigCanvas _rig;
    private readonly Button _apply;
    private readonly Node _meshNode;
    private bool _refreshing;

    public BoneFollowForm(FbxSession session, AnimationPlayer player, ScenePart part,
        Func<PointF[]> worldPositions, Action previewChanged)
    {
        if (session.Scene is null || session.Scene.RootNode is null)
            throw new InvalidOperationException("Open an FBX before editing its rig.");

        _part = part;
        _player = player;
        _worldPositions = worldPositions;
        _previewChanged = previewChanged;
        _originalPreserve = part.PreserveShape;
        _originalBone = part.FollowBoneName;
        var mesh = session.Scene.Meshes[part.MeshIndex];
        _meshNode = session.NodesByName[part.NodeName];

        Text = $"Edit bone binding — {part.DisplayName}";
        StartPosition = FormStartPosition.Manual;
        Size = new Size(850, 730);
        MinimumSize = new Size(780, 570);
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 10f);

        var usage = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var source in session.Parts)
        {
            var sourceMesh = session.Scene.Meshes[source.MeshIndex];
            foreach (var bone in sourceMesh.Bones)
                AddUsage(bone.Name, source.Alias ?? source.Name);
            AddUsage(source.NodeName, source.Alias ?? source.Name);
        }

        void AddUsage(string name, string sourceName)
        {
            if (!session.NodesByName.ContainsKey(name))
                return;
            if (!usage.TryGetValue(name, out var names))
                usage[name] = names = new HashSet<string>(StringComparer.Ordinal);
            names.Add(sourceName);
        }

        _choices.Add(new Choice(null, "Original skin — original weights for every vertex", 0));
        _choices.Add(new Choice(null, "Automatic — follow this mesh's strongest bone", 1));
        foreach (var (name, sourceNames) in usage.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sources = string.Join(", ", sourceNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(4));
            if (sourceNames.Count > 4)
                sources += $" (+{sourceNames.Count - 4})";
            _choices.Add(new Choice(name, $"{name}   •   {sources}", 2));
        }

        var weights = mesh.Bones
            .Select(b => (b.Name, Weight: b.VertexWeights.Sum(v => (double)v.Weight)))
            .OrderByDescending(b => b.Weight).ToList();
        var weightTotal = weights.Sum(b => b.Weight);
        var strongest = weights.FirstOrDefault().Name;
        var originalSummary = weights.Count == 0
            ? "This mesh has no original skin weights. You can still follow a rig node."
            : "Original bones / summed weights: " + string.Join("  |  ", weights.Take(8)
                .Select(b => $"{b.Name} {b.Weight / Math.Max(weightTotal, 0.00001):P0}")) +
              (weights.Count > 8 ? $"  (+{weights.Count - 8} more)" : "");

        _rig = new RigCanvas(session.Scene.RootNode, player, mesh,
            worldPositions, usage.Keys, strongest) { Dock = DockStyle.Fill };

        var header = new Label
        {
            Text = $"Mesh: {part.DisplayName}\n{originalSummary}\n" +
                   "Choose a rig node to test it live. The mesh keeps its rest shape; the FBX weights stay intact.",
            AutoSize = true,
            MaximumSize = new Size(790, 0),
            Padding = new Padding(12, 10, 12, 6),
            Dock = DockStyle.Top
        };

        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var searchTitle = new Label
        {
            Text = "Find a bone or a mesh (for example: face)",
            Dock = DockStyle.Top, AutoSize = true
        };
        _search.Dock = DockStyle.Top;
        _search.PlaceholderText = "Bone name or mesh name";
        _search.TextChanged += (_, _) => RefreshChoices();
        _bones.Dock = DockStyle.Fill;
        _bones.HorizontalScrollbar = true;
        _bones.SelectedIndexChanged += (_, _) => PreviewChoice();
        _selection.Dock = DockStyle.Bottom;
        _selection.AutoSize = true;
        _selection.MaximumSize = new Size(360, 0);
        _selection.Padding = new Padding(0, 8, 0, 4);
        left.Controls.Add(_bones);
        left.Controls.Add(_selection);
        left.Controls.Add(_search);
        left.Controls.Add(searchTitle);

        var right = new Panel { Dock = DockStyle.Fill };
        var fit = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(5)
        };
        var fitMesh = new Button { Text = "Fit mesh", AutoSize = true };
        fitMesh.Click += (_, _) => _rig.Fit(includeRig: false);
        var fitRig = new Button { Text = "Fit mesh + rig", AutoSize = true };
        fitRig.Click += (_, _) => _rig.Fit(includeRig: true);
        fit.Controls.Add(fitMesh);
        fit.Controls.Add(fitRig);
        fit.Controls.Add(new Label
        {
            Text = "Wheel: zoom  •  Drag: pan  •  Red: selected  •  Yellow: original main bone",
            AutoSize = true, Padding = new Padding(8, 6, 0, 0)
        });
        right.Controls.Add(_rig);
        right.Controls.Add(fit);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 260,
            Panel2MinSize = 300,
            SplitterWidth = 6
        };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        split.Resize += (_, _) =>
        {
            if (split.Width > 700 && split.SplitterDistance < 260)
                split.SplitterDistance = Math.Min(360, split.Width - split.Panel2MinSize - split.SplitterWidth);
        };

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(10, 7, 10, 7)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _timeLabel.AutoSize = true;
        _timeLabel.Anchor = AnchorStyles.Left;
        _timeLabel.Text = "Pose: 0.00s";
        _timeline.Minimum = 0;
        _timeline.Maximum = 1000;
        _timeline.TickStyle = TickStyle.None;
        _timeline.Dock = DockStyle.Fill;
        _timeline.Enabled = player.DurationSeconds > 0;
        _timeline.Value = player.DurationSeconds > 0
            ? Math.Clamp((int)Math.Round(player.TimeSeconds / player.DurationSeconds * 1000), 0, 1000)
            : 0;
        _timeline.ValueChanged += (_, _) =>
        {
            _player.SetTime(_timeline.Value / 1000.0 * _player.DurationSeconds);
            _timeLabel.Text = $"Pose: {_player.TimeSeconds:0.00}s";
            _rig.Invalidate();
            _previewChanged();
            PreviewChoice();
        };
        _timeLabel.Text = $"Pose: {player.TimeSeconds:0.00}s";

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        _apply = new Button
        {
            Text = "Apply", AutoSize = true, MinimumSize = new Size(90, 0),
            DialogResult = DialogResult.OK
        };
        var cancel = new Button
        {
            Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 0),
            DialogResult = DialogResult.Cancel
        };
        actions.Controls.Add(_apply);
        actions.Controls.Add(cancel);
        footer.Controls.Add(_timeLabel, 0, 0);
        footer.Controls.Add(_timeline, 1, 0);
        footer.Controls.Add(actions, 2, 0);

        Controls.Add(split);
        Controls.Add(footer);
        Controls.Add(header);
        Shown += (_, _) =>
        {
            if (Owner is not null)
            {
                var area = Screen.FromControl(Owner).WorkingArea;
                Location = new Point(
                    Math.Clamp(Owner.Left + 18, area.Left, Math.Max(area.Left, area.Right - Width)),
                    Math.Clamp(Owner.Top + 45, area.Top, Math.Max(area.Top, area.Bottom - Height)));
            }
            split.SplitterDistance = Math.Min(320,
                Math.Max(split.Panel1MinSize, split.Width - split.Panel2MinSize - split.SplitterWidth));
        };
        AcceptButton = _apply;
        CancelButton = cancel;
        FormClosing += (_, _) =>
        {
            if (DialogResult == DialogResult.OK)
                return;
            _part.PreserveShape = _originalPreserve;
            _part.FollowBoneName = _originalBone;
            _previewChanged();
        };

        RefreshChoices();
    }

    private void RefreshChoices()
    {
        _refreshing = true;
        try
        {
            _bones.BeginUpdate();
            _bones.Items.Clear();
            foreach (var choice in _choices.Where(c =>
                c.Mode != 2 || c.Caption.Contains(_search.Text, StringComparison.OrdinalIgnoreCase)))
                _bones.Items.Add(choice);
            _bones.EndUpdate();
            var active = _choices.FirstOrDefault(c => c.Mode == 2 &&
                _part.PreserveShape && c.Name == _part.FollowBoneName)
                ?? _choices[_part.PreserveShape ? 1 : 0];
            if (_bones.Items.Contains(active))
                _bones.SelectedItem = active;
            else
                _selection.Text = "Filter the list and select a bone to preview it.";
        }
        finally
        {
            _refreshing = false;
        }
        if (_bones.SelectedItem is Choice)
            PreviewChoice();
    }

    private void PreviewChoice()
    {
        if (_refreshing || _bones.SelectedItem is not Choice choice)
            return;
        _part.PreserveShape = choice.Mode != 0;
        _part.FollowBoneName = choice.Mode == 2 ? choice.Name : null;
        _selection.Text = choice.Mode switch
        {
            0 => "Current: original vertex skinning. Apply, then Save profile.",
            1 => "Current: rigid shape following the mesh's strongest bone. Apply, then Save profile.",
            _ => $"Current: following {choice.Name}. Apply, then Save profile."
        };
        _apply.Enabled = choice.Mode != 2 ||
            _player.GetFollowBoneMatrix(_meshNode, choice.Name!) is not null;
        if (!_apply.Enabled)
            _selection.Text = $"Cannot follow {choice.Name} at this pose: its rig transform is invalid.";
        _rig.SelectedBone = _part.FollowBoneName;
        _rig.Invalidate();
        _previewChanged();
    }

    private sealed class RigCanvas : Control
    {
        private readonly AnimationPlayer _player;
        private readonly Func<PointF[]> _positions;
        private readonly int[] _indices;
        private readonly Dictionary<string, string?> _parents = new(StringComparer.Ordinal);
        private readonly HashSet<string> _names;
        private readonly string? _originalBone;
        private float _zoom = 1f;
        private PointF _pan;
        private bool _dragging;
        private Point _dragStart;
        private PointF _panStart;
        private bool _fitRig;
        public string? SelectedBone { get; set; }

        public RigCanvas(Node root, AnimationPlayer player, Mesh mesh,
            Func<PointF[]> positions, IEnumerable<string> names, string? originalBone)
        {
            _player = player;
            _positions = positions;
            _indices = mesh.GetUnsignedIndices().Select(x => (int)x).ToArray();
            _names = names.ToHashSet(StringComparer.Ordinal);
            _originalBone = originalBone;
            BackColor = Color.FromArgb(28, 28, 28);
            DoubleBuffered = true;
            ResizeRedraw = true;
            Cursor = Cursors.Hand;
            Collect(root, null);
        }

        private void Collect(Node node, string? parent)
        {
            _parents[node.Name] = parent;
            foreach (var child in node.Children)
                Collect(child, node.Name);
        }

        public void Fit(bool includeRig)
        {
            _fitRig = includeRig;
            _zoom = 1f;
            _pan = PointF.Empty;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var mesh = _positions();
            var validMesh = mesh.Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y)).ToArray();
            if (validMesh.Length == 0)
            {
                TextRenderer.DrawText(g, "This mesh has no visible vertices at this pose.", Font,
                    ClientRectangle, Color.White);
                return;
            }

            var joints = new Dictionary<string, PointF>(StringComparer.Ordinal);
            foreach (var name in _names)
            {
                var m = _player.GetGlobalTransform(name);
                if (float.IsFinite(m.M41) && float.IsFinite(m.M42))
                    joints[name] = new PointF(m.M41, m.M42);
            }
            var bounds = _fitRig ? validMesh.Concat(joints.Values).ToArray() : validMesh;
            var minX = bounds.Min(p => p.X);
            var maxX = bounds.Max(p => p.X);
            var minY = bounds.Min(p => p.Y);
            var maxY = bounds.Max(p => p.Y);
            var span = Math.Max(0.01f, Math.Max(maxX - minX, maxY - minY));
            var scale = Math.Min(Math.Max(1, Width - 70), Math.Max(1, Height - 70)) /
                (span * 1.25f) * _zoom;
            var cx = (minX + maxX) * 0.5f;
            var cy = (minY + maxY) * 0.5f;
            PointF Project(PointF p) => new(
                Width * 0.5f + (p.X - cx) * scale + _pan.X,
                Height * 0.5f - (p.Y - cy) * scale + _pan.Y);

            using var edgePen = new Pen(Color.FromArgb(95, 180, 220, 240), 1f);
            for (var i = 0; i + 2 < _indices.Length; i += 3)
            {
                var a = (int)_indices[i];
                var b = (int)_indices[i + 1];
                var c = (int)_indices[i + 2];
                if (a < 0 || b < 0 || c < 0 || a >= mesh.Length || b >= mesh.Length || c >= mesh.Length ||
                    !float.IsFinite(mesh[a].X) || !float.IsFinite(mesh[a].Y) ||
                    !float.IsFinite(mesh[b].X) || !float.IsFinite(mesh[b].Y) ||
                    !float.IsFinite(mesh[c].X) || !float.IsFinite(mesh[c].Y))
                    continue;
                g.DrawPolygon(edgePen, new[] { Project(mesh[a]), Project(mesh[b]), Project(mesh[c]) });
            }

            using var linkPen = new Pen(Color.FromArgb(130, 100, 210, 180), 2f);
            foreach (var (name, joint) in joints)
            {
                if (_parents.TryGetValue(name, out var parent) && parent is not null &&
                    joints.TryGetValue(parent, out var parentJoint))
                    g.DrawLine(linkPen, Project(parentJoint), Project(joint));
            }
            foreach (var (name, joint) in joints)
            {
                var selected = name == SelectedBone;
                var original = name == _originalBone;
                var point = Project(joint);
                var radius = selected ? 7f : original ? 5f : 3f;
                using var brush = new SolidBrush(selected ? Color.OrangeRed :
                    original ? Color.Gold : Color.MediumAquamarine);
                g.FillEllipse(brush, point.X - radius, point.Y - radius, radius * 2, radius * 2);
                if (selected || original)
                    TextRenderer.DrawText(g, name, Font,
                        Point.Round(new PointF(point.X + 9, point.Y - 10)), brush.Color);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
                return;
            _dragging = true;
            _dragStart = e.Location;
            _panStart = _pan;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging)
                return;
            _pan = new PointF(_panStart.X + e.X - _dragStart.X,
                _panStart.Y + e.Y - _dragStart.Y);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var factor = (float)Math.Pow(1.12, e.Delta / 120.0);
            var next = Math.Clamp(_zoom * factor, 0.05f, 50f);
            var applied = next / _zoom;
            _pan = new PointF((e.X - Width * 0.5f) * (1 - applied) + _pan.X * applied,
                (e.Y - Height * 0.5f) * (1 - applied) + _pan.Y * applied);
            _zoom = next;
            Invalidate();
        }
    }
}
