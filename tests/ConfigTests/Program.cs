using System;
using System.IO;
using SmartRelay;

int failed = 0;
string dir = Path.Combine(Path.GetTempPath(), "appcfg-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);

void Case(string name, string json, Action<AppConfig> check)
{
    string path = Path.Combine(dir, "cfg.json");
    File.WriteAllText(path, json);
    var cfg = AppConfig.Load(path);
    try
    {
        check(cfg);
        Console.WriteLine($"OK   {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

void Eq<T>(T expected, T actual, string what)
{
    if (!Equals(expected, actual))
        throw new Exception($"{what}: ожидалось {expected}, получено {actual}");
}

Case("showDiag=false скрывает кнопку", "{\"showDiag\": false}", c => Eq(false, c.ShowDiag ?? true, "ShowDiag"));
Case("ShowDiag (регистр) тоже работает", "{\"ShowDiag\": false}", c => Eq(false, c.ShowDiag ?? true, "ShowDiag"));
Case("SHOWDIAG в верхнем регистре", "{\"SHOWDIAG\": false}", c => Eq(false, c.ShowDiag ?? true, "ShowDiag"));
Case("строка \"false\" понимается", "{\"showDiag\": \"false\"}", c => Eq(false, c.ShowDiag ?? true, "ShowDiag"));
Case("синоним diag", "{\"diag\": false}", c => Eq(false, c.ShowDiag ?? true, "ShowDiag"));
Case("showDiag=true показывает", "{\"showDiag\": true}", c => Eq(true, c.ShowDiag ?? false, "ShowDiag"));
Case("ключа нет — null (по умолчанию true)", "{\"ip\": \"10.0.0.5\"}", c => Eq(null, c.ShowDiag, "ShowDiag"));
Case("комментарии // допустимы", "{ // настройки\n \"showDiag\": false }", c => Eq(false, c.ShowDiag ?? true, "ShowDiag"));
Case("запятая перед } допустима", "{\"showDiag\": false, }", c => Eq(false, c.ShowDiag ?? true, "ShowDiag"));
Case("число 0 = false", "{\"showDiag\": 0}", c => Eq(false, c.ShowDiag ?? true, "ShowDiag"));
Case("число 1 = true", "{\"showDiag\": 1}", c => Eq(true, c.ShowDiag ?? false, "ShowDiag"));
Case("hideDiag=true скрывает", "{\"hideDiag\": true}", c => Eq(false, c.ShowDiag ?? true, "ShowDiag"));
Case("hideDiag=false показывает", "{\"hideDiag\": false}", c => Eq(true, c.ShowDiag ?? false, "ShowDiag"));
Case("showDiag сильнее hideDiag", "{\"showDiag\": true, \"hideDiag\": true}", c => Eq(true, c.ShowDiag ?? false, "ShowDiag"));
Case("непонятное значение — предупреждение", "{\"showDiag\": \"нет\"}", c =>
{
    Eq(null, c.ShowDiag, "ShowDiag");
    if (c.Warning == null) throw new Exception("ожидалось предупреждение");
});
Case("ключ найден — ShowDiagKeyFound", "{\"showDiag\": false}", c =>
{
    if (!c.ShowDiagKeyFound) throw new Exception("ShowDiagKeyFound=false");
});
Case("ключа нет — ShowDiagKeyFound=false", "{\"ip\": \"1.1.1.1\"}", c =>
{
    if (c.ShowDiagKeyFound) throw new Exception("ShowDiagKeyFound=true");
});
Case("битый JSON — предупреждение, без исключения", "{ \"showDiag\": }", c =>
{
    Eq(null, c.ShowDiag, "ShowDiag");
    if (c.Warning == null) throw new Exception("ожидалось предупреждение");
});
Case("остальные поля читаются", "{\"dev_id\":\"d1\",\"local_key\":\"k\",\"ip\":\"1.2.3.4\",\"port\":6667,\"index\":2,\"timeout\":7,\"poll\":5000,\"ver\":\"3.1\",\"btnOnText\":\"ON\",\"btnOffText\":\"OFF\",\"architect\":\"avk013\",\"executor\":\"Cline (AI coding agent) mokoki\"}",
    c =>
    {
        Eq("d1", c.DevId, "DevId");
        Eq("k", c.LocalKey, "LocalKey");
        Eq("1.2.3.4", c.Ip, "Ip");
        Eq(6667, c.Port ?? 0, "Port");
        Eq(2, c.Index ?? 0, "Index");
        Eq(7, c.TimeoutSec ?? 0, "Timeout");
        Eq(5000, c.PollMs ?? 0, "Poll");
        Eq("3.1", c.Ver, "Ver");
        Eq("ON", c.BtnOn, "BtnOn");
        Eq("OFF", c.BtnOff, "BtnOff");
        Eq("avk013", c.Architect, "Architect");
        Eq("Cline (AI coding agent) mokoki", c.Executor, "Executor");
    });
Case("poll=0 (опрос выключен) сохраняется", "{\"poll\": 0}", c => Eq(0, c.PollMs ?? -1, "Poll"));
Case("файла нет — Exists=false", "{}", _ => { });
var missing = AppConfig.Load(Path.Combine(dir, "nope.json"));
if (missing.Exists || missing.ShowDiag != null || missing.Warning != null) { failed++; Console.WriteLine("FAIL отсутствующий файл"); }
else Console.WriteLine("OK   отсутствующий файл — значения по умолчанию");

// Итоговое решение (AppConfig.ResolveShowDiag) — то, что вызывает Program.Main
void Resolve(string name, AppConfig? cfg, bool? flag, bool expected)
{
    bool actual = AppConfig.ResolveShowDiag(cfg, flag);
    if (actual == expected)
        Console.WriteLine($"OK   {name}");
    else
    {
        failed++;
        Console.WriteLine($"FAIL {name}: ожидалось {expected}, получено {actual}");
    }
}

string cfgPath = Path.Combine(dir, "cfg.json");
File.WriteAllText(cfgPath, "{\"showDiag\": false}");
var offCfg = AppConfig.Load(cfgPath);
File.WriteAllText(cfgPath, "{\"showDiag\": true}");
var onCfg = AppConfig.Load(cfgPath);
File.WriteAllText(cfgPath, "{}");
var emptyCfg = AppConfig.Load(cfgPath);

Resolve("файл false — кнопка скрыта", offCfg, null, false);
Resolve("файл true — кнопка видна", onCfg, null, true);
Resolve("ключа нет — видна по умолчанию", emptyCfg, null, true);
Resolve("файла нет — видна по умолчанию", AppConfig.Load(Path.Combine(dir, "nope.json")), null, true);
Resolve("--no-diag сильнее файла", onCfg, false, false);
Resolve("--show-diag сильнее файла", offCfg, true, true);

Directory.Delete(dir, true);
Console.WriteLine(failed == 0 ? "\nВСЕ ТЕСТЫ ПРОЙДЕНЫ" : $"\nПРОВАЛЕНО: {failed}");
return failed == 0 ? 0 : 1;
