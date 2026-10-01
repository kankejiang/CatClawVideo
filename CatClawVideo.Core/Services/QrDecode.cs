namespace CatClawVideo.Core.Services;

/// <summary>
/// 解码二维码 PNG 到文本：任何源给的码都能还原成 URL，宿主因此不需要知道源是谁。
///
/// 桥（guest 里的 ART）没有 java.awt，上行的 PNG 是桥侧手写的（8bit 灰度 + Deflate），
/// 而 QrPng 用的 QRCoder 出的是 1-bit 灰度 —— 两种位深都要吃，所以自带一个只依赖
/// System.IO.Compression 的极小 PNG 解码器，再交给 ZXing 做识别。于是"URL 从哪来"
/// 与"UI 怎么画"解耦：源给 URL 就直接出码，源只给码也能拿到 URL。
/// </summary>
public static class QrDecode
{
    /// <summary>从 PNG 字节解出二维码内容；解不出返回 null（不抛，调用方按"没有 URL"处理）。</summary>
    public static string? FromPng(byte[]? png)
    {
        try
        {
            if (png is not { Length: > 8 } || png[0] != 0x89 || png[1] != (byte)'P') return null;
            int w = 0, h = 0, bit = 0, color = 0, pos = 8;
            var idat = new MemoryStream();
            while (pos + 12 <= png.Length)
            {
                int len = ReadBE(png, pos);
                if (len < 0 || pos + 12 + len > png.Length) break;
                string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
                switch (type)
                {
                    case "IHDR":
                        w = ReadBE(png, pos + 8);
                        h = ReadBE(png, pos + 12);
                        bit = png[pos + 16];
                        color = png[pos + 17];
                        break;
                    case "IDAT":
                        idat.Write(png, pos + 8, len);
                        break;
                }
                pos += 12 + len;
                if (type == "IEND") break;
            }
            return DecodePixels(w, h, bit, color, Inflate(idat.ToArray()));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[QrDecode] " + ex);
            if (Environment.GetEnvironmentVariable("CATCLAW_QR_DEBUG") == "1")
                Console.Error.WriteLine("[QrDecode] " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    private static int ReadBE(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];

    private static string? DecodePixels(int w, int h, int bit, int color, byte[] raw)
    {
        // QRCoder 出 1-bit 灰度、桥上行是 8-bit 灰度 —— 位深 1/2/4/8 都吃
        if (w <= 0 || h <= 0 || (bit != 1 && bit != 2 && bit != 4 && bit != 8)
            || (color != 0 && color != 2)) return null;
        int channels = color == 0 ? 1 : 3;
        int bytesPerRow = (w * channels * bit + 7) / 8;
        if (raw.Length < h * (bytesPerRow + 1)) return null;
        var lum = new byte[w * h];
        var prev = new byte[bytesPerRow];
        for (int y = 0; y < h; y++)
        {
            int row = y * (bytesPerRow + 1);
            var line = new byte[bytesPerRow];
            Array.Copy(raw, row + 1, line, 0, bytesPerRow);
            Unfilter(raw[row], line, prev, Math.Max(1, channels * bit / 8));
            for (int x = 0; x < w; x++)
                lum[y * w + x] = channels == 1 ? Sample(line, x, bit) : Grey(line, x, bit);
            prev = line;
        }
        // ZXing.Net 这个重载要的是每像素 3 字节 RGB（喂 ARGB 会亮度错位、解不出）
        var rgb = new byte[lum.Length * 3];
        for (int i = 0; i < lum.Length; i++)
        {
            rgb[i * 3] = lum[i]; rgb[i * 3 + 1] = lum[i]; rgb[i * 3 + 2] = lum[i];
        }
        return new ZXing.BarcodeReaderGeneric
        {
            Options = new ZXing.Common.DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new List<ZXing.BarcodeFormat> { ZXing.BarcodeFormat.QR_CODE },
            },
        }.Decode(new ZXing.RGBLuminanceSource(rgb, w, h))?.Text;
    }

    private static int BitAt(byte[] line, int bitPos, int bit)
    {
        int byteIndex = bitPos >> 3, shift = 8 - bit - (bitPos & 7);
        int v = (line[byteIndex] >> shift) & ((1 << bit) - 1);
        return bit == 8 ? v : v * 255 / ((1 << bit) - 1);
    }

    private static byte Sample(byte[] line, int x, int bit) => (byte)BitAt(line, x * bit, bit);

    private static byte Grey(byte[] line, int x, int bit)
    {
        int r = BitAt(line, (x * 3) * bit, bit);
        int g = BitAt(line, (x * 3 + 1) * bit, bit);
        int b = BitAt(line, (x * 3 + 2) * bit, bit);
        return (byte)((r * 299 + g * 587 + b * 114) / 1000);
    }

    private static void Unfilter(byte f, byte[] line, byte[] prev, int bpp)
    {
        for (int i = 0; i < line.Length; i++)
        {
            int a = i >= bpp ? line[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
            int p = f switch { 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => 0 };
            line[i] = (byte)((line[i] + p) & 0xFF);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
    }

    private static byte[] Inflate(byte[] z)
    {
        using var ms = new MemoryStream(z);
        ms.Seek(2, SeekOrigin.Begin);        // 跳 zlib 头；末尾 adler32 不会被读到
        using var d = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
        using var o = new MemoryStream();
        d.CopyTo(o);
        return o.ToArray();
    }
}
