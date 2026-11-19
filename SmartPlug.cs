using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SmartRelay;

/// <summary>Ошибка протокола/связи с понятным текстом для пользователя.</summary>
public class TuyaException : Exception
{
    public TuyaException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Результат операции: значение + текст, который можно показать пользователю.</summary>
public sealed class PlugResult
{
    public bool? Value { get; }
    public string Message { get; }

    /// <summary>Команда принята устройством (retcode=0), но фактическое состояние не подтверждено.</summary>
    public bool CommandAccepted { get; }

    public bool Ok => Value.HasValue || CommandAccepted;

    private PlugResult(bool? value, string message, bool commandAccepted = false)
    {
        Value = value;
        Message = message;
        CommandAccepted = commandAccepted;
    }

    public static PlugResult Success(bool value, string message) => new(value, message);
    public static PlugResult Accepted(string message) => new(null, message, commandAccepted: true);
    public static PlugResult Fail(string message) => new(null, message);
}

/// <summary>
/// Клиент розетки SmartLife / Tuya по LAN.
/// Держит ОДНО постоянное TCP-соединение: в протоколе 3.4 сессионный ключ
/// привязан к соединению, поэтому handshake и команды должны идти в одном сокете.
/// </summary>
public class SmartPlug : IDisposable
{
    public string DevId { get; }
    public byte[] LocalKey { get; }
    public string Ip { get; }
    public int Port { get; }
    public int Index { get; }
    public TimeSpan Timeout { get; }
    /// <summary>Версия из конфига (в отличие от Version, которая может быть определена автоматически).</summary>
    public string ConfiguredVersion { get; }

    /// <summary>Фактически используемая версия: из конфига либо определённая автоматически.</summary>
    public string Version { get; set; }
    public bool PortFallback { get; set; } = true;

    /// <summary>
    /// Если true (по умолчанию) и с версией из конфига связи нет, пробуем другую версию (3.4 ↔ 3.1).
    /// Диагностике это отключается, чтобы она честно показывала результат каждой версии.
    /// </summary>
    public bool AutoDetectVersion { get; set; } = true;

    /// <summary>Порт, на котором реально установлено соединение.</summary>
    public int ActivePort { get; private set; }

    /// <summary>Последняя ошибка человеческим языком.</summary>
    public string? LastError { get; private set; }

    private Socket? _sock;
    private uint _seqno;
    private byte[]? _sessionKey;

    /// <summary>Предупреждение о несовпадении версии; печатается после первой успешной операции.</summary>
    private string? _pendingVersionNote;
    private int _forcePort = -1;

    /// <summary>Операции идут строго по очереди: одно TCP-соединение, один кадр за раз.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private double VersionNumber => TuyaProtocol.VersionToNumber(Version);

    public SmartPlug(string devId, string localKey, string ip,
        int port = TuyaProtocol.DefaultPort, int index = 1, int timeoutSec = 5, string version = "3.4")
    {
        DevId = string.IsNullOrWhiteSpace(devId) ? throw new ArgumentException("Device ID не задан.") : devId.Trim();
        LocalKey = TuyaProtocol.NormalizeKey(localKey);
        Ip = string.IsNullOrWhiteSpace(ip) ? throw new ArgumentException("IP не задан.") : ip.Trim();
        Port = port;
        Index = index;
        Timeout = TimeSpan.FromSeconds(timeoutSec <= 0 ? 5 : timeoutSec);
        Version = string.IsNullOrWhiteSpace(version) ? "3.4" : version.Trim();
        ConfiguredVersion = Version;
    }

    public void Dispose() => Close();

    public void Close()
    {
        try { _sock?.Shutdown(SocketShutdown.Both); } catch { }
        try { _sock?.Dispose(); } catch { }
        _sock = null;
        _sessionKey = null;
        _pendingVersionNote = null;
    }

