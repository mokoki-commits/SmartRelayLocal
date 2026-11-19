using System;
using System.IO;
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
        // по умолчанию конфиг лежит рядом с exe (двойной щёлчок, ярлык, .bat из другой папки)
        string configPath = Path.Combine(AppContext.BaseDirectory, "smartrelay.json");
        bool explicitConfig = false;
        string? action = null;
        bool guiMode = true;
        bool verbose = false;
        bool? showDiagFlag = null;   // --no-diag / --show-diag перекрывают конфиг

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
                case "--config": configPath = args[++i]; explicitConfig = true; break;
                case "--no-diag": case "--hide-diag": showDiagFlag = false; break;
                case "--show-diag": showDiagFlag = true; break;
                case "--verbose": case "-v": verbose = true; break;
                case "on": case "off": case "status": case "toggle": case "diag":
                    action = args[i]; guiMode = false; break;
                case "--gui": guiMode = true; break;
                case "--cli": guiMode = false; break;
                case "--version": case "-V":
                    PrintVersion();
                    return 0;
                case "--help": case "-h":
                    PrintHelp();
                    return 0;
            }
        }

        Diag.Verbose = verbose || action == "diag";

        string? cfgDevId = null, cfgKey = null, cfgIp = null;
        int cfgPort = port, cfgIndex = index, cfgTimeout = timeoutSec;
        string cfgBtnOn = "ВКЛ", cfgBtnOff = "ВЫКЛ", cfgVer = "3.4";
        int cfgPoll = 3000;
        bool cfgShowDiag = true;
        string? cfgWarning = null;
        string? cfgArchitect = null, cfgExecutor = null;

        if (!explicitConfig && !File.Exists(configPath))
        {
            string inCwd = Path.Combine(Directory.GetCurrentDirectory(), "smartrelay.json");
            if (File.Exists(inCwd))
                configPath = inCwd;
        }

        var cfg = AppConfig.Load(configPath);
        cfgDevId = cfg.DevId;
        cfgKey = cfg.LocalKey;
        cfgIp = cfg.Ip;
        cfgPort = cfg.Port ?? port;
        cfgIndex = cfg.Index ?? index;
        cfgTimeout = cfg.TimeoutSec ?? timeoutSec;
        cfgBtnOn = cfg.BtnOn ?? "ВКЛ";
        cfgBtnOff = cfg.BtnOff ?? "ВЫКЛ";
        cfgVer = cfg.Ver ?? "3.4";
        cfgPoll = cfg.PollMs ?? 3000;
        cfgShowDiag = cfg.ShowDiag ?? true;
        cfgArchitect = cfg.Architect;
        cfgExecutor = cfg.Executor;
        cfgWarning = cfg.Warning;

        if (cfgWarning != null && guiMode)
        {
            MessageBox.Show(
                cfgWarning + "\n\nБудут использованы значения по умолчанию:\n" +
                "кнопка диагностики видна, опрос каждые 3 с.",
                "SmartRelay — конфигурация", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        if (showDiagFlag.HasValue)
            cfgShowDiag = showDiagFlag.Value;

        AppInfo.Configure(cfgArchitect, cfgExecutor);

        Diag.Log(AppInfo.Summary);
        Diag.Log($"Конфиг: {(File.Exists(configPath) ? configPath : "не найден, значения по умолчанию")}");
        Diag.Log($"Настройки: port={cfgPort}, index={cfgIndex}, ver={cfgVer}, timeout={cfgTimeout} с, " +
                 $"poll={cfgPoll} мс, showDiag={(cfgShowDiag ? "true" : "false")}");
        if (!string.IsNullOrWhiteSpace(cfgWarning))
            Diag.Log("ВНИМАНИЕ: " + cfgWarning);

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
                    "Пример: SmartRelay.exe --id ... --key ... --ip ... --port 6668",
                    "SmartRelay — ошибка конфигурации",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                PrintHelp();
                return 2;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(devId, localKey, ip, port, index, timeoutSec, cfgBtnOn, cfgBtnOff, cfgVer, cfgPoll, cfgShowDiag));
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

            Console.WriteLine(AppInfo.Summary);
            Console.WriteLine(AppInfo.ShortLine);
            Console.WriteLine($"IP={ip}, порт={port}, версия протокола={cfgVer}, index={index}");
            Console.WriteLine("Журнал: " + Diag.LogFile);
            Console.WriteLine();

            switch (action)
            {
                case "diag":
                    Console.WriteLine(plug.BuildDiagnosticsReport());
                    return 0;

                case "on":
                case "off":
                {
                    var r = action == "on" ? plug.TurnOn() : plug.TurnOff();
                    Console.WriteLine(r.Ok ? r.Message : "НЕУДАЧА: " + r.Message);
                    return r.Ok ? 0 : 1;
                }

                case "status":
                {
                    var r = plug.Status();
                    Console.WriteLine(r.Ok ? "Состояние: " + r.Message : "НЕУДАЧА: " + r.Message);
                    return r.Ok ? 0 : 1;
                }

                case "toggle":
                {
                    var r = plug.Toggle();
                    Console.WriteLine(r.Ok ? r.Message : "НЕУДАЧА: " + r.Message);
                    return r.Ok ? 0 : 1;
                }

                default:
                    PrintHelp();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Ошибка: " + ex.Message);
            return 1;
        }
    }

    static void PrintVersion()
    {
        Console.WriteLine(AppInfo.AboutText);
        Console.WriteLine();
        Console.WriteLine($"Журнал: {Diag.LogFile}");
    }

    static void PrintHelp()
    {
        Console.WriteLine($"{AppInfo.Title} — локальное управление розеткой SmartLife / Tuya без интернета.");
        Console.WriteLine(AppInfo.ShortLine);
        Console.WriteLine();
        Console.WriteLine("ГУИ (по умолчанию, без аргументов):");
        Console.WriteLine("  SmartRelay.exe              окно с кнопками ВКЛ / ВЫКЛ и диагностикой");
        Console.WriteLine();
        Console.WriteLine("Режимы командной строки:");
        Console.WriteLine("  SmartRelay.exe [опции] on|off|status|toggle");
        Console.WriteLine("  SmartRelay.exe [опции] diag     расширенная диагностика (порты, UDP, handshake)");
        Console.WriteLine();
        Console.WriteLine("Опции:");
        Console.WriteLine("  --id        Device ID (gwId/devId) розетки");
        Console.WriteLine("  --key       Local Key (16 символов или 32 hex)");
        Console.WriteLine("  --ip        IP-адрес розетки в локальной сети");
        Console.WriteLine("  --port      TCP порт (по умолчанию 6668; проверяются также 6667, 8886)");
        Console.WriteLine("  --index     Номер точки данных DP (по умолчанию 1)");
        Console.WriteLine("  --timeout   Таймаут сети, сек (по умолчанию 5)");
        Console.WriteLine("  --config    Путь к JSON-конфигу (по умолчанию smartrelay.json рядом с exe)");
        Console.WriteLine("  --no-diag   Скрыть кнопку «Диагностика (журнал)» (то же, что \"showDiag\": false)");
        Console.WriteLine("  --show-diag Показать кнопку диагностики, даже если в конфиге false");
        Console.WriteLine("  --verbose   Подробный журнал с hex-дампами кадров");
        Console.WriteLine("  --version   Версия программы, лицензия и авторы (архитектор/исполнитель)");
        Console.WriteLine("  --gui       Запустить графическое окно");
        Console.WriteLine("  --cli       Работать в консоли");
        Console.WriteLine();
        Console.WriteLine("Пример:");
        Console.WriteLine("  SmartRelay.exe --id 0123456789abcdef0123 --key abcd1234efgh5678 --ip 192.168.1.42 --port 6668 diag");
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
        Console.WriteLine("    \"ver\": \"3.4\",");
        Console.WriteLine("    \"poll\": 3000,");
        Console.WriteLine("    \"showDiag\": true,");
        Console.WriteLine("    \"architect\": \"avk013\",");
        Console.WriteLine("    \"executor\": \"Cline (AI coding agent) mokoki\",");
        Console.WriteLine("    \"btnOnText\": \"ВКЛ\",");
        Console.WriteLine("    \"btnOffText\": \"ВЫКЛ\"");
        Console.WriteLine("  }");
    }
}
