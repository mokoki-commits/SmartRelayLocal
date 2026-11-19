using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;

namespace SmartRelay;

class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string? devId = null;
        string? localKey = null;
        string? ip = null;
        int port = TuyaProtocol.DefaultPort;
        int index = 1;
        int timeoutSec = 5;
        string configPath = "smartrelay.json";
        string? action = null;
        bool guiMode = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--id": devId = args[++i]; break;
                case "--key": localKey = args[++i]; break;
                case "--ip": ip = args[++i]; break;
                case "--port": port = int.Parse(args[++i]); break;
                case "--index": index = int.Parse(args[++i]); break;
                case "--timeout": timeoutSec = int.Parse(args[++i]); break;
                case "--config": configPath = args[++i]; break;
                case "on": case "off": case "status": case "toggle": action = args[i]; guiMode = false; break;
                case "--gui": guiMode = true; break;
                case "--cli": guiMode = false; break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
            }
        }

        string? cfgDevId = null, cfgKey = null, cfgIp = null;
        int cfgPort = port, cfgIndex = index, cfgTimeout = timeoutSec;
        string cfgBtnOn = "ВКЛ", cfgBtnOff = "ВЫКЛ";
        string cfgVer = "3.1";

        if (File.Exists(configPath))
        {
            try
            {
                var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(configPath));
                if (json != null)
                {
                    cfgDevId = GetStr(json, "dev_id");
                    cfgKey = GetStr(json, "local_key");
                    cfgIp = GetStr(json, "ip");
                    if (json.TryGetValue("port", out var p)) cfgPort = p.GetInt32();
                    if (json.TryGetValue("index", out var ix)) cfgIndex = ix.GetInt32();
                    if (json.TryGetValue("timeout", out var t)) cfgTimeout = t.GetInt32();
                    cfgBtnOn = GetStr(json, "btnOnText") ?? "ВКЛ";
                    cfgBtnOff = GetStr(json, "btnOffText") ?? "ВЫКЛ";
                    cfgVer = GetStr(json, "ver") ?? "3.1";
                }
            }
            catch { }
        }

        devId ??= cfgDevId;
        localKey ??= cfgKey;
        ip ??= cfgIp;
        port = cfgPort;
        index = cfgIndex;
        timeoutSec = cfgTimeout;

        if (guiMode && action == null)
        {
            if (string.IsNullOrWhiteSpace(devId) ||
                string.IsNullOrWhiteSpace(localKey) ||
                string.IsNullOrWhiteSpace(ip))
            {
                MessageBox.Show(
                    "Не заданы Device ID, Local Key или IP.\n" +
                    "Укажите их в файле smartrelay.json или через аргументы командной строки.\n\n" +
                    "Пример: SmartRelay.exe --id ... --key ... --ip ...",
                    "SmartRelay — ошибка конфигурации",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                PrintHelp();
                return 2;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(devId, localKey, ip, port, index, timeoutSec, cfgBtnOn, cfgBtnOff, cfgVer));
            return 0;
        }

        if (string.IsNullOrWhiteSpace(devId) ||
            string.IsNullOrWhiteSpace(localKey) ||
            string.IsNullOrWhiteSpace(ip) ||
            action == null)
        {
            PrintHelp();
            PrintExampleConfig();
            return 2;
        }

        try
        {
            var plug = new SmartPlug(devId, localKey, ip, port, index, timeoutSec, cfgVer);
            bool? result;

            switch (action)
            {
                case "on":
                    result = plug.TurnOn();
                    Console.WriteLine(result == null
                        ? "Команда ВКЛ отправлена, ответ не получен (проверьте IP и ключ)."
                        : "Розетка ВКЛ.");
                    return 0;
                case "off":
                    result = plug.TurnOff();
                    Console.WriteLine(result == null
                        ? "Команда ВЫКЛ отправлена, ответ не получен (проверьте IP и ключ)."
                        : "Розетка ВЫКЛ.");
                    return 0;
                case "status":
                    result = plug.Status();
                    if (result == null)
                    {
                        Console.WriteLine("Не удалось получить состояние розетки.");
                        return 1;
                    }
                    Console.WriteLine("Состояние розетки: " + (result.Value ? "ВКЛ" : "ВЫКЛ"));
                    return 0;
                case "toggle":
                    result = plug.Toggle();
                    if (result == null)
                    {
                        Console.WriteLine("Не удалось переключить розетку (проверьте IP и ключ).");
                        return 1;
                    }
                    Console.WriteLine("Розетка " + (result.Value ? "ВКЛ" : "ВЫКЛ") + ".");
                    return 0;
                default:
                    PrintHelp();
                    return 2;
            }
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or System.IO.IOException)
        {
            Console.Error.WriteLine("Ошибка связи с розеткой: " + ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Ошибка: " + ex.Message);
            return 2;
        }
    }

    static string? GetStr(Dictionary<string, JsonElement> dict, string name)
    {
        return dict.TryGetValue(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    static void PrintHelp()
    {
        Console.WriteLine("SmartRelay — локальное управление розеткой SmartLife / Tuya без интернета.");
        Console.WriteLine();
        Console.WriteLine("ГУИ (по умолчанию, без аргументов):");
        Console.WriteLine("  SmartRelay.exe");
        Console.WriteLine("  (кнопки ВКЛ / ВЫКЛ, надписи из smartrelay.json)");
        Console.WriteLine();
        Console.WriteLine("CLI режим:");
        Console.WriteLine("  SmartRelay.exe --id <DEVICE_ID> --key <LOCAL_KEY> --ip <IP> [действие]");
        Console.WriteLine();
        Console.WriteLine("Действия: on | off | status | toggle");
        Console.WriteLine();
        Console.WriteLine("Опции:");
        Console.WriteLine("  --id        Device ID (gwId/devId) розетки");
        Console.WriteLine("  --key       Local Key (16 символов или 32 hex)");
        Console.WriteLine("  --ip        IP-адрес розетки в локальной сети");
        Console.WriteLine("  --port      TCP порт (по умолчанию 6668)");
        Console.WriteLine("  --index     Номер точки данных DP (по умолчанию 1)");
        Console.WriteLine("  --timeout   Таймаут сети, сек (по умолчанию 5)");
        Console.WriteLine("  --config    Путь к JSON-конфигу (по умолчанию smartrelay.json)");
        Console.WriteLine("  --gui       Запустить графическое окно (по умолчанию)");
        Console.WriteLine("  --cli       Запустить в консольном режиме");
        Console.WriteLine();
        Console.WriteLine("Примеры:");
        Console.WriteLine("  SmartRelay.exe --id 0123456789abcdef0123 --key abcd1234efgh5678 --ip 192.168.1.42 on");
        Console.WriteLine("  SmartRelay.exe --id 0123456789abcdef0123 --key abcd1234efgh5678 --ip 192.168.1.42 off");
        Console.WriteLine("  SmartRelay.exe --id 0123456789abcdef0123 --key abcd1234efgh5678 --ip 192.168.1.42 status");
        Console.WriteLine("  SmartRelay.exe --id 0123456789abcdef0123 --key abcd1234efgh5678 --ip 192.168.1.42 toggle");
    }

    static void PrintExampleConfig()
    {
        Console.WriteLine();
        Console.WriteLine("Пример файла smartrelay.json:");
        Console.WriteLine("  {");
        Console.WriteLine("    \"dev_id\": \"0123456789abcdef0123\",");
        Console.WriteLine("    \"local_key\": \"abcd1234efgh5678\",");
        Console.WriteLine("    \"ip\": \"192.168.1.42\",");
        Console.WriteLine("    \"port\": 6668,");
        Console.WriteLine("    \"index\": 1,");
        Console.WriteLine("    \"timeout\": 5,");
        Console.WriteLine("    \"btnOnText\": \"ВКЛ\",");
        Console.WriteLine("    \"btnOffText\": \"ВЫКЛ\"");
        Console.WriteLine("  }");
    }
}
