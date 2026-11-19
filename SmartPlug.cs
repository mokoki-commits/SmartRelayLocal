using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
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

    private uint _seqno;

    public SmartPlug(string devId, string localKey, string ip,
        int port = TuyaProtocol.DefaultPort, int index = 1, int timeoutSec = 5)
    {
        DevId = devId ?? throw new ArgumentNullException(nameof(devId));
        LocalKey = TuyaProtocol.NormalizeKey(localKey);
        Ip = ip ?? throw new ArgumentNullException(nameof(ip));
        Port = port;
        Index = index;
        Timeout = TimeSpan.FromSeconds(timeoutSec);
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
                    if (ms.Length >= 16 + length)
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

    public bool? Set(bool value)
    {
        var dps = new Dictionary<string, object> { [Index.ToString()] = value };
        byte[] frame = TuyaProtocol.BuildFrame(DevId, LocalKey, TuyaProtocol.CmdControl, dps, ref _seqno);
        byte[] raw = SendRecv(frame, waitResponse: true);

        if (raw != null && TuyaProtocol.TryParseFrame(raw, out var fr))
        {
            var decoded = TuyaProtocol.DecodeResponse(LocalKey, fr.Payload);
            if (decoded != null &&
                decoded.TryGetValue("dps", out var dpsEl) &&
                dpsEl.ValueKind == JsonValueKind.Object &&
                dpsEl.TryGetProperty(Index.ToString(), out var val))
            {
                return val.ValueKind == JsonValueKind.True || val.GetBoolean();
            }
        }
        return null;
    }

    public bool? Query()
    {
        byte[] frame = TuyaProtocol.BuildFrame(DevId, LocalKey, TuyaProtocol.CmdStatus, null, ref _seqno);
        byte[] raw = SendRecv(frame, waitResponse: true);

        if (raw != null && TuyaProtocol.TryParseFrame(raw, out var fr))
        {
            var decoded = TuyaProtocol.DecodeResponse(LocalKey, fr.Payload);
            if (decoded != null &&
                decoded.TryGetValue("dps", out var dpsEl) &&
                dpsEl.ValueKind == JsonValueKind.Object &&
                dpsEl.TryGetProperty(Index.ToString(), out var val))
            {
                return val.ValueKind == JsonValueKind.True || val.GetBoolean();
            }
        }
        return null;
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
