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

    private static uint Crc32(byte[] data, int length)
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

    public static byte[] BuildFrame(string devId, byte[] localKey, byte cmd, Dictionary<string, object> dps, ref uint seqno)
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

        byte[] encrypted = AesEcbEncrypt(localKey, payload);
        string b64 = Convert.ToBase64String(encrypted);

        string pre = "data=" + b64 + "||lpv=3.1||" + Encoding.UTF8.GetString(localKey);
        byte[] md5 = MD5.HashData(Encoding.UTF8.GetBytes(pre));
        string md5Slice = Encoding.Latin1.GetString(md5, 8, 16);

        byte[] msgPayload = Encoding.UTF8.GetBytes("3.1" + md5Slice + b64);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Prefix);
        bw.Write(seqno);
        bw.Write(cmd);
        bw.Write(msgPayload.Length + 8);
        bw.Write(msgPayload);

        byte[] headerAndBody = ms.ToArray();
        uint crc = Crc32(headerAndBody, headerAndBody.Length);
        bw.Write(crc);
        bw.Write(Suffix);

        return ms.ToArray();
    }

    private static byte[] AesEcbEncrypt(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] AesEcbDecrypt(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }

    public static bool TryParseFrame(byte[] raw, out Frame frame)
    {
        frame = null;
        if (raw == null || raw.Length < 24)
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

        byte[] body = new byte[frameLength];
        Array.Copy(raw, 16, body, 0, frameLength);

        int payloadLen = frameLength - 8;
        byte[] msgPayload = new byte[payloadLen];
        Array.Copy(body, 0, msgPayload, 0, payloadLen);

        uint crc = BitConverter.ToUInt32(body, payloadLen);
        uint suffix = BitConverter.ToUInt32(body, frameLength - 4);
        if (suffix != Suffix)
            return false;

        uint calcCrc = Crc32(raw, 16 + payloadLen);
        if (calcCrc != crc)
            return false;

        frame = new Frame(seqno, (byte)cmd, msgPayload);
        return true;
    }

    public static Dictionary<string, JsonElement>? DecodeResponse(byte[] localKey, byte[] payload)
    {
        try
        {
            string text = Encoding.UTF8.GetString(payload);
            if (text.StartsWith("3.1"))
            {
                string b64 = text.Substring(3 + 16);
                byte[] encrypted = Convert.FromBase64String(b64);
                byte[] decrypted = AesEcbDecrypt(localKey, encrypted);
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
