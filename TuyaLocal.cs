using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartRelay;

public static class TuyaProtocol
{
    public const int DefaultPort = 6668;
    public const uint Prefix = 0x000055AA;
    public const uint Suffix = 0x0000AA55;

    public const byte CmdControl = 0x07;
    public const byte CmdStatus = 0x08;
    public const byte CmdDpQuery = 0x0A;
    public const byte CmdSessKeyNegStart = 0x03;
    public const byte CmdSessKeyNegResp = 0x04;
    public const byte CmdSessKeyNegFinish = 0x05;

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
        var h = new byte[15];
        var verBytes = Encoding.ASCII.GetBytes(version);
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

    public static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    /// <summary>
    /// Построить кадр Tuya 3.4 с сессионным ключом.
    /// Формат: header(16) + AES-ECB(sessionKey, version_header(15) + JSON) + HMAC-SHA256(sessionKey, header+encrypted) + suffix(4)
    /// </summary>
    public static byte[] BuildFrame34(string devId, byte[] sessionKey, byte cmd, Dictionary<string, object> dps, ref uint seqno, bool includeVersionHeader = true)
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

        if (includeVersionHeader)
        {
            byte[] vh = VersionHeader("3.4");
            var combined = new byte[vh.Length + payload.Length];
            Array.Copy(vh, combined, vh.Length);
            Array.Copy(payload, 0, combined, vh.Length, payload.Length);
            payload = combined;
        }

        byte[] encrypted = AesEcbEncrypt(sessionKey, payload);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Prefix);
        bw.Write(seqno);
        bw.Write(cmd);
        bw.Write(encrypted.Length + 32 + 4); // length = encrypted + HMAC + suffix
        bw.Write(encrypted);

        byte[] headerAndBody = ms.ToArray();
        byte[] hmac = HmacSha256(sessionKey, headerAndBody);
        bw.Write(hmac);
        bw.Write(Suffix);

        return ms.ToArray();
    }

    /// <summary>
    /// Построить кадр для согласования ключа (шаг 1 и 3).
    /// Payload шифруется ключом localKey (не сессионным!).
    /// </summary>
    public static byte[] BuildNegotiationFrame(string devId, byte[] localKey, byte cmd, byte[] payload, ref uint seqno)
    {
        seqno = (seqno + 1) & 0xFFFFFFFF;

        byte[] encrypted = AesEcbEncrypt(localKey, payload);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Prefix);
        bw.Write(seqno);
        bw.Write(cmd);
        bw.Write(encrypted.Length + 32 + 4);
        bw.Write(encrypted);

        byte[] headerAndBody = ms.ToArray();
        byte[] hmac = HmacSha256(localKey, headerAndBody);
        bw.Write(hmac);
        bw.Write(Suffix);

        return ms.ToArray();
    }

    /// <summary>Разобрать ответ Tuya 3.4 с сессионным ключом.</summary>
    public static bool TryParseFrame34(byte[] raw, byte[] sessionKey, out Frame frame)
    {
        frame = null;
        if (raw == null || raw.Length < 16 + 32 + 4)
            return false;

        uint prefix = BitConverter.ToUInt32(raw, 0);
        if (prefix != Prefix)
            return false;

        uint seqno = BitConverter.ToUInt32(raw, 4);
        uint cmd = BitConverter.ToUInt32(raw, 8);
        uint length = BitConverter.ToUInt32(raw, 12);
        int frameLength = (int)length;

        if (raw.Length < 16 + frameLength)
            return false;

        int hmacOffset = frameLength - 32 - 4;
        int encryptedLen = hmacOffset;

        byte[] encrypted = new byte[encryptedLen];
        Array.Copy(raw, 16, encrypted, 0, encryptedLen);

        byte[] hmacReceived = new byte[32];
        Array.Copy(raw, 16 + encryptedLen, hmacReceived, 0, 32);

        uint suffix = BitConverter.ToUInt32(raw, 16 + frameLength - 4);
        if (suffix != Suffix)
            return false;

        byte[] headerAndBody = new byte[16 + encryptedLen];
        Array.Copy(raw, 0, headerAndBody, 0, 16 + encryptedLen);
        byte[] hmacCalc = HmacSha256(sessionKey, headerAndBody);

        if (!hmacReceived.SequenceEqual(hmacCalc))
            return false;

        byte[] decrypted = AesEcbDecrypt(sessionKey, encrypted);

        // Удалить заголовок версии (15 байт), если есть
        if (decrypted.Length > 15 && Encoding.ASCII.GetString(decrypted, 0, 3) == "3.4")
        {
            var jsonBytes = new byte[decrypted.Length - 15];
            Array.Copy(decrypted, 15, jsonBytes, 0, jsonBytes.Length);
            decrypted = jsonBytes;
        }

        frame = new Frame(seqno, (byte)cmd, decrypted);
        return true;
    }

    /// <summary>Разобрать ответ на согласование ключа (шифрован localKey).</summary>
    public static bool TryParseNegotiationResponse(byte[] raw, byte[] localKey, out Frame frame)
    {
        frame = null;
        if (raw == null || raw.Length < 16 + 32 + 4)
            return false;

        uint prefix = BitConverter.ToUInt32(raw, 0);
        if (prefix != Prefix)
            return false;

        uint seqno = BitConverter.ToUInt32(raw, 4);
        uint cmd = BitConverter.ToUInt32(raw, 8);
        uint length = BitConverter.ToUInt32(raw, 12);
        int frameLength = (int)length;

        if (raw.Length < 16 + frameLength)
            return false;

        int hmacOffset = frameLength - 32 - 4;
        int encryptedLen = hmacOffset;

        byte[] encrypted = new byte[encryptedLen];
        Array.Copy(raw, 16, encrypted, 0, encryptedLen);

        byte[] hmacReceived = new byte[32];
        Array.Copy(raw, 16 + encryptedLen, hmacReceived, 0, 32);

        uint suffix = BitConverter.ToUInt32(raw, 16 + frameLength - 4);
        if (suffix != Suffix)
            return false;

        byte[] headerAndBody = new byte[16 + encryptedLen];
        Array.Copy(raw, 0, headerAndBody, 0, 16 + encryptedLen);
        byte[] hmacCalc = HmacSha256(localKey, headerAndBody);

        if (!hmacReceived.SequenceEqual(hmacCalc))
            return false;

        byte[] decrypted = AesEcbDecrypt(localKey, encrypted);
        frame = new Frame(seqno, (byte)cmd, decrypted);
        return true;
    }

    public static Dictionary<string, JsonElement>? DecodeJson(byte[] payload)
    {
        try
        {
            string text = Encoding.UTF8.GetString(payload);
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

    public static byte[] GenerateNonce(int length = 16)
    {
        using var rng = RandomNumberGenerator.Create();
        var nonce = new byte[length];
        rng.GetBytes(nonce);
        return nonce;
    }

    /// <summary>Сессионный ключ = local_nonce XOR remote_nonce (байт-в-байт).</summary>
    public static byte[] DeriveSessionKeyXor(byte[] nonce1, byte[] nonce2)
    {
        var result = new byte[16];
        for (int i = 0; i < 16; i++)
            result[i] = (byte)(nonce1[i] ^ nonce2[i]);
        return result;
    }

    public sealed class Frame
    {
        public uint Seqno { get; }
        public byte Cmd { get; }
        public byte[] Payload { get; }
        public Frame(uint seqno, byte cmd, byte[] payload)
        {
            Seqno = seqno;
            Cmd = cmd;
            Payload = payload;
        }
    }
}
