using System.Text;

namespace CatClawVideo.Core.Services.QemuThunder;

/// <summary>种子内单个文件条目。</summary>
public sealed record TorrentEntry(int Index, long Size, string Rel);

/// <summary>
/// 极简 bencode 解析器（只服务 .torrent 文件列表展开）。
/// 移植自实验装置 <c>JavaBridge/qemu-src/build/ctrlserver2.py</c> 的 <c>bdec</c> / <c>parse_torrent</c>
/// —— 该实现已在磁力全链路中验证（mag26/mag28 完整下载）。
/// </summary>
internal static class Bencode
{
    /// <summary>解析种子字节流 →（文件列表，种子名）。</summary>
    public static (List<TorrentEntry> Files, string Name) ParseTorrent(byte[] data)
    {
        var root = Decode(data) as Dictionary<string, object?>
            ?? throw new InvalidDataException("种子根节点不是字典");
        if (root["info"] is not Dictionary<string, object?> info)
            throw new InvalidDataException("种子缺少 info 字典");

        var name = info.TryGetValue("name", out var n) && n is byte[] nb
            ? Encoding.UTF8.GetString(nb)
            : "";

        // 多文件：info.files = [ {length, path:[...]}, ... ]
        if (info.TryGetValue("files", out var fv) && fv is List<object?> list)
        {
            var files = new List<TorrentEntry>(list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is not Dictionary<string, object?> fd) continue;
                var size = fd.TryGetValue("length", out var lv) && lv is long l ? l : 0L;
                var rel = fd.TryGetValue("path", out var pv) && pv is List<object?> parts
                    ? string.Join('/', parts.OfType<byte[]>().Select(Encoding.UTF8.GetString))
                    : "";
                files.Add(new TorrentEntry(i, size, rel));
            }
            return (files, name);
        }

        // 单文件：info.length
        var single = info.TryGetValue("length", out var lv2) && lv2 is long l2 ? l2 : 0L;
        return (new List<TorrentEntry> { new(0, single, name) }, name);
    }

    private static object? Decode(byte[] b) { var i = 0; return Decode(b, ref i); }

    private static object? Decode(byte[] b, ref int i)
    {
        if (i >= b.Length) throw new InvalidDataException("bencode 意外结束");
        switch ((char)b[i])
        {
            case 'i':
            {
                var j = IndexOf(b, (byte)'e', i);
                var n = long.Parse(Encoding.ASCII.GetString(b, i + 1, j - i - 1));
                i = j + 1;
                return n;
            }
            case 'l':
            {
                i++;
                var outList = new List<object?>();
                while (i < b.Length && b[i] != (byte)'e') outList.Add(Decode(b, ref i));
                i++;
                return outList;
            }
            case 'd':
            {
                i++;
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                while (i < b.Length && b[i] != (byte)'e')
                {
                    var key = Decode(b, ref i);
                    var val = Decode(b, ref i);
                    dict[Encoding.UTF8.GetString(key as byte[] ?? [])] = val;
                }
                i++;
                return dict;
            }
            default:
            {
                var j = IndexOf(b, (byte)':', i);
                var n = int.Parse(Encoding.ASCII.GetString(b, i, j - i));
                i = j + 1;
                var s = new byte[n];
                Buffer.BlockCopy(b, i, s, 0, n);
                i += n;
                return s;
            }
        }
    }

    private static int IndexOf(byte[] b, byte target, int from)
    {
        for (var k = from; k < b.Length; k++)
            if (b[k] == target) return k;
        throw new InvalidDataException($"bencode 缺少定界符 0x{target:X2}");
    }
}
