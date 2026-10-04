using AS.FBXReader.Core;
using AS.FBXReader.Models;
using AS.FBXReader.Rendering;

namespace AS.FBXReader.Export;

public sealed class ExportDialog : Form
{
    private readonly FbxSession _session;
    private readonly AnimationPlayer _player;
    private readonly ViewerControl _preview = new();

    private readonly ComboBox _animation = new();
    private readonly ComboBox _format = new();
    private readonly ComboBox _resolutionPreset = new();
    private readonly NumericUpDown _width = new();
    private readonly NumericUpDown _height = new();
    private readonly NumericUpDown _fps = new();
    private readonly NumericUpDown _quality = new();
    private readonly CheckBox _transparent = new();
    private readonly CheckBox _pma = new();
    private readonly Button _backgroundButton = new();
    private readonly RadioButton _currentAnimation = new();
    private readonly RadioButton _allAnimations = new();
    private readonly RadioButton _selectedAnimations = new();
    private readonly CheckedListBox _selectedTakes = new();
    private readonly TextBox _output = new();
    private readonly NumericUpDown _zoom = new();
    private readonly NumericUpDown _offsetX = new();
    private readonly NumericUpDown _offsetY = new();
    private readonly TrackBar _timeline = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();
    private readonly Button _exportButton = new();
    private readonly Button _cancelButton = new();
    private readonly Panel _previewHost = new();
    private readonly Panel _previewFrame = new();
    private SplitContainer _root = null!;
    private Panel _settingsScroll = null!;
    private CancellationTokenSource? _exportCancellation;

    private Color _backgroundColor = Color.Black;
    private readonly int _originalAnimationIndex;
    private readonly double _originalTime;
    private readonly bool _originalPlaying;
    private bool _syncingResolution;
    private bool _syncingCamera;
    private bool _exporting;
    private bool BatchExport => _allAnimations.Checked || _selectedAnimations.Checked;

    public ExportDialog(
        FbxSession session,
        AnimationPlayer player,
        bool premultiplyAlpha)
    {
        _session = session;
        _player = player;

        _originalAnimationIndex = player.AnimationIndex;
        _originalTime = player.TimeSeconds;
        _originalPlaying = player.Playing;
        _player.Playing = false;

        Text = "Export / Framing";
        Width = 1380;
        Height = 900;
        MinimumSize = new Size(1050, 700);
        StartPosition = FormStartPosition.CenterParent;
        WindowState = FormWindowState.Maximized;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 10.5F);

        BuildUi(premultiplyAlpha);

        // This is a second OpenGL viewer hosted by the export window. It must be
        // explicitly attached to the already-loaded FBX/session; otherwise it
        // remains in its default "Open an FBX" state.
        _preview.Attach(_session, _player);

        HookEvents();
        PopulateAnimations();

        Shown += (_, _) =>
        {
            BeginInvoke(new Action(() => UiLayout.OpenSidebar(_root, DeviceDpi)));
            _preview.SetPremultiplyAlpha(_pma.Checked);
            ApplyBackground();
            ResetFraming();
            LayoutPreview();
        };

