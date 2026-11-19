using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace SmartRelay;

/// <summary>Окно «О программе»: версия, дата выпуска, лицензия, архитектор и исполнитель.</summary>
public class AboutForm : Form
{
    public AboutForm()
    {
        var text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Font = new Font("Consolas", 9f),
            Location = new Point(12, 12),
            Size = new Size(456, 232),
            Text = AppInfo.AboutText
        };

        var btnCopy = new Button { Text = "Копировать", Location = new Point(12, 254), Size = new Size(120, 30) };
        btnCopy.Click += (_, _) =>
        {
            try { Clipboard.SetText(AppInfo.AboutText); } catch { }
        };

        var btnLicense = new Button { Text = "Открыть лицензию", Location = new Point(142, 254), Size = new Size(150, 30) };
        btnLicense.Click += (_, _) => OpenLicense();

        var btnOk = new Button
        {
            Text = "OK",
            Location = new Point(378, 254),
            Size = new Size(90, 30),
            DialogResult = DialogResult.OK
        };

        Text = $"{AppInfo.Title} — о программе";
        ClientSize = new Size(480, 296);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        AcceptButton = btnOk;
        CancelButton = btnOk;

        Controls.Add(text);
        Controls.Add(btnCopy);
        Controls.Add(btnLicense);
        Controls.Add(btnOk);
    }

    private void OpenLicense()
    {
        string? path = AppInfo.LicensePath;
        if (path == null)
        {
            MessageBox.Show(this,
                $"Файл {AppInfo.LicenseFileName} не найден рядом с программой.\n" +
                $"Текст лицензии {AppInfo.License} есть в README.md репозитория.",
                AppInfo.Product, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось открыть " + path + ": " + ex.Message,
                AppInfo.Product, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
