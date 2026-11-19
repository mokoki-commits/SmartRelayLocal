using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartRelay;

/// <summary>
/// Окно управления розеткой: ВКЛ / ВЫКЛ, текущее состояние и диагностика.
/// Состояние берётся с устройства по таймеру (один пакетный запрос статуса
/// на постоянном TCP-соединении), поэтому видно и изменения, сделанные
/// приложением SmartLife или кнопкой на самой розетке.
/// </summary>
public class MainForm : Form
{
    private static readonly Color OnColor = Color.FromArgb(0, 150, 0);
    private static readonly Color OffColor = Color.FromArgb(180, 0, 0);
    private static readonly Color IdleOnColor = Color.FromArgb(110, 150, 110);
    private static readonly Color IdleOffColor = Color.FromArgb(170, 120, 120);

    private readonly SmartPlug _plug;
    private readonly Label _stateLabel;
    private readonly Label _statusLabel;
    private readonly Button _btnOn;
    private readonly Button _btnOff;
    private readonly Button _btnDiag;
    private readonly System.Windows.Forms.Timer _poll;
    private bool? _lastState;
    private readonly int _pollMs;
    private int _busy;

    public MainForm(string devId, string localKey, string ip, int port, int index, int timeoutSec,
        string btnOnText, string btnOffText, string version = "3.4", int pollMs = 3000,
        bool showDiag = true)
    {
        _plug = new SmartPlug(devId, localKey, ip, port, index, timeoutSec, version);
        Diag.Log(AppInfo.Summary);

        _stateLabel = new Label
        {
            Text = "Состояние: проверяю…",
            Location = new Point(20, 12),
            Size = new Size(305, 24),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.Gray
        };

        _btnOn = new Button
        {
            Text = btnOnText,
            Location = new Point(20, 42),
            Size = new Size(145, 50),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            BackColor = OnColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnOn.FlatAppearance.BorderSize = 0;
        _btnOn.Click += async (_, _) => await SetRelayAsync(true);

        _btnOff = new Button
        {
            Text = btnOffText,
            Location = new Point(180, 42),
            Size = new Size(145, 50),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            BackColor = OffColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnOff.FlatAppearance.BorderSize = 0;
        _btnOff.Click += async (_, _) => await SetRelayAsync(false);

        _statusLabel = new Label
        {
            Text = "Готово",
            Location = new Point(20, 100),
            Size = new Size(305, 40),
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.Gray
        };

        _btnDiag = new Button
        {
            Text = "Диагностика (журнал)",
            Location = new Point(20, 147),
            Size = new Size(305, 32),
            Font = new Font("Segoe UI", 9),
            FlatStyle = FlatStyle.System
        };
        _btnDiag.Click += (_, _) =>
        {
            using var form = new DiagForm(_plug);
            form.ShowDialog(this);
        };

        Text = $"{AppInfo.Title} — управление розеткой";
        Size = new Size(361, showDiag ? 272 : 226);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Padding = new Padding(10);

        // кнопку диагностики можно скрыть в конфиге: "showDiag": false
        _btnDiag.Visible = showDiag;

        var aboutLink = new LinkLabel
        {
            Text = $"v{AppInfo.Version} · {AppInfo.License} · О программе…",
            Location = new Point(20, showDiag ? 190 : 148),
            Size = new Size(305, 18),
            Font = new Font("Segoe UI", 8.5f),
            LinkColor = Color.SteelBlue,
            ActiveLinkColor = Color.MidnightBlue
        };
        aboutLink.LinkClicked += (_, _) =>
        {
            using var form = new AboutForm();
            form.ShowDialog(this);
        };

        Controls.Add(_stateLabel);
        Controls.Add(_btnOn);
        Controls.Add(_btnOff);
        Controls.Add(_statusLabel);
        Controls.Add(_btnDiag);
        Controls.Add(aboutLink);

        _pollMs = pollMs;
        _poll = new System.Windows.Forms.Timer { Interval = pollMs < 1000 ? 3000 : pollMs };
        _poll.Tick += async (_, _) => await PollAsync();

        Shown += async (_, _) =>
        {
            if (_pollMs > 0)
                _poll.Start();   // poll=0 в конфиге — опрос только по нажатию кнопок
            await PollAsync();
        };
        FormClosed += (_, _) =>
        {
            _poll.Stop();
            _poll.Dispose();
            _plug.Dispose();
        };
    }

    private bool TryBegin() => Interlocked.Exchange(ref _busy, 1) == 0;
    private void End() => Interlocked.Exchange(ref _busy, 0);

    /// <summary>Периодический опрос: один пакетный запрос статуса (0x10 / 0x0A) на всё устройство.</summary>
    private async Task PollAsync()
    {
        if (!TryBegin())
            return;

        try
        {
            var result = await Task.Run(() => _plug.Status());
            if (result.Ok && result.Value.HasValue)
            {
                bool first = _lastState == null;
                UpdateState(result.Value.Value);
                if (first)
                    SetStatus(result.Message, Color.Gray);
            }
            else if (_lastState == null)
            {
                SetStatus("Нет связи: " + result.Message, Color.Red);
            }
            else
            {
                _stateLabel.Text = "Состояние: нет связи";
                _stateLabel.ForeColor = Color.Red;
            }
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка опроса: " + ex.Message, Color.Red);
        }
        finally
        {
            End();
        }
    }

    private async Task SetRelayAsync(bool on)
    {
        if (!TryBegin())
        {
            SetStatus("Дождитесь завершения предыдущей операции…", Color.Gray);
            return;
        }

        _btnOn.Enabled = false;
        _btnOff.Enabled = false;
        _btnDiag.Enabled = false;

        SetStatus(on ? "Выполняется включение…" : "Выполняется выключение…", Color.Blue);

        try
        {
            var result = await Task.Run(() => on ? _plug.TurnOn() : _plug.TurnOff());

            if (result.Ok && result.Value.HasValue)
                UpdateState(result.Value.Value);

            if (result.Ok)
                SetStatus(result.Message, result.Value.HasValue ? Color.Green : Color.DarkOrange);
            else
                SetStatus("ОШИБКА: " + result.Message + "  Откройте «Диагностика (журнал)».", Color.Red);
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message, Color.Red);
        }
        finally
        {
            _btnOn.Enabled = true;
            _btnOff.Enabled = true;
            _btnDiag.Enabled = true;
            End();
        }
    }

    /// <summary>Отражает реальное состояние устройства: надпись и подсветка кнопок.</summary>
    private void UpdateState(bool on)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<bool>(UpdateState), on);
            return;
        }

        _lastState = on;
        _stateLabel.Text = on ? "Состояние: ВКЛ" : "Состояние: ВЫКЛ";
        _stateLabel.ForeColor = on ? OnColor : OffColor;
        _btnOn.BackColor = on ? OnColor : IdleOnColor;
        _btnOff.BackColor = on ? IdleOffColor : OffColor;
    }

    private void SetStatus(string text, Color color)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<string, Color>(SetStatus), text, color);
            return;
        }
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }
}
