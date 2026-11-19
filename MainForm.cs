using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartRelay;

/// <summary>
/// Окно управления розеткой с двумя кнопками: ВКЛ / ВЫКЛ.
/// Надписи на кнопках загружаются из smartrelay.json.
/// </summary>
public partial class MainForm : Form
{
    private readonly SmartPlug _plug;
    private readonly string _btnOnText;
    private readonly string _btnOffText;
    private readonly Label _statusLabel;

    private readonly Button _btnOn;
    private readonly Button _btnOff;

    public MainForm(string devId, string localKey, string ip, int port, int index, int timeoutSec,
        string btnOnText, string btnOffText, string version = "3.1")
    {
        _btnOnText = btnOnText;
        _btnOffText = btnOffText;

        _plug = new SmartPlug(devId, localKey, ip, port, index, timeoutSec, version);

        // Кнопка ВКЛ
        _btnOn = new Button
        {
            Text = _btnOnText,
            Location = new Point(20, 40),
            Size = new Size(120, 45),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            BackColor = Color.FromArgb(0, 150, 0),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnOn.FlatAppearance.BorderSize = 0;
        _btnOn.Click += async (_, _) => await SetRelayAsync(true);

        // Кнопка ВЫКЛ
        _btnOff = new Button
        {
            Text = _btnOffText,
            Location = new Point(160, 40),
            Size = new Size(120, 45),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            BackColor = Color.FromArgb(180, 0, 0),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnOff.FlatAppearance.BorderSize = 0;
        _btnOff.Click += async (_, _) => await SetRelayAsync(false);

        // Статус
        _statusLabel = new Label
        {
            Text = "Готово",
            Location = new Point(20, 105),
            Size = new Size(300, 25),
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.Gray
        };

        // Форма
        Text = "SmartRelay — управление розеткой";
        Size = new Size(340, 170);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Padding = new Padding(10);
        Controls.Add(_btnOn);
        Controls.Add(_btnOff);
        Controls.Add(_statusLabel);
    }

    private async Task SetRelayAsync(bool on)
    {
        // Блокируем кнопки во время запроса
        _btnOn.Enabled = false;
        _btnOff.Enabled = false;

        string action = on ? "включение" : "выключение";
        SetStatus($"Выполняется {action}...", Color.Blue);

        try
        {
            bool? result = await Task.Run(() => on ? _plug.TurnOn() : _plug.TurnOff());

            if (result == null)
            {
                SetStatus("Ответ не получен. Проверьте IP и ключ.", Color.Red);
            }
            else
            {
                SetStatus(on ? "Розетка ВКЛ." : "Розетка ВЫКЛ.", Color.Green);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message, Color.Red);
        }
        finally
        {
            _btnOn.Enabled = true;
            _btnOff.Enabled = true;
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
