using AS.FBXReader.Core;
using AS.FBXReader.Rendering;

namespace AS.FBXReader;

/// <summary>
/// Standalone pose-to-PNG 3D preview. Own animation state and GL viewer; the
/// main 2D editor and its profile remain untouched.
/// </summary>
public sealed class ThreeDViewerForm : Form
{
    private readonly FbxSession _session;
    private readonly AnimationPlayer _player;
    private readonly ViewerControl _viewer = new();
    private readonly OrbitSphereControl _sphere = new();
    private readonly ComboBox _animations = new();
    private readonly TrackBar _time = new();
    private readonly NumericUpDown _yaw = Number(-360, 360, 25, 1);
    private readonly NumericUpDown _pitch = Number(-85, 85, 12, 1);
    private readonly NumericUpDown _zoom = Number(0.1m, 20m, 1m, .1m);
    private readonly NumericUpDown _width = Number(64, 4096, 2048, 64);
    private readonly NumericUpDown _height = Number(64, 4096, 2048, 64);
    private readonly Label _status = new();
    private readonly Label _clock = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private bool _syncing;
    private DateTime _tickTime = DateTime.UtcNow;

    public ThreeDViewerForm(FbxSession session)
    {
        _session = session;
        _player = new AnimationPlayer(session);
        _player.Rebuild();
        Text = "AS_FBX-reader — 3D Viewer → PNG (preview)";
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(1060, 720);
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.FromArgb(33, 37, 44);
        ForeColor = Color.FromArgb(234, 238, 245);

        BuildUi();

        _viewer.Enable3DMode();
        _viewer.Attach(_session, _player);
        _viewer.SetBackground(false, Color.FromArgb(31, 35, 44));
        _viewer.OrbitChanged += (_, angle) => SyncOrbit(angle);
        _sphere.AnglesChanged += (yaw, pitch) => _viewer.SetOrbit(yaw, pitch, _viewer.GetOrbit().Zoom);

        foreach (var take in session.Animations)
            _animations.Items.Add(take);
        if (_animations.Items.Count > 0)
            _animations.SelectedIndex = 0;
        else
            _player.SelectAnimation(-1);

        foreach (var n in new[] { _yaw, _pitch, _zoom })
            n.ValueChanged += (_, _) => ApplyOrbit();
        SyncOrbit(_viewer.GetOrbit());

        _time.ValueChanged += (_, _) =>
        {
            if (_syncing || _player.DurationSeconds <= 0) return;
            _player.SetTime(_time.Value / 1000d * _player.DurationSeconds);
            _viewer.InvalidateScene();
            UpdateClock();
        };
        _animations.SelectedIndexChanged += (_, _) =>
        {
            _player.Playing = false;
            _player.SelectAnimation(_animations.SelectedIndex);
            _time.Value = 0;
            _viewer.InvalidateScene();
            UpdateClock();
        };

        _timer.Tick += (_, _) => Tick();
        Shown += (_, _) => { _tickTime = DateTime.UtcNow; _timer.Start(); };
        FormClosing += (_, _) => _timer.Stop();
        _status.Text = $"{Path.GetFileName(session.FilePath)}  •  " +
            $"{session.Scene?.MeshCount ?? 0} mesh(es)  •  " +
            $"{session.Animations.Count} animation(s)  •  Drag: orbit  |  Right-drag: pan  |  Wheel: zoom";
    }

