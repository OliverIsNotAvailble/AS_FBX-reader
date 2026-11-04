using AS.FBXReader.Batch;
using AS.FBXReader.Export;
using AS.FBXReader.Models;
using AS.FBXReader.Rendering;

namespace AS.FBXReader;

// Batch UI lives inside the existing MainForm and reuses its single
// FbxSession, AnimationPlayer and OpenGL ViewerControl. No second preview.
public sealed partial class MainForm
{
    private readonly TextBox _batchRoot = new() { PlaceholderText = "Pasta raiz dos FBX..." };
    private readonly Label _batchCurrentLabel = new();
    private readonly Label _batchCounterLabel = new();
    private readonly Label _batchMessage = new();
    private readonly ComboBox _batchFormat = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _batchWidth = Number(64, 8192, 1024);
    private readonly NumericUpDown _batchHeight = Number(64, 8192, 1024);
    private readonly NumericUpDown _batchFps = Number(1, 240, 30);
    private readonly NumericUpDown _batchQuality = Number(1, 100, 90);
    private readonly NumericUpDown _batchZoom = Number(10, 1000, 100);
    private readonly NumericUpDown _batchOffsetX = Number(-500, 500, 0);
    private readonly NumericUpDown _batchOffsetY = Number(-500, 500, 0);
    private readonly CheckBox _batchAllTakes = new() { Text = "Exportar TODAS as animações", Checked = true, AutoSize = true };
    private readonly ComboBox _batchBackgroundMode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _batchColorButton = new() { Text = "Escolher cor..." };
    private readonly Button _batchImageButton = new() { Text = "Escolher imagem..." };
    private readonly Label _batchImageLabel = new() { Text = "Nenhuma imagem selecionada", AutoSize = true, UseMnemonic = false };
    private Color _batchBackgroundColor = Color.Black;
    private string? _batchBackgroundImagePath;
    private readonly CheckBox _batchPma = new() { Text = "Premultiply Alpha (PMA)", Checked = true, AutoSize = true };
    private readonly Button _batchExport = new() { Text = "EXPORTAR → _OK", Height = 50, BackColor = Color.FromArgb(38, 104, 74), ForeColor = Color.White };
    private readonly Button _batchNull = new() { Text = "NULL → _Null", Height = 50, BackColor = Color.FromArgb(130, 58, 64), ForeColor = Color.White };
    private readonly Button _batchCancel = new() { Text = "Cancelar exportação", Height = 36 };
    private readonly Button _batchScan = new() { Text = "Ler pasta / atualizar fila", Height = 38 };
    private readonly Button _batchBrowse = new() { Text = "Selecionar pasta raiz...", Height = 38 };
    private readonly Button _batchReset = new() { Text = "Fit / reset enquadramento", Height = 38 };
    private readonly TabControl _leftTabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _batchTab = new("VLote");
    private readonly TabPage _meshTab = new("Meshes");
    private TableLayoutPanel? _batchControls;
    private FlowLayoutPanel? _mainToolbar;
    private CheckBox? _mainPmaToggle;
    private VLoteQueue? _batchQueue;
    private BatchEntry? _batchEntry;
    private CancellationTokenSource? _batchCancellation;
    private bool _batchBusy;
    private bool _batchUpdatingFraming;
    private bool _batchUpdatingPma;

