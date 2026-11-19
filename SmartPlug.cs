using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SmartRelay;

/// <summary>Клиент розетки SmartLife / Tuya для локального управления по LAN.</summary>
public class SmartPlug
{
    public string DevId { get; }
    public byte[] LocalKey { get; }
    public string Ip { get; }
    public int Port { get; }
    public int Index { get; }
    public TimeSpan Timeout { get; }
    public string Version { get; }  // "3.1" или "3.4"

    private uint _seqno;
    private byte[]? _sessionKey;
    private bool _sessionNegotiated;

    public SmartPlug(string devId, string localKey, string ip,
        int port = TuyaProtocol.DefaultPort, int index = 1, int timeoutSec = 5, string version = "3.1")
    {
        DevId = devId ?? throw new ArgumentNullException(nameof(devId));
        LocalKey = TuyaProtocol.NormalizeKey(localKey);
        Ip = ip ?? throw new ArgumentNullException(nameof(ip));
        Port = port;
        Index = index;
        Timeout = TimeSpan.FromSeconds(timeoutSec);
        Version = version ?? "3.1";
    }

    private byte[] SendRecv(byte[] frame, bool waitResponse)
    {
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        sock.ReceiveTimeout = (int)Timeout.TotalMilliseconds;
        sock.SendTimeout = (int)Timeout.TotalMilliseconds;
        sock.Connect(Ip, Port);
        sock.Send(frame);

        if (!waitResponse)
            return null;

        System.Threading.Thread.Sleep(250);

        using var ms = new MemoryStream();
        byte[] buffer = new byte[4096];
        sock.ReceiveTimeout = 2000;
        while (true)
        {
            try
            {
                int read = sock.Receive(buffer, SocketFlags.None);
                if (read <= 0)
                    break;
                ms.Write(buffer, 0, read);

                if (ms.Length >= 16)
                {
                    uint length = BitConverter.ToUInt32(ms.GetBuffer(), 12);
                    int frameLength = (int)length;

                    if (Version == "3.4")
                    {
                        if (ms.Length >= 16 + frameLength)
                            break;
                    }
                    else
                    {
                        if (ms.Length >= 16 + frameLength)
                            break;
                    }
                }
            }
            catch (SocketException)
            {
                break;
            }
        }
        return ms.Length > 0 ? ms.ToArray() : null;
    }

