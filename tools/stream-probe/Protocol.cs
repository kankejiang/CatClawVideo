using System.Buffers.Binary;
using System.Text;

namespace StreamProbe;

/// <summary>
/// 协议 <c>CATCLAW/1</c> 编解码（定义见 docs/tasks/README.md 第二节，**不得改动**）。
///
/// <para><b>握手</b>：客户端发一行 ASCII（\n 结尾）<c>CATCLAW/1</c>；
/// 服务端回 <c>OK &lt;width&gt; &lt;height&gt; &lt;codec&gt; &lt;fps&gt;</c>（codec ∈ { h264, jpeg }）。</para>
///
/// <para><b>帧</b>：<c>&lt;4B 大端长度 length&gt;&lt;1B 类型 type&gt;&lt;length 字节负载&gt;</c>。
/// length 是<b>负载</b>字节数（不含 type 字节本身）。单帧负载上限 4 MiB；
/// 未知类型必须按长度跳过（不能崩）；0x00 关闭（负载空）。所有多字节整数均<b>大端</b>。</para>
///
/// <para><b>半包/粘包</b>：TCP 是字节流 —— 读取一律走 <see cref="ReadExactAsync"/>（循环补齐），
/// 绝不假设一次 Read 恰好一帧。</para>
/// </summary>
public static class Protocol
{
    /// <summary>客户端握手行（不含 \n）。</summary>
    public const string HandshakeRequest = "CATCLAW/1";

    public const byte TypeClose = 0x00;
    public const byte TypeVideo = 0x01;
    public const byte TypeHeartbeat = 0x02;
    public const byte TypeTouch = 0x10;
    public const byte TypeKey = 0x11;
    public const byte TypeWheel = 0x12;

    /// <summary>单帧负载上限 4 MiB，超限直接断开（避免内存失控）。</summary>
    public const int MaxPayload = 4 * 1024 * 1024;

    /// <summary>触摸 action：0=down, 1=up, 2=move。</summary>
    public const byte TouchDown = 0, TouchUp = 1, TouchMove = 2;

    /// <summary>按键 action：0=down, 1=up。</summary>
    public const byte KeyDown = 0, KeyUp = 1;

    public sealed record HandshakeInfo(int Width, int Height, string Codec, int Fps);

