using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SmartRelay;

/// <summary>Журнал диагностики: консоль + файл smartrelay.log + событие для GUI.</summary>
public static class Diag
{
    private static readonly object Sync = new();

    public static bool Verbose { get; set; }
    public static string LogFile { get; set; } = Path.Combine(AppContext.BaseDirectory, "smartrelay.log");
    public static event Action<string>? OnLog;

    public static void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        WriteLine(line);
    }

    public static void Detail(string message)
    {
        if (!Verbose)
            return;
        WriteLine($"{DateTime.Now:HH:mm:ss.fff} · {message}");
    }

    private static void WriteLine(string line)
    {
        lock (Sync)
        {
            try { Console.WriteLine(line); } catch { }
            try
            {
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line.Substring(13)}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { }
        }
        try { OnLog?.Invoke(line); } catch { }
    }

    public static void Hex(string title, byte[]? data)
    {
        if (!Verbose || data == null || data.Length == 0)
            return;

        WriteLine($"{DateTime.Now:HH:mm:ss.fff} {title} — {data.Length} байт");
        for (int off = 0; off < data.Length; off += 16)
        {
            int n = Math.Min(16, data.Length - off);
            var hex = new StringBuilder();
            var ascii = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                byte b = data[off + i];
                hex.Append(b.ToString("x2")).Append(i == 7 ? "  " : " ");
                ascii.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            WriteLine($"{DateTime.Now:HH:mm:ss.fff}   {off:x4}  {hex.ToString().PadRight(50)} {ascii}");
        }
    }

    public static void Reset()
    {
        try
        {
            if (File.Exists(LogFile))
                File.Delete(LogFile);
        }
        catch { }
    }
}

/// <summary>Сетевые проверки: TCP-порт, автоподбор порта, UDP-обнаружение Tuya-устройств.</summary>
public static class NetProbe
{
    /// <summary>Проверить, открыт ли TCP-порт.</summary>
    public static (bool ok, long ms, string message) TestTcp(string ip, int port, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var task = sock.ConnectAsync(IPAddress.Parse(ip), port);
            if (!task.Wait(timeoutMs))
                return (false, sw.ElapsedMilliseconds, $"таймаут {timeoutMs} мс");
            sw.Stop();
            return (sock.Connected, sw.ElapsedMilliseconds, sock.Connected ? "порт открыт, соединение установлено" : "нет соединения");
        }
        catch (Exception ex)
        {
            sw.Stop();
            var inner = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;
            string hint = inner is SocketException se
                ? se.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => "порт закрыт (connection refused) — неверный порт или устройство не слушает LAN-протокол",
                    SocketError.TimedOut => "таймаут — устройство недоступно/блокирует порт",
                    SocketError.HostUnreachable => "хост недостижим — проверьте IP и подсеть",
                    SocketError.NetworkUnreachable => "сеть недостижима — проверьте подключение к Wi-Fi/роутеру",
                    _ => se.SocketErrorCode.ToString()
                }
                : inner.Message;
            return (false, sw.ElapsedMilliseconds, hint);
        }
    }

    /// <summary>Стандартный порт Tuya и типичные альтернативы.</summary>
    public static List<int> CandidatePorts(int configured)
    {
        var list = new List<int> { configured };
        foreach (int p in new[] { 6668, 6667, 8886, 8887, 6666 })
            if (!list.Contains(p))
                list.Add(p);
        return list;
    }

    /// <summary>UDP-обнаружение Tuya: широковещательный запрос на 6666/6667, разбор ответов.</summary>
    public static List<string> DiscoverUdp(string devId, int listenMs = 3000)
    {
        var found = new List<string>();
        string payload = "{\"gwId\":\"" + devId + "\",\"devId\":\"" + devId + "\"}";
        byte[] msg = Encoding.UTF8.GetBytes(payload);
        var seen = new HashSet<string>();

        foreach (int port in new[] { 6666, 6667 })
        {
            try
            {
                using var udp = new UdpClient();
                udp.EnableBroadcast = true;
                udp.Client.ReceiveTimeout = listenMs;
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                foreach (var target in new[] { IPAddress.Broadcast, IPAddress.Parse("255.255.255.255") })
                {
                    try { udp.Send(msg, msg.Length, new IPEndPoint(target, port)); } catch { }
                }

                var end = DateTime.UtcNow.AddMilliseconds(listenMs);
                while (DateTime.UtcNow < end)
                {
                    try
                    {
                        IPEndPoint? remote = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = udp.Receive(ref remote);
                        string text = Encoding.UTF8.GetString(data).TrimEnd('\0');
                        string summary = SummarizeBroadcast(remote.Address.ToString(), port, text);
                        if (seen.Add(summary))
                            found.Add(summary);
                        Diag.Hex($"UDP ответ от {remote.Address}:{port}", data);
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Detail($"UDP порт {port}: {ex.Message}");
            }
        }
        return found;
    }

    private static string SummarizeBroadcast(string ip, int port, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text.Substring(text.IndexOf('{')));
            var root = doc.RootElement;
            string Get(string name) => root.TryGetProperty(name, out var el) ? el.ToString() : "?";
            return $"IP={ip} порт={port} id={Get("gwId")} ver={Get("version")} productKey={Get("productKey")} encrypt={Get("encrypt")}";
        }
        catch
        {
            return $"IP={ip} порт={port} сырой ответ: {text}";
        }
    }
}
