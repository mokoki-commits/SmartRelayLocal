using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartRelay;

/// <summary>
/// Низкоуровневый протокол Tuya LAN (версии 3.1 - 3.4).
/// ВАЖНО: все многобайтовые поля кадра передаются в BIG-ENDIAN (">4I" в tinytuya).
/// </summary>
public static class TuyaProtocol
{
    public const int DefaultPort = 6668;
    public const uint Prefix = 0x000055AA;
    public const uint Suffix = 0x0000AA55;

    public const byte CmdSessKeyNegStart = 0x03;
    public const byte CmdSessKeyNegResp = 0x04;
    public const byte CmdSessKeyNegFinish = 0x05;
    public const byte CmdControl = 0x07;
    public const byte CmdStatus = 0x08;
    public const byte CmdDpQuery = 0x0A;
    // В протоколе 3.4+ используются "новые" команды (как в tinytuya: payload_dict["v3.4"])
    public const byte CmdControlNew = 0x0D;
    public const byte CmdStatusNew = 0x10;

    public const int HeaderLength = 16;
    public const int RetcodeLength = 4;
    public const int HmacLength = 32;
    public const int CrcLength = 4;
    public const int SuffixLength = 4;
    public const int VersionHeaderLength = 15; // "3.x" + 12 нулевых байт

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (0xEDB88320u ^ (crc >> 1)) : (crc >> 1);
            table[i] = crc;
        }
        return table;
    }

    public static uint Crc32(byte[] data, int length)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < length; i++)
            crc = Crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    public static byte[] BigEndianBytes(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return b;
    }

    public static double VersionToNumber(string version)
    {
        return double.TryParse(version?.Replace(',', '.'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 3.1;
    }

    public static byte[] NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Local Key не задан.");

        if (key.Length == 32 && key.All(c => "0123456789abcdefABCDEF".Contains(c)))
            return HexToBytes(key);

        var bytes = Encoding.UTF8.GetBytes(key);
        if (bytes.Length != 16)
            throw new ArgumentException($"Local Key должен быть 16 символами (или 32 hex). Сейчас {bytes.Length} байт.");
        return bytes;
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    public static byte[] VersionHeader(string version)
    {
        var h = new byte[VersionHeaderLength];
        var verBytes = Encoding.ASCII.GetBytes(version ?? "3.4");
        Array.Copy(verBytes, 0, h, 0, Math.Min(verBytes.Length, 3));
        return h;
    }

    public static byte[] AesEcbEncrypt(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    public static byte[] AesEcbDecrypt(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>AES-ECB без выравнивания (нужно для вывода сессионного ключа 3.4).</summary>
    public static byte[] AesEcbEncryptNoPadding(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(data, 0, data.Length - data.Length % 16);
    }

    public static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    public static byte[] GenerateNonce(int length = 16)
    {
        using var rng = RandomNumberGenerator.Create();
        var nonce = new byte[length];
        rng.GetBytes(nonce);
        return nonce;
    }

    /// <summary>
    /// Сессионный ключ 3.4 (как в tinytuya, _negotiate_session_key_generate_finalize):
    /// AES-128-ECB-БЕЗ-паддинга(local_key, nonce1 XOR nonce2). Не голый XOR!
    /// </summary>
    public static byte[] DeriveSessionKey34(byte[] localKey, byte[] nonce1, byte[] nonce2) =>
        AesEcbEncryptNoPadding(localKey, DeriveSessionKeyXor(nonce1, nonce2));

    /// <summary>Сессионный ключ = local_nonce XOR remote_nonce.</summary>
    public static byte[] DeriveSessionKeyXor(byte[] nonce1, byte[] nonce2)
    {
        var result = new byte[Math.Min(nonce1.Length, nonce2.Length)];
        for (int i = 0; i < result.Length; i++)
            result[i] = (byte)(nonce1[i] ^ nonce2[i]);
        return result;
    }

    public static string BuildJson(string devId, Dictionary<string, object>? dps, bool includeDps)
    {
        var json = new Dictionary<string, object>
        {
            ["gwId"] = devId,
            ["devId"] = devId,
            ["uid"] = "",
            ["t"] = ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString()
        };
        if (includeDps && dps != null)
            json["dps"] = dps;
        return JsonSerializer.Serialize(json);
    }

    /// <summary>Собрать кадр: header(BE) + body + (HMAC-SHA256 | CRC32) + suffix(BE).</summary>
    private static byte[] Pack55AA(uint seqno, byte cmd, byte[] body, byte[]? hmacKey)
    {
        bool withHmac = hmacKey != null;
        int tailLen = withHmac ? HmacLength : CrcLength;

        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), Prefix);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), seqno);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), cmd);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)(body.Length + tailLen + SuffixLength));

        using var ms = new MemoryStream();
        ms.Write(header, 0, header.Length);
        ms.Write(body, 0, body.Length);

        byte[] headerAndBody = ms.ToArray();
        byte[] tail = withHmac
            ? HmacSha256(hmacKey!, headerAndBody)
            : BigEndianBytes(Crc32(headerAndBody, headerAndBody.Length));
        ms.Write(tail, 0, tail.Length);
        ms.Write(BigEndianBytes(Suffix), 0, 4);
        return ms.ToArray();
    }

    /// <summary>
    /// Команды, для которых заголовок версии НЕ добавляется
    /// (tinytuya: H.NO_PROTOCOL_HEADER_CMDS).
    /// </summary>
    public static bool NeedsVersionHeader(byte cmd) =>
        cmd is not (CmdSessKeyNegStart or CmdSessKeyNegResp or CmdSessKeyNegFinish
                    or 0x09 or CmdDpQuery or CmdStatusNew or 0x12);

    /// <summary>JSON для 3.4: {"protocol":5,"t":&lt;unixtime&gt;,"data":{"dps":{...}}}</summary>
    public static string BuildJson34Control(Dictionary<string, object> dps) =>
        "{\"protocol\":5,\"t\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() +
        ",\"data\":{\"dps\":" + JsonSerializer.Serialize(dps) + "}}";

    /// <summary>Запрос статуса в 3.4 — пустой JSON.</summary>
    public const string EmptyJson34 = "{}";

    /// <summary>
    /// Кадр 3.4: AES-ECB(сессионный ключ) от (заголовок версии + JSON) + HMAC-SHA256
    /// сессионным ключом. Для запроса статуса (0x10) заголовок версии не добавляется.
    /// </summary>
    public static byte[] BuildFrame34(byte[] sessionKey, byte cmd, string json,
        ref uint seqno, string version = "3.4")
    {
        seqno = (seqno + 1) & 0xFFFFFFFF;

        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        byte[] plain = jsonBytes;
        if (NeedsVersionHeader(cmd))
        {
            byte[] vh = VersionHeader(version);
            plain = new byte[vh.Length + jsonBytes.Length];
            Buffer.BlockCopy(vh, 0, plain, 0, vh.Length);
            Buffer.BlockCopy(jsonBytes, 0, plain, vh.Length, jsonBytes.Length);
        }

        byte[] encrypted = AesEcbEncrypt(sessionKey, plain);
        return Pack55AA(seqno, cmd, encrypted, sessionKey);
    }

    /// <summary>Кадр согласования ключа (0x03/0x05): AES и HMAC исходным Local Key, без version header.</summary>
    public static byte[] BuildNegotiationFrame(byte[] localKey, byte cmd, byte[] payload, ref uint seqno)
    {
        seqno = (seqno + 1) & 0xFFFFFFFF;
        byte[] encrypted = AesEcbEncrypt(localKey, payload);
        return Pack55AA(seqno, cmd, encrypted, localKey);
    }

    /// <summary>Кадр 3.1 (и 3.2/3.3 с оговорками) с CRC32.</summary>
    public static byte[] BuildFrame31(string devId, byte[] localKey, byte cmd,
        Dictionary<string, object>? dps, ref uint seqno)
    {
        seqno = (seqno + 1) & 0xFFFFFFFF;

        byte[] jsonBytes = Encoding.UTF8.GetBytes(BuildJson(devId, dps, dps != null));
        byte[] encrypted = AesEcbEncrypt(localKey, jsonBytes);
        string b64 = Convert.ToBase64String(encrypted);

        string keyStr = Encoding.Latin1.GetString(localKey);
        string pre = "data=" + b64 + "||lpv=3.1||" + keyStr;
        string md5Hex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(pre))).ToLowerInvariant();
        string md5Slice = md5Hex.Substring(8, 16);

        byte[] body = Encoding.UTF8.GetBytes("3.1" + md5Slice + b64);
        return Pack55AA(seqno, cmd, body, null);
    }

    /// <summary>
    /// Разобрать входящий кадр 55AA. Автоматически определяет наличие 4-байтового retcode
    /// (устройство его добавляет, мы при отправке - нет) и проверяет HMAC (если ключ задан) или CRC32.
    /// </summary>
    public static bool TryParseFrame(byte[]? raw, byte[]? key, out Frame frame, out string note)
    {
        var candidates = ParseCandidates(raw, key, out note);
        if (candidates.Count > 0)
        {
            frame = candidates[0];
            return true;
        }
        frame = null!;
        return false;
    }

    /// <summary>
    /// Вернуть все корректные по HMAC/CRC варианты разбора кадра: устройство добавляет
    /// 4-байтовый retcode, но встречаются прошивки без него — вызывающий код выбирает
    /// вариант, который успешно расшифровывается.
    /// </summary>
    public static List<Frame> ParseCandidates(byte[]? raw, byte[]? key, out string note)
    {
        var result = new List<Frame>();
        note = "";

        if (raw == null || raw.Length < HeaderLength + CrcLength + SuffixLength)
        {
            note = $"кадр слишком короткий ({raw?.Length ?? 0} байт)";
            return result;
        }

        uint prefix = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(0));
        if (prefix != Prefix)
        {
            note = $"неверный префикс 0x{prefix:X8} (ожидался 0x{Prefix:X8})";
            return result;
        }

        uint seqno = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(4));
        uint cmd = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(8));
        uint length = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(12));
        bool withHmac = key != null;
        int tailLen = withHmac ? HmacLength : CrcLength;

        if (length < (uint)(tailLen + SuffixLength))
        {
            note = $"некорректная длина в заголовке: {length}";
            return result;
        }
        if (raw.Length < HeaderLength + (int)length)
        {
            note = $"кадр обрезан: есть {raw.Length} байт, нужно {HeaderLength + (int)length}";
            return result;
        }

        int frameEnd = HeaderLength + (int)length;
        uint suffix = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(frameEnd - SuffixLength));
        if (suffix != Suffix)
        {
            note = $"неверный суффикс 0x{suffix:X8} (ожидался 0x{Suffix:X8})";
            return result;
        }

        int payloadEnd = frameEnd - SuffixLength - tailLen;
        byte[] receivedTail = raw.AsSpan(payloadEnd, tailLen).ToArray();

        byte[] calculated = withHmac
            ? HmacSha256(key!, raw.AsSpan(0, payloadEnd).ToArray())
            : BigEndianBytes(Crc32(raw, payloadEnd));

        if (!calculated.SequenceEqual(receivedTail))
        {
            note = withHmac
                ? "HMAC-SHA256 не совпал — неверный Local Key либо чужая/просроченная сессия"
                : "CRC32 не совпал — повреждённый кадр";
            return result;
        }

        foreach (int retcodeLen in new[] { RetcodeLength, 0 })
        {
            int bodyStart = HeaderLength + retcodeLen;
            if (payloadEnd <= bodyStart)
                continue;

            uint retcode = retcodeLen == RetcodeLength
                ? BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(HeaderLength, RetcodeLength))
                : 0;

            result.Add(new Frame(seqno, (byte)cmd, raw.AsSpan(bodyStart, payloadEnd - bodyStart).ToArray(), retcode));
        }

        note = result.Count == 2 ? "ok (retcode=4 и без retcode)" : "ok";
        return result;
    }

    /// <summary>Достать JSON из расшифрованного payload 3.4 (срезав 15-байтовый version header).</summary>
    public static byte[] StripVersionHeader(byte[] decrypted, string version)
    {
        var verBytes = Encoding.ASCII.GetBytes(version ?? "3.4");
        if (decrypted.Length > VersionHeaderLength &&
            decrypted[0] == verBytes[0] && decrypted[1] == verBytes[1] && decrypted[2] == verBytes[2])
        {
            return decrypted.AsSpan(VersionHeaderLength).ToArray();
        }
        return decrypted;
    }

    /// <summary>Расшифровать payload 3.1 ("3.1" + 16 hex MD5 + base64).</summary>
    public static byte[] DecryptPayload31(byte[] localKey, byte[] payload)
    {
        string text;
        try { text = Encoding.UTF8.GetString(payload); }
        catch { return payload; }

        if (text.StartsWith("3.1") && text.Length > 19)
        {
            try
            {
                byte[] encrypted = Convert.FromBase64String(text.Substring(19));
                return AesEcbDecrypt(localKey, encrypted);
            }
            catch (Exception ex)
            {
                Diag.Log("3.1: не удалось расшифровать payload: " + ex.Message);
                return payload;
            }
        }
        return payload;
    }

    public static Dictionary<string, JsonElement>? DecodeJson(byte[] payload)
    {
        try
        {
            string text = Encoding.UTF8.GetString(payload);
            int brace = text.IndexOf('{');
            if (brace < 0)
                return null;
            using var doc = JsonDocument.Parse(text.Substring(brace));
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

    public sealed class Frame
    {
        public uint Seqno { get; }
        public byte Cmd { get; }
        public byte[] Payload { get; }
        public uint Retcode { get; }

        public Frame(uint seqno, byte cmd, byte[] payload, uint retcode = 0)
        {
            Seqno = seqno;
            Cmd = cmd;
            Payload = payload;
            Retcode = retcode;
        }
    }
}