    /// <summary>精确读 n 字节（循环补齐，处理半包）；流关闭时抛 EndOfStream。</summary>
    public static async Task<byte[]> ReadExactAsync(Stream s, int n, CancellationToken ct)
    {
        if (n < 0 || n > MaxPayload) throw new IOException($"ReadExact 长度非法: {n}");
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = await s.ReadAsync(buf.AsMemory(off, n - off), ct);
            if (r <= 0) throw new EndOfStreamException($"对端关闭（已读 {off}/{n} 字节）");
            off += r;
        }
        return buf;
    }

    /// <summary>读一行（以 \n 结尾，容忍 \r）。</summary>
    public static async Task<string> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            int r = await s.ReadAsync(one.AsMemory(), ct);
            if (r <= 0) throw new EndOfStreamException("对端在握手时关闭");
            char c = (char)one[0];
            if (c == '\n') break;
            if (c != '\r') sb.Append(c);          // 容忍 \r\n
            if (sb.Length > 512) throw new IOException("握手行过长");
        }
        return sb.ToString();
    }

    /// <summary>客户端握手：发 CATCLAW/1，读 OK 行并解析。</summary>
    public static async Task<HandshakeInfo> HandshakeAsClientAsync(Stream s, CancellationToken ct)
    {
        var req = Encoding.ASCII.GetBytes(HandshakeRequest + "\n");
        await s.WriteAsync(req, ct);
        await s.FlushAsync(ct);

        var line = await ReadLineAsync(s, ct);
        // 期望：OK <width> <height> <codec> <fps>
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || !parts[0].Equals("OK", StringComparison.Ordinal))
            throw new IOException($"握手失败：服务端回了「{line}」（期望 OK w h codec fps）");
        if (!int.TryParse(parts[1], out var w) || !int.TryParse(parts[2], out var h) || !int.TryParse(parts[4], out var fps))
            throw new IOException($"握手失败：数字字段解析不了「{line}」");
        if (w <= 0 || h <= 0 || fps <= 0) throw new IOException($"握手失败：宽高/fps 非法「{line}」");
        var codec = parts[3].ToLowerInvariant();
        if (codec is not ("jpeg" or "h264")) throw new IOException($"握手失败：未知 codec「{codec}」（jpeg|h264）");
        return new HandshakeInfo(w, h, codec, fps);
    }

    /// <summary>服务端握手：读客户端的 CATCLAW/1 行，回 OK 行。</summary>
    public static async Task HandshakeAsServerAsync(Stream s, int width, int height, string codec, int fps, CancellationToken ct)
    {
        var line = await ReadLineAsync(s, ct);
        if (!line.Equals(HandshakeRequest, StringComparison.Ordinal))
            throw new IOException($"客户端握手行不对：{line}（期望 {HandshakeRequest}）");
        var resp = Encoding.ASCII.GetBytes($"OK {width} {height} {codec} {fps}\n");
        await s.WriteAsync(resp, ct);
        await s.FlushAsync(ct);
    }

    // ── 帧组装（写入端）─────────────────────────────────────────────

    /// <summary>组装一帧：4B 大端长度（=负载字节数）+ 1B 类型 + 负载。</summary>
    public static byte[] BuildFrame(byte type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload) throw new IOException($"帧负载 {payload.Length}B 超上限 {MaxPayload}B");
        var buf = new byte[4 + 1 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)payload.Length);
        buf[4] = type;
        payload.CopyTo(buf.AsSpan(5));
        return buf;
    }

    /// <summary>
    /// 读一帧：4B 大端长度 + 1B 类型 + 负载（半包安全）。
    /// 负载超 <see cref="MaxPayload"/> 抛 IOException（按协议断开）；返回 payload 为空数组表示空负载。
    /// </summary>
    public static async Task<(byte Type, byte[] Payload)> ReadFrameAsync(Stream s, CancellationToken ct)
    {
        var lenBuf = await ReadExactAsync(s, 4, ct);
        uint length = (uint)(lenBuf[0] << 24 | lenBuf[1] << 16 | lenBuf[2] << 8 | lenBuf[3]);
        var typeBuf = await ReadExactAsync(s, 1, ct);
        byte type = typeBuf[0];
        if (length > MaxPayload)
            throw new IOException($"帧负载 {length}B 超上限 {MaxPayload}B —— 按协议断开");
        var payload = length == 0 ? [] : await ReadExactAsync(s, (int)length, ct);
        return (type, payload);
    }

    /// <summary>触摸帧负载：u16 x, u16 y, u8 action（共 5B）。</summary>
    public static byte[] BuildTouchPayload(ushort x, ushort y, byte action)
    {
        var p = new byte[5];
        BinaryPrimitives.WriteUInt16BigEndian(p, x);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), y);
        p[4] = action;
        return p;
    }

    /// <summary>按键帧负载：u32 keycode, u8 action（共 5B）。</summary>
    public static byte[] BuildKeyPayload(uint keycode, byte action)
    {
        var p = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(p, keycode);
        p[4] = action;
        return p;
    }

    /// <summary>滚轮帧负载：i16 dx, i16 dy（共 4B）。</summary>
    public static byte[] BuildWheelPayload(short dx, short dy)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteInt16BigEndian(p, dx);
        BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(2), dy);
        return p;
    }

    /// <summary>心跳负载：8B 大端单调毫秒时间戳。</summary>
    public static byte[] BuildHeartbeatPayload(long monotonicMs)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(p, monotonicMs);
        return p;
    }

    /// <summary>解析触摸负载（x, y, action）。</summary>
    public static (ushort X, ushort Y, byte Action) ParseTouch(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5) throw new IOException($"触摸负载应 5B，实得 {payload.Length}B");
        return (BinaryPrimitives.ReadUInt16BigEndian(payload),
                BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2)),
                payload[4]);
    }

    /// <summary>解析按键负载（keycode, action）。</summary>
    public static (uint Keycode, byte Action) ParseKey(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5) throw new IOException($"按键负载应 5B，实得 {payload.Length}B");
        return (BinaryPrimitives.ReadUInt32BigEndian(payload), payload[4]);
    }

    /// <summary>解析滚轮负载（dx, dy）。</summary>
    public static (short Dx, short Dy) ParseWheel(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) throw new IOException($"滚轮负载应 4B，实得 {payload.Length}B");
        return (BinaryPrimitives.ReadInt16BigEndian(payload),
                BinaryPrimitives.ReadInt16BigEndian(payload.Slice(2)));
    }

    /// <summary>解析心跳负载（单调毫秒时间戳）。</summary>
    public static long ParseHeartbeat(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) throw new IOException($"心跳负载应 8B，实得 {payload.Length}B");
        return BinaryPrimitives.ReadInt64BigEndian(payload);
    }
}