    private static NumericUpDown Number(decimal min, decimal max, decimal value, decimal step)
        => new()
        {
            Minimum = min, Maximum = max, Value = value,
            Increment = step, DecimalPlaces = step < 1 ? 2 : 0,
            Width = 104, TextAlign = HorizontalAlignment.Center
        };

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3, ColumnCount = 2,
            Padding = new Padding(8),
            BackColor = BackColor
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 76));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        _animations.DropDownStyle = ComboBoxStyle.DropDownList;
        _animations.Width = 280;
        actions.Controls.Add(new Label { Text = "Motion:", AutoSize = true, Padding = new Padding(4, 9, 0, 0) });
        actions.Controls.Add(_animations);
        actions.Controls.Add(ActionButton("▶ Play", (_, _) => _player.Playing = true));
        actions.Controls.Add(ActionButton("Ⅱ Pause", (_, _) => _player.Playing = false));
        actions.Controls.Add(ActionButton("Rest pose", (_, _) =>
        {
            _player.Playing = false;
            _player.SelectAnimation(-1);
            _animations.SelectedIndex = -1;
            _viewer.InvalidateScene();
        }));
        actions.Controls.Add(ActionButton("Reset camera", (_, _) => _viewer.ResetOrbit()));
        root.Controls.Add(actions, 0, 0);

        var instruction = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Text = "3D CAMERA  •  ORIENTATION SPHERE", AutoSize = false
        };
        root.Controls.Add(instruction, 1, 0);

        var previewHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(2) };
        previewHost.Controls.Add(_viewer);
        root.Controls.Add(previewHost, 0, 1);

        var sidebar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(12),
            BackColor = Color.FromArgb(39, 44, 54)
        };
        sidebar.Resize += (_, _) =>
        {
            var w = Math.Max(205, sidebar.ClientSize.Width - 38);
            _sphere.Width = w;
        };
        _sphere.Height = 230;
        sidebar.Controls.Add(_sphere);
        var presets = new FlowLayoutPanel { Width = 270, Height = 82, WrapContents = true };
        foreach (var (text, yaw, pitch) in new (string, float, float)[]
        {
            ("Front", 0, 0), ("Back", 180, 0), ("Right", 90, 0),
            ("Left", -90, 0), ("Top", 0, 85), ("Bottom", 0, -85)
        })
        {
            var button = ActionButton(text, (_, _) => _viewer.SetOrbit(yaw, pitch, _viewer.GetOrbit().Zoom));
            button.Width = 82;
            presets.Controls.Add(button);
        }
        sidebar.Controls.Add(presets);
        sidebar.Controls.Add(Heading("Camera rotation"));
        sidebar.Controls.Add(Field("Yaw (°)", _yaw));
        sidebar.Controls.Add(Field("Pitch (°)", _pitch));
        sidebar.Controls.Add(Field("Zoom", _zoom));
        sidebar.Controls.Add(Heading("PNG export — current pose"));
        sidebar.Controls.Add(Field("Width", _width));
        sidebar.Controls.Add(Field("Height", _height));
        var export = ActionButton("EXPORT PNG (RGBA)", (_, _) => ExportPng());
        export.Height = 52;
        export.Width = 250;
        export.Font = new Font(Font, FontStyle.Bold);
        export.BackColor = Color.FromArgb(31, 114, 168);
        export.ForeColor = Color.White;
        sidebar.Controls.Add(export);
        sidebar.Controls.Add(new Label
        {
            Text = "Save a flat 2D image with transparent background.\r\n" +
                   "Source FBX and textures remain unchanged.",
            AutoSize = true, MaximumSize = new Size(265, 100),
            Margin = new Padding(5, 10, 2, 2)
        });

        root.Controls.Add(sidebar, 1, 1);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 85));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        _time.Dock = DockStyle.Fill;
        _time.TickStyle = TickStyle.None;
        _time.Maximum = 1000;
        _clock.Dock = DockStyle.Fill;
        _clock.TextAlign = ContentAlignment.MiddleRight;
        _status.Dock = DockStyle.Fill;
        _status.ForeColor = Color.FromArgb(185, 201, 220);
        bottom.Controls.Add(_time, 0, 0);
        bottom.Controls.Add(_clock, 1, 0);
        bottom.Controls.Add(_status, 0, 1);
        bottom.SetColumnSpan(_status, 2);
        root.Controls.Add(bottom, 0, 2);
        root.SetColumnSpan(bottom, 2);
        Controls.Add(root);
    }

    private Label Heading(string text)
        => new()
        {
            Text = text, Width = 255, Height = 33,
            Padding = new Padding(0, 10, 0, 0),
            Font = new Font(Font, FontStyle.Bold)
        };

    private Control Field(string caption, NumericUpDown input)
    {
        var panel = new FlowLayoutPanel { Width = 265, Height = 35, WrapContents = false };
        panel.Controls.Add(new Label
        {
            Text = caption, Width = 126, Height = 30,
            TextAlign = ContentAlignment.MiddleLeft
        });
        panel.Controls.Add(input);
        return panel;
    }

    private Button ActionButton(string title, EventHandler handler)
    {
        var button = new Button
        {
            Text = title, AutoSize = false, Width = 98, Height = 35,
            Margin = new Padding(3, 3, 3, 3),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(61, 69, 83),
            ForeColor = Color.White
        };
        button.Click += handler;
        return button;
    }

    private void SyncOrbit(OrbitAngles angles)
    {
        _syncing = true;
        try
        {
            _yaw.Value = Math.Clamp((decimal)angles.Yaw, _yaw.Minimum, _yaw.Maximum);
            _pitch.Value = Math.Clamp((decimal)angles.Pitch, _pitch.Minimum, _pitch.Maximum);
            _zoom.Value = Math.Clamp((decimal)angles.Zoom, _zoom.Minimum, _zoom.Maximum);
            _sphere.SetAngles(angles.Yaw, angles.Pitch);
        }
        finally { _syncing = false; }
    }

    private void ApplyOrbit()
    {
        if (_syncing) return;
        _viewer.SetOrbit((float)_yaw.Value, (float)_pitch.Value, (float)_zoom.Value);
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        var dt = Math.Clamp((now - _tickTime).TotalSeconds, 0, 0.1);
        _tickTime = now;
        _player.Update(dt);
        if (_player.Playing)
            _viewer.InvalidateScene();
        if (_player.DurationSeconds > 0)
        {
            _syncing = true;
            _time.Value = Math.Clamp(
                (int)Math.Round(_player.TimeSeconds / _player.DurationSeconds * 1000),
                _time.Minimum, _time.Maximum);
            _syncing = false;
        }
        UpdateClock();
    }

    private void UpdateClock() =>
        _clock.Text = $"{_player.TimeSeconds:0.00}s / {_player.DurationSeconds:0.00}s";

    private void ExportPng()
    {
        using var file = new SaveFileDialog
        {
            Title = "Save the current 3D pose as 2D PNG",
            Filter = "Transparent PNG (*.png)|*.png",
            FileName = $"{Path.GetFileNameWithoutExtension(_session.FilePath)}_" +
                       $"{(_player.CurrentAnimationName ?? "Rest")}_{_player.TimeSeconds:0.00}s.png"
        };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        var wasPlaying = _player.Playing;
        var transparent = _viewer.TransparentBackground;
        var previousColor = _viewer.BackgroundColor;
        try
        {
            UseWaitCursor = true;
            _player.Playing = false;
            _viewer.SetBackground(true, Color.Black);
            var frame = _viewer.CaptureFramePixels((int)_width.Value, (int)_height.Value);
            ViewerControl.SavePngFrame(frame, file.FileName);
            _status.Text = "PNG exported: " + file.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "3D PNG export failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _viewer.SetBackground(transparent, previousColor);
            _player.Playing = wasPlaying;
            UseWaitCursor = false;
            _viewer.InvalidateScene();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }
}