    private void OpenSocket(int port)
    {
        Close();
        Diag.Log($"TCP: подключение к {Ip}:{port} ...");
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            ReceiveTimeout = (int)Timeout.TotalMilliseconds,
            SendTimeout = (int)Timeout.TotalMilliseconds
        };
        sock.Connect(Ip, port);
        _sock = sock;
        _seqno = 0;
        ActivePort = port;
        Diag.Log($"TCP: соединение установлено ({Ip}:{port})");
    }

    /// <summary>Гарантировать активное соединение (и handshake для 3.4).</summary>
    private void EnsureConnected()
    {
        if (_sock != null && _sock.Connected)
            return;

        var ports = _forcePort > 0
            ? new List<int> { _forcePort }
            : (PortFallback ? NetProbe.CandidatePorts(Port) : new List<int> { Port });

        var errors = new List<string>();
        string configuredVersion = Version;
        var versions = VersionCandidates().ToList();

        foreach (int p in ports)
        {
            var (ok, ms, msg) = NetProbe.TestTcp(Ip, p, (int)Timeout.TotalMilliseconds);
            Diag.Log($"TCP {Ip}:{p} → {(ok ? "открыт" : "закрыт")} ({ms} мс): {msg}");
            if (!ok)
            {
                errors.Add($"порт {p}: {msg}");
                continue;
            }

            foreach (string ver in versions)
            {
                Version = ver;
                try
                {
                    OpenSocket(p);

                    if (VersionNumber >= 3.4 && !NegotiateSessionKey())
                    {
                        errors.Add($"порт {p}, версия {ver}: handshake не удался — {LastError}");
                        Close();
                        continue;
                    }

                    // Для 3.1 рукопожатия нет, поэтому факт соединения ещё ничего не подтверждает.
                    // Предупреждение откладываем до первой успешной операции (_pendingVersionNote).
                    if (ver != configuredVersion)
                        _pendingVersionNote = $"ВНИМАНИЕ: устройство работает по протоколу {ver}, а в конфиге указано {configuredVersion}. " +
                                              $"Исправьте \"ver\": \"{ver}\" в smartrelay.json.";
                    if (p != Port)
                        Diag.Log($"ВНИМАНИЕ: устройство отвечает на порту {p}, а в конфиге указан {Port}. Укажите \"port\": {p}.");
                    return;
                }
                catch (Exception ex)
                {
                    errors.Add($"порт {p}, версия {ver}: {ex.Message}");
                    Close();
                }
            }
        }

        Version = configuredVersion;
        throw new TuyaException("Не удалось установить связь с розеткой: " + errors[0] +
            (errors.Count > 1 ? $" (ещё {errors.Count - 1} неудачных попыток — подробности в журнале)" : ""));
    }

    /// <summary>Версия из конфига, затем (если разрешено) альтернативная.</summary>
    private IEnumerable<string> VersionCandidates()
    {
        yield return Version;
        if (!AutoDetectVersion)
            yield break;
        yield return VersionNumber >= 3.4 ? "3.1" : "3.4";
    }

    /// <summary>Device ID в отчётах показываем частично — журнал можно пересылать безопасно.</summary>
    private static string MaskId(string id) =>
        id.Length <= 8 ? new string('*', id.Length)
                       : id.Substring(0, 4) + "…" + id.Substring(id.Length - 4);

    private void Send(byte[] frame)
    {
        int sent = 0;
        while (sent < frame.Length)
            sent += _sock!.Send(frame, sent, frame.Length - sent, SocketFlags.None);
    }

    private byte[]? ReadExact(int count, out string? error)
    {
        error = null;
        var buf = new byte[count];
        int off = 0;
        while (off < count)
        {
            int read;
            try
            {
                read = _sock!.Receive(buf, off, count - off, SocketFlags.None);
            }
            catch (SocketException ex)
            {
                error = ex.SocketErrorCode == SocketError.TimedOut
                    ? $"таймаут чтения ({_sock?.ReceiveTimeout} мс), получено {off} из {count} байт"
                    : "ошибка сокета: " + ex.SocketErrorCode;
                return null;
            }
            catch (ObjectDisposedException)
            {
                error = "соединение закрыто";
                return null;
            }

            if (read <= 0)
            {
                error = "устройство закрыло соединение";
                return null;
            }
            off += read;
        }
        return buf;
    }