        FormClosing += (_, e) =>
        {
            if (_exporting)
            {
                e.Cancel = true;
                CancelExport();
                return;
            }

            RestorePlayer();
        };
    }

    private void BuildUi(bool premultiplyAlpha)
    {
        var root = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1
        };
        _root = root;

        var settingsScroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(12)
        };
        _settingsScroll = settingsScroll;

        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddSection(settings, "Animation");
        ConfigureCombo(_animation);
        AddRow(settings, "Preview take", _animation);

        _timeline.Minimum = 0;
        _timeline.Maximum = 1000;
        _timeline.TickStyle = TickStyle.None;
        _timeline.Dock = DockStyle.Fill;
        AddRow(settings, "Frame", _timeline);

        var scopePanel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        scopePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _currentAnimation.Text = "Only this take";
        _currentAnimation.Checked = true;
        _currentAnimation.AutoSize = true;
        _allAnimations.Text = "All takes";
        _allAnimations.AutoSize = true;
        _selectedAnimations.Text = "Selected takes";
        _selectedAnimations.AutoSize = true;
        scopePanel.Controls.Add(_currentAnimation, 0, 0);
        scopePanel.Controls.Add(_allAnimations, 0, 1);
        scopePanel.Controls.Add(_selectedAnimations, 0, 2);
        AddRow(settings, "Export", scopePanel);

        _selectedTakes.CheckOnClick = true;
        _selectedTakes.IntegralHeight = false;
        _selectedTakes.HorizontalScrollbar = true;
        _selectedTakes.Dock = DockStyle.Fill;
        _selectedTakes.MinimumSize = new Size(0, Font.Height * 5 + 12);
        var selectedLabel = AddRow(settings, "Choose takes", _selectedTakes);
        selectedLabel.Visible = false;
        _selectedTakes.Visible = false;
        _selectedAnimations.CheckedChanged += (_, _) =>
        {
            selectedLabel.Visible = _selectedAnimations.Checked;
            _selectedTakes.Visible = _selectedAnimations.Checked;
            if (_selectedAnimations.Checked)
                UpdateDefaultOutput();
        };

        AddSection(settings, "Output");
        ConfigureCombo(_format);
        _format.Items.AddRange(["WebP", "MP4", "MOV"]);
        _format.SelectedIndex = 0;
        AddRow(settings, "Format", _format);

        ConfigureCombo(_resolutionPreset);
        _resolutionPreset.Items.AddRange([
            "512 × 512",
            "1024 × 1024",
            "2048 × 2048",
            "1920 × 1080",
            "1080 × 1920",
            "2560 × 1440",
            "Custom"
        ]);
        _resolutionPreset.SelectedIndex = 2;
        AddRow(settings, "Preset", _resolutionPreset);

        ConfigureNumber(_width, 64, 8192, 2048);
        ConfigureNumber(_height, 64, 8192, 2048);

        var sizePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3
        };
        sizePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        sizePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sizePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _width.Dock = DockStyle.Fill;
        _height.Dock = DockStyle.Fill;
        _width.MinimumSize = new Size(96, 32);
        _height.MinimumSize = new Size(96, 32);

        var sizeX = new Label
        {
            Text = "×",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };

        sizePanel.Controls.Add(_width, 0, 0);
        sizePanel.Controls.Add(sizeX, 1, 0);
        sizePanel.Controls.Add(_height, 2, 0);
        AddRow(settings, "Size", sizePanel);

        ConfigureNumber(_fps, 1, 240, 30);
        AddRow(settings, "FPS", _fps);

        ConfigureNumber(_quality, 1, 100, 90);
        AddRow(settings, "Quality", _quality);

        _transparent.Text = "Transparent background";
        _transparent.Checked = true;
        _transparent.AutoSize = true;
        AddRow(settings, "Background", _transparent);

        _backgroundButton.Text = "Solid color...";
        _backgroundButton.AutoSize = true;
        _backgroundButton.BackColor = _backgroundColor;
        _backgroundButton.ForeColor = Color.White;
        AddRow(settings, string.Empty, _backgroundButton);

        _pma.Text = "Premultiply Alpha (PMA)";
        _pma.Checked = premultiplyAlpha;
        _pma.AutoSize = true;
        AddRow(settings, "Alpha", _pma);

        AddSection(settings, "Framing");
        ConfigureNumber(_zoom, 10, 1000, 100);
        _zoom.DecimalPlaces = 1;
        _zoom.Increment = 1;
        AddRow(settings, "Zoom %", _zoom);

        ConfigureSignedNumber(_offsetX, -500, 500, 0);
        ConfigureSignedNumber(_offsetY, -500, 500, 0);
        _offsetX.DecimalPlaces = 1;
        _offsetY.DecimalPlaces = 1;
        _offsetX.Increment = 1;
        _offsetY.Increment = 1;
        AddRow(settings, "X %", _offsetX);
        AddRow(settings, "Y %", _offsetY);

        var freeCamera = new CheckBox
        {
            Text = "Free camera",
            AutoSize = true,
            Checked = false
        };
        freeCamera.CheckedChanged += (_, _) => _preview.FreeCameraEnabled = freeCamera.Checked;
        AddRow(settings, "Preview", freeCamera);

        var fitButton = ActionButton("Fit current pose", (_, _) => ResetFraming());
        AddRow(settings, string.Empty, fitButton);

        AddSection(settings, "Destination");

        var outputPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2
        };
        outputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _output.Dock = DockStyle.Fill;
        var browse = ActionButton("Browse...", (_, _) => BrowseOutput());
        browse.Dock = DockStyle.Fill;
        outputPanel.Controls.Add(_output, 0, 0);
        outputPanel.Controls.Add(browse, 1, 0);
        settings.Controls.Add(outputPanel, 0, settings.RowCount);
        settings.SetColumnSpan(outputPanel, 2);
        settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        settings.RowCount++;

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(12, 8, 12, 10)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _exportButton.Text = "EXPORT";
        _exportButton.Font = new Font(Font, FontStyle.Bold);
        _exportButton.Dock = DockStyle.Fill;
        _exportButton.AutoSize = true;
        _exportButton.MinimumSize = new Size(84, 46);
        _exportButton.Click += async (_, _) => await ExportAsync();

        _cancelButton.Text = "Cancel export";
        _cancelButton.Dock = DockStyle.Fill;
        _cancelButton.AutoSize = true;
        _cancelButton.MinimumSize = new Size(84, 46);
        _cancelButton.Enabled = false;
        _cancelButton.Click += (_, _) => CancelExport();
        footer.Controls.Add(_exportButton, 0, 0);
        footer.Controls.Add(_cancelButton, 1, 0);

        _progress.Dock = DockStyle.Fill;
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.MinimumSize = new Size(0, 24);
        footer.Controls.Add(_progress, 0, 1);
        footer.SetColumnSpan(_progress, 2);

        _status.Text = "Adjust crop, then export.";
        _status.AutoSize = true;
        footer.Resize += (_, _) =>
            _status.MaximumSize = new Size(
                Math.Max(180, footer.ClientSize.Width - footer.Padding.Horizontal), 0);
        footer.Controls.Add(_status, 0, 2);
        footer.SetColumnSpan(_status, 2);

        settingsScroll.Controls.Add(settings);
        root.Panel1.Controls.Add(settingsScroll);
        root.Panel1.Controls.Add(footer);

        _previewHost.Dock = DockStyle.Fill;
        _previewHost.BackColor = Color.FromArgb(15, 15, 15);
        _previewHost.Padding = new Padding(12);

        // Red border = exact output frame. What is inside this box is
        // what the selected resolution/aspect will export.
        _previewFrame.BackColor = Color.Red;
        _previewFrame.Padding = new Padding(2);

        _preview.Dock = DockStyle.Fill;
        _previewFrame.Controls.Add(_preview);
        _previewHost.Controls.Add(_previewFrame);
        root.Panel2.Controls.Add(_previewHost);

        Controls.Add(root);

        UpdateFormatUi();
        UpdateDefaultOutput();
    }

    private void HookEvents()
    {
        _previewHost.Resize += (_, _) => LayoutPreview();

        _animation.SelectedIndexChanged += (_, _) =>
        {
            if (_animation.SelectedItem is not AnimationTake take)
                return;

            _player.SelectAnimation(take.Index);
            _player.Playing = false;
            _timeline.Value = 0;
            _preview.InvalidateScene();

            if (_currentAnimation.Checked)
                UpdateDefaultOutput();
        };

        _timeline.Scroll += (_, _) =>
        {
            if (_player.DurationSeconds <= 0)
                return;

            _player.SetTime(
                _timeline.Value / 1000.0 *
                _player.DurationSeconds);

            _preview.InvalidateScene();
        };

        _format.SelectedIndexChanged += (_, _) =>
        {
            UpdateFormatUi();
            UpdateDefaultOutput();
        };

        _resolutionPreset.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingResolution)
                return;

            _syncingResolution = true;
            try
            {
                switch (_resolutionPreset.SelectedIndex)
                {
                    case 0: SetSize(512, 512); break;
                    case 1: SetSize(1024, 1024); break;
                    case 2: SetSize(2048, 2048); break;
                    case 3: SetSize(1920, 1080); break;
                    case 4: SetSize(1080, 1920); break;
                    case 5: SetSize(2560, 1440); break;
                }
            }
            finally
            {
                _syncingResolution = false;
            }

            LayoutPreview();
            _preview.InvalidateScene();
        };

        _width.ValueChanged += (_, _) => ResolutionChanged();
        _height.ValueChanged += (_, _) => ResolutionChanged();

        _zoom.ValueChanged += (_, _) => ApplyFraming();
        _offsetX.ValueChanged += (_, _) => ApplyFraming();
        _offsetY.ValueChanged += (_, _) => ApplyFraming();
        _preview.CameraChanged += (_, framing) =>
        {
            // Keep the visible red export frame and the numeric controls in
            // sync with mouse navigation; captured frames use this same view.
            _syncingCamera = true;
            try
            {
                _zoom.Value = Math.Clamp(Math.Round((decimal)framing.Zoom * 100m, 1), _zoom.Minimum, _zoom.Maximum);
                _offsetX.Value = Math.Clamp(Math.Round((decimal)framing.OffsetX * 100m, 1), _offsetX.Minimum, _offsetX.Maximum);
                _offsetY.Value = Math.Clamp(Math.Round((decimal)framing.OffsetY * 100m, 1), _offsetY.Minimum, _offsetY.Maximum);
            }
            finally
            {
                _syncingCamera = false;
            }
            ApplyFraming();
        };

        _transparent.CheckedChanged += (_, _) =>
        {
            ApplyBackground();
            _backgroundButton.Enabled = !_transparent.Checked;
        };

        _backgroundButton.Click += (_, _) =>
        {
            using var colorDialog = new ColorDialog
            {
                Color = _backgroundColor,
                FullOpen = true
            };

            if (colorDialog.ShowDialog(this) != DialogResult.OK)
                return;

            _backgroundColor = colorDialog.Color;
            _backgroundButton.BackColor = _backgroundColor;
            _backgroundButton.ForeColor =
                _backgroundColor.GetBrightness() < 0.45f
                    ? Color.White
                    : Color.Black;

            ApplyBackground();
        };

        _pma.CheckedChanged += (_, _) =>
        {
            _preview.SetPremultiplyAlpha(_pma.Checked);
            _preview.InvalidateScene();
        };

        _currentAnimation.CheckedChanged += (_, _) =>
        {
            UpdateDefaultOutput();
        };

        _allAnimations.CheckedChanged += (_, _) =>
        {
            UpdateDefaultOutput();
        };
        _selectedTakes.ItemCheck += (_, _) =>
        {
            if (IsHandleCreated)
                BeginInvoke(new Action(() =>
                    _status.Text = $"{_selectedTakes.CheckedItems.Count} takes selected."));
        };
    }

    private void PopulateAnimations()
    {
        _animation.Items.Clear();
        _selectedTakes.Items.Clear();

        foreach (var take in _session.Animations)
        {
            _animation.Items.Add(take);
            _selectedTakes.Items.Add(take);
        }

        if (_animation.Items.Count == 0)
            return;

        var index = _originalAnimationIndex >= 0 &&
                    _originalAnimationIndex < _session.Animations.Count
            ? _originalAnimationIndex
            : 0;

        _animation.SelectedIndex = index;
        _selectedTakes.SetItemChecked(index, true);

        if (_originalAnimationIndex >= 0)
            _player.SetTime(_originalTime);

        _preview.InvalidateScene();
    }

    private void ResetFraming()
    {
        _zoom.Value = 100;
        _offsetX.Value = 0;
        _offsetY.Value = 0;
        _preview.ResetFraming();
        _preview.InvalidateScene();
        _status.Text = "Framing reset to the current pose.";
    }

    private void ApplyFraming()
    {
        if (_syncingCamera)
            return;

        _preview.SetFramingTransform(
            (float)_zoom.Value / 100f,
            (float)_offsetX.Value / 100f,
            (float)_offsetY.Value / 100f);
    }

    private void ApplyBackground()
    {
        _preview.SetBackground(
            _transparent.Checked,
            _backgroundColor);

        _previewHost.BackColor = _transparent.Checked
            ? Color.FromArgb(38, 38, 38)
            : _backgroundColor;
    }

    private void LayoutPreview()
    {
        if (_previewHost.ClientSize.Width <= 0 ||
            _previewHost.ClientSize.Height <= 0)
        {
            return;
        }

        var availableWidth = Math.Max(1, _previewHost.ClientSize.Width - 28);
        var availableHeight = Math.Max(1, _previewHost.ClientSize.Height - 28);
        var aspect = (double)_width.Value / Math.Max(1.0, (double)_height.Value);

        var w = availableWidth;
        var h = (int)Math.Round(w / aspect);

        if (h > availableHeight)
        {
            h = availableHeight;
            w = (int)Math.Round(h * aspect);
        }

        w = Math.Max(1, w);
        h = Math.Max(1, h);

        _previewFrame.Bounds = new Rectangle(
            (_previewHost.ClientSize.Width - w) / 2,
            (_previewHost.ClientSize.Height - h) / 2,
            w,
            h);

        _preview.InvalidateScene();
    }

    private void ResolutionChanged()
    {
        if (!_syncingResolution)
        {
            var matchingPreset = FindPreset(
                (int)_width.Value,
                (int)_height.Value);

            _syncingResolution = true;
            try
            {
                _resolutionPreset.SelectedIndex = matchingPreset;
            }
            finally
            {
                _syncingResolution = false;
            }
        }

        LayoutPreview();
    }

    private int FindPreset(int width, int height)
    {
        return (width, height) switch
        {
            (512, 512) => 0,
            (1024, 1024) => 1,
            (2048, 2048) => 2,
            (1920, 1080) => 3,
            (1080, 1920) => 4,
            (2560, 1440) => 5,
            _ => 6
        };
    }

    private void SetSize(int width, int height)
    {
        _width.Value = Math.Clamp(width, (int)_width.Minimum, (int)_width.Maximum);
        _height.Value = Math.Clamp(height, (int)_height.Minimum, (int)_height.Maximum);
    }

    private void UpdateFormatUi()
    {
        var format = CurrentFormat();
        var alphaSupported = format != ExportFormat.Mp4;

        _transparent.Enabled = alphaSupported;

        if (!alphaSupported)
            _transparent.Checked = false;

        _quality.Enabled = format != ExportFormat.Mov;
        _backgroundButton.Enabled = !_transparent.Checked;
        ApplyBackground();
    }

    private ExportFormat CurrentFormat()
        => _format.SelectedIndex switch
        {
            1 => ExportFormat.Mp4,
            2 => ExportFormat.Mov,
            _ => ExportFormat.WebP
        };

    private void UpdateDefaultOutput()
    {
        if (_session.Scene is null || _animation.SelectedItem is not AnimationTake take)
            return;

        var directory = Path.Combine(
            Path.GetDirectoryName(_session.FilePath)!,
            "AS_FBX_exports");

        if (BatchExport)
        {
            _output.Text = directory;
            return;
        }

        var settings = ReadSettings();
        _output.Text = Path.Combine(
            directory,
            SanitizeFileName(take.Name) + settings.Extension);
    }

    private void BrowseOutput()
    {
        if (BatchExport)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select output folder for the chosen animations",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(_output.Text)
                    ? _output.Text
                    : Path.GetDirectoryName(_session.FilePath)
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
                _output.Text = dialog.SelectedPath;

            return;
        }

        var settings = ReadSettings();

        using var save = new SaveFileDialog
        {
            Filter = settings.Format switch
            {
                ExportFormat.WebP => "Animated WebP (*.webp)|*.webp",
                ExportFormat.Mov => "QuickTime MOV (*.mov)|*.mov",
                _ => "MP4 video (*.mp4)|*.mp4"
            },
            DefaultExt = settings.Extension.TrimStart('.'),
            AddExtension = true,
            FileName = _animation.SelectedItem is AnimationTake take
                ? SanitizeFileName(take.Name) + settings.Extension
                : "animation" + settings.Extension
        };

        if (save.ShowDialog(this) == DialogResult.OK)
            _output.Text = save.FileName;
    }

    private ExportSettings ReadSettings()
        => new()
        {
            Format = CurrentFormat(),
            Width = (int)_width.Value,
            Height = (int)_height.Value,
            Fps = (int)_fps.Value,
            Quality = (int)_quality.Value,
            TransparentBackground = _transparent.Checked,
            BackgroundColor = _backgroundColor,
            ExportAllAnimations = BatchExport,
            OutputPath = _output.Text.Trim()
        };

    private async Task ExportAsync()
    {
        if (_exporting)
            return;

        if (_session.Scene is null ||
            _animation.SelectedItem is not AnimationTake selected)
        {
            MessageBox.Show(this, "No animation selected.");
            return;
        }

        var settings = ReadSettings();

        if (string.IsNullOrWhiteSpace(settings.OutputPath))
        {
            MessageBox.Show(this, "Select an output file/folder first.");
            return;
        }

        if (settings.TransparentBackground && !settings.SupportsTransparency)
        {
            MessageBox.Show(
                this,
                "MP4/H.264 does not support an alpha channel. Choose WebP or MOV for transparent output.");
            return;
        }

        var takes = _allAnimations.Checked
            ? _session.Animations.ToList()
            : _selectedAnimations.Checked
                ? _selectedTakes.CheckedItems.OfType<AnimationTake>().ToList()
                : [selected];

        if (takes.Count == 0)
        {
            MessageBox.Show(this, "Select at least one take to export.");
            return;
        }

        var restoreAnimation = _player.AnimationIndex;
        var restoreTime = _player.TimeSeconds;
        var restorePlaying = _player.Playing;
        var restoreFreeCamera = _preview.FreeCameraEnabled;

        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        _exporting = true;
        _player.Playing = false;
        _preview.FreeCameraEnabled = false;
        _settingsScroll.Enabled = false;
        _exportButton.Enabled = false;
        _cancelButton.Enabled = true;
        _progress.Value = 0;
        _status.Text = "Starting export...";

        try
        {
            // Let WinForms paint the locked settings and Cancel button first.
            await Task.Yield();
            cancellation.Token.ThrowIfCancellationRequested();
            if (settings.ExportAllAnimations)
                Directory.CreateDirectory(settings.OutputPath);
            else
                Directory.CreateDirectory(Path.GetDirectoryName(settings.OutputPath)!);

            var exporter = new AnimationExporter();

            for (var takeIndex = 0; takeIndex < takes.Count; takeIndex++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var take = takes[takeIndex];

                _player.SelectAnimation(take.Index);
                _player.SetTime(0);
                _preview.InvalidateScene();

                var temp = Path.Combine(
                    Path.GetTempPath(),
                    "AS_FBX_reader",
                    Guid.NewGuid().ToString("N"));

                try
                {
                    var localTakeIndex = takeIndex;
                    var progress = new Progress<int>(p =>
                    {
                        if (!_exporting || cancellation.IsCancellationRequested)
                            return;

                        var overall =
                            (localTakeIndex * 100 + p) /
                            Math.Max(1, takes.Count);

                        _progress.Value = Math.Clamp(overall, 0, 100);
                        _status.Text =
                            $"Rendering {take.Name} | {p}% | {localTakeIndex + 1}/{takes.Count}";
                    });

                    await exporter.ExportPngSequenceAsync(
                        _preview,
                        _player,
                        temp,
                        settings.Width,
                        settings.Height,
                        settings.Fps,
                        progress,
                        cancellation.Token);

                    cancellation.Token.ThrowIfCancellationRequested();

                    var outputPath = settings.ExportAllAnimations
                        ? Path.Combine(
                            settings.OutputPath,
                            SanitizeFileName(take.Name) + settings.Extension)
                        : EnsureExtension(
                            settings.OutputPath,
                            settings.Extension);

                    _status.Text =
                        $"Encoding {Path.GetFileName(outputPath)}...";

                    await exporter.EncodeWithFfmpegAsync(
                        temp,
                        outputPath,
                        settings,
                        cancellation.Token);
                }
                finally
                {
                    try
                    {
                        await Task.Run(() =>
                        {
                            if (Directory.Exists(temp))
                                Directory.Delete(temp, true);
                        });
                    }
                    catch
                    {
                    }
                }
            }

            _progress.Value = 100;
            _status.Text = settings.ExportAllAnimations
                ? $"Done: {takes.Count} animations exported."
                : "Done.";

            MessageBox.Show(
                this,
                settings.ExportAllAnimations
                    ? $"Exported {takes.Count} animations to:\r\n{settings.OutputPath}"
                    : $"Exported:\r\n{EnsureExtension(settings.OutputPath, settings.Extension)}",
                "Export complete");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _status.Text = "Export canceled. Any completed takes remain in the output folder.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.ToString(),
                "Export failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            _status.Text = "Export failed.";
        }
        finally
        {
            _player.SelectAnimation(restoreAnimation);

            if (restoreAnimation >= 0)
                _player.SetTime(restoreTime);

            _player.Playing = restorePlaying;
            _preview.InvalidateScene();

            _exporting = false;
            _exportCancellation = null;
            _settingsScroll.Enabled = true;
            _exportButton.Enabled = true;
            _cancelButton.Enabled = false;
            _preview.FreeCameraEnabled = restoreFreeCamera;
        }
    }

    private void CancelExport()
    {
        if (!_exporting || _exportCancellation?.IsCancellationRequested != false)
            return;

        _cancelButton.Enabled = false;
        _status.Text = "Canceling export...";
        _exportCancellation.Cancel();
    }

    private void RestorePlayer()
    {
        _player.SelectAnimation(_originalAnimationIndex);

        if (_originalAnimationIndex >= 0)
            _player.SetTime(_originalTime);

        _player.Playing = _originalPlaying;
    }

    private static string EnsureExtension(string path, string extension)
        => string.Equals(
               Path.GetExtension(path),
               extension,
               StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.ChangeExtension(path, extension);

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray();

        var result = new string(chars).Trim();

        return string.IsNullOrWhiteSpace(result)
            ? "animation"
            : result;
    }

    private static void ConfigureCombo(ComboBox combo)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Dock = DockStyle.Fill;
        combo.Margin = new Padding(3, 4, 3, 4);
    }

    private static void ConfigureNumber(
        NumericUpDown number,
        int min,
        int max,
        int value)
    {
        number.Minimum = min;
        number.Maximum = max;
        number.Value = value;
        number.Dock = DockStyle.Fill;
        number.ThousandsSeparator = true;
    }

    private static void ConfigureSignedNumber(
        NumericUpDown number,
        int min,
        int max,
        int value)
    {
        number.Minimum = min;
        number.Maximum = max;
        number.Value = value;
        number.Dock = DockStyle.Fill;
    }

    private static void AddSection(
        TableLayoutPanel table,
        string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(table.Font, FontStyle.Bold),
            Padding = new Padding(0, 12, 0, 5)
        };

        table.Controls.Add(label, 0, table.RowCount);
        table.SetColumnSpan(label, 2);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowCount++;
    }

    private static Label AddRow(
        TableLayoutPanel table,
        string label,
        Control control)
    {
        var name = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 7, 0, 0)
        };

        control.Margin = new Padding(3, 4, 3, 4);

        table.Controls.Add(name, 0, table.RowCount);
        table.Controls.Add(control, 1, table.RowCount);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowCount++;
        return name;
    }

    private static Button ActionButton(
        string text,
        EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(84, 36),
            Padding = new Padding(8, 4, 8, 4),
            Margin = new Padding(3)
        };

        button.Click += click;
        return button;
    }
}
