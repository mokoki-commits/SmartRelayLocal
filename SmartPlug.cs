using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
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
                    if (ms.Length >= 16 + frameLength)
                        break;
                }
            }
            catch (SocketException)
            {
                break;
            }
        }
        return ms.Length > 0 ? ms.ToArray() : null;
    }

    /// <summary>Согласование сессионного ключа для протокола 3.4 (по схеме tinytuya).</summary>
    private bool NegotiateSessionKey()
    {
        if (_sessionNegotiated && _sessionKey != null)
            return true;

        // Шаг 1: SESS_KEY_NEG_START (0x03) — enviar локальный nonce (сырые 16 байт, НЕ JSON!)
        byte[] localNonce = TuyaProtocol.GenerateNonce(16);

        byte[] step1Frame = TuyaProtocol.BuildNegotiationFrame(DevId, LocalKey, TuyaProtocol.CmdSessKeyNegStart, localNonce, ref _seqno);
        byte[] raw = SendRecv(step1Frame, waitResponse: true);
        if (raw == null)
            return false;

        // Шаг 2: Разобрать SESS_KEY_NEG_RESP (0x04)
        if (!TuyaProtocol.TryParseNegotiationResponse(raw, LocalKey, out var respFrame))
            return false;

        if (respFrame.Cmd != TuyaProtocol.CmdSessKeyNegResp)
            return false;

        byte[] payload = respFrame.Payload;
        if (payload.Length < 48)
            return false;

        // remote_nonce = первые 16 байт
        byte[] remoteNonce = new byte[16];
        Array.Copy(payload, 0, remoteNonce, 0, 16);

        // HMAC-SHA256(localKey, localNonce) = байты 16-47
        byte[] expectedHmac = TuyaProtocol.HmacSha256(LocalKey, localNonce);
        byte[] receivedHmac = new byte[32];
        Array.Copy(payload, 16, receivedHmac, 0, 32);

        if (!expectedHmac.SequenceEqual(receivedHmac))
            return false;

        // Шаг 3: SESS_KEY_NEG_FINISH (0x05) — HMAC-SHA256(localKey, remoteNonce) (32 байта)
        byte[] finishPayload = TuyaProtocol.HmacSha256(LocalKey, remoteNonce);
        byte[] step3Frame = TuyaProtocol.BuildNegotiationFrame(DevId, LocalKey, TuyaProtocol.CmdSessKeyNegFinish, finishPayload, ref _seqno);
        byte[] step3Raw = SendRecv(step3Frame, waitResponse: false);

        // Финализировать: sessionKey = localNonce XOR remoteNonce
        _sessionKey = TuyaProtocol.DeriveSessionKeyXor(localNonce, remoteNonce);
        _sessionNegotiated = true;

        return true;
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
            byte[] frame = BuildFrame31(DevId, LocalKey, TuyaProtocol.CmdControl, dps, ref _seqno);
            byte[] raw = SendRecv(frame, waitResponse: true);

            if (raw != null && TryParseFrame31(raw, out var fr))
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
            byte[] frame = BuildFrame31(DevId, LocalKey, TuyaProtocol.CmdStatus, null, ref _seqno);
            byte[] raw = SendRecv(frame, waitResponse: true);

            if (raw != null && TryParseFrame31(raw, out var fr))
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

    // ========== Tuya 3.1 (legacy) ==========

    private static byte[] BuildFrame31(string devId, byte[] localKey, byte cmd, Dictionary<string, object> dps, ref uint seqno)
    {
        seqno = (seqno + 1) & 0xFFFFFFFF;

        var json = new Dictionary<string, object>
        {
            ["gwId"] = devId,
            ["devId"] = devId,
            ["uid"] = "",
            ["t"] = ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString(),
        };
        if (dps != null)
            json["dps"] = dps;

        string jsonStr = JsonSerializer.Serialize(json);
        byte[] payload = Encoding.UTF8.GetBytes(jsonStr);

        byte[] encrypted = TuyaProtocol.AesEcbEncrypt(localKey, payload);
        string b64 = Convert.ToBase64String(encrypted);

        string keyStr = Encoding.Latin1.GetString(localKey);
        string pre = "data=" + b64 + "||lpv=3.1||" + keyStr;
        byte[] md5Bytes = MD5.HashData(Encoding.UTF8.GetBytes(pre));
        string md5Hex = Convert.ToHexString(md5Bytes).ToLowerInvariant();
        string md5Slice = md5Hex.Substring(8, 16);

        byte[] msgPayload = Encoding.UTF8.GetBytes("3.1" + md5Slice + b64);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(TuyaProtocol.Prefix);
        bw.Write(seqno);
        bw.Write(cmd);
        bw.Write(msgPayload.Length + 8);
        bw.Write(msgPayload);

        byte[] headerAndBody = ms.ToArray();
        uint crc = TuyaProtocol.Crc32(headerAndBody, headerAndBody.Length);
        bw.Write(crc);
        bw.Write(TuyaProtocol.Suffix);

        return ms.ToArray();
    }

    private static bool TryParseFrame31(byte[] raw, out TuyaProtocol.Frame frame)
    {
        frame = null;
        if (raw == null || raw.Length < 24)
            return false;

        uint prefix = BitConverter.ToUInt32(raw, 0);
        if (prefix != TuyaProtocol.Prefix)
            return false;

        uint seqno = BitConverter.ToUInt32(raw, 4);
        uint cmd = BitConverter.ToUInt32(raw, 8);
        uint length = BitConverter.ToUInt32(raw, 12);
        int frameLength = (int)length;

        if (raw.Length < 16 + frameLength)
            return false;

        byte[] body = new byte[frameLength];
        Array.Copy(raw, 16, body, 0, frameLength);

        int payloadLen = frameLength - 8;
        byte[] msgPayload = new byte[payloadLen];
        Array.Copy(body, 0, msgPayload, 0, payloadLen);

        uint crc = BitConverter.ToUInt32(body, payloadLen);
        uint suffix = BitConverter.ToUInt32(body, frameLength - 4);
        if (suffix != TuyaProtocol.Suffix)
            return false;

        uint calcCrc = TuyaProtocol.Crc32(raw, 16 + payloadLen);
        if (calcCrc != crc)
            return false;

        frame = new TuyaProtocol.Frame(seqno, (byte)cmd, msgPayload);
        return true;
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