    private byte[]? ReadFrame(out string? error)
    {
        error = null;

        byte[]? header = ReadExact(TuyaProtocol.HeaderLength, out error);
        if (header == null)
            return null;

        uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
        if (length == 0 || length > 65535)
        {
            error = $"неправдоподобная длина кадра в заголовке: {length}";
            return null;
        }

        byte[]? rest = ReadExact((int)length, out error);
        if (rest == null)
            return null;

        var full = new byte[header.Length + rest.Length];
        Buffer.BlockCopy(header, 0, full, 0, header.Length);
        Buffer.BlockCopy(rest, 0, full, header.Length, rest.Length);

        Diag.Hex("← принят кадр", full);
        return full;
    }

    /// <summary>Согласование сессионного ключа 3.4 (схема tinytuya: nonce + XOR + HMAC).</summary>
    private bool NegotiateSessionKey()
    {
        Diag.Log("3.4: старт согласования сессионного ключа");

        byte[] localNonce = TuyaProtocol.GenerateNonce(16);
        Diag.Hex("   nonce1 (наш)", localNonce);

        byte[] step1 = TuyaProtocol.BuildNegotiationFrame(LocalKey, TuyaProtocol.CmdSessKeyNegStart, localNonce, ref _seqno);
        Diag.Hex("→ SESS_KEY_NEG_START (0x03)", step1);
        Send(step1);

        byte[]? raw = ReadFrame(out string? err);
        if (raw == null)
        {
            LastError = "на запрос 0x03 ответа нет: " + err +
                " (если соединение открыто, но ответа нет — чаще всего неверный Local Key)";
            return false;
        }

        var candidates = TuyaProtocol.ParseCandidates(raw, LocalKey, out string note);
        if (candidates.Count == 0)
        {
            LastError = "ответ на 0x03 не разобран (" + note + ")";
            return false;
        }

        byte[]? decrypted = null;
        TuyaProtocol.Frame? frame = null;
        string decryptError = "нет подходящего варианта разбора";

        foreach (var cand in candidates)
        {
            if (cand.Cmd != TuyaProtocol.CmdSessKeyNegResp)
            {
                LastError = $"ожидался ответ 0x04, пришёл 0x{cand.Cmd:X2}";
                return false;
            }

            try
            {
                byte[] plain = TuyaProtocol.AesEcbDecrypt(LocalKey, cand.Payload);
                if (plain.Length >= 48 + TuyaProtocol.VersionHeaderLength && plain[0] == (byte)'3')
                    plain = plain.AsSpan(TuyaProtocol.VersionHeaderLength).ToArray();

                if (plain.Length < 48)
                {
                    decryptError = $"после расшифровки {plain.Length} байт вместо 48";
                    continue;
                }

                decrypted = plain;
                frame = cand;
                break;
            }
            catch (Exception ex)
            {
                decryptError = ex.Message;
            }
        }

        if (decrypted == null || frame == null)
        {
            LastError = "не удалось расшифровать ответ 0x04 — скорее всего неверный Local Key (" + decryptError + ")";
            return false;
        }

        Diag.Log($"   шаг 2: cmd=0x{frame.Cmd:X2}, retcode={frame.Retcode}, payload={frame.Payload.Length} байт");
        Diag.Hex("   расшифрованный ответ 0x04", decrypted);

        byte[] remoteNonce = decrypted.AsSpan(0, 16).ToArray();
        Diag.Hex("   nonce2 (устройства)", remoteNonce);

        byte[] expectedHmac = TuyaProtocol.HmacSha256(LocalKey, localNonce);
        byte[] receivedHmac = decrypted.AsSpan(16, 32).ToArray();
        if (!expectedHmac.AsSpan().SequenceEqual(receivedHmac))
        {
            LastError = "HMAC в ответе 0x04 не совпал — почти наверняка неверный Local Key";
            return false;
        }

        byte[] step3 = TuyaProtocol.BuildNegotiationFrame(LocalKey, TuyaProtocol.CmdSessKeyNegFinish,
            TuyaProtocol.HmacSha256(LocalKey, remoteNonce), ref _seqno);
        Diag.Hex("→ SESS_KEY_NEG_FINISH (0x05)", step3);
        Send(step3);

        _sessionKey = TuyaProtocol.DeriveSessionKey34(LocalKey, localNonce, remoteNonce);
        Diag.Hex("   сессионный ключ = AES-ECB(local_key, nonce1 XOR nonce2)", _sessionKey);
        Diag.Log("3.4: сессионный ключ согласован успешно");
        return true;
    }

