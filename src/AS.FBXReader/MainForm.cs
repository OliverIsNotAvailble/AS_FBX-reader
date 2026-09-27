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
    private TreeNode? _dragNode;
    private bool _updatingTree;
    private ImageList? _checkStateImages;
    private Font? _groupFont;
    private ContextMenuStrip? _partsMenu;

    private const int StateUnchecked = 0;
    private const int StateChecked = 1;
    private const int StateMixed = 2;

    public MainForm()
    {
        _player = new AnimationPlayer(_session);

        Text = "AS_FBX-reader 0.1.8";
        Width = 1500;
        Height = 900;
        MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterScreen;
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
            Height = 62,
            Padding = new Padding(8, 9, 8, 7),
            WrapContents = false,
            AutoSize = false
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
        _animations.Width = 300;
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
            pma
        ]);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 390,
            FixedPanel = FixedPanel.Panel1
        };

        var leftButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(6, 7, 6, 5),
            WrapContents = false
        };
        leftButtons.Controls.Add(Button("Show all", (_, _) => SetAll(true)));
        leftButtons.Controls.Add(Button("Hide all", (_, _) => SetAll(false)));
        leftButtons.Controls.Add(Button("Solo", (_, _) => SoloSelected()));
        leftButtons.Controls.Add(Button("New group", (_, _) => CreateRootGroup()));
        leftButtons.Controls.Add(new Label
        {
            Text = "TOP = FRONT",
            AutoSize = true,
            Padding = new Padding(10, 9, 0, 0)
        });

        ConfigurePartsTree();

        split.Panel1.Controls.Add(_parts);
        split.Panel1.Controls.Add(leftButtons);
        split.Panel2.Controls.Add(_viewer);

        _timeline.Dock = DockStyle.Bottom;
        _timeline.Height = 44;
        _timeline.Minimum = 0;
        _timeline.Maximum = 1000;
        _timeline.TickStyle = TickStyle.None;

        _status.Dock = DockStyle.Bottom;
        _status.Height = 30;
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
        _parts.NodeMouseClick += (_, e) =>
        {
            _parts.SelectedNode = e.Node;

            if (e.Button == MouseButtons.Left && IsStateImageClick(e.Node, e.Location))
            {
                ToggleNode(e.Node);
            }
        };

        _parts.KeyDown += (_, e) =>
        {
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
            _parts.DoDragDrop(node, DragDropEffects.Move);
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
            var target = _parts.GetNodeAt(client);

            if (target != null && IsNodeInside(_dragNode, target))
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            if (target != null)
                _parts.SelectedNode = target;

            e.Effect = DragDropEffects.Move;
        };

        _parts.DragDrop += (_, e) =>
        {
            if (_dragNode is null)
                return;

            var source = _dragNode;
            _dragNode = null;

            var client = _parts.PointToClient(new Point(e.X, e.Y));
            var target = _parts.GetNodeAt(client);

            if (target != null && IsNodeInside(source, target))
                return;

            MoveTreeNode(source, target);
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
            ? $"{group.Name}: {(makeVisible ? "ON" : "OFF")} ({CountPartNodes(node)} parts)"
            : $"{GetNodeDisplayName(node)}: {(makeVisible ? "ON" : "OFF")}";
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

            _status.Text =
                $"{Path.GetFileName(dialog.FileName)} | {_session.Parts.Count} parts | " +
                $"{_session.Animations.Count} animations | drag parts into groups";
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
            part.Visible = visible;

        RefreshGroupStates();
        _viewer.InvalidateScene();
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
        }

        foreach (var part in _session.Parts)
            part.Visible = selectedParts.Contains(part);

        RefreshGroupStates();
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

    private void MoveTreeNode(TreeNode source, TreeNode? target)
    {
        if (source == target)
            return;

        source.Remove();

        if (target == null)
        {
            _parts.Nodes.Add(source);
        }
        else if (target.Tag is PartGroup)
        {
            target.Nodes.Add(source);
            target.Expand();
        }
        else
        {
            var collection = target.Parent?.Nodes ?? _parts.Nodes;
            collection.Insert(target.Index, source);
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

        _status.Text = $"Moved: {movedName} -> {destination} | TOP = FRONT";
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

        if (selected?.Tag is ScenePart)
        {
            _partsMenu.Items.Add("Rename alias...", null, (_, _) => RenameSelectedPart());
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

    private void RenameSelectedPart()
    {
        if (_parts.SelectedNode?.Tag is not ScenePart part)
            return;

        var alias = PromptForText(
            "Rename mesh alias",
            $"Original: {part.Name}",
            part.Alias ?? string.Empty);

        if (alias is null)
            return;

        part.Alias = string.IsNullOrWhiteSpace(alias)
            ? null
            : alias.Trim();

        _parts.SelectedNode.Text = part.DisplayName;

        _status.Text = string.IsNullOrWhiteSpace(part.Alias)
            ? $"Alias cleared: {part.Name}"
            : $"Alias: {part.Alias} <- {part.Name}";
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
            PartOrderIds = _session.Parts
                .Select(p => p.Id)
                .ToList(),
            PartAliases = _session.Parts
                .Where(p => !string.IsNullOrWhiteSpace(p.Alias))
                .ToDictionary(p => p.Id, p => p.Alias!, StringComparer.Ordinal),
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
        foreach (var part in _session.Parts)
            part.Visible = !hidden.Contains(part.Id);

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

        var label = new Label
        {
            Text = description,
            AutoSize = false,
            Left = 14,
            Top = 14,
            Width = 510,
            Height = 32
        };

        var input = new TextBox
        {
            Left = 14,
            Top = 52,
            Width = 510,
            Text = currentValue
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Left = 350,
            Top = 102,
            Width = 82,
            Height = 36
        };

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Left = 442,
            Top = 102,
            Width = 82,
            Height = 36
        };

        dialog.Controls.AddRange([label, input, ok, cancel]);
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
