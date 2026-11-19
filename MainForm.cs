using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartRelay;

/// <summary>
/// Окно управления розеткой: ВКЛ / ВЫКЛ + диагностика.
/// Надписи на кнопках загружаются из smartrelay.json.
/// </summary>
public class MainForm : Form
{
    private readonly SmartPlug _plug;
    private readonly Label _statusLabel;
    private readonly Button _btnOn;
    private readonly Button _btnOff;
    private readonly Button _btnDiag;

    public MainForm(string devId, string localKey, string ip, int port, int index, int timeoutSec,
        string btnOnText, string btnOffText, string version = "3.1")
    {
        _plug = new SmartPlug(devId, localKey, ip, port, index, timeoutSec, version);

        _btnOn = new Button
        {
            Text = btnOnText,
            Location = new Point(20, 40),
            Size = new Size(145, 50),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            BackColor = Color.FromArgb(0, 150, 0),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnOn.FlatAppearance.BorderSize = 0;
        _btnOn.Click += async (_, _) => await SetRelayAsync(true);

        _btnOff = new Button
        {
            Text = btnOffText,
            Location = new Point(180, 40),
            Size = new Size(145, 50),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            BackColor = Color.FromArgb(180, 0, 0),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnOff.FlatAppearance.BorderSize = 0;
        _btnOff.Click += async (_, _) => await SetRelayAsync(false);

        _statusLabel = new Label
        {
            Text = "Готово",
            Location = new Point(20, 98),
            Size = new Size(305, 40),
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.Gray
        };

        _btnDiag = new Button
        {
            Text = "Диагностика (журнал)",
            Location = new Point(20, 145),
            Size = new Size(305, 32),
            Font = new Font("Segoe UI", 9),
            FlatStyle = FlatStyle.System
        };
        _btnDiag.Click += (_, _) =>
        {
            using var form = new DiagForm(_plug);
            form.ShowDialog(this);
        };

        Text = "SmartRelay — управление розеткой";
        Size = new Size(361, 228);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Padding = new Padding(10);

        Controls.Add(_btnOn);
        Controls.Add(_btnOff);
        Controls.Add(_statusLabel);
        Controls.Add(_btnDiag);

        FormClosed += (_, _) => _plug.Dispose();
    }

    private async Task SetRelayAsync(bool on)
    {
        _btnOn.Enabled = false;
        _btnOff.Enabled = false;
        _btnDiag.Enabled = false;

        SetStatus(on ? "Выполняется включение..." : "Выполняется выключение...", Color.Blue);

        try
        {
            var result = await Task.Run(() => on ? _plug.TurnOn() : _plug.TurnOff());

            if (result.Ok)
                SetStatus(result.Message, Color.Green);
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
        }
    }

    private void SetStatus(string text, Color color)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => SetStatus(text, color)));
            return;
        }
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }
}
