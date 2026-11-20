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
        var font = new Font("Consolas", 9f);
        int lineHeight = TextRenderer.MeasureText("Ag", font).Height;
        int lines = AppInfo.AboutText.Replace("\r\n", "\n").Split('\n').Length;

        const int margin = 12;
        const int width = 480;
        int boxHeight = lines * lineHeight + 10;

        var text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Font = font,
            Location = new Point(margin, margin),
            Size = new Size(width - 2 * margin, boxHeight),
            Text = AppInfo.AboutText
        };

        int buttonsY = margin + boxHeight + 10;

        var btnCopy = new Button { Text = "Копировать", Location = new Point(margin, buttonsY), Size = new Size(120, 30) };
        btnCopy.Click += (_, _) =>
        {
            try { Clipboard.SetText(AppInfo.AboutText); } catch { }
        };

        var btnLicense = new Button { Text = "Открыть лицензию", Location = new Point(margin + 130, buttonsY), Size = new Size(150, 30) };
        btnLicense.Click += (_, _) => OpenLicense();

        var btnOk = new Button
        {
            Text = "OK",
            Location = new Point(width - margin - 90, buttonsY),
            Size = new Size(90, 30),
            DialogResult = DialogResult.OK
        };

        Text = $"{AppInfo.Title} — о программе";
        ClientSize = new Size(width, buttonsY + 30 + margin);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        AcceptButton = btnOk;
        CancelButton = btnOk;

        // значок из smartrelay.bmp рядом с exe (если файл есть)
        if (AppIcon.Large != null)
            Icon = AppIcon.Large;

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
