using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartRelay;

/// <summary>
/// Окно расширенной диагностики с живым журналом.
///
/// Живой вывод включён только на время проверки и не дольше <see cref="MaxRunSeconds"/>
/// секунд: затем подписка на журнал снимается, подробный режим выключается, и окно
/// перестаёт получать сообщения фонового опроса (иначе они шли бы бесконечно).
/// </summary>
public class DiagForm : Form
{
    /// <summary>Предельная длительность одной проверки, секунд.</summary>
    public const int MaxRunSeconds = 30;

    private readonly SmartPlug _plug;
    private readonly TextBox _log;
    private readonly Button _btnRun;
    private readonly Button _btnCopy;
    private readonly Button _btnSave;

    private CancellationTokenSource? _cts;
    private bool _running;

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

        Text = $"SmartRelay — диагностика (не дольше {MaxRunSeconds} с)";
        Size = new Size(800, 525);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterParent;
        Controls.Add(_log);
        Controls.Add(_btnRun);
        Controls.Add(_btnCopy);
        Controls.Add(_btnSave);

        Shown += async (_, _) => await RunAsync();
        FormClosed += (_, _) =>
        {
            Diag.OnLog -= AppendLog;
            try { _cts?.Cancel(); } catch { }
            Diag.Verbose = false;
        };
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
        if (_running)
            return;

        _running = true;
        _btnRun.Enabled = false;
        var sw = Stopwatch.StartNew();

        try
        {
            _log.Clear();
            AppendLog($"SmartRelay {AppInfo.Version} — диагностика, лимит {MaxRunSeconds} с");
            AppendLog($"Журнал: {Diag.LogFile}");
            AppendLog("");

            Diag.Verbose = true;
            Diag.OnLog += AppendLog;      // живой журнал — только на время проверки

            _cts = new CancellationTokenSource();
            var task = Task.Run(() => _plug.BuildDiagnosticsReport(), _cts.Token);
            var limit = Task.Delay(TimeSpan.FromSeconds(MaxRunSeconds));

            if (await Task.WhenAny(task, limit) == task)
            {
                await task;               // пробросит исключение, если проверка упала
                AppendLog("");
                AppendLog($"OK: диагностика завершена за {sw.Elapsed.TotalSeconds:0.0} с.");
            }
            else
            {
                AppendLog("");
                AppendLog($"СТОП: прошло {MaxRunSeconds} с — живой вывод остановлен по лимиту.");
                AppendLog("Остальные строки дописываются только в файл журнала.");
            }
        }
        catch (Exception ex)
        {
            AppendLog("Критическая ошибка диагностики: " + ex);
        }
        finally
        {
            Diag.OnLog -= AppendLog;      // фоновый опрос больше не подмешивается в окно
            Diag.Verbose = false;
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _running = false;
            _btnRun.Enabled = true;

            AppendLog("");
            AppendLog($"— конец вывода. Файл журнала: {Diag.LogFile} ({LogFileSize()})");
        }
    }

    private static string LogFileSize()
    {
        try
        {
            if (!File.Exists(Diag.LogFile))
                return "файла нет";
            long bytes = new FileInfo(Diag.LogFile).Length;
            return bytes < 1024 ? $"{bytes} байт" : $"{bytes / 1024.0:0.#} КБ";
        }
        catch
        {
            return "размер неизвестен";
        }
    }
}
