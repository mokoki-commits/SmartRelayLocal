using System;
using System.Drawing;
using System.IO;

namespace SmartRelay;

/// <summary>
/// Значок программы из файла рядом с exe (smartrelay.bmp / smartrealy.bmp / smartrelay.png / .ico).
/// Формат BMP/PNG превращается в значок на ходу, поэтому отдельный .ico не нужен:
/// достаточно положить картинку в папку программы.
/// Если файла нет или он не читается — окно и трей работают со значком по умолчанию.
/// </summary>
public static class AppIcon
{
    /// <summary>Имена, которые ищутся рядом с exe (в порядке приоритета).</summary>
    private static readonly string[] FileNames =
    {
        "smartrelay.ico", "smartrealy.ico", "smartrelay.bmp", "smartrealy.bmp", "smartleray.bmp",
        "smartrelay.png", "smartrealy.png"
    };

    private static bool _loaded;
    private static Icon? _large;   // окно, меню, панель задач — 32×32
    private static Icon? _small;   // трей — 16×16

    /// <summary>Значок 32×32 (заголовок окна, Alt+Tab). null — использовать стандартный.</summary>
    public static Icon? Large
    {
        get { EnsureLoaded(); return _large; }
    }

    /// <summary>Значок 16×16 (область уведомлений). null — использовать стандартный.</summary>
    public static Icon? Small
    {
        get { EnsureLoaded(); return _small; }
    }

    /// <summary>Путь, из которого взят значок (для журнала); null — файла нет.</summary>
    public static string? SourcePath { get; private set; }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;

        foreach (string name in FileNames)
        {
            string path = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(path))
                continue;

            Icon? icon = TryLoad(path);
            if (icon == null)
                continue;

            SourcePath = path;
            _large = icon;
            _small = TryLoad(path, 16) ?? icon;
            Diag.Log($"Значок программы: {path}");
            return;
        }

        Diag.Log("Значок программы: файл smartrelay.bmp не найден рядом с exe — используется стандартный");
    }

    /// <summary>
    /// Читает .ico как есть либо строит значок из BMP/PNG нужного размера.
    /// </summary>
    private static Icon? TryLoad(string path, int size = 32)
    {
        try
        {
            if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                return new Icon(path, size, size);

            using var source = new Bitmap(path);
            using var scaled = new Bitmap(source, new Size(size, size));
            // GetHicon создаёт копию значка, поэтому Bitmap можно освобождать:
            // полученный HICON живёт до конца работы программы (один-два дескриптора).
            return Icon.FromHandle(scaled.GetHicon());
        }
        catch (Exception ex)
        {
            Diag.Log($"Значок {path} не прочитан: {ex.Message}");
            return null;
        }
    }
}
