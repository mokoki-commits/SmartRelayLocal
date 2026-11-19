using System;
using System.IO;
using System.Reflection;

namespace SmartRelay;

/// <summary>
/// Сведения о программе: версия, дата выпуска, лицензия и авторы.
/// Версия берётся из SmartRelay.csproj (&lt;Version&gt;) — один источник правды,
/// она же попадает в свойства файла SmartRelay.exe в Проводнике.
/// Архитектор (avk013) и исполнитель (Cline (AI coding agent) mokoki) зашиты здесь;
/// при необходимости переопределяются в smartrelay.json ("architect" / "executor").
/// </summary>
public static class AppInfo
{
    public const string Product = "SmartRelay";
    public const string ReleaseDate = "19.11.2026";
    public const string License = "MIT";
    public const string LicenseFileName = "LICENSE";
    public const string Repository = "smartrelay-csharp";

    public static string Version { get; } = ReadVersion();

    /// <summary>Архитектор решения (кто проектировал протокол/архитектуру).</summary>
    public static string Architect { get; private set; } = "avk013";

    /// <summary>Исполнитель работ (кто реализовал код).</summary>
    public static string Executor { get; private set; } = "Cline (AI coding agent) mokoki";

    public static string Title => $"{Product} {Version}";

    public static string Summary => $"{Product} {Version} от {ReleaseDate}, лицензия {License}";

    /// <summary>Короткая подпись для заголовка окна и журнала.</summary>
    public static string ShortLine => $"v{Version} · {License} · архитектор: {Architect} · исполнитель: {Executor}";

    public static void Configure(string? architect, string? executor)
    {
        if (!string.IsNullOrWhiteSpace(architect))
            Architect = architect.Trim();
        if (!string.IsNullOrWhiteSpace(executor))
            Executor = executor.Trim();
    }

    /// <summary>Полный текст окна «О программе» (его можно скопировать).</summary>
    public static string AboutText =>
        $"{Product} {Version} — локальное управление розеткой SmartLife / Tuya по LAN," + Environment.NewLine +
        "без облака и без интернета." + Environment.NewLine +
        Environment.NewLine +
        $"Дата выпуска:  {ReleaseDate}" + Environment.NewLine +
        $"Протокол:      Tuya 3.4 (по умолчанию) и 3.1, определяется автоматически" + Environment.NewLine +
        $"Лицензия:      {License} — файл {LicenseFileName} рядом с программой" + Environment.NewLine +
        Environment.NewLine +
        $"Архитектор:    {Architect}" + Environment.NewLine +
        $"Исполнитель:   {Executor}" + Environment.NewLine +
        Environment.NewLine +
        "Сторонние компоненты: .NET 7 / Windows Forms, System.Text.Json (MIT)." + Environment.NewLine +
        "Протокол Tuya реализован самостоятельно, без сторонних библиотек." + Environment.NewLine +
        Environment.NewLine +
        "Файл конфигурации: smartrelay.json; журнал: smartrelay.log.";

    /// <summary>Путь к файлу лицензии рядом с exe (если он был скопирован при публикации).</summary>
    public static string? LicensePath
    {
        get
        {
            string path = Path.Combine(AppContext.BaseDirectory, LicenseFileName);
            return File.Exists(path) ? path : null;
        }
    }

    private static string ReadVersion()
    {
        var asm = typeof(AppInfo).Assembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
            return info.Split('+')[0].Trim();

        var v = asm.GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
