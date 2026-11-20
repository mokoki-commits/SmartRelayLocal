using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartRelay;

/// <summary>
/// Окно управления розеткой: ВКЛ / ВЫКЛ и динамическое состояние устройства.
///
/// Розеткой можно управлять не только отсюда: её меняет приложение SmartLife,
/// сценарии и кнопка на корпусе, поэтому состояние перечитывается с устройства
/// по таймеру. Под кнопками видно текущее состояние и дату/время последнего
/// переключения, а также время последней успешной проверки.
/// </summary>
public class MainForm : Form
{
    private static readonly Color OnColor = Color.FromArgb(0, 150, 0);
    private static readonly Color OffColor = Color.FromArgb(180, 0, 0);
    private static readonly Color IdleOnColor = Color.FromArgb(110, 150, 110);
    private static readonly Color IdleOffColor = Color.FromArgb(170, 120, 120);

    private readonly SmartPlug _plug;
    private readonly Label _stateLabel;
    private readonly Label _checkedLabel;
    private readonly Label _statusLabel;
    private readonly Button _btnOn;
    private readonly Button _btnOff;
    private readonly Button _btnDiag;
    private readonly System.Windows.Forms.Timer _poll;
    private readonly ToolTip _tip;
    private readonly bool _showDiag;
    private readonly int _pollMs;

    /// <summary>Показывать окно ещё и на панели задач (false — только значок в трее).</summary>
    private readonly bool _taskbar;

    /// <summary>Значок в области уведомлений: программа всегда доступна из трея.</summary>
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _trayShowItem;
    private readonly ToolStripMenuItem _trayStateItem;
    private readonly ToolStripMenuItem _trayOnItem;
    private readonly ToolStripMenuItem _trayOffItem;

    /// <summary>Выход разрешён только из меню трея, иначе крестик прячет окно в трей.</summary>
    private bool _exitRequested;
    private bool _balloonShown;

    /// <summary>
    /// Последнее известное состояние: файл рядом с exe. Нужен, чтобы после
    /// перезапуска показать реальное время переключения, а не время старта.
    /// </summary>
    private readonly string _stateFile = Path.Combine(AppContext.BaseDirectory, "smartrelay.state.json");

    private bool? _lastState;
    private DateTime _stateSince;
    private bool? _savedState;
    private DateTime? _savedSince;
    private int _busy;

