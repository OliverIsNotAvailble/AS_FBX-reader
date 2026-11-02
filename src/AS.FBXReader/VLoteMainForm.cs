using AS.FBXReader.Batch;
using AS.FBXReader.Export;
using AS.FBXReader.Models;

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
    private readonly CheckBox _batchTransparent = new() { Text = "Fundo transparente (WebP/MOV)", Checked = true, AutoSize = true };
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
    private FlowLayoutPanel? _batchControls;
    private FlowLayoutPanel? _mainToolbar;
    private CheckBox? _mainPmaToggle;
    private VLoteQueue? _batchQueue;
    private BatchEntry? _batchEntry;
    private CancellationTokenSource? _batchCancellation;
    private bool _batchBusy;
    private bool _batchUpdatingFraming;
    private bool _batchUpdatingPma;

    private static NumericUpDown Number(int min, int max, int val) => new()
    {
        Minimum = min, Maximum = max, Value = val,
        TextAlign = HorizontalAlignment.Center,
        Width = 112
    };

    private Control BuildBatchPanel()
    {
        var outer = new Panel { Dock = DockStyle.Fill, AutoScroll = true,
            BackColor = Color.FromArgb(34, 39, 48) };
        var list = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(12)
        };
        _batchControls = list;
        void Add(Control control)
        {
            control.Margin = new Padding(2, 3, 2, 6);
            list.Controls.Add(control);
        }
        Label Label(string s, bool large = false) => new()
        {
            Text = s, ForeColor = Color.White, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Height = large ? 36 : 28,
            Font = large ? new Font(Font, FontStyle.Bold) : Font
        };
        Control Field(string name, Control control)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 36 };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            row.Controls.Add(new Label
            {
                Text = name, ForeColor = Color.White, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            control.Dock = DockStyle.Fill;
            row.Controls.Add(control, 1, 0);
            return row;
        }

        Add(Label("VLote 0.1 — FBX em lote", true));
        Add(Label("Pasta raiz (subpastas com .fbx)"));
        Add(_batchRoot);
        Add(_batchBrowse);
        Add(_batchScan);
        _batchCurrentLabel.ForeColor = Color.White;
        _batchCurrentLabel.Height = 54;
        _batchCounterLabel.ForeColor = Color.LightSkyBlue;
        _batchCounterLabel.Height = 36;
        _batchCounterLabel.Font = new Font(Font, FontStyle.Bold);
        Add(_batchCurrentLabel);
        Add(_batchCounterLabel);

        Add(Label("CONFIGURAÇÕES DE EXPORTAÇÃO", true));
        _batchFormat.Items.AddRange(["WebP", "MP4", "MOV"]);
        _batchFormat.SelectedIndex = 0;
        Add(Field("Formato", _batchFormat));
        Add(Field("Largura", _batchWidth));
        Add(Field("Altura", _batchHeight));
        Add(Field("FPS", _batchFps));
        Add(Field("Qualidade", _batchQuality));
        Add(_batchAllTakes);
        Add(_batchTransparent);
        Add(_batchPma);

        Add(Label("ENQUADRAMENTO DO PREVIEW", true));
        Add(Field("Zoom (%)", _batchZoom));
        Add(Field("X (%)", _batchOffsetX));
        Add(Field("Y (%)", _batchOffsetY));
        Add(_batchReset);
        Add(Label("Usa a cena principal; pode ajustar câmera livre no preview."));

        Add(Label("EXPORTAR / CLASSIFICAR", true));
        _batchExport.Font = new Font(Font, FontStyle.Bold);
        _batchNull.Font = new Font(Font, FontStyle.Bold);
        Add(_batchExport);
        Add(_batchNull);
        Add(_batchCancel);
        _batchMessage.ForeColor = Color.FromArgb(198, 220, 242);
        _batchMessage.Height = 96;
        Add(_batchMessage);
        Add(Label("Vídeos → _EXT  |  Originais → _OK / _Null"));
        Add(Label("Pasta só é movida após classificar TODOS os FBX dela."));

        outer.Controls.Add(list);
        outer.Resize += (_, _) =>
        {
            var width = Math.Max(210, outer.ClientSize.Width - 30);
            list.Width = width;
            foreach (Control c in list.Controls)
                c.Width = Math.Max(190, width - list.Padding.Horizontal - 10);
        };
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
            if (_batchFormat.SelectedIndex == 1)
                _batchTransparent.Checked = false;
            _batchTransparent.Enabled = _batchFormat.SelectedIndex != 1;
        };
        _batchTab.Controls.Add(outer);
        _batchExport.Enabled = _batchNull.Enabled = false;
        _batchCancel.Enabled = false;
        _batchCurrentLabel.Text = "Nenhuma fila aberta";
        _batchCounterLabel.Text = "0 / 0";
        _batchMessage.Text = "Escolha a pasta raiz para iniciar.";
        return _leftTabs;
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
        if (_batchTransparent.Checked && _batchFormat.SelectedIndex == 1)
        {
            MessageBox.Show(this,
                "MP4 não preserva transparência. Use WebP/MOV ou desmarque fundo transparente.");
            return;
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
            TransparentBackground = _batchTransparent.Checked,
            ExportAllAnimations = _batchAllTakes.Checked
        };
        if (opts.Format == ExportFormat.Mp4 &&
            (opts.Width % 2 != 0 || opts.Height % 2 != 0))
        {
            MessageBox.Show(this, "MP4 precisa de largura e altura pares.");
            return;
        }

        string? exportStage = null;
        string? frameTemp = null;
        _batchBusy = true;
        _batchCancellation = new CancellationTokenSource();
        var token = _batchCancellation.Token;
        var originalIndex = _player.AnimationIndex;
        var originalTime = _player.TimeSeconds;
        var originalPlaying = _player.Playing;
        var originalBackground = _viewer.BackgroundColor;
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
            BatchStatus("FALHA: " + ex.Message + " | FBX mantido no lugar.");
            MessageBox.Show(this, ex.ToString(), "VLote: exportação falhou",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
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