    /// <summary>Выполнить согласование сессионного ключа для протокола 3.4.</summary>
    private bool NegotiateSessionKey()
    {
        if (_sessionNegotiated && _sessionKey != null)
            return true;

        byte[] nonce1 = TuyaProtocol.GenerateNonce(16);

        var negJson = new Dictionary<string, object>
        {
            ["gwId"] = DevId,
            ["devId"] = DevId,
            ["uid"] = "",
            ["t"] = ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString(),
            ["nonce1"] = Convert.ToBase64String(nonce1)
        };

        string negJsonStr = JsonSerializer.Serialize(negJson);
        byte[] negPayload = Encoding.UTF8.GetBytes(negJsonStr);

        byte[] encrypted = TuyaProtocol.AesEcbEncrypt(LocalKey, negPayload);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(TuyaProtocol.Prefix);
        bw.Write((_seqno + 1) & 0xFFFFFFFF);
        bw.Write(TuyaProtocol.CmdSessKeyNegStart);
        bw.Write(encrypted.Length + 32 + 4);
        bw.Write(encrypted);

        byte[] headerAndBody = ms.ToArray();
        byte[] hmac = TuyaProtocol.HmacSha256(LocalKey, headerAndBody);
        bw.Write(hmac);
        bw.Write(TuyaProtocol.Suffix);

        byte[] frame = ms.ToArray();
        _seqno = (uint)((_seqno + 1) & 0xFFFFFFFF);

        byte[] raw = SendRecv(frame, waitResponse: true);
        if (raw == null)
            return false;

        if (!TuyaProtocol.TryParseFrame34(raw, LocalKey, out var respFrame))
            return false;

        byte[] decrypted = TuyaProtocol.AesEcbDecrypt(LocalKey, respFrame.Payload);
        string text = Encoding.UTF8.GetString(decrypted);

        string? nonce2B64 = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("nonce2", out var el))
                nonce2B64 = el.GetString();
        }
        catch { return false; }

        if (string.IsNullOrEmpty(nonce2B64))
            return false;

        byte[] nonce2 = Convert.FromBase64String(nonce2B64);

        _sessionKey = TuyaProtocol.DeriveSessionKey(nonce1, nonce2);

        byte[] finishFrame = TuyaProtocol.BuildFrame34(DevId, _sessionKey, TuyaProtocol.CmdSessKeyNegFinish, null, ref _seqno, includeVersionHeader: false);
        byte[] finishRaw = SendRecv(finishFrame, waitResponse: true);

        _sessionNegotiated = finishRaw != null;
        return _sessionNegotiated;
    }

    public bool? Set(bool value)
    {
        if (Version == "3.4")
        {
            if (!_sessionNegotiated && !NegotiateSessionKey())
                return null;

            var dps = new Dictionary<string, object> { [Index.ToString()] = value };
            byte[] frame = TuyaProtocol.BuildFrame34(DevId, _sessionKey!, TuyaProtocol.CmdControl, dps, ref _seqno);
            byte[] raw = SendRecv(frame, waitResponse: true);

            if (raw != null && TuyaProtocol.TryParseFrame34(raw, _sessionKey!, out var fr))
            {
                var decoded = TuyaProtocol.DecodeJson(fr.Payload);
                if (decoded != null && decoded.TryGetValue("dps", out var dpsEl) && dpsEl.ValueKind == JsonValueKind.Object)
                {
                    if (dpsEl.TryGetProperty(Index.ToString(), out var val))
                        return val.ValueKind == JsonValueKind.True || val.GetBoolean();
                }
            }
            return null;
        }
        else
        {
            var dps = new Dictionary<string, object> { [Index.ToString()] = value };
            byte[] frame = TuyaProtocol.BuildFrame31(DevId, LocalKey, TuyaProtocol.CmdControl, dps, ref _seqno);
            byte[] raw = SendRecv(frame, waitResponse: true);

            if (raw != null && TuyaProtocol.TryParseFrame31(raw, out var fr))
            {
                var decoded = DecodeResponse31(LocalKey, fr.Payload);
                if (decoded != null && decoded.TryGetValue("dps", out var dpsEl) && dpsEl.ValueKind == JsonValueKind.Object)
                {
                    if (dpsEl.TryGetProperty(Index.ToString(), out var val))
                        return val.ValueKind == JsonValueKind.True || val.GetBoolean();
                }
            }
            return null;
        }
    }

    public bool? Query()
    {
        if (Version == "3.4")
        {
            if (!_sessionNegotiated && !NegotiateSessionKey())
                return null;

            byte[] frame = TuyaProtocol.BuildFrame34(DevId, _sessionKey!, TuyaProtocol.CmdDpQuery, null, ref _seqno);
            byte[] raw = SendRecv(frame, waitResponse: true);

            if (raw != null && TuyaProtocol.TryParseFrame34(raw, _sessionKey!, out var fr))
            {
                var decoded = TuyaProtocol.DecodeJson(fr.Payload);
                if (decoded != null && decoded.TryGetValue("dps", out var dpsEl) && dpsEl.ValueKind == JsonValueKind.Object)
                {
                    if (dpsEl.TryGetProperty(Index.ToString(), out var val))
                        return val.ValueKind == JsonValueKind.True || val.GetBoolean();
                }
            }
            return null;
        }
        else
        {
            byte[] frame = TuyaProtocol.BuildFrame31(DevId, LocalKey, TuyaProtocol.CmdStatus, null, ref _seqno);
            byte[] raw = SendRecv(frame, waitResponse: true);

            if (raw != null && TuyaProtocol.TryParseFrame31(raw, out var fr))
            {
                var decoded = DecodeResponse31(LocalKey, fr.Payload);
                if (decoded != null && decoded.TryGetValue("dps", out var dpsEl) && dpsEl.ValueKind == JsonValueKind.Object)
                {
                    if (dpsEl.TryGetProperty(Index.ToString(), out var val))
                        return val.ValueKind == JsonValueKind.True || val.GetBoolean();
                }
            }
            return null;
        }
    }

    private static Dictionary<string, JsonElement>? DecodeResponse31(byte[] localKey, byte[] payload)
    {
        try
        {
            string text = Encoding.UTF8.GetString(payload);
            if (text.StartsWith("3.1") && text.Length > 19)
            {
                string b64 = text.Substring(19);
                byte[] encrypted = Convert.FromBase64String(b64);
                byte[] decrypted = TuyaProtocol.AesEcbDecrypt(localKey, encrypted);
                text = Encoding.UTF8.GetString(decrypted);
            }

            using var doc = JsonDocument.Parse(text);
            var result = new Dictionary<string, JsonElement>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                result[prop.Name] = prop.Value.Clone();
            return result;
        }
        catch
        {
            return null;
        }
    }

    public bool? TurnOn() => Set(true);
    public bool? TurnOff() => Set(false);
    public bool? Status() => Query();
    public bool? Toggle()
    {
        var cur = Status();
        if (cur == null)
            return null;
        return Set(!cur.Value);
    }
}