    /// <summary>
    /// Выполняет операцию; при обрыве, характерном для неверной версии протокола,
    /// один раз повторяет её с другой версией (3.4 ↔ 3.1).
    /// </summary>
    private PlugResult Execute(bool query, Dictionary<string, object>? dps)
    {
        _gate.Wait();
        try
        {
            return ExecuteLocked(query, dps);
        }
        finally
        {
            _gate.Release();
        }
    }

    private PlugResult ExecuteLocked(bool query, Dictionary<string, object>? dps)
    {
        var res = ExecuteCore(query, dps);
        if (res.Ok)
        {
            NoteVersionMismatch();
            return res;
        }
        if (!AutoDetectVersion || !LooksLikeProtocolMismatch(res.Message))
            return res;

        string tried = Version;
        string alt = VersionNumber >= 3.4 ? "3.1" : "3.4";
        Diag.Log($"ВНИМАНИЕ: версия {tried} не подтвердилась ({res.Message}). Пробую {alt}...");
        Version = alt;
        Close();

        var retry = ExecuteCore(query, dps);
        if (retry.Ok)
        {
            _pendingVersionNote ??= $"ВНИМАНИЕ: устройство работает по протоколу {alt}, а в конфиге указано {ConfiguredVersion}. " +
                                    $"Исправьте \"ver\": \"{alt}\" в smartrelay.json.";
            NoteVersionMismatch();
            return retry;
        }

        Version = ConfiguredVersion;
        Close();
        return PlugResult.Fail($"{res.Message} (версия {alt}: {retry.Message}). " +
                               "Если порт открыт, но обе версии обрываются — проверьте local_key в smartrelay.json.");
    }

    /// <summary>Один раз на соединение сообщает, что версия протокола в конфиге не совпадает с фактической.</summary>
    private void NoteVersionMismatch()
    {
        if (_pendingVersionNote == null)
            return;
        Diag.Log(_pendingVersionNote);
        _pendingVersionNote = null;
    }