    public MainForm(string devId, string localKey, string ip, int port, int index, int timeoutSec,
        string btnOnText, string btnOffText, string version = "3.4", int pollMs = 3000,
        bool showDiag = true, bool showInTaskbar = true)
    {
        _taskbar = showInTaskbar;

        _plug = new SmartPlug(devId, localKey, ip, port, index, timeoutSec, version);
        Diag.Log($"Опрос состояния: {(pollMs > 0 ? $"каждые {pollMs} мс" : "выключен (poll=0)")}");

        _pollMs = pollMs;
        _poll = new System.Windows.Forms.Timer { Interval = pollMs < 1000 ? 3000 : pollMs };

        _btnOn = new Button
        {
            Text = btnOnText,
            Location = new Point(20, 18),
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
            Location = new Point(180, 18),
            Size = new Size(145, 50),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            BackColor = OffColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnOff.FlatAppearance.BorderSize = 0;
        _btnOff.Click += async (_, _) => await SetRelayAsync(false);

        // Состояние — под кнопками, с датой и временем последнего переключения.
        _stateLabel = new Label
        {
            Text = "Состояние: проверяю…",
            Location = new Point(20, 74),
            Size = new Size(305, 24),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _checkedLabel = new Label
        {
            Text = "",
            Location = new Point(20, 99),
            Size = new Size(305, 16),
            Font = new Font("Segoe UI", 8, FontStyle.Italic),
            ForeColor = Color.Gray
        };

        _statusLabel = new Label
        {
            Text = "Готово",
            Location = new Point(20, 117),
            Size = new Size(305, 38),
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.Gray
        };

        _btnDiag = new Button
        {
            Text = "Диагностика (журнал)",
            Location = new Point(20, 158),
            Size = new Size(305, 32),
            Font = new Font("Segoe UI", 9),
            FlatStyle = FlatStyle.System
        };
        _btnDiag.Click += (_, _) => ShowDiagnostics();

        Text = $"{AppInfo.Title} — управление розеткой";
        Size = new Size(361, showDiag ? 280 : 238);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Padding = new Padding(10);

        // Значок окна и трея берётся из файла рядом с exe (smartrelay.ico, затем .png, затем .bmp).
        if (AppIcon.Large != null)
            Icon = AppIcon.Large;

        // Панель задач задаётся конфигом: taskbar = true — окно и в трее, и на панели задач;
        // false — только значок в трее. Значок в трее присутствует всегда.
        ShowInTaskbar = _taskbar;
        Diag.Log(_taskbar
            ? "Окно: показано и на панели задач, и в трее (taskbar=true)"
            : "Окно: только значок в трее, на панели задач не показывается (taskbar=false)");

        // Кнопку диагностики можно скрыть в конфиге: "showDiag": false (или --no-diag).
        // Недостаточно Visible = false: если кнопку всё-таки добавить в окно, она может
        // появиться (например, при смене темы/пересоздании окна). Поэтому при showDiag = false
        // кнопка вообще не попадает в Controls и физически не может быть показана.
        _showDiag = showDiag;
        _btnDiag.Visible = showDiag;
        Diag.Log(showDiag
            ? "Кнопка «Диагностика»: показана (showDiag=true)"
            : "Кнопка «Диагностика»: скрыта (showDiag=false)");

        var aboutLink = new LinkLabel
        {
            Text = $"v{AppInfo.Version} · {AppInfo.License} · О программе…",
            Location = new Point(20, showDiag ? 196 : 158),
            Size = new Size(305, 18),
            Font = new Font("Segoe UI", 8.5f),
            LinkColor = Color.SteelBlue,
            ActiveLinkColor = Color.MidnightBlue
        };
        aboutLink.LinkClicked += (_, _) => ShowAbout();

        _tip = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 200 };
        _tip.SetToolTip(_stateLabel,
            "Время — момент, когда программа увидела переключение." + Environment.NewLine +
            "Розетку можно менять из SmartLife или кнопкой на корпусе —" + Environment.NewLine +
            "состояние обновится автоматически.");
        _tip.SetToolTip(_checkedLabel, "Время последней успешной проверки состояния устройства.");

        Controls.Add(_stateLabel);
        Controls.Add(_checkedLabel);
        Controls.Add(_btnOn);
        Controls.Add(_btnOff);
        Controls.Add(_statusLabel);
        if (_showDiag)
            Controls.Add(_btnDiag);   // при showDiag=false кнопки в окне нет вообще
        Controls.Add(aboutLink);

        // ---- Значок в области уведомлений: программа доступна из трея всегда ----
        _trayShowItem = new ToolStripMenuItem("Свернуть в трей", null, (_, _) => ToggleWindow());
        _trayOnItem = new ToolStripMenuItem("Включить", null, async (_, _) => await SetRelayAsync(true));
        _trayOffItem = new ToolStripMenuItem("Выключить", null, async (_, _) => await SetRelayAsync(false));
        _trayStateItem = new ToolStripMenuItem("Состояние: проверяю…") { Enabled = false };

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add(_trayShowItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(_trayOnItem);
        trayMenu.Items.Add(_trayOffItem);
        trayMenu.Items.Add(_trayStateItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        if (_showDiag)
        {
            trayMenu.Items.Add(new ToolStripMenuItem("Диагностика (журнал)…", null, (_, _) =>
            {
                ShowMainWindow();
                ShowDiagnostics();
            }));
        }
        trayMenu.Items.Add(new ToolStripMenuItem("О программе…", null, (_, _) => ShowAbout()));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => ExitApplication()));

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Small ?? SystemIcons.Application,
            Text = AppInfo.Title,
            Visible = true,
            ContextMenuStrip = trayMenu
        };
        _tray.DoubleClick += (_, _) => ToggleWindow();
        _tray.BalloonTipClicked += (_, _) => ShowMainWindow();
        Diag.Log("Трей: значок создан, двойной щелчок — показать/скрыть окно, меню — команды и выход");

        LoadSavedState();
        UpdateTrayText();

        _poll.Tick += async (_, _) => await PollAsync();

        Shown += async (_, _) =>
        {
            if (_pollMs > 0)
                _poll.Start();   // poll=0 в конфиге — опрос только по нажатию кнопок
            await PollAsync();
        };
        // Крестик и Alt+F4 не завершают программу: окно уходит в трей.
        // Полный выход — «Выход» в меню значка.
        FormClosing += (_, e) =>
        {
            if (_exitRequested || e.CloseReason != CloseReason.UserClosing)
                return;
            e.Cancel = true;
            HideToTray();
        };

        // При taskbar=false кнопка «свернуть» тоже прячет окно в трей;
        // при taskbar=true окно сворачивается на панель задач как обычно.
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && !_taskbar)
                HideToTray();
        };

        FormClosed += (_, _) =>
        {
            _poll.Stop();
            _poll.Dispose();
            _plug.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        };
    }

    private bool TryBegin() => Interlocked.Exchange(ref _busy, 1) == 0;
    private void End() => Interlocked.Exchange(ref _busy, 0);

    /// <summary>Окно сейчас видно пользователю (не спрятано в трей и не свёрнуто).</summary>
    private bool IsWindowShown => Visible && WindowState != FormWindowState.Minimized;

    /// <summary>Показать / спрятать окно по двойному щелчку на значке в трее.</summary>
    private void ToggleWindow()
    {
        if (IsWindowShown)
            HideToTray();
        else
            ShowMainWindow();
    }

    /// <summary>Вернуть окно из трея.</summary>
    private void ShowMainWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        UpdateTrayText();
    }

    /// <summary>Спрятать окно в трей (программа продолжает работать и опрашивать розетку).</summary>
    private void HideToTray()
    {
        Hide();
        UpdateTrayText();

        if (_balloonShown)
            return;

        _balloonShown = true;
        _tray.ShowBalloonTip(4000, AppInfo.Title,
            _taskbar
                ? "Программа осталась в трее и продолжает опрашивать розетку." + Environment.NewLine +
                  "Двойной щелчок по значку — открыть окно, меню значка — команды и выход."
                : "Программа свёрнута в трей и продолжает опрашивать розетку." + Environment.NewLine +
                  "На панели задач её нет (taskbar=false). Двойной щелчок по значку — открыть окно.",
            ToolTipIcon.Info);
    }

    /// <summary>Полный выход: только из меню значка в трее.</summary>
    private void ExitApplication()
    {
        _exitRequested = true;
        _tray.Visible = false;
        Close();
    }

    /// <summary>
    /// Подсказка и строка состояния в меню трея: видно текущее состояние и время
    /// последнего переключения, даже когда окно спрятано.
    /// </summary>
    private void UpdateTrayText()
    {
        string state = _lastState.HasValue
            ? (_lastState.Value
                ? $"ВКЛ с {_stateSince:dd.MM.yyyy HH:mm}"
                : $"ВЫКЛ с {_stateSince:dd.MM.yyyy HH:mm}")
            : (_savedSince.HasValue
                ? $"нет связи, последнее: {(_savedState == true ? "ВКЛ" : "ВЫКЛ")} с {_savedSince.Value:dd.MM.yyyy HH:mm}"
                : "состояние неизвестно");

        _trayStateItem.Text = "Состояние: " + state;
        _trayShowItem.Text = IsWindowShown ? "Свернуть в трей" : "Показать окно";

        // NotifyIcon.Text ограничен по длине — обрезаем, иначе Windows отбросит подсказку.
        string text = $"{AppInfo.Product} {AppInfo.Version} — {state}";
        _tray.Text = text.Length <= 63 ? text : text.Substring(0, 60) + "…";
    }

    /// <summary>Окно «О программе» (общее для ссылки в окне и пункта меню трея).</summary>
    private void ShowAbout()
    {
        using var form = new AboutForm();
        form.ShowDialog(this);
    }

    /// <summary>
    /// Окно диагностики (общее для кнопки и меню трея).
    /// Пока оно открыто, периодический опрос остановлен: он засоряет журнал
    /// и занимает единственное соединение.
    /// </summary>
    private void ShowDiagnostics()
    {
        bool wasPolling = _pollMs > 0 && _poll.Enabled;
        _poll.Stop();
        try
        {
            using var form = new DiagForm(_plug);
            form.ShowDialog(this);
        }
        finally
        {
            if (wasPolling)
                _poll.Start();
        }
    }

    /// <summary>Читает сохранённое состояние (если файл есть и корректен).</summary>
    private void LoadSavedState()
    {
        try
        {
            if (!File.Exists(_stateFile))
                return;

            using var doc = JsonDocument.Parse(File.ReadAllText(_stateFile));
            var root = doc.RootElement;
            if (root.TryGetProperty("on", out var onEl) &&
                (onEl.ValueKind == JsonValueKind.True || onEl.ValueKind == JsonValueKind.False))
                _savedState = onEl.GetBoolean();
            if (root.TryGetProperty("since", out var sinceEl) && sinceEl.TryGetDateTime(out var dt))
                _savedSince = dt;
        }
        catch
        {
            // файл состояния вспомогательный: ошибки чтения не критичны
        }
    }

    private void SaveState()
    {
        try
        {
            if (!_lastState.HasValue)
                return;

            string json = $"{{\"on\":{(_lastState.Value ? "true" : "false")},\"since\":\"{_stateSince:yyyy-MM-ddTHH:mm:ss}\"}}";
            File.WriteAllText(_stateFile, json);
        }
        catch
        {
            // нет прав на запись — время определится при следующем запуске
        }
    }

    /// <summary>Периодический опрос: один пакетный запрос статуса (0x10 / 0x0A) на всё устройство.</summary>
    private async Task PollAsync()
    {
        if (!TryBegin())
            return;

        try
        {
            // Журнал не засоряем: служебные строки опроса пишутся только в подробном режиме.
            _plug.QuietLog = true;
            var result = await Task.Run(() => _plug.Status());

            if (result.Ok && result.Value.HasValue)
            {
                bool recovered = _statusLabel.Text.StartsWith("Нет связи", StringComparison.Ordinal);
                UpdateState(result.Value.Value);
                UpdateChecked();
                if (recovered)
                    SetStatus("Связь восстановлена.", Color.Gray);
            }
            else if (_lastState == null && !_savedState.HasValue)
            {
                ShowDisconnected();
                SetStatus("Нет связи: " + result.Message, Color.Red);
            }
            else
            {
                ShowDisconnected();
            }
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка опроса: " + ex.Message, Color.Red);
        }
        finally
        {
            _plug.QuietLog = false;
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
        _trayOnItem.Enabled = false;
        _trayOffItem.Enabled = false;

        SetStatus(on ? "Выполняется включение…" : "Выполняется выключение…", Color.Blue);

        try
        {
            _plug.QuietLog = false;
            var result = await Task.Run(() => on ? _plug.TurnOn() : _plug.TurnOff());

            if (result.Ok && result.Value.HasValue)
            {
                UpdateState(result.Value.Value);
                UpdateChecked();
            }

            if (result.Ok)
                SetStatus(result.Message, result.Value.HasValue ? Color.Green : Color.DarkOrange);
            else
                SetStatus("ОШИБКА: " + result.Message +
                          (_showDiag ? "  Откройте «Диагностика (журнал)»."
                                     : "  Подробности: " + Diag.LogFile), Color.Red);
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
            _trayOnItem.Enabled = true;
            _trayOffItem.Enabled = true;
            _plug.QuietLog = false;
            End();
        }
    }

    /// <summary>
    /// Отражает реальное состояние устройства: подсветка кнопок, надпись под ними
    /// и дата/время переключения. Вызывается и из опроса, и из команд.
    /// </summary>
    private void UpdateState(bool on)
    {
        if (InvokeRequired)
        {
            try { Invoke(new Action<bool>(UpdateState), on); } catch { }
            return;
        }

        if (!_lastState.HasValue)
        {
            // Первый ответ устройства:
            //  • состояние совпало с сохранённым → переключения не было, время сохраняем;
            //  • не совпало → розетку переключили без нас (SmartLife/кнопка), время — сейчас.
            _stateSince = _savedState.HasValue && _savedState.Value == on && _savedSince.HasValue
                ? _savedSince.Value
                : DateTime.Now;
            _lastState = on;
            SaveState();
        }
        else if (_lastState.Value != on)
        {
            _lastState = on;
            _stateSince = DateTime.Now;   // переключение произошло только что
            SaveState();
        }

        _stateLabel.Text = on
            ? $"Сейчас ВКЛ · с {_stateSince:dd.MM.yyyy HH:mm:ss}"
            : $"Сейчас ВЫКЛ · с {_stateSince:dd.MM.yyyy HH:mm:ss}";
        _stateLabel.ForeColor = on ? OnColor : OffColor;
        _btnOn.BackColor = on ? OnColor : IdleOnColor;
        _btnOff.BackColor = on ? IdleOffColor : OffColor;
        UpdateTrayText();
    }

    /// <summary>Связи нет: показываем последнее известное состояние и его время.</summary>
    private void ShowDisconnected()
    {
        bool? last = _lastState ?? _savedState;
        DateTime? since = _lastState.HasValue ? _stateSince : _savedSince;

        if (last.HasValue && since.HasValue)
        {
            string word = last.Value ? "ВКЛ" : "ВЫКЛ";
            _stateLabel.Text = $"Нет связи · последнее: {word} с {since.Value:dd.MM.yyyy HH:mm:ss}";
            _stateLabel.ForeColor = Color.Red;
            _btnOn.BackColor = last.Value ? OnColor : IdleOnColor;
            _btnOff.BackColor = last.Value ? IdleOffColor : OffColor;
        }
        else
        {
            _stateLabel.Text = "Нет связи · состояние неизвестно";
            _stateLabel.ForeColor = Color.Red;
        }

        UpdateTrayText();
    }

    private void UpdateChecked()
    {
        if (InvokeRequired)
        {
            try { Invoke(new Action(UpdateChecked)); } catch { }
            return;
        }

        _checkedLabel.Text = _pollMs > 0
            ? $"Проверено в {DateTime.Now:HH:mm:ss} · опрос каждые {_pollMs / 1000.0:0.#} с"
            : $"Проверено в {DateTime.Now:HH:mm:ss} · автоопрос выключен";
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
