using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartRelay;

/// <summary>Окно расширенной диагностики с живым журналом.</summary>
public class DiagForm : Form
{
    private readonly SmartPlug _plug;
    private readonly TextBox _log;
    private readonly Button _btnRun;
    private readonly Button _btnCopy;
    private readonly Button _btnSave;

    public DiagForm(SmartPlug plug)
    {
        _plug = plug;

        _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 8.5f),
            BackColor = Color.FromArgb(20, 20, 20),
            ForeColor = Color.Gainsboro,
            Location = new Point(12, 12),
            Size = new Size(760, 420),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
        };

        _btnRun = new Button { Text = "Повторить", Location = new Point(12, 442), Size = new Size(120, 32) };
        _btnRun.Click += async (_, _) => await RunAsync();

        _btnCopy = new Button { Text = "Копировать", Location = new Point(144, 442), Size = new Size(120, 32) };
        _btnCopy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_log.Text); } catch { }
        };

        _btnSave = new Button { Text = "Сохранить в файл", Location = new Point(276, 442), Size = new Size(150, 32) };
        _btnSave.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog
            {
                FileName = $"smartrelay-diag-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                Filter = "Текст (*.txt)|*.txt|Все файлы (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;
            try
            {
                File.WriteAllText(dlg.FileName, _log.Text, Encoding.UTF8);
                MessageBox.Show(this, "Сохранено: " + dlg.FileName, "SmartRelay", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось сохранить: " + ex.Message, "SmartRelay", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        Text = "SmartRelay — диагностика";
        Size = new Size(800, 525);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterParent;
        Controls.Add(_log);
        Controls.Add(_btnRun);
        Controls.Add(_btnCopy);
        Controls.Add(_btnSave);

        Diag.OnLog += AppendLog;
        FormClosed += (_, _) => Diag.OnLog -= AppendLog;
        Shown += async (_, _) => await RunAsync();
    }

    private void AppendLog(string line)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<string>(AppendLog), line); } catch { }
            return;
        }

        _log.AppendText(line + Environment.NewLine);
    }

    private async Task RunAsync()
    {
        _btnRun.Enabled = false;
        try
        {
            _log.Clear();
            Diag.Verbose = true;
            AppendLog($"Журнал: {Diag.LogFile}");
            AppendLog("");
            await Task.Run(() => _plug.BuildDiagnosticsReport());
        }
        catch (Exception ex)
        {
            AppendLog("Критическая ошибка диагностики: " + ex);
        }
        finally
        {
            _btnRun.Enabled = true;
        }
    }
}