    /// <summary>Признаки того, что устройство отвергло кадр из-за несовпадения версии протокола.</summary>
    private static bool LooksLikeProtocolMismatch(string msg) =>
        msg.Contains("закрыл", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("таймаут", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("Ответ не получен", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Отправить команду и разобрать ответ.
    /// 3.4+: CONTROL_NEW (0x0D) с {"protocol":5,"t":...,"data":{"dps":...}} и
    /// DP_QUERY_NEW (0x10) с "{}"; 3.1: старые 0x07/0x0A с плоским JSON.
    /// </summary>
    private PlugResult ExecuteCore(bool query, Dictionary<string, object>? dps)
    {
        try
        {
            EnsureConnected();

            byte cmd;
            byte[] frame;

            if (VersionNumber >= 3.4)
            {
                cmd = query ? TuyaProtocol.CmdStatusNew : TuyaProtocol.CmdControlNew;
                string json = query
                    ? TuyaProtocol.EmptyJson34
                    : TuyaProtocol.BuildJson34Control(dps!);
                Diag.Detail($"   JSON для 3.4: {json}");
                frame = TuyaProtocol.BuildFrame34(_sessionKey!, cmd, json, ref _seqno, Version);
            }
            else
            {
                cmd = query ? TuyaProtocol.CmdDpQuery : TuyaProtocol.CmdControl;
                frame = TuyaProtocol.BuildFrame31(DevId, LocalKey, cmd, dps, ref _seqno);
            }

            Diag.Hex($"→ отправка cmd=0x{cmd:X2} (порт {ActivePort}, версия {Version})", frame);
            Send(frame);

            string lastPayloadText = "";
            bool acked = false;
            bool ackOnly = false;

            for (int attempt = 0; attempt < 4; attempt++)
            {
                byte[]? raw = ReadFrame(out string? readErr);
                if (raw == null)
                {
                    Close();
                    LastError = "Ответ не получен: " + readErr;
                    return PlugResult.Fail(LastError);
                }

                // 3.4 проверяется по HMAC сессионным ключом, 3.1 — по CRC32 (ключ не нужен)
                byte[]? frameKey = VersionNumber >= 3.4 ? _sessionKey : null;
                var candidates = TuyaProtocol.ParseCandidates(raw, frameKey, out string note);
                if (candidates.Count == 0)
                {
                    Diag.Log("   кадр отклонён: " + note);
                    continue;
                }

                foreach (var fr in candidates)
                {
                    byte[] payload;
                    try
                    {
                        payload = VersionNumber >= 3.4
                            ? TuyaProtocol.StripVersionHeader(TuyaProtocol.AesEcbDecrypt(_sessionKey!, fr.Payload), Version)
                            : TuyaProtocol.DecryptPayload31(LocalKey, fr.Payload);
                    }
                    catch (Exception ex)
                    {
                        Diag.Detail("   вариант разбора не расшифровался: " + ex.Message);
                        continue;
                    }

                    Diag.Hex($"   расшифровано (cmd=0x{fr.Cmd:X2}, retcode={fr.Retcode})", payload);
                    lastPayloadText = SafeText(payload);
                    if (lastPayloadText.Length > 0)
                        Diag.Detail("   ответ устройства: " + Shorten(lastPayloadText));

                    if (fr.Retcode == 0)
                        acked = true;

                    if (fr.Retcode != 0)
                    {
                        LastError = $"устройство вернуло retcode={fr.Retcode}" +
                                    (lastPayloadText.Length > 0 ? $": {Shorten(lastPayloadText)}" : "");
                        return PlugResult.Fail(LastError);
                    }

                    bool? value = TryExtractDp(payload);
                    if (value.HasValue)
                        return PlugResult.Success(value.Value, value.Value ? "Розетка ВКЛ." : "Розетка ВЫКЛ.");

                    // Пустой ответ {} — это подтверждение команды, а не начало новой посылки:
                    // ждать следующий кадр бессмысленно (иначе таймаут и ложный обрыв).
                    if (fr.Retcode == 0 && lastPayloadText.Length <= 2)
                        ackOnly = true;
                }

                if (ackOnly)
                    break;
            }

            // Команда принята (retcode=0), но состояние DP в ответе не пришло — так ведут себя
            // многие прошивки 3.4. Подтверждаем фактическое состояние запросом 0x10
            // в том же соединении (пакетно, все DP сразу).
            if (!query && acked)
            {
                Diag.Log("   команда принята (retcode=0), состояние не передано — подтверждаю запросом статуса");
                var confirm = ExecuteCore(query: true, dps: null);
                if (confirm.Ok && confirm.Value.HasValue)
                    return confirm;
                return PlugResult.Accepted("Команда отправлена, но состояние не подтвердилось" +
                                           (confirm.Message.Length > 0 ? ": " + confirm.Message : "."));
            }

            LastError = $"Ответ получен, но DP \"{Index}\" в нём не найден (проверьте \"index\" в конфиге)." +
                        (lastPayloadText.Length > 0 ? $" Ответ устройства: {Shorten(lastPayloadText)}" : "");
            return PlugResult.Fail(LastError);
        }
        catch (TuyaException ex)
        {
            Close();
            LastError = ex.Message;
            return PlugResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            Close();
            LastError = "Ошибка связи: " + ex.Message;
            return PlugResult.Fail(LastError);
        }
    }

    private static string SafeText(byte[] payload)
    {
        try { return Encoding.UTF8.GetString(payload).Trim('\0', ' ', '\r', '\n'); }
        catch { return ""; }
    }

    private static string Shorten(string text, int max = 200) =>
        text.Length <= max ? text : text.Substring(0, max) + "…";

    private bool? TryExtractDp(byte[] payload)
    {
        var json = TuyaProtocol.DecodeJson(payload);
        if (json == null)
            return null;

        JsonElement dps;
        if (json.TryGetValue("dps", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            dps = d;
        }
        else if (json.TryGetValue("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                 data.TryGetProperty("dps", out var inner) && inner.ValueKind == JsonValueKind.Object)
        {
            dps = inner;
        }
        else
        {
            return null;
        }

        if (!dps.TryGetProperty(Index.ToString(), out var v))
            return null;

        switch (v.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number: return v.GetInt32() != 0;
            case JsonValueKind.String: return v.GetString() == "true" || v.GetString() == "1";
            default: return null;
        }
    }

    public PlugResult Set(bool value)
    {
        var dps = new Dictionary<string, object> { [Index.ToString()] = value };
        return Execute(query: false, dps);
    }

    public PlugResult TurnOn() => Set(true);
    public PlugResult TurnOff() => Set(false);
    public PlugResult Status() => Execute(query: true, null);

    public PlugResult Toggle()
    {
        var st = Status();
        if (!st.Ok)
            return st;
        return Set(!st.Value!.Value);
    }

    /// <summary>Полная диагностика: порты, UDP-обнаружение, попытки 3.4 и 3.1.</summary>
    public string BuildDiagnosticsReport()
    {
        var sb = new StringBuilder();
        void Add(string s)
        {
            sb.AppendLine(s);
            Diag.Log(s);
        }

        Add("=== Диагностика SmartRelay ===");
        Add($"Время:          {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Add($"IP:             {Ip}");
        Add($"Порт в конфиге: {Port}");
        Add($"Версия:         {Version}");
        Add($"Device ID:      {MaskId(DevId)} (частично скрыт)");
        Add($"Local Key:      {LocalKey.Length} байт (скрыт)");
        Add($"Index (DP):     {Index}");
        Add("");

        Add("--- 1. Проверка TCP-портов ---");
        var openPorts = new List<int>();
        foreach (int p in NetProbe.CandidatePorts(Port))
        {
            var r = NetProbe.TestTcp(Ip, p, (int)Timeout.TotalMilliseconds);
            Add($"  {Ip}:{p} → {(r.ok ? "ОТКРЫТ" : "закрыт")} ({r.ms} мс) — {r.message}");
            if (r.ok)
                openPorts.Add(p);
        }
        if (openPorts.Count == 0)
            Add("  Ни один порт не открыт: проверьте IP, Wi-Fi и LAN-режим розетки.");
        Add("");

        Add("--- 2. UDP-обнаружение (broadcast) ---");
        var found = NetProbe.DiscoverUdp(DevId, 2500);
        if (found.Count == 0)
            Add("  Ответа на broadcast нет (роутер может его блокировать). Это не критично, если TCP открыт.");
        foreach (var f in found)
            Add("  " + f);
        Add("");

        Add("--- 3. Рукопожатие и запрос статуса ---");
        foreach (int p in openPorts)
        {
            foreach (string ver in new[] { "3.4", "3.1" })
            {
                string saved = Version;
                bool savedAuto = AutoDetectVersion;
                Version = ver;
                AutoDetectVersion = false;   // в диагностике версии проверяем строго по одной
                _forcePort = p;
                Close();

                Add($"  Попытка версии {ver} на порту {p}...");
                var res = Status();
                Add("    → " + (res.Ok ? "УСПЕХ: " + res.Message : "неудача: " + res.Message));

                Version = saved;
                AutoDetectVersion = savedAuto;
                if (res.Ok)
                    break;
            }
            _forcePort = -1;
            Close();
        }

        Add("");
        Add("Журнал сохранён в: " + Diag.LogFile);
        return sb.ToString();
    }
}
