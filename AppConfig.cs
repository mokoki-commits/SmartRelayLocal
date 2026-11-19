using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SmartRelay;

/// <summary>
/// Чтение smartrelay.json.
///
/// Особенности, из-за которых настройки раньше «не срабатывали» и которые здесь учтены:
///  • имена ключей сравниваются без учёта регистра — showDiag = ShowDiag = showdiag;
///  • допускаются комментарии // и запятая перед закрывающей скобкой (правка руками);
///  • ошибки разбора не бросаются наружу: возвращается Warning, значения остаются по умолчанию;
///  • ключ showDiag понимает и true/false, и "true"/"false", а также синоним diag.
///
/// Не зависит от WinForms, поэтому разбор проверяется тестами без запуска окна.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Порт LAN-протокола Tuya (см. TuyaProtocol.DefaultPort).</summary>
    public const int DefaultPort = 6668;

    public string? DevId { get; private set; }
    public string? LocalKey { get; private set; }
    public string? Ip { get; private set; }
    public int? Port { get; private set; }
    public int? Index { get; private set; }
    public int? TimeoutSec { get; private set; }
    public int? PollMs { get; private set; }
    public string? BtnOn { get; private set; }
    public string? BtnOff { get; private set; }
    public string? Ver { get; private set; }

    /// <summary>Показывать кнопку «Диагностика (журнал)»; null — ключа нет в конфиге.</summary>
    public bool? ShowDiag { get; private set; }

    public string? Architect { get; private set; }
    public string? Executor { get; private set; }

    /// <summary>Путь, откуда читали (для журнала).</summary>
    public string SourcePath { get; private set; } = "smartrelay.json";

    /// <summary>Найден ли файл.</summary>
    public bool Exists { get; private set; }

    /// <summary>Замечание для пользователя (файла нет — null; ошибка разбора — текст).</summary>
    public string? Warning { get; private set; }

    /// <summary>Прочитать файл. Отсутствие файла — не ошибка: все свойства остаются null.</summary>
    public static AppConfig Load(string path)
    {
        var cfg = new AppConfig { SourcePath = path, Exists = File.Exists(path) };
        if (!cfg.Exists)
            return cfg;

        try
        {
            var options = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(path), options);
            if (parsed == null)
                return cfg;

            var json = new Dictionary<string, JsonElement>(parsed, StringComparer.OrdinalIgnoreCase);

            cfg.DevId = GetStr(json, "dev_id");
            cfg.LocalKey = GetStr(json, "local_key");
            cfg.Ip = GetStr(json, "ip");
            cfg.Port = GetInt(json, "port");
            cfg.Index = GetInt(json, "index");
            cfg.TimeoutSec = GetInt(json, "timeout");
            cfg.PollMs = GetInt(json, "poll");
            cfg.BtnOn = GetStr(json, "btnOnText");
            cfg.BtnOff = GetStr(json, "btnOffText");
            cfg.Ver = GetStr(json, "ver");
            cfg.Architect = GetStr(json, "architect");
            cfg.Executor = GetStr(json, "executor");

            bool? showDiag = GetBool(json, "showDiag") ?? GetBool(json, "diag");
            if (showDiag.HasValue)
                cfg.ShowDiag = showDiag.Value;
            else if (json.ContainsKey("showDiag") || json.ContainsKey("diag"))
                cfg.Warning = "Ключ \"showDiag\" есть, но значение не true/false — оставлено true.";
        }
        catch (Exception ex)
        {
            cfg.Warning = "Не удалось прочитать " + path + ": " + ex.Message;
        }

        return cfg;
    }

    private static string? GetStr(Dictionary<string, JsonElement> dict, string name)
    {
        if (!dict.TryGetValue(name, out var el))
            return null;

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString()?.Trim(),
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int? GetInt(Dictionary<string, JsonElement> dict, string name)
    {
        if (!dict.TryGetValue(name, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int num))
            return num;

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int parsed))
            return parsed;

        return null;
    }

    private static bool? GetBool(Dictionary<string, JsonElement> dict, string name)
    {
        if (!dict.TryGetValue(name, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.True)
            return true;
        if (el.ValueKind == JsonValueKind.False)
            return false;

        return el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b) ? b : null;
    }
}
