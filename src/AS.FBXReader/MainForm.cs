using AS.FBXReader.Core;
using AS.FBXReader.Export;
using AS.FBXReader.Models;
using AS.FBXReader.Rendering;

namespace AS.FBXReader;

public sealed class MainForm : Form
{
    private readonly FbxSession _session = new();
    private readonly AnimationPlayer _player;
    private readonly ViewerControl _viewer = new();

    // V0.1.6: real hierarchical organizer instead of a flat CheckedListBox.
    // Tree order is authoritative for painter/layer order: TOP = FRONT.
    private readonly TreeView _parts = new();

    private readonly ComboBox _animations = new();
    private readonly TrackBar _timeline = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };

    private DateTime _lastTick = DateTime.UtcNow;
    private DateTime _lastDragScroll = DateTime.MinValue;
    private TreeNode? _dragNode;
    private TreeNode? _dropTarget;
    private DropPlacement _dropPlacement;
    private bool _updatingTree;
    private ImageList? _checkStateImages;
    private Font? _groupFont;
    private ContextMenuStrip? _partsMenu;

    private const int StateUnchecked = 0;
    private const int StateChecked = 1;
    private const int StateMixed = 2;

    private enum DropPlacement { None, Before, After, Inside }

    public MainForm()
    {
        _player = new AnimationPlayer(_session);

        Text = "AS_FBX-reader 0.2.1";
        Width = 1500;
        Height = 900;
        MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 11F);

        _groupFont = new Font(Font, FontStyle.Bold);

        BuildUi();
        HookEvents();
        _timer.Start();
    }

    private void BuildUi()
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 9, 8, 7),
            WrapContents = true
        };

        var open = Button("Open FBX", (_, _) => OpenFbx());
        var play = Button("Play", (_, _) => _player.Playing = true);
        var pause = Button("Pause", (_, _) => _player.Playing = false);
        var rest = Button("Rest", (_, _) =>
        {
            _player.SelectAnimation(-1);
            _animations.SelectedIndex = -1;
            _viewer.InvalidateScene();
        });
        var saveProfile = Button("Save profile", (_, _) => SaveProfile());
        var loadProfile = Button("Load profile", (_, _) => LoadProfile());
        var export = Button("Export / Framing", (_, _) => OpenExportDialog());
        var freeCamera = new CheckBox
        {
            Text = "Free camera",
            AutoSize = true,
            Padding = new Padding(8, 9, 6, 0),
            Margin = new Padding(6, 3, 3, 3)
        };
        freeCamera.CheckedChanged += (_, _) => _viewer.FreeCameraEnabled = freeCamera.Checked;

        var pma = new CheckBox
        {
            Text = "PMA",
            Checked = true,
            AutoSize = true,
            Padding = new Padding(8, 9, 6, 0),
            Margin = new Padding(6, 3, 3, 3)
        };
        pma.CheckedChanged += (_, _) =>
        {
            _viewer.SetPremultiplyAlpha(pma.Checked);
            _status.Text = pma.Checked
                ? "Alpha mode: Premultiplied Alpha (PMA)"
                : "Alpha mode: Straight Alpha";
        };

        _animations.DropDownStyle = ComboBoxStyle.DropDownList;
        _animations.Width = 240;
        _animations.Margin = new Padding(5);

        toolbar.Controls.AddRange([
            open,
            new Label
            {
                Text = "Animation:",
                AutoSize = true,
                Padding = new Padding(8, 10, 2, 0),
                Margin = new Padding(3)
            },
            _animations,
            play,
            pause,
            rest,
            saveProfile,
            loadProfile,
            export,
            freeCamera,
            pma,
            Button("Allow all", (_, _) => SetAll(true)),
            Button("Hide all", (_, _) => SetAll(false)),
            Button("Force all", (_, _) => ForceAll()),
            Button("Solo", (_, _) => SoloSelected()),
            Button("New group", (_, _) => CreateRootGroup())
        ]);
        toolbar.Controls.Add(Button("Fit view", (_, _) => _viewer.ResetFraming()));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1
        };
        Shown += (_, _) => BeginInvoke(new Action(() =>
            UiLayout.OpenSidebar(split, DeviceDpi)));

        ConfigurePartsTree();

        split.Panel1.Controls.Add(_parts);
        split.Panel2.Controls.Add(_viewer);

        _timeline.Dock = DockStyle.Bottom;
        _timeline.AutoSize = true;
        _timeline.Minimum = 0;
        _timeline.Maximum = 1000;
        _timeline.TickStyle = TickStyle.None;

        _status.Dock = DockStyle.Bottom;
        _status.AutoSize = true;
        _status.Padding = new Padding(6, 5, 0, 0);
        _status.Text = "Open an FBX exported by AssetStudio.";

        Controls.Add(split);
        Controls.Add(_timeline);
        Controls.Add(_status);
        Controls.Add(toolbar);
    }

    private void ConfigurePartsTree()
    {
        // The native TreeView checkboxes stay tiny on high-DPI displays.
        // Use custom state images sized from the actual font height instead.
        var checkSize = Math.Max(24, (int)Math.Ceiling(Font.Height * 1.35));
        _checkStateImages = CreateCheckStateImages(checkSize);

        _parts.Dock = DockStyle.Fill;
        _parts.HideSelection = false;
        _parts.FullRowSelect = false;
        _parts.ShowLines = true;
        _parts.ShowPlusMinus = true;
        _parts.ShowRootLines = true;
        _parts.AllowDrop = true;
        _parts.LabelEdit = true;
        _parts.DrawMode = TreeViewDrawMode.OwnerDrawText;
        _parts.StateImageList = _checkStateImages;
        _parts.ItemHeight = Math.Max(checkSize + 8, Font.Height + 12);
        _parts.Indent = Math.Max(checkSize + 10, 32);

        _partsMenu = new ContextMenuStrip
        {
            Font = Font
        };
        _partsMenu.Opening += (_, _) => BuildPartsContextMenu();
        _parts.ContextMenuStrip = _partsMenu;
    }

    private ImageList CreateCheckStateImages(int size)
    {
        var images = new ImageList
        {
            ImageSize = new Size(size, size),
            ColorDepth = ColorDepth.Depth32Bit
        };

        images.Images.Add(DrawCheckState(size, StateUnchecked));
        images.Images.Add(DrawCheckState(size, StateChecked));
        images.Images.Add(DrawCheckState(size, StateMixed));
        return images;
    }

    private static Bitmap DrawCheckState(int size, int state)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var pad = Math.Max(3, size / 7);
        var rect = new Rectangle(
            pad,
            pad,
            size - pad * 2 - 1,
            size - pad * 2 - 1);

        using var fill = new SolidBrush(Color.White);
        using var border = new Pen(Color.FromArgb(90, 90, 90), Math.Max(2f, size / 14f));

        g.FillRectangle(fill, rect);
        g.DrawRectangle(border, rect);

        if (state == StateChecked)
        {
            using var pen = new Pen(Color.FromArgb(25, 105, 190), Math.Max(2.5f, size / 9f))
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };

            var p1 = new PointF(rect.Left + rect.Width * 0.20f, rect.Top + rect.Height * 0.53f);
            var p2 = new PointF(rect.Left + rect.Width * 0.43f, rect.Top + rect.Height * 0.76f);
            var p3 = new PointF(rect.Left + rect.Width * 0.82f, rect.Top + rect.Height * 0.27f);
            g.DrawLines(pen, [p1, p2, p3]);
        }
        else if (state == StateMixed)
        {
            using var mixed = new SolidBrush(Color.FromArgb(25, 105, 190));
            var barHeight = Math.Max(3, rect.Height / 5);
            g.FillRectangle(
                mixed,
                rect.Left + rect.Width / 5,
                rect.Top + (rect.Height - barHeight) / 2,
                rect.Width * 3 / 5,
                barHeight);
        }

        return bmp;
    }

    private void HookEvents()
    {
        _parts.DrawNode += (_, e) =>
        {
            if (e.Node != _dropTarget ||
                _dropPlacement is not (DropPlacement.Before or DropPlacement.After))
            {
                e.DrawDefault = true;
                return;
            }

            // Draw the target text ourselves so WinForms does not erase the
            // insertion line with its default text painting afterwards.
            var selected = (e.State & TreeNodeStates.Selected) != 0;
            var back = selected ? SystemColors.Highlight : _parts.BackColor;
            var fore = selected ? SystemColors.HighlightText : _parts.ForeColor;
            using (var brush = new SolidBrush(back))
                e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(
                e.Graphics, e.Node.Text, e.Node.NodeFont ?? _parts.Font,
                e.Bounds, fore,
                TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);

            var y = _dropPlacement == DropPlacement.Before
                ? e.Node.Bounds.Top + 1
                : e.Node.Bounds.Bottom - 2;
            using var pen = new Pen(Color.FromArgb(25, 105, 190), 3);
            e.Graphics.DrawLine(pen, 2, y, _parts.ClientSize.Width - 4, y);
        };

        _parts.NodeMouseClick += (_, e) =>
        {
            _parts.SelectedNode = e.Node;

            if (e.Button == MouseButtons.Left && IsStateImageClick(e.Node, e.Location))
            {
                ToggleNode(e.Node);
            }
        };

        _parts.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left &&
                e.Node.Tag is ScenePart &&
                !IsStateImageClick(e.Node, e.Location))
                BeginRenamePart(e.Node);
        };

        _parts.BeforeLabelEdit += (_, e) =>
        {
            if (e.Node?.Tag is not ScenePart)
                e.CancelEdit = true;
        };

        _parts.AfterLabelEdit += (_, e) =>
        {
            // Always restore the display suffixes after editing the short alias.
            e.CancelEdit = true;
            var node = e.Node;
            if (node?.Tag is not ScenePart part)
                return;

            if (e.Label is not null)
            {
                var name = e.Label.Trim();
                part.Alias = name.Length == 0 || name == part.Name ? null : name;
                _status.Text = part.Alias is null
                    ? $"Alias cleared: {part.Name}"
                    : $"Alias: {part.Alias} <- {part.Name}";
            }

            _parts.BeginInvoke(new Action(() =>
            {
                if (node.TreeView == _parts)
                    node.Text = part.DisplayName;
            }));
        };

        _parts.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F2 && _parts.SelectedNode is { Tag: ScenePart } node)
            {
                BeginRenamePart(node);
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.Space && _parts.SelectedNode != null)
            {
                ToggleNode(_parts.SelectedNode);
                e.Handled = true;
            }
        };

        _parts.ItemDrag += (_, e) =>
        {
            if (e.Item is not TreeNode node)
                return;

            _dragNode = node;
            try
            {
                _parts.DoDragDrop(node, DragDropEffects.Move);
            }
            finally
            {
                _dragNode = null;
                ClearDropIndicator();
            }
        };

        _parts.DragOver += (_, e) =>
        {
            if (_dragNode is null ||
                !e.Data!.GetDataPresent(typeof(TreeNode)))
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            var client = _parts.PointToClient(new Point(e.X, e.Y));
            ScrollTreeDuringDrag(client.Y);
            var (target, placement) = GetDropLocation(client);

            if (target == null || IsNodeInside(_dragNode, target))
            {
                e.Effect = DragDropEffects.None;
                ClearDropIndicator();
                return;
            }

            SetDropIndicator(target, placement);
            e.Effect = DragDropEffects.Move;
        };

        _parts.DragLeave += (_, _) => ClearDropIndicator();

        _parts.DragDrop += (_, e) =>
        {
            if (_dragNode is null)
                return;

            var source = _dragNode;
            var client = _parts.PointToClient(new Point(e.X, e.Y));
            var (target, placement) = GetDropLocation(client);
            ClearDropIndicator();

            if (target == null || IsNodeInside(source, target))
                return;

            MoveTreeNode(source, target, placement);
        };

        _parts.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right)
                return;

            var node = _parts.GetNodeAt(e.Location);
            if (node != null)
                _parts.SelectedNode = node;
        };

        _viewer.PartPicked += (_, part) =>
        {
            var node = FindPartNode(part);
            if (node == null)
                return;

            _parts.SelectedNode = node;
            node.EnsureVisible();
            _status.Text = $"Selected from preview: {part.DisplayName}";
        };
        _viewer.PartMoved += (_, part) =>
        {
            _status.Text = $"Moved {part.DisplayName}: X {part.OffsetX:0.###}, Y {part.OffsetY:0.###}. Save profile to keep it.";
        };

        _animations.SelectedIndexChanged += (_, _) =>
        {
            if (_animations.SelectedItem is AnimationTake take)
            {
                _player.SelectAnimation(take.Index);
                _timeline.Value = 0;
                _viewer.InvalidateScene();
            }
        };

        _timeline.Scroll += (_, _) =>
        {
            if (_player.DurationSeconds <= 0)
                return;

            _player.SetTime(_timeline.Value / 1000.0 * _player.DurationSeconds);
            _viewer.InvalidateScene();
        };

        _timer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            var delta = (now - _lastTick).TotalSeconds;
            _lastTick = now;

            if (!_player.Playing)
                return;

            _player.Update(delta);
            if (_player.DurationSeconds > 0)
            {
                _timeline.Value = Math.Clamp(
                    (int)(_player.TimeSeconds / _player.DurationSeconds * 1000.0),
                    0,
                    1000);
            }

            _viewer.InvalidateScene();
        };
    }

    private bool IsStateImageClick(TreeNode node, Point point)
    {
        if (_parts.StateImageList is null)
            return false;

        var width = _parts.StateImageList.ImageSize.Width;

        // TreeNode.Bounds starts around the text/image area; the state icon sits
        // immediately to the left. Give it a generous hitbox for high-DPI use.
        var right = node.Bounds.Left + 2;
        var left = right - width - 8;

        return point.X >= left &&
               point.X <= right &&
               point.Y >= node.Bounds.Top &&
               point.Y <= node.Bounds.Bottom;
    }

    private void ToggleNode(TreeNode node)
    {
        if (_updatingTree)
            return;

        var makeVisible = node.StateImageIndex != StateChecked;
        SetNodeVisibilityRecursive(node, makeVisible);
        RefreshGroupStates();
        _viewer.InvalidateScene();

        _status.Text = node.Tag is PartGroup group
            ? $"{group.Name}: {(makeVisible ? "allowed" : "hidden")} ({CountPartNodes(node)} parts)"
            : $"{GetNodeDisplayName(node)}: {(makeVisible ? "allowed (FBX controls timing)" : "hidden")}";
    }

    private void SetNodeVisibilityRecursive(TreeNode node, bool visible)
    {
        if (node.Tag is ScenePart part)
        {
            part.Visible = visible;
            node.StateImageIndex = visible ? StateChecked : StateUnchecked;
            return;
        }

        foreach (TreeNode child in node.Nodes)
            SetNodeVisibilityRecursive(child, visible);

        node.StateImageIndex = visible ? StateChecked : StateUnchecked;
    }

    private void RefreshGroupStates()
    {
        _updatingTree = true;
        try
        {
            foreach (TreeNode root in _parts.Nodes)
                RefreshNodeState(root);
        }
        finally
        {
            _updatingTree = false;
        }
    }

    private int RefreshNodeState(TreeNode node)
    {
        if (node.Tag is ScenePart part)
        {
            node.StateImageIndex = part.Visible ? StateChecked : StateUnchecked;
            return node.StateImageIndex;
        }

        if (node.Nodes.Count == 0)
        {
            node.StateImageIndex = StateUnchecked;
            return StateUnchecked;
        }

        var checkedCount = 0;
        var uncheckedCount = 0;

        foreach (TreeNode child in node.Nodes)
        {
            var state = RefreshNodeState(child);

            if (state == StateChecked)
                checkedCount++;
            else if (state == StateUnchecked)
                uncheckedCount++;
            else
            {
                checkedCount++;
                uncheckedCount++;
            }
        }

        node.StateImageIndex =
            uncheckedCount == 0 ? StateChecked :
            checkedCount == 0 ? StateUnchecked :
            StateMixed;

        return node.StateImageIndex;
    }

    private void OpenFbx()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "FBX files (*.fbx)|*.fbx|All files (*.*)|*.*",
            Title = "Open AssetStudio FBX"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            Cursor = Cursors.WaitCursor;

            _session.Open(dialog.FileName);
            _player.Rebuild();
            _viewer.Attach(_session, _player);

            BuildDefaultPartTree();

            _animations.Items.Clear();
            foreach (var animation in _session.Animations)
                _animations.Items.Add(animation);

            if (_animations.Items.Count > 0)
                _animations.SelectedIndex = 0;

            var duplicateNote = _session.DuplicateModelNamesFixed > 0
                ? $" | {_session.DuplicateModelNamesFixed} duplicate FBX Model names isolated"
                : string.Empty;
            var bindNote = _player.RecoveredBoneOffsets > 0
                ? $" | {_player.RecoveredBoneOffsets} broken skin binds recovered"
                : string.Empty;
            var scaleNote = _player.RestoredCollapsedMeshes > 0
                ? $" | {_player.RestoredCollapsedMeshes} zero-scale meshes restored"
                : string.Empty;

            _status.Text =
                $"{Path.GetFileName(dialog.FileName)} | {_session.Parts.Count} parts | " +
                $"{_session.Animations.Count} animations{duplicateNote}{scaleNote}{bindNote} | drag parts into groups";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.ToString(),
                "Open FBX failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void BuildDefaultPartTree()
    {
        _updatingTree = true;
        try
        {
            _parts.BeginUpdate();
            _parts.Nodes.Clear();

            foreach (var part in _session.Parts)
                _parts.Nodes.Add(CreatePartNode(part));
        }
        finally
        {
            _parts.EndUpdate();
            _updatingTree = false;
        }

        RefreshGroupStates();
    }

    private TreeNode CreatePartNode(ScenePart part)
        => new(part.DisplayName)
        {
            Tag = part,
            StateImageIndex = part.Visible ? StateChecked : StateUnchecked
        };

    private TreeNode CreateGroupNode(PartGroup group)
        => new($"📁 {group.Name}")
        {
            Tag = group,
            StateImageIndex = StateUnchecked,
            NodeFont = _groupFont
        };

    private void SetAll(bool visible)
    {
        foreach (var part in _session.Parts)
        {
            part.Visible = visible;
            if (visible)
                part.ForceVisible = false;
        }

        RefreshGroupStates();
        RefreshPartNodeTexts();
        _viewer.InvalidateScene();
        _status.Text = visible
            ? "All parts allowed; FBX timing restored."
            : "All parts hidden.";
    }

    private void ForceAll()
    {
        foreach (var part in _session.Parts)
        {
            part.Visible = true;
            part.ForceVisible = true;
        }

        RefreshGroupStates();
        RefreshPartNodeTexts();
        _viewer.InvalidateScene();
        _status.Text = "All parts forced visible; Allow all restores FBX timing.";
    }

    private void SoloSelected()
    {
        var selected = _parts.SelectedNode;
        if (selected == null)
            return;

        var selectedParts = EnumeratePartNodes(selected)
            .Select(x => (ScenePart)x.Tag!)
            .ToHashSet();

        if (selected.Tag is ScenePart selectedPart)
        {
            selectedParts.Clear();
            selectedParts.Add(selectedPart);
            selectedPart.ForceVisible = true;
        }

        foreach (var part in _session.Parts)
            part.Visible = selectedParts.Contains(part);

        RefreshGroupStates();
        RefreshPartNodeTexts();
        _viewer.InvalidateScene();
    }

    private void CreateRootGroup()
    {
        var name = PromptForText(
            "New group",
            "Group name:",
            "new group");

        if (string.IsNullOrWhiteSpace(name))
            return;

        var node = CreateGroupNode(new PartGroup
        {
            Name = name.Trim()
        });

        _parts.Nodes.Add(node);
        _parts.SelectedNode = node;
        node.EnsureVisible();
        RefreshGroupStates();

        _status.Text = $"Created group: {name}. Drag parts onto the folder.";
    }

    private void CreateSubgroup(TreeNode parent)
    {
        if (parent.Tag is not PartGroup)
            return;

        var name = PromptForText(
            "New subgroup",
            "Subgroup name:",
            "new subgroup");

        if (string.IsNullOrWhiteSpace(name))
            return;

        var node = CreateGroupNode(new PartGroup
        {
            Name = name.Trim()
        });

        parent.Nodes.Add(node);
        parent.Expand();
        _parts.SelectedNode = node;
        RefreshGroupStates();
    }

    private void RenameGroup(TreeNode node)
    {
        if (node.Tag is not PartGroup group)
            return;

        var name = PromptForText(
            "Rename group",
            "Group name:",
            group.Name);

        if (string.IsNullOrWhiteSpace(name))
            return;

        group.Name = name.Trim();
        node.Text = $"📁 {group.Name}";
        _status.Text = $"Group renamed: {group.Name}";
    }

    private void DeleteGroupKeepContents(TreeNode node)
    {
        if (node.Tag is not PartGroup)
            return;

        var parentCollection = node.Parent?.Nodes ?? _parts.Nodes;
        var insertAt = node.Index;

        var children = node.Nodes
            .Cast<TreeNode>()
            .ToArray();

        node.Nodes.Clear();
        node.Remove();

        foreach (var child in children)
            parentCollection.Insert(insertAt++, child);

        SyncPartOrderFromTree();
        RefreshGroupStates();
        _viewer.InvalidateScene();
    }

    private (TreeNode? Target, DropPlacement Placement) GetDropLocation(Point client)
    {
        if (_parts.Nodes.Count == 0)
            return (null, DropPlacement.None);

        var target = _parts.GetNodeAt(client);
        if (target == null)
        {
            // Win32 hit testing can miss a row when the pointer is left/right
            // of its label. Use the visible row's Y coordinate instead.
            for (var row = _parts.TopNode; row != null; row = row.NextVisibleNode)
            {
                if (client.Y >= row.Bounds.Top && client.Y < row.Bounds.Bottom)
                {
                    target = row;
                    break;
                }
                if (row.Bounds.Top > _parts.ClientSize.Height)
                    break;
            }
        }

        if (target == null)
        {
            if (client.Y <= (_parts.TopNode?.Bounds.Top ?? 0))
                return (_parts.Nodes[0], DropPlacement.Before);

            if (client.Y >= 0 && client.Y < _parts.ClientSize.Height)
                return (_parts.Nodes[_parts.Nodes.Count - 1], DropPlacement.After);

            return (null, DropPlacement.None);
        }

        var bounds = target.Bounds;
        if (target.Tag is PartGroup)
        {
            var edge = Math.Max(5, bounds.Height / 4);
            if (client.Y < bounds.Top + edge)
                return (target, DropPlacement.Before);
            if (client.Y >= bounds.Bottom - edge)
                return (target, DropPlacement.After);
            return (target, DropPlacement.Inside);
        }

        return (target, client.Y < bounds.Top + bounds.Height / 2
            ? DropPlacement.Before
            : DropPlacement.After);
    }

    private void ScrollTreeDuringDrag(int y)
    {
        if (DateTime.UtcNow - _lastDragScroll < TimeSpan.FromMilliseconds(140))
            return;

        var top = _parts.TopNode;
        if (top == null)
            return;

        if (y < 20 && top.PrevVisibleNode is { } previous)
            _parts.TopNode = previous;
        else if (y > _parts.ClientSize.Height - 20 && top.NextVisibleNode is { } next)
            _parts.TopNode = next;
        else
            return;

        _lastDragScroll = DateTime.UtcNow;
    }

    private void SetDropIndicator(TreeNode target, DropPlacement placement)
    {
        if (_dropTarget == target && _dropPlacement == placement)
            return;

        _dropTarget = target;
        _dropPlacement = placement;
        _parts.Invalidate();
    }

    private void ClearDropIndicator()
    {
        if (_dropTarget == null)
            return;

        _dropTarget = null;
        _dropPlacement = DropPlacement.None;
        _parts.Invalidate();
    }

    private void MoveTreeNode(TreeNode source, TreeNode target, DropPlacement placement)
    {
        if (source == target || placement == DropPlacement.None)
            return;

        source.Remove();

        if (placement == DropPlacement.Inside && target.Tag is PartGroup)
        {
            target.Nodes.Add(source);
            target.Expand();
        }
        else
        {
            var collection = target.Parent?.Nodes ?? _parts.Nodes;
            collection.Insert(target.Index + (placement == DropPlacement.After ? 1 : 0), source);
        }

        _parts.SelectedNode = source;
        source.EnsureVisible();

        SyncPartOrderFromTree();
        RefreshGroupStates();
        _viewer.InvalidateScene();

        var movedName = GetNodeDisplayName(source);
        var destination = source.Parent?.Tag is PartGroup group
            ? group.Name
            : "ROOT";

        _status.Text = $"Moved: {movedName} -> {destination}";
    }

    private static bool IsNodeInside(TreeNode source, TreeNode potentialDescendant)
    {
        var current = potentialDescendant;

        while (current != null)
        {
            if (ReferenceEquals(current, source))
                return true;

            current = current.Parent;
        }

        return false;
    }

    private void SyncPartOrderFromTree()
    {
        var ordered = EnumerateTreePartNodes()
            .Select(x => (ScenePart)x.Tag!)
            .ToList();

        if (ordered.Count != _session.Parts.Count)
            return;

        _session.Parts.Clear();
        _session.Parts.AddRange(ordered);
    }

    private IEnumerable<TreeNode> EnumerateTreePartNodes()
    {
        foreach (TreeNode root in _parts.Nodes)
        {
            foreach (var part in EnumeratePartNodes(root))
                yield return part;
        }
    }

    private static IEnumerable<TreeNode> EnumeratePartNodes(TreeNode node)
    {
        if (node.Tag is ScenePart)
        {
            yield return node;
            yield break;
        }

        foreach (TreeNode child in node.Nodes)
        {
            foreach (var part in EnumeratePartNodes(child))
                yield return part;
        }
    }

    private static int CountPartNodes(TreeNode node)
        => EnumeratePartNodes(node).Count();

    private TreeNode? FindPartNode(ScenePart part)
        => EnumerateTreePartNodes()
            .FirstOrDefault(x => ReferenceEquals(x.Tag, part));

    private static string GetNodeDisplayName(TreeNode node)
        => node.Tag switch
        {
            ScenePart part => part.DisplayName,
            PartGroup group => group.Name,
            _ => node.Text
        };

    private void BuildPartsContextMenu()
    {
        if (_partsMenu == null)
            return;

        _partsMenu.Items.Clear();

        var selected = _parts.SelectedNode;

        if (selected?.Tag is ScenePart part)
        {
            if (_player.CurrentAnimationName is string take)
            {
                var seconds = Math.Round(_player.TimeSeconds, 3);
                _partsMenu.Items.Add($"Set visible at {seconds:0.000}s", null,
                    (_, _) => SetVisibilityKey(part, take, seconds, true));
                _partsMenu.Items.Add($"Set hidden at {seconds:0.000}s", null,
                    (_, _) => SetVisibilityKey(part, take, seconds, false));

                var keys = part.VisibilityKeys
                    .Where(x => x.Animation == take)
                    .OrderBy(x => x.Seconds)
                    .ToList();
                if (keys.Count > 0)
                {
                    var remove = new ToolStripMenuItem("Remove timing key...");
                    foreach (var key in keys)
                    {
                        var captured = key;
                        remove.DropDownItems.Add(
                            $"{key.Seconds:0.000}s: {(key.Visible ? "visible" : "hidden")}",
                            null,
                            (_, _) => RemoveVisibilityKey(part, captured));
                    }
                    _partsMenu.Items.Add(remove);
                }
                _partsMenu.Items.Add(new ToolStripSeparator());
            }

            _partsMenu.Items.Add(new ToolStripMenuItem(
                "Force visible (ignore FBX swaps)",
                null,
                (_, _) => ToggleForceVisible(part))
            {
                Checked = part.ForceVisible
            });
            _partsMenu.Items.Add(new ToolStripSeparator());
            _partsMenu.Items.Add("Move mesh in preview (drag)", null,
                (_, _) => BeginMovePart(part));
            _partsMenu.Items.Add("Edit mesh shape...", null,
                (_, _) => EditMeshShape(part));
            _partsMenu.Items.Add("Edit bone binding...", null,
                (_, _) => EditBoneBinding(part));
            _partsMenu.Items.Add(new ToolStripMenuItem(
                "Reset mesh position", null, (_, _) => ResetMeshPosition(part))
            {
                Enabled = part.OffsetX != 0f || part.OffsetY != 0f
            });
            if (part.HasBones)
            {
                _partsMenu.Items.Add(new ToolStripMenuItem(
                    "Rebuild skin bind pose (deformed mesh)", null,
                    (_, _) => ToggleRebuildBindPose(part))
                {
                    Checked = part.RebuildBindPose
                });
                _partsMenu.Items.Add(new ToolStripMenuItem(
                    "Preserve shape (follow main bone)", null,
                    (_, _) => TogglePreserveShape(part))
                {
                    Checked = part.PreserveShape
                });
            }
            _partsMenu.Items.Add(new ToolStripSeparator());
            _partsMenu.Items.Add("Rename...", null, (_, _) => RenameSelectedPart());
            _partsMenu.Items.Add("Clear alias", null, (_, _) => ClearSelectedAlias());
            _partsMenu.Items.Add(new ToolStripSeparator());
        }
        else if (selected?.Tag is PartGroup)
        {
            _partsMenu.Items.Add("New subgroup...", null, (_, _) => CreateSubgroup(selected));
            _partsMenu.Items.Add("Rename group...", null, (_, _) => RenameGroup(selected));
            _partsMenu.Items.Add("Delete group (keep contents)", null, (_, _) => DeleteGroupKeepContents(selected));
            _partsMenu.Items.Add(new ToolStripSeparator());
        }

        _partsMenu.Items.Add("New root group...", null, (_, _) => CreateRootGroup());
    }

    private void ToggleForceVisible(ScenePart part)
    {
        part.ForceVisible = !part.ForceVisible;
        RefreshPartNodeTexts();
        _viewer.InvalidateScene();
        _status.Text = $"{part.Name}: {(part.ForceVisible ? "forced visible" : "FBX animation visibility")}";
    }

    private void BeginMovePart(ScenePart part)
    {
        _viewer.BeginMovePart(part);
        _status.Text = $"Drag in preview to move {part.DisplayName}; Esc cancels. Save profile after moving.";
    }

    private void EditMeshShape(ScenePart part)
    {
        if (_session.Scene is null)
            return;

        _viewer.EndMovePart();
        var mesh = _session.Scene.Meshes[part.MeshIndex];
        if (mesh.VertexCount == 0)
        {
            _status.Text = $"{part.DisplayName}: mesh has no vertices to edit.";
            return;
        }
        var texture = part.MaterialIndex >= 0 && part.MaterialIndex < _session.Scene.MaterialCount
            ? _session.ResolveTexturePath(_session.Scene.Materials[part.MaterialIndex])
            : string.Empty;

        var wasPlaying = _player.Playing;
        _player.Playing = false;
        try
        {
            using var editor = new MeshDeformForm(part, mesh, texture,
                () => _viewer.GetPartWorldPositions(part)
                    .Select(p => new PointF(p.X, p.Y)).ToArray(),
                _viewer.InvalidateScene);
            if (editor.ShowDialog(this) == DialogResult.OK)
                _status.Text = $"Mesh shape updated: {part.DisplayName}. Save profile to keep it.";
            else
                _status.Text = $"Mesh edit canceled: {part.DisplayName}.";
        }
        finally
        {
            _player.Playing = wasPlaying;
        }
    }

    private void EditBoneBinding(ScenePart part)
    {
        if (_session.Scene is null)
            return;

        _viewer.EndMovePart();
        var wasPlaying = _player.Playing;
        var originalTime = _player.TimeSeconds;
        _player.Playing = false;
        try
        {
            using var editor = new BoneFollowForm(_session, _player, part,
                () => _viewer.GetPartWorldPositions(part)
                    .Select(p => new PointF(p.X, p.Y)).ToArray(),
                _viewer.InvalidateScene);
            _status.Text = editor.ShowDialog(this) == DialogResult.OK
                ? $"Bone binding updated: {part.DisplayName}. Save profile to keep it."
                : $"Bone binding canceled: {part.DisplayName}.";
        }
        finally
        {
            _player.SetTime(originalTime);
            _player.Playing = wasPlaying;
            _viewer.InvalidateScene();
        }
    }

    private void ResetMeshPosition(ScenePart part)
    {
        _viewer.EndMovePart();
        part.OffsetX = 0f;
        part.OffsetY = 0f;
        _viewer.InvalidateScene();
        _status.Text = $"Position reset: {part.DisplayName}";
    }

    private void ToggleRebuildBindPose(ScenePart part)
    {
        part.RebuildBindPose = !part.RebuildBindPose;
        _viewer.InvalidateScene();
        _status.Text = $"{part.DisplayName}: bind pose repair {(part.RebuildBindPose ? "on" : "off")}. Save profile to keep it.";
    }

    private void TogglePreserveShape(ScenePart part)
    {
        part.PreserveShape = !part.PreserveShape;
        _viewer.InvalidateScene();
        _status.Text = $"{part.DisplayName}: preserve shape {(part.PreserveShape ? "on" : "off")}. Save profile to keep it.";
    }

    private void SetVisibilityKey(ScenePart part, string take, double seconds, bool visible)
    {
        part.VisibilityKeys.RemoveAll(x =>
            x.Animation == take && Math.Abs(x.Seconds - seconds) < 0.0005);
        part.VisibilityKeys.Add(new VisibilityKeyframe
        {
            PartId = part.Id,
            Animation = take,
            Seconds = seconds,
            Visible = visible
        });
        part.Visible = true;
        part.ForceVisible = false;
        RefreshGroupStates();
        RefreshPartNodeTexts();
        _viewer.InvalidateScene();
        _status.Text = $"{part.Name}: {(visible ? "visible" : "hidden")} from {seconds:0.000}s in {take}";
    }

    private void RemoveVisibilityKey(ScenePart part, VisibilityKeyframe key)
    {
        part.VisibilityKeys.Remove(key);
        RefreshPartNodeTexts();
        _viewer.InvalidateScene();
        _status.Text = $"Timing removed: {part.Name} at {key.Seconds:0.000}s";
    }

    private void RenameSelectedPart()
    {
        if (_parts.SelectedNode is not { Tag: ScenePart } node)
            return;

        BeginRenamePart(node);
    }

    private void BeginRenamePart(TreeNode node)
    {
        if (node.Tag is not ScenePart part)
            return;

        _parts.BeginInvoke(new Action(() =>
        {
            if (node.TreeView != _parts)
                return;

            _parts.SelectedNode = node;
            _parts.Focus();
            node.Text = part.Alias ?? part.Name;
            node.BeginEdit();
        }));
    }

    private void ClearSelectedAlias()
    {
        if (_parts.SelectedNode?.Tag is not ScenePart part)
            return;

        part.Alias = null;
        _parts.SelectedNode.Text = part.DisplayName;
        _status.Text = $"Alias cleared: {part.Name}";
    }

    private void SaveProfile()
    {
        if (_session.Scene is null)
            return;

        using var dialog = new SaveFileDialog
        {
            Filter = "AS FBX profile (*.json)|*.json",
            FileName = Path.GetFileNameWithoutExtension(_session.FilePath) + ".profile.json"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        SyncPartOrderFromTree();

        new VisibilityProfile
        {
            SourceFbx = Path.GetFileName(_session.FilePath),
            Animation = (_animations.SelectedItem as AnimationTake)?.Name,
            HiddenPartIds = _session.Parts
                .Where(p => !p.Visible)
                .Select(p => p.Id)
                .ToList(),
            ForcedPartIds = _session.Parts
                .Where(p => p.ForceVisible)
                .Select(p => p.Id)
                .ToList(),
            VisibilityKeys = _session.Parts
                .SelectMany(p => p.VisibilityKeys)
                .OrderBy(x => x.Animation)
                .ThenBy(x => x.Seconds)
                .ToList(),
            PartOrderIds = _session.Parts
                .Select(p => p.Id)
                .ToList(),
            PartAliases = _session.Parts
                .Where(p => !string.IsNullOrWhiteSpace(p.Alias))
                .ToDictionary(p => p.Id, p => p.Alias!, StringComparer.Ordinal),
            MeshAdjustments = _session.Parts
                .Where(p => p.OffsetX != 0f || p.OffsetY != 0f || p.RebuildBindPose ||
                            p.PreserveShape || p.FollowBoneName is not null || p.VertexOffsets.Count > 0)
                .ToDictionary(
                    p => p.Id,
                    p => new MeshAdjustmentProfile
                    {
                        OffsetX = p.OffsetX,
                        OffsetY = p.OffsetY,
                        RebuildBindPose = p.RebuildBindPose,
                        PreserveShape = p.PreserveShape,
                        FollowBoneName = p.FollowBoneName,
                        VertexOffsets = p.VertexOffsets.Values
                            .OrderBy(x => x.VertexIndex)
                            .Select(x => new VertexOffset
                            {
                                VertexIndex = x.VertexIndex,
                                X = x.X,
                                Y = x.Y
                            }).ToList()
                    }, StringComparer.Ordinal),
            Organization = BuildOrganizationProfile()
        }.Save(dialog.FileName);

        _status.Text = $"Profile saved: {Path.GetFileName(dialog.FileName)}";
    }

    private List<OrganizationNodeProfile> BuildOrganizationProfile()
    {
        var result = new List<OrganizationNodeProfile>();

        foreach (TreeNode node in _parts.Nodes)
            result.Add(ToOrganizationProfile(node));

        return result;
    }

    private static OrganizationNodeProfile ToOrganizationProfile(TreeNode node)
    {
        if (node.Tag is ScenePart part)
        {
            return new OrganizationNodeProfile
            {
                Type = "part",
                Id = part.Id
            };
        }

        if (node.Tag is PartGroup group)
        {
            return new OrganizationNodeProfile
            {
                Type = "group",
                Id = group.Id,
                Name = group.Name,
                Children = node.Nodes
                    .Cast<TreeNode>()
                    .Select(ToOrganizationProfile)
                    .ToList()
            };
        }

        return new OrganizationNodeProfile();
    }

    private void LoadProfile()
    {
        if (_session.Scene is null)
            return;

        using var dialog = new OpenFileDialog
        {
            Filter = "AS FBX profile (*.json)|*.json|Legacy visibility profile (*.json)|*.json"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var profile = VisibilityProfile.Load(dialog.FileName);

        foreach (var part in _session.Parts)
        {
            part.Alias = profile.PartAliases.TryGetValue(part.Id, out var alias)
                ? alias
                : null;
        }

        if (profile.Organization.Count > 0)
        {
            BuildTreeFromOrganization(profile.Organization);
        }
        else
        {
            ApplyLegacyPartOrder(profile.PartOrderIds);
            BuildDefaultPartTree();
        }

        var hidden = profile.HiddenPartIds.ToHashSet(StringComparer.Ordinal);
        var forced = profile.ForcedPartIds.ToHashSet(StringComparer.Ordinal);
        foreach (var part in _session.Parts)
        {
            part.Visible = !hidden.Contains(part.Id);
            part.ForceVisible = forced.Contains(part.Id);
            profile.MeshAdjustments.TryGetValue(part.Id, out var adjustment);
            part.OffsetX = adjustment?.OffsetX ?? 0f;
            part.OffsetY = adjustment?.OffsetY ?? 0f;
            part.RebuildBindPose = adjustment?.RebuildBindPose ?? false;
            part.PreserveShape = adjustment?.PreserveShape ?? false;
            part.FollowBoneName = adjustment?.FollowBoneName is string boneName &&
                _session.NodesByName.ContainsKey(boneName) ? boneName : null;
            part.VertexOffsets.Clear();
            foreach (var edit in adjustment?.VertexOffsets ?? new List<VertexOffset>())
            {
                if (edit.VertexIndex >= 0 && edit.VertexIndex <
                    _session.Scene.Meshes[part.MeshIndex].VertexCount &&
                    float.IsFinite(edit.X) && float.IsFinite(edit.Y))
                    part.VertexOffsets[edit.VertexIndex] = edit;
            }
            part.VisibilityKeys.Clear();
            part.VisibilityKeys.AddRange(profile.VisibilityKeys.Where(x => x.PartId == part.Id));
        }

        RefreshPartNodeTexts();
        RefreshGroupStates();

        if (!string.IsNullOrWhiteSpace(profile.Animation))
        {
            var index = _session.Animations.FindIndex(x => x.Name == profile.Animation);
            if (index >= 0)
                _animations.SelectedIndex = index;
        }

        _viewer.InvalidateScene();
        _status.Text = $"Profile loaded: {Path.GetFileName(dialog.FileName)}";
    }

    private void BuildTreeFromOrganization(IReadOnlyList<OrganizationNodeProfile> organization)
    {
        var partLookup = _session.Parts.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var usedParts = new HashSet<string>(StringComparer.Ordinal);

        _updatingTree = true;
        try
        {
            _parts.BeginUpdate();
            _parts.Nodes.Clear();

            foreach (var profileNode in organization)
            {
                var node = BuildTreeNodeFromProfile(profileNode, partLookup, usedParts);
                if (node != null)
                    _parts.Nodes.Add(node);
            }

            // A newer FBX can contain parts that did not exist when the profile
            // was saved. Keep them available at root instead of losing them.
            foreach (var part in _session.Parts)
            {
                if (!usedParts.Contains(part.Id))
                    _parts.Nodes.Add(CreatePartNode(part));
            }

            _parts.ExpandAll();
        }
        finally
        {
            _parts.EndUpdate();
            _updatingTree = false;
        }

        SyncPartOrderFromTree();
    }

    private TreeNode? BuildTreeNodeFromProfile(
        OrganizationNodeProfile profile,
        IReadOnlyDictionary<string, ScenePart> partLookup,
        HashSet<string> usedParts)
    {
        if (string.Equals(profile.Type, "part", StringComparison.OrdinalIgnoreCase))
        {
            if (!partLookup.TryGetValue(profile.Id, out var part) ||
                !usedParts.Add(part.Id))
            {
                return null;
            }

            return CreatePartNode(part);
        }

        if (string.Equals(profile.Type, "group", StringComparison.OrdinalIgnoreCase))
        {
            var group = new PartGroup
            {
                Id = string.IsNullOrWhiteSpace(profile.Id)
                    ? Guid.NewGuid().ToString("N")
                    : profile.Id,
                Name = string.IsNullOrWhiteSpace(profile.Name)
                    ? "Group"
                    : profile.Name
            };

            var node = CreateGroupNode(group);

            foreach (var childProfile in profile.Children)
            {
                var child = BuildTreeNodeFromProfile(
                    childProfile,
                    partLookup,
                    usedParts);

                if (child != null)
                    node.Nodes.Add(child);
            }

            return node;
        }

        return null;
    }

    private void ApplyLegacyPartOrder(IReadOnlyList<string> orderedIds)
    {
        if (orderedIds.Count == 0)
            return;

        var rank = orderedIds
            .Select((id, index) => new { id, index })
            .ToDictionary(x => x.id, x => x.index, StringComparer.Ordinal);

        var reordered = _session.Parts
            .Select((part, originalIndex) => new { part, originalIndex })
            .OrderBy(x => rank.TryGetValue(x.part.Id, out var r) ? r : int.MaxValue)
            .ThenBy(x => x.originalIndex)
            .Select(x => x.part)
            .ToList();

        _session.Parts.Clear();
        _session.Parts.AddRange(reordered);
    }

    private void RefreshPartNodeTexts()
    {
        foreach (var node in EnumerateTreePartNodes())
        {
            if (node.Tag is ScenePart part)
                node.Text = part.DisplayName;
        }
    }

    private string? PromptForText(string title, string description, string currentValue)
    {
        using var dialog = new Form
        {
            Text = title,
            Width = 560,
            Height = 200,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Font,
            Font = Font
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14, 14, 14, 10)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var label = new Label { Text = description, AutoSize = true };
        var input = new TextBox { Dock = DockStyle.Top, Text = currentValue };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var ok = new Button
        {
            Text = "OK", DialogResult = DialogResult.OK,
            AutoSize = true, MinimumSize = new Size(82, 0)
        };
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            AutoSize = true, MinimumSize = new Size(82, 0)
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(ok);
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(input, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        dialog.Shown += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return dialog.ShowDialog(this) == DialogResult.OK
            ? input.Text
            : null;
    }

    private void OpenExportDialog()
    {
        if (_session.Scene is null || _session.Animations.Count == 0)
        {
            MessageBox.Show(
                this,
                "Open an FBX with at least one animation first.");
            return;
        }

        _player.Playing = false;

        using var dialog = new ExportDialog(
            _session,
            _player,
            _viewer.PremultiplyAlpha);

        dialog.ShowDialog(this);

        // ExportDialog restores the previous animation/time when it closes.
        // Keep the main animation selector visually in sync with the player.
        if (_player.AnimationIndex >= 0 &&
            _player.AnimationIndex < _animations.Items.Count)
        {
            _animations.SelectedIndex = _player.AnimationIndex;
        }

        _viewer.InvalidateScene();
    }

    private static Button Button(string text, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(92, 38),
            Padding = new Padding(10, 5, 10, 5),
            Margin = new Padding(4, 2, 4, 2)
        };

        button.Click += click;
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _session.Dispose();
            _checkStateImages?.Dispose();
            _groupFont?.Dispose();
            _partsMenu?.Dispose();
        }

        base.Dispose(disposing);
    }
}
