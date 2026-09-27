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
    private readonly CheckedListBox _parts = new();
    private readonly ComboBox _animations = new();
    private readonly TrackBar _timeline = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private DateTime _lastTick = DateTime.UtcNow;
    private int _dragPartIndex = -1;
    private bool _reorderingParts;

    public MainForm()
    {
        _player = new AnimationPlayer(_session);

        Text = "AS_FBX-reader 0.1.4";
        Width = 1500;
        Height = 900;
        MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 11F);

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
        var rest = Button("Rest", (_, _) => { _player.SelectAnimation(-1); _animations.SelectedIndex = -1; _viewer.InvalidateScene(); });
        var saveProfile = Button("Save visibility", (_, _) => SaveProfile());
        var loadProfile = Button("Load visibility", (_, _) => LoadProfile());
        var export = Button("Export MP4/WebP", async (_, _) => await ExportAnimationAsync());

        _animations.DropDownStyle = ComboBoxStyle.DropDownList;
        _animations.Width = 300;
        _animations.Margin = new Padding(5, 5, 5, 5);

        toolbar.Controls.AddRange([
            open,
            new Label { Text = "Animation:", AutoSize = true, Padding = new Padding(8, 10, 2, 0), Margin = new Padding(3, 3, 3, 3) },
            _animations,
            play,
            pause,
            rest,
            saveProfile,
            loadProfile,
            export
        ]);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 330,
            FixedPanel = FixedPanel.Panel1
        };

        var leftButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(6, 7, 6, 5),
            WrapContents = false
        };
        leftButtons.Controls.Add(Button("Show all", (_, _) => SetAll(true)));
        leftButtons.Controls.Add(Button("Hide all", (_, _) => SetAll(false)));
        leftButtons.Controls.Add(Button("Solo", (_, _) => SoloSelected()));
        leftButtons.Controls.Add(new Label
        {
            Text = "Drag layers: TOP = FRONT",
            AutoSize = true,
            Padding = new Padding(10, 9, 0, 0)
        });

        _parts.Dock = DockStyle.Fill;
        _parts.CheckOnClick = true;
        _parts.IntegralHeight = false;
        _parts.AllowDrop = true;
        split.Panel1.Controls.Add(_parts);
        split.Panel1.Controls.Add(leftButtons);
        split.Panel2.Controls.Add(_viewer);

        _timeline.Dock = DockStyle.Bottom;
        _timeline.Height = 44;
        _timeline.Minimum = 0;
        _timeline.Maximum = 1000;
        _timeline.TickStyle = TickStyle.None;

        _status.Dock = DockStyle.Bottom;
        _status.Height = 28;
        _status.Padding = new Padding(6, 4, 0, 0);
        _status.Text = "Open an FBX exported by AssetStudio.";

        Controls.Add(split);
        Controls.Add(_timeline);
        Controls.Add(_status);
        Controls.Add(toolbar);
    }

    private void HookEvents()
    {
        _parts.ItemCheck += (_, e) =>
        {
            if (_reorderingParts)
                return;

            BeginInvoke(() =>
            {
                if (e.Index >= 0 && e.Index < _session.Parts.Count)
                {
                    _session.Parts[e.Index].Visible = e.NewValue == CheckState.Checked;
                    _viewer.InvalidateScene();
                }
            });
        };

        _parts.MouseDown += (_, e) =>
        {
            _dragPartIndex = _parts.IndexFromPoint(e.Location);
            if (_dragPartIndex < 0 || _dragPartIndex >= _parts.Items.Count)
                return;

            _parts.SelectedIndex = _dragPartIndex;
            _parts.DoDragDrop(_parts.Items[_dragPartIndex]!, DragDropEffects.Move);
        };

        _parts.DragOver += (_, e) =>
        {
            if (_dragPartIndex < 0 || !e.Data!.GetDataPresent(typeof(ScenePart)))
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            e.Effect = DragDropEffects.Move;
        };

        _parts.DragDrop += (_, e) =>
        {
            if (_dragPartIndex < 0 || _dragPartIndex >= _parts.Items.Count)
                return;

            var clientPoint = _parts.PointToClient(new Point(e.X, e.Y));
            var targetIndex = _parts.IndexFromPoint(clientPoint);
            if (targetIndex < 0)
                targetIndex = _parts.Items.Count - 1;

            if (targetIndex == _dragPartIndex)
            {
                _dragPartIndex = -1;
                return;
            }

            MovePart(_dragPartIndex, targetIndex);
            _dragPartIndex = -1;
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

            if (_player.Playing)
            {
                _player.Update(delta);
                if (_player.DurationSeconds > 0)
                {
                    _timeline.Value = Math.Clamp(
                        (int)(_player.TimeSeconds / _player.DurationSeconds * 1000.0),
                        0,
                        1000);
                }
                _viewer.InvalidateScene();
            }
        };
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

            _parts.Items.Clear();
            foreach (var part in _session.Parts)
                _parts.Items.Add(part, part.Visible);

            _animations.Items.Clear();
            foreach (var animation in _session.Animations)
                _animations.Items.Add(animation);

            if (_animations.Items.Count > 0)
                _animations.SelectedIndex = 0;

            _status.Text = $"{Path.GetFileName(dialog.FileName)} | {_session.Parts.Count} parts | {_session.Animations.Count} animations";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Open FBX failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void SetAll(bool visible)
    {
        for (var i = 0; i < _session.Parts.Count; i++)
        {
            _session.Parts[i].Visible = visible;
            _parts.SetItemChecked(i, visible);
        }
        _viewer.InvalidateScene();
    }

    private void SoloSelected()
    {
        var selected = _parts.SelectedIndex;
        if (selected < 0)
            return;
        for (var i = 0; i < _session.Parts.Count; i++)
        {
            var visible = i == selected;
            _session.Parts[i].Visible = visible;
            _parts.SetItemChecked(i, visible);
        }
        _viewer.InvalidateScene();
    }

    private void MovePart(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _session.Parts.Count ||
            toIndex < 0 || toIndex >= _session.Parts.Count ||
            fromIndex == toIndex)
        {
            return;
        }

        var moved = _session.Parts[fromIndex];
        var wasChecked = moved.Visible;

        _reorderingParts = true;
        try
        {
            _session.Parts.RemoveAt(fromIndex);
            _session.Parts.Insert(toIndex, moved);

            _parts.Items.RemoveAt(fromIndex);
            _parts.Items.Insert(toIndex, moved);
            _parts.SetItemChecked(toIndex, wasChecked);
            _parts.SelectedIndex = toIndex;
        }
        finally
        {
            _reorderingParts = false;
        }

        _status.Text = $"Layer moved: {moved.Name} | top = front, bottom = back";
        _viewer.InvalidateScene();
    }

    private void ApplyPartOrder(IReadOnlyList<string> orderedIds)
    {
        if (orderedIds.Count == 0 || _session.Parts.Count == 0)
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
        RebuildPartList();
    }

    private void RebuildPartList()
    {
        _reorderingParts = true;
        try
        {
            _parts.Items.Clear();
            foreach (var part in _session.Parts)
                _parts.Items.Add(part, part.Visible);
        }
        finally
        {
            _reorderingParts = false;
        }

        _viewer.InvalidateScene();
    }

    private void SaveProfile()
    {
        if (_session.Scene is null)
            return;
        using var dialog = new SaveFileDialog
        {
            Filter = "AS FBX visibility profile (*.json)|*.json",
            FileName = Path.GetFileNameWithoutExtension(_session.FilePath) + ".visibility.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        new VisibilityProfile
        {
            SourceFbx = Path.GetFileName(_session.FilePath),
            Animation = (_animations.SelectedItem as AnimationTake)?.Name,
            HiddenPartIds = _session.Parts.Where(p => !p.Visible).Select(p => p.Id).ToList(),
            PartOrderIds = _session.Parts.Select(p => p.Id).ToList()
        }.Save(dialog.FileName);
    }

    private void LoadProfile()
    {
        if (_session.Scene is null)
            return;
        using var dialog = new OpenFileDialog
        {
            Filter = "AS FBX visibility profile (*.json)|*.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var profile = VisibilityProfile.Load(dialog.FileName);

        if (profile.PartOrderIds.Count > 0)
            ApplyPartOrder(profile.PartOrderIds);

        var hidden = profile.HiddenPartIds.ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < _session.Parts.Count; i++)
        {
            var visible = !hidden.Contains(_session.Parts[i].Id);
            _session.Parts[i].Visible = visible;
            _parts.SetItemChecked(i, visible);
        }

        if (!string.IsNullOrWhiteSpace(profile.Animation))
        {
            var index = _session.Animations.FindIndex(x => x.Name == profile.Animation);
            if (index >= 0)
                _animations.SelectedIndex = index;
        }
        _viewer.InvalidateScene();
    }

    private async Task ExportAnimationAsync()
    {
        if (_session.Scene is null || _player.AnimationIndex < 0)
        {
            MessageBox.Show(this, "Open an FBX and select an animation first.");
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "MP4 video (*.mp4)|*.mp4|Animated WebP (*.webp)|*.webp",
            FileName = (_animations.SelectedItem as AnimationTake)?.Name + ".mp4"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var temp = Path.Combine(Path.GetTempPath(), "AS_FBX_reader", Guid.NewGuid().ToString("N"));
        try
        {
            Cursor = Cursors.WaitCursor;
            var exporter = new AnimationExporter();
            await exporter.ExportPngSequenceAsync(_viewer, _player, temp, 1920, 1080, 30);
            await exporter.EncodeWithFfmpegAsync(temp, dialog.FileName, 30);
            MessageBox.Show(this, $"Exported:\r\n{dialog.FileName}", "Done");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
        }
    }

    private static Button Button(string text, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(88, 38),
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
        }
        base.Dispose(disposing);
    }
}
