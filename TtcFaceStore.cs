using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace FontScope;

// TTC 抽取面的进程级共享缓存：Skia 与 GDI+ 共用同一份重建字节，避免重复拷贝。
// AddMemoryFont 要求缓冲在集合生命周期内固定且存活，故 GDI+ 侧连同固定句柄一起缓存。
// 抽取涉及整文件级 I/O，用信号量限并发，防止大结果集时内存风暴。
internal static class TtcFaceStore
{
    sealed class Entry
    {
        public byte[] Bytes = Array.Empty<byte>();
        public GCHandle Pin;                    // 仅 GDI+ 路径需要
        public PrivateFontCollection? Pfc;
    }

    static readonly object _gate = new();
    static readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly SemaphoreSlim _io = new(2, 2);

    static string Key(string path, int idx) => path + "|" + idx;

    /// <summary>抽取并缓存独立 sfnt 字节；失败返回 null。</summary>
    public static byte[]? GetBytes(string path, int faceIndex)
    {
        var key = Key(path, faceIndex);
        lock (_gate)
            if (_cache.TryGetValue(key, out var e) && e.Bytes.Length > 0) return e.Bytes;

        byte[]? bytes;
        if (_io.Wait(0))
        {
            try { bytes = ExtractOrReuse(key, path, faceIndex); }
            finally { _io.Release(); }
        }
        else
            bytes = QueueExtract(key, path, faceIndex);
        return bytes;
    }

    // 并发槽占满时退化为串行等待（保证调用方拿到结果即可，不做异步）
    static byte[]? QueueExtract(string key, string path, int faceIndex)
    {
        _io.Wait();
        try { return ExtractOrReuse(key, path, faceIndex); }
        finally { _io.Release(); }
    }

    static byte[]? ExtractOrReuse(string key, string path, int faceIndex)
    {
        lock (_gate)
            if (_cache.TryGetValue(key, out var e0) && e0.Bytes.Length > 0) return e0.Bytes;
        var bytes = TtcFaceExtractor.Extract(path, faceIndex);
        if (bytes == null) return null;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var e1)) return e1.Bytes;
            _cache[key] = new Entry { Bytes = bytes };
            return bytes;
        }
    }

    /// <summary>取（必要时建）该 face 的 PrivateFontCollection；失败返回 null。</summary>
    public static PrivateFontCollection? GetPfc(string path, int faceIndex)
    {
        var bytes = GetBytes(path, faceIndex);
        if (bytes == null) return null;
        var key = Key(path, faceIndex);
        lock (_gate)
        {
            var e = _cache[key];
            if (e.Pfc != null) return e.Pfc;
            try
            {
                e.Pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                var pfc = new PrivateFontCollection();
                pfc.AddMemoryFont(e.Pin.AddrOfPinnedObject(), bytes.Length);
                e.Pfc = pfc;
                return pfc;
            }
            catch { return null; }
        }
    }
}