    // One OpenGL viewer: the frame around it resizes to the ACTUAL export
    // aspect ratio, while _viewer continues to render and capture normally.
    private readonly Panel _outputPreviewHost = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(20, 25, 34)
    };
    private readonly Panel _outputPreviewFrame = new()
    {
        BackColor = Color.FromArgb(105, 168, 231),
        Padding = new Padding(2)
    };
    private bool _exportPreviewPinned;

    private static NumericUpDown Number(int min, int max, int val) => new()
    {
        Minimum = min, Maximum = max, Value = val,
        TextAlign = HorizontalAlignment.Center,
        Width = 112
    };

    private Control BuildBatchPanel()
    {
        // The old FlowLayoutPanel forced all text into fixed 28/36 px rows.
        // At 150-200% Windows DPI, those rows cut off labels, inputs and
        // buttons. A one-column TableLayoutPanel lets EVERY row grow to the
        // actual font/preferred height, with vertical scrolling in the host.
        var panelColor = Color.FromArgb(34, 39, 48);
        var textColor = Color.FromArgb(239, 243, 249);
        var mutedColor = Color.FromArgb(190, 204, 222);
        var accentColor = Color.FromArgb(125, 190, 245);
        var normalButtonColor = Color.FromArgb(57, 70, 90);

        var outer = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = panelColor,
            ForeColor = textColor,
            Font = new Font("Segoe UI", 10f),
            Padding = Padding.Empty
        };
        var list = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Padding = new Padding(12, 12, 16, 18),
            Margin = Padding.Empty,
            BackColor = panelColor
        };
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _batchControls = list;

        // Wrap by the available SIDEBAR width; no text element can expand
        // the table horizontally and hide the right side of the controls.
        var wrapLabels = new List<Label>();
        void Add(Control control, int gapAfter = 4)
        {
            control.Margin = new Padding(2, 3, 2, 3 + gapAfter);
            control.Dock = DockStyle.Fill;
            list.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            list.Controls.Add(control, 0, list.RowCount++);
        }
        Label TextLabel(string value, bool heading = false, bool subtitle = false)
        {
            var label = new Label
            {
                Text = value,
                AutoSize = true,
                UseMnemonic = false,
                ForeColor = heading ? accentColor :
                    subtitle ? mutedColor : textColor,
                Font = heading
                    ? new Font(outer.Font, FontStyle.Bold)
                    : outer.Font,
                TextAlign = ContentAlignment.MiddleLeft
            };
            wrapLabels.Add(label);
            return label;
        }
        void Heading(string title)
        {
            var label = TextLabel(title, heading: true);
            label.Margin = new Padding(2, 17, 2, 8);
            Add(label, 5);
        }
        void StyleButton(Button button, Color background, bool prominent = false)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.BackColor = background;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = Color.FromArgb(107, 124, 148);
            button.FlatAppearance.BorderSize = 1;
            button.MinimumSize = new Size(0,
                Math.Max(prominent ? 50 : 42, outer.Font.Height + 21));
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.Padding = new Padding(10, 7, 10, 7);
            button.Font = prominent
                ? new Font(outer.Font, FontStyle.Bold)
                : outer.Font;
        }
        void StyleCheck(CheckBox check)
        {
            check.AutoSize = false;  // allow text to wrap on narrow sidebars
            check.Height = Math.Max(48, outer.Font.Height * 2 + 14);
            check.MinimumSize = new Size(0, check.Height);
            check.ForeColor = textColor;
            check.BackColor = panelColor;
            check.Font = outer.Font;
            check.UseVisualStyleBackColor = false;
            check.Padding = new Padding(2, 2, 0, 2);
            check.TextAlign = ContentAlignment.MiddleLeft;
        }
        Control Field(string name, Control input)
        {
            var row = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = panelColor,
                MinimumSize = new Size(0, Math.Max(49, outer.Font.Height + 21)),
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57f));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var caption = new Label
            {
                Text = name,
                Dock = DockStyle.Fill,
                AutoSize = true,
                UseMnemonic = false,
                ForeColor = textColor,
                Font = outer.Font,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 4, 7, 4)
            };
            input.Font = outer.Font;
            input.BackColor = Color.White;
            input.ForeColor = Color.FromArgb(25, 31, 41);
            input.Dock = DockStyle.Fill;
            input.MinimumSize = new Size(0, Math.Max(32, outer.Font.Height + 11));
            input.Margin = new Padding(3, 5, 0, 5);
            row.Controls.Add(caption, 0, 0);
            row.Controls.Add(input, 1, 0);
            return row;
        }

        Add(TextLabel("VLote 0.10  |  FBX em lote", heading: true), 8);
        Add(TextLabel("Pasta raiz (procura FBX nas subpastas)"), 1);
        _batchRoot.Font = outer.Font;
        _batchRoot.AutoSize = false;
        _batchRoot.BackColor = Color.White;
        _batchRoot.ForeColor = Color.FromArgb(25, 31, 41);
        _batchRoot.MinimumSize = new Size(0, Math.Max(35, outer.Font.Height + 11));
        Add(_batchRoot);
        StyleButton(_batchBrowse, normalButtonColor);
        StyleButton(_batchScan, normalButtonColor);
        Add(_batchBrowse);
        Add(_batchScan, 11);

        _batchCurrentLabel.ForeColor = textColor;
        _batchCurrentLabel.Font = new Font(outer.Font, FontStyle.Bold);
        _batchCurrentLabel.AutoSize = true;
        _batchCurrentLabel.UseMnemonic = false;
        wrapLabels.Add(_batchCurrentLabel);
        Add(_batchCurrentLabel, 2);

        _batchCounterLabel.ForeColor = accentColor;
        _batchCounterLabel.Font = new Font(outer.Font, FontStyle.Bold);
        _batchCounterLabel.AutoSize = true;
        _batchCounterLabel.UseMnemonic = false;
        wrapLabels.Add(_batchCounterLabel);
        Add(_batchCounterLabel, 8);

        Heading("CONFIGURAÇÕES DE EXPORTAÇÃO");
        _batchFormat.Items.AddRange(["WebP", "MP4", "MOV"]);
        _batchFormat.SelectedIndex = 0;
        Add(Field("Formato", _batchFormat));
        Add(Field("Largura", _batchWidth));
        Add(Field("Altura", _batchHeight));
        Add(Field("FPS", _batchFps));
        Add(Field("Qualidade", _batchQuality));
        foreach (var check in new[] { _batchAllTakes, _batchPma })
        {
            StyleCheck(check);
            Add(check, 2);
        }

        Heading("FUNDO / BACKGROUND");
        _batchBackgroundMode.Items.AddRange(["Transparente (WebP/MOV)", "Cor sólida", "Imagem"]);
        _batchBackgroundMode.SelectedIndex = 0;
        Add(Field("Modo", _batchBackgroundMode));
        StyleButton(_batchColorButton, normalButtonColor);
        StyleButton(_batchImageButton, normalButtonColor);
        Add(Field("Cor", _batchColorButton));
        Add(Field("Imagem", _batchImageButton));
        _batchImageLabel.ForeColor = mutedColor;
        _batchImageLabel.Font = outer.Font;
        wrapLabels.Add(_batchImageLabel);
        Add(_batchImageLabel, 5);
        Add(TextLabel("Imagem ajustada ao quadro sem distorcer (recorte central).",
            subtitle: true), 6);

        Heading("ENQUADRAMENTO DO PREVIEW");
        Add(Field("Zoom (%)", _batchZoom));
        Add(Field("X (%)", _batchOffsetX));
        Add(Field("Y (%)", _batchOffsetY));
        StyleButton(_batchReset, normalButtonColor);
        Add(_batchReset, 5);
        Add(TextLabel("Os ajustes usam o preview principal.", subtitle: true), 5);

        Heading("EXPORTAR / CLASSIFICAR");
        StyleButton(_batchExport, Color.FromArgb(38, 104, 74), prominent: true);
        StyleButton(_batchNull, Color.FromArgb(130, 58, 64), prominent: true);
        StyleButton(_batchCancel, normalButtonColor);
        Add(_batchExport, 5);
        Add(_batchNull, 5);
        Add(_batchCancel, 13);

        _batchMessage.ForeColor = mutedColor;
        _batchMessage.Font = outer.Font;
        _batchMessage.AutoSize = true;
        _batchMessage.UseMnemonic = false;
        wrapLabels.Add(_batchMessage);
        Add(_batchMessage, 10);

        Add(TextLabel("Vídeos → _EXT  |  Arquivos → _OK / _Null",
            subtitle: true), 4);
        Add(TextLabel("A pasta só é movida depois de classificar todos os FBX dela.",
            subtitle: true), 12);

        outer.Controls.Add(list);

        // Account for actual scrollbar width and Windows scaling. Labels
        // have no fixed height: they wrap and the table grows vertically.
        void ResizeContent()
        {
            var width = Math.Max(140,
                outer.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
            list.Width = width;
            var textWidth = Math.Max(100, width - list.Padding.Horizontal - 14);
            foreach (var label in wrapLabels)
                label.MaximumSize = new Size(textWidth, 0);
        }
        outer.Resize += (_, _) => ResizeContent();
        outer.HandleCreated += (_, _) => ResizeContent();
        ResizeContent();
        _batchBrowse.Click += (_, _) =>
        {
            if (_batchBusy) return;
            using var picker = new FolderBrowserDialog
            {
                Description = "Selecione a pasta raiz que contém as subpastas FBX",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(_batchRoot.Text)
                    ? _batchRoot.Text : Environment.GetFolderPath(
                        Environment.SpecialFolder.MyDocuments)
            };
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                _batchRoot.Text = picker.SelectedPath;
                ScanBatch();
            }
        };
        _batchScan.Click += (_, _) => ScanBatch();
        _batchWidth.ValueChanged += (_, _) => LayoutMainPreview();
        _batchHeight.ValueChanged += (_, _) => LayoutMainPreview();
        _leftTabs.SelectedIndexChanged += (_, _) => LayoutMainPreview();
        _batchExport.Click += async (_, _) => await ExportBatchAsync();
        _batchNull.Click += (_, _) => MarkBatchNull();
        _batchCancel.Click += (_, _) => _batchCancellation?.Cancel();
        _batchReset.Click += (_, _) =>
        {
            if (_batchBusy || _session.Scene is null) return;
            _viewer.ResetFraming();
            SyncBatchFraming();
        };
        foreach (var num in new[] { _batchZoom, _batchOffsetX, _batchOffsetY })
            num.ValueChanged += (_, _) =>
            {
                if (_batchBusy || _batchUpdatingFraming || _session.Scene is null) return;
                _viewer.SetFramingTransform((float)_batchZoom.Value / 100,
                    (float)_batchOffsetX.Value / 100,
                    (float)_batchOffsetY.Value / 100);
            };
        _batchPma.CheckedChanged += (_, _) =>
        {
            if (_batchUpdatingPma) return;
            SetSharedPma(_batchPma.Checked);
        };
        _batchFormat.SelectedIndexChanged += (_, _) =>
        {
            if (_batchFormat.SelectedIndex == 1 && _batchBackgroundMode.SelectedIndex == 0)
                _batchBackgroundMode.SelectedIndex = 1; // MP4 cannot store alpha.
            ApplyBatchBackground();
        };
        _batchBackgroundMode.SelectedIndexChanged += (_, _) =>
        {
            if (_batchFormat.SelectedIndex == 1 && _batchBackgroundMode.SelectedIndex == 0)
            {
                _batchBackgroundMode.SelectedIndex = 1;
                return;
            }
            ApplyBatchBackground();
        };
        _batchColorButton.Click += (_, _) =>
        {
            using var picker = new ColorDialog { Color = _batchBackgroundColor, FullOpen = true };
            if (picker.ShowDialog(this) != DialogResult.OK)
                return;
            _batchBackgroundColor = picker.Color;
            _batchBackgroundMode.SelectedIndex = 1;
            ApplyBatchBackground();
        };
        _batchImageButton.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog
            {
                Title = "Selecionar imagem de fundo",
                Filter = "Imagens (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Todos os arquivos (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
                FileName = _batchBackgroundImagePath ?? string.Empty
            };
            if (picker.ShowDialog(this) != DialogResult.OK)
                return;
            try
            {
                using var bitmap = new Bitmap(picker.FileName);
                if (bitmap.Width <= 0 || bitmap.Height <= 0)
                    throw new InvalidDataException("Imagem inválida.");
                _batchBackgroundImagePath = Path.GetFullPath(picker.FileName);
                _batchBackgroundMode.SelectedIndex = 2;
                ApplyBatchBackground();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or
                                       OutOfMemoryException or InvalidDataException)
            {
                MessageBox.Show(this, "Não consegui carregar a imagem: " + ex.Message,
                    "VLote: fundo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        ApplyBatchBackground();
        _batchTab.Controls.Add(outer);
        _batchExport.Enabled = _batchNull.Enabled = false;
        _batchCancel.Enabled = false;
        _batchCurrentLabel.Text = "Nenhuma fila aberta";
        _batchCounterLabel.Text = "0 / 0";
        _batchMessage.Text = "Escolha a pasta raiz para iniciar.";
        return _leftTabs;
    }

    /// <summary>
    /// Called when main editor is resized, when VLote/Meshes tab changes,
    /// and whenever export Width/Height is edited. The content of the blue
    /// frame has the SAME aspect ratio as the offscreen PNG/FFmpeg capture.
    /// </summary>
    private void LayoutMainPreview()
    {
        if (_outputPreviewHost.IsDisposed || _outputPreviewFrame.IsDisposed)
            return;

        bool showExportFrame = _leftTabs.SelectedTab == _batchTab || _exportPreviewPinned;
        _outputPreviewFrame.SuspendLayout();
        try
        {
            if (!showExportFrame)
            {
                // Preserve the original full-size viewer for Meshes mode,
                // until an export format has explicitly been chosen.
                _outputPreviewFrame.Padding = Padding.Empty;
                _outputPreviewFrame.BackColor = _outputPreviewHost.BackColor;
                _outputPreviewFrame.Bounds = _outputPreviewHost.ClientRectangle;
            }
            else
            {
                // 1024x1024 => square, 1920x1080 => 16:9,
                // 720x1280 => portrait. Borders are outside the GL image.
                var box = OutputAspectLayout.Fit(
                    _outputPreviewHost.ClientSize,
                    (int)_batchWidth.Value,
                    (int)_batchHeight.Value);
                if (box == Rectangle.Empty) return;
                bool tiny = box.Size == _outputPreviewHost.ClientSize;
                _outputPreviewFrame.Padding = tiny ? Padding.Empty : new Padding(2);
                _outputPreviewFrame.BackColor = tiny
                    ? _outputPreviewHost.BackColor
                    : Color.FromArgb(105, 168, 231);
                _outputPreviewFrame.Bounds = box;
            }
        }
        finally
        {
            _outputPreviewFrame.ResumeLayout();
        }
        _viewer.InvalidateScene();
    }

    private void InitializeMainPreview()
    {
        _viewer.Dock = DockStyle.Fill;
        _outputPreviewFrame.Controls.Add(_viewer);
        _outputPreviewHost.Controls.Add(_outputPreviewFrame);
        _outputPreviewHost.Resize += (_, _) => LayoutMainPreview();
        _outputPreviewHost.HandleCreated += (_, _) => LayoutMainPreview();
        LayoutMainPreview();
    }

    /// <summary>
    /// Keep dimensions AND camera in the main viewer after the original
    /// Export / Framing window closes, instead of reverting to a stretched
    /// 16:9 editor canvas that doesn't match the export.
    /// </summary>
    private void UseExportDialogFraming(int width, int height, FramingState frame)
    {
        if (width < _batchWidth.Minimum || width > _batchWidth.Maximum ||
            height < _batchHeight.Minimum || height > _batchHeight.Maximum)
            return;
        _batchWidth.Value = width;
        _batchHeight.Value = height;
        _exportPreviewPinned = true;
        _viewer.SetFraming(frame);
        SyncBatchFraming();
        LayoutMainPreview();
    }

    private void ApplyBatchBackground()
    {
        var mode = _batchBackgroundMode.SelectedIndex;
        _batchColorButton.Visible = mode == 1;
        _batchImageButton.Visible = mode == 2;
        _batchImageLabel.Visible = mode == 2;
        _batchColorButton.BackColor = _batchBackgroundColor;
        _batchColorButton.ForeColor = _batchBackgroundColor.GetBrightness() < .45f
            ? Color.White : Color.Black;
        _batchImageLabel.Text = string.IsNullOrWhiteSpace(_batchBackgroundImagePath)
            ? "Nenhuma imagem selecionada"
            : Path.GetFileName(_batchBackgroundImagePath);
        _batchImageLabel.Tag = _batchBackgroundImagePath;

        _viewer.SetBackground(mode == 0, _batchBackgroundColor);
        if (mode == 2 && !string.IsNullOrWhiteSpace(_batchBackgroundImagePath) &&
            File.Exists(_batchBackgroundImagePath))
        {
            try
            {
                _viewer.SetBackgroundImage(_batchBackgroundImagePath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException)
            {
                _viewer.SetBackgroundImage(null);
                _batchImageLabel.Text = "Erro ao abrir imagem: " + ex.Message;
            }
        }
        else
        {
            _viewer.SetBackgroundImage(null);
        }
        _viewer.InvalidateScene();
    }

    private void SetSharedPma(bool enabled)
    {
        if (_batchUpdatingPma) return;
        _batchUpdatingPma = true;
        try
        {
            if (_mainPmaToggle is not null)
                _mainPmaToggle.Checked = enabled;
            _batchPma.Checked = enabled;
            _viewer.SetPremultiplyAlpha(enabled);
        }
        finally { _batchUpdatingPma = false; }
    }

    private void SyncBatchFraming()
    {
        if (_session.Scene is null) return;
        var f = _viewer.GetFraming();
        _batchUpdatingFraming = true;
        try
        {
            _batchZoom.Value = Math.Clamp((decimal)(f.Zoom * 100),
                _batchZoom.Minimum, _batchZoom.Maximum);
            _batchOffsetX.Value = Math.Clamp((decimal)(f.OffsetX * 100),
                _batchOffsetX.Minimum, _batchOffsetX.Maximum);
            _batchOffsetY.Value = Math.Clamp((decimal)(f.OffsetY * 100),
                _batchOffsetY.Minimum, _batchOffsetY.Maximum);
        }
        finally { _batchUpdatingFraming = false; }
    }

    private void BatchStatus(string message)
    {
        _batchMessage.Text = message;
        _status.Text = "VLote: " + message;
        Console.WriteLine("[VLote] " + message);
    }

    private void UpdateBatchUi()
    {
        _batchCurrentLabel.Text = _batchEntry is null
            ? "Fila concluída / sem FBX pendente"
            : $"{_batchEntry.Name}\r\n{Path.GetFileName(_batchEntry.GroupPath)}";
        _batchCounterLabel.Text = _batchQueue?.Counter ?? "0 / 0";
        _batchExport.Enabled = !_batchBusy && _batchEntry is not null &&
                               _session.Scene is not null &&
                               string.Equals(_session.FilePath, _batchEntry.FilePath,
                                   StringComparison.OrdinalIgnoreCase) &&
                               _session.Animations.Count > 0;
        _batchNull.Enabled = !_batchBusy && _batchEntry is not null;
        _batchCancel.Enabled = _batchBusy;
        _batchScan.Enabled = !_batchBusy;
        _batchBrowse.Enabled = !_batchBusy;
        _batchRoot.Enabled = !_batchBusy;
        _batchControls?.SuspendLayout();
        try
        {
            if (_batchControls is not null)
            foreach (Control c in _batchControls.Controls)
            {
                if (c == _batchCancel || c == _batchNull || c == _batchExport ||
                    c == _batchCurrentLabel || c == _batchCounterLabel ||
                    c == _batchMessage || c == _batchBrowse || c == _batchScan ||
                    c == _batchRoot)
                    continue;
                c.Enabled = !_batchBusy;
            }
        }
        finally { _batchControls?.ResumeLayout(); }
        if (_mainToolbar is not null)
            _mainToolbar.Enabled = !_batchBusy;
        _parts.Enabled = !_batchBusy;
        _timeline.Enabled = !_batchBusy;
    }

    private void ScanBatch()
    {
        if (_batchBusy) return;
        try
        {
            _batchQueue = new VLoteQueue(_batchRoot.Text);
            _batchEntry = null;
            _leftTabs.SelectedTab = _batchTab;
            LoadNextBatch();
        }
        catch (Exception ex)
        {
            _batchQueue = null;
            _batchEntry = null;
            UpdateBatchUi();
            MessageBox.Show(this, ex.Message, "VLote: erro na pasta",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadNextBatch()
    {
        _batchEntry = _batchQueue?.Current;
        if (_batchEntry is null)
        {
            BatchStatus(_batchQueue?.Total > 0
                ? "Todos os FBX da fila foram classificados!"
                : "Nenhum FBX encontrado nas subpastas (excluindo _OK/_Null/_EXT).");
            UpdateBatchUi();
            return;
        }
        try
        {
            LoadFbxFile(_batchEntry.FilePath);
            _viewer.ResetFraming();
            SyncBatchFraming();
            _player.Playing = false; // no moving model while choosing export settings
            BatchStatus($"Carregado {_batchEntry.Name}; ajuste e exporte ou marque NULL.");
        }
        catch (Exception ex)
        {
            // Retain the pending item so the user can move a broken FBX to NULL.
            BatchStatus($"Não abriu {_batchEntry.Name}: {ex.Message}. Marque NULL ou revise os arquivos.");
        }
        UpdateBatchUi();
    }

    private void ClearBatchForManualOpen()
    {
        _batchQueue = null;
        _batchEntry = null;
        UpdateBatchUi();
        BatchStatus("Fila pausada. Selecione a mesma raiz para retomar.");
    }

    private void MarkBatchNull()
    {
        if (_batchBusy || _batchQueue is null || _batchEntry is null) return;
        var entry = _batchEntry;
        try
        {
            // Remove the current importer references before moving its folder.
            ReleaseBatchScene();
            var message = _batchQueue.Complete(entry, BatchDecision.Null);
            BatchStatus(message);
            LoadNextBatch();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "VLote: erro ao mover para _Null",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            LoadNextBatch();
        }
    }

    private void ReleaseBatchScene()
    {
        _session.Clear();
        _player.Rebuild();
        _viewer.Attach(_session, _player);
        _animations.Items.Clear();
        _parts.Nodes.Clear();
        _viewer.InvalidateScene();
    }

    private async Task ExportBatchAsync()
    {
        if (_batchBusy || _batchQueue is null || _batchEntry is null ||
            _session.Scene is null)
            return;
        var entry = _batchEntry;
        var queue = _batchQueue;
        var takes = _batchAllTakes.Checked
            ? _session.Animations.ToList()
            : _animations.SelectedItem is AnimationTake take
                ? new List<AnimationTake> { take } : new List<AnimationTake>();
        if (takes.Count == 0)
        {
            MessageBox.Show(this, "Nenhuma animação disponível.", "VLote");
            return;
        }
        if (_batchBackgroundMode.SelectedIndex == 0 && _batchFormat.SelectedIndex == 1)
        {
            MessageBox.Show(this,
                "MP4 não preserva transparência. Use WebP/MOV ou desmarque fundo transparente.");
            return;
        }
        // ForceVisible deliberately ignores FBX hide/show curves. Exporting
        // several takes with forced sprites duplicates alternative eyes,
        // mouths, arms and legs. Offer to restore automatic timing first.
        var forcedCount = _session.Parts.Count(part => part.Visible && part.ForceVisible);
        if (forcedCount > 0)
        {
            var answer = MessageBox.Show(this,
                $"{forcedCount} mesh(es) estão em FORCED.\r\n\r\n" +
                "Isso ignora os switches 0/1 do FBX e pode duplicar olhos, bocas, " +
                "braços e pernas durante a animação.\r\n\r\n" +
                "SIM = restaurar a visibilidade automática de TODAS as meshes e exportar.\r\n" +
                "NÃO = exportar exatamente com os FORCED atuais (modo debug).\r\n" +
                "CANCELAR = voltar sem exportar.",
                "VLote: visibilidade automática ou Forced?",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);
            if (answer == DialogResult.Cancel)
                return;
            if (answer == DialogResult.Yes)
                SetAll(true);
        }

        var opts = new ExportSettings
        {
            Format = (ExportFormat)(_batchFormat.SelectedIndex switch
            {
                0 => (int)ExportFormat.WebP,
                1 => (int)ExportFormat.Mp4,
                _ => (int)ExportFormat.Mov
            }),
            Width = (int)_batchWidth.Value,
            Height = (int)_batchHeight.Value,
            Fps = (int)_batchFps.Value,
            Quality = (int)_batchQuality.Value,
            TransparentBackground = _batchBackgroundMode.SelectedIndex == 0,
            BackgroundColor = _batchBackgroundColor,
            BackgroundImagePath = _batchBackgroundMode.SelectedIndex == 2
                ? _batchBackgroundImagePath : null,
            ExportAllAnimations = _batchAllTakes.Checked
        };
        if (_batchBackgroundMode.SelectedIndex == 2 &&
            (string.IsNullOrWhiteSpace(opts.BackgroundImagePath) ||
             !File.Exists(opts.BackgroundImagePath)))
        {
            MessageBox.Show(this, "Selecione uma imagem de fundo válida antes de exportar.",
                "VLote: fundo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (opts.Format == ExportFormat.Mp4 &&
            (opts.Width % 2 != 0 || opts.Height % 2 != 0))
        {
            MessageBox.Show(this, "MP4 precisa de largura e altura pares.");
            return;
        }

        string? exportStage = null;
        string? publishedDestination = null;
        string? frameTemp = null;
        _batchBusy = true;
        _batchCancellation = new CancellationTokenSource();
        var token = _batchCancellation.Token;
        var originalIndex = _player.AnimationIndex;
        var originalTime = _player.TimeSeconds;
        var originalPlaying = _player.Playing;
        var originalBackground = _viewer.BackgroundColor;
        var originalBackgroundImage = _viewer.BackgroundImagePath;
        var originalTransparency = _viewer.TransparentBackground;
        var originalFraming = _viewer.GetFraming();
        _player.Playing = false;
        UpdateBatchUi();
        try
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            var destination = queue.ExportDestination(entry);
            var parent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(parent);
            exportStage = Path.Combine(parent,
                ".vlote-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(exportStage);
            var exporter = new AnimationExporter();

            // We render directly from the principal viewer. It owns the
            // OpenGL context and keeps the current camera, mesh toggles/PMA.
            _viewer.SetBackground(opts.TransparentBackground, opts.BackgroundColor);
            _viewer.SetBackgroundImage(opts.BackgroundImagePath);
            for (var i = 0; i < takes.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var current = takes[i];
                _player.SelectAnimation(current.Index);
                _player.SetTime(0);
                _viewer.InvalidateScene();
                BatchStatus($"{entry.Name}: renderizando {current.Name} ({i+1}/{takes.Count})");

                frameTemp = Path.Combine(Path.GetTempPath(),
                    "AS_FBX_reader_VLote", Guid.NewGuid().ToString("N"));
                try
                {
                    var position = i;
                    var progress = new Progress<int>(p =>
                    {
                        if (_batchBusy && !token.IsCancellationRequested)
                            BatchStatus($"{entry.Name}: {current.Name} {p}% ({position+1}/{takes.Count})");
                    });
                    await exporter.ExportPngSequenceAsync(
                        _viewer, _player, frameTemp, opts.Width, opts.Height,
                        opts.Fps, progress, token);
                    token.ThrowIfCancellationRequested();
                    var filename = SafeBatchName(current.Name);
                    var target = Path.Combine(exportStage,
                        $"{i + 1:000}_{filename}{opts.Extension}");
                    BatchStatus($"Codificando {Path.GetFileName(target)}...");
                    await exporter.EncodeWithFfmpegAsync(
                        frameTemp, target, opts, token);
                    if (!File.Exists(target) || new FileInfo(target).Length == 0)
                        throw new IOException("FFmpeg não gerou um vídeo válido: " + target);
                }
                finally
                {
                    if (frameTemp is not null && Directory.Exists(frameTemp))
                        await Task.Run(() => Directory.Delete(frameTemp, true));
                    frameTemp = null;
                }
            }

            token.ThrowIfCancellationRequested();
            // Publish all or nothing to the final _EXT location.
            Directory.Move(exportStage, destination);
            exportStage = null;
            publishedDestination = destination;

            // Free source resources before moving the input folder.
            ReleaseBatchScene();
            var result = queue.Complete(entry, BatchDecision.OK);
            BatchStatus($"Exportado em {destination}  |  {result}");
            LoadNextBatch();
        }
        catch (OperationCanceledException)
        {
            BatchStatus("Exportação cancelada: FBX permanece na fila.");
        }
        catch (Exception ex)
        {
            if (publishedDestination is not null)
            {
                BatchStatus($"Vídeos SALVOS em {publishedDestination}; " +
                    $"a movimentação/classificação da origem falhou: {ex.Message}");
                MessageBox.Show(this,
                    "Os vídeos já foram exportados e NÃO foram apagados!\r\n\r\n" +
                    $"Pasta: {publishedDestination}\r\n\r\n" +
                    "O erro aconteceu somente ao organizar o FBX original. " +
                    "Não exporte tudo novamente antes de conferir _EXT.\r\n\r\n" +
                    ex, "VLote: vídeos salvos; organização falhou",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                BatchStatus("FALHA NA EXPORTAÇÃO: " + ex.Message + " | FBX mantido no lugar.");
                MessageBox.Show(this, ex.ToString(), "VLote: exportação falhou",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (frameTemp is not null && Directory.Exists(frameTemp))
            {
                try { Directory.Delete(frameTemp, true); } catch { }
            }
            if (exportStage is not null && Directory.Exists(exportStage))
            {
                try { Directory.Delete(exportStage, true); } catch { }
            }
            // The current session may already be the NEXT FBX after a
            // successful classification. Never restore the old take/camera
            // into that newly loaded model.
            if (_session.Scene is not null &&
                string.Equals(_session.FilePath, entry.FilePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                _player.SelectAnimation(originalIndex);
                if (originalIndex >= 0)
                    _player.SetTime(originalTime);
                _player.Playing = originalPlaying;
                _viewer.SetFraming(originalFraming);
            }
            _viewer.SetBackground(originalTransparency, originalBackground);
            _viewer.SetBackgroundImage(
                originalBackgroundImage is not null && File.Exists(originalBackgroundImage)
                    ? originalBackgroundImage : null);
            _viewer.InvalidateScene();
            _batchCancellation?.Dispose();
            _batchCancellation = null;
            _batchBusy = false;
            UpdateBatchUi();
        }
    }

    private static string SafeBatchName(string value)
    {
        var cleaned = string.Concat(value.Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim(' ', '.');
        return cleaned.Length == 0 ? "animation" : cleaned;
    }

    private void VLoteFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_batchBusy) return;
        e.Cancel = true;
        _batchCancellation?.Cancel();
        BatchStatus("Cancelando exportação antes de fechar...");
    }
}
