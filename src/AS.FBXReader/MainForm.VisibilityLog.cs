using System.Text;
using AS.FBXReader.Core;

namespace AS.FBXReader;

public sealed partial class MainForm
{
    private void ExportFbxVisibilityLog()
    {
        if (_session.Scene is null)
        {
            MessageBox.Show(this, "Abra um FBX antes de gerar o LOG.",
                "VLote: LOG", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_batchBusy)
        {
            MessageBox.Show(this,
                "Conclua ou cancele a exportação antes de gerar o LOG.",
                "VLote: LOG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Salvar diagnóstico de visibilidade / VLote",
            Filter = "Log de texto (*.txt)|*.txt",
            DefaultExt = "txt",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = Path.GetFileNameWithoutExtension(_session.FilePath) +
                       "_VLote_visibility_log.txt",
            InitialDirectory = Path.GetDirectoryName(_session.FilePath)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            UseWaitCursor = true;
            _status.Text = "Gerando LOG: lendo estados de todas as animações...";
            // Create() restores the selected take, timestamp and playback
            // status even after an exception. It never edits FBX assets.
            var report = FbxVisibilityLog.Create(_session, _player);
            File.WriteAllText(dialog.FileName, report, new UTF8Encoding(false));
            _viewer.InvalidateScene();
            _status.Text = "LOG salvo: " + dialog.FileName;
            MessageBox.Show(this,
                "LOG criado com sucesso!\r\n\r\n" +
                "Envia este TXT na conversa para analisarmos quais meshes " +
                "estão ocultas e qual nó está bloqueando cada uma.\r\n\r\n" +
                dialog.FileName,
                "VLote: LOG gerado", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _viewer.InvalidateScene();
            MessageBox.Show(this, ex.ToString(),
                "VLote: erro gerando LOG",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }
}
