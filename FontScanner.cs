using System.Collections.Concurrent;
using System.IO;

namespace FontScope;

// 枚举系统/用户/自定义字体，后台并行构建 cmap 索引。
// 按来源增量缓存解析结果：扫描范围的勾选切换只是重新拼装（毫秒级、零 IO），
// 只有 ScanAsync 才真正读盘——因此磁盘上文件的变化需手动重扫才能感知。
// FaceInfo 实例随缓存跨切换存活，其占坑探测等懒缓存得以延续。
public sealed class FontScanner
{
    public static readonly string SystemFontDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    public static readonly string UserFontDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Fonts");

    public List<FaceInfo> Faces { get; private set; } = new();
    public int FailedFiles { get; private set; }

    static readonly string[] FontExts = { ".ttf", ".otf", ".ttc", ".otc" };

    sealed class SourceResult { public List<FaceInfo> Faces = new(); public int Failed; }

    // (目录, 递归标记) -> 解析结果缓存（跨勾选切换存活；force 重扫时清除）。
    // 键必须含递归标记：同一目录递归与否得到的文件集合不同，混用会互相污染。
    readonly Dictionary<string, SourceResult> _cache = new(StringComparer.OrdinalIgnoreCase);

    // 缓存键：目录 + 递归标记（'|' 不可能是 Windows 路径字符，安全分隔）
    static string CacheKey(string dir, bool recursive) => recursive ? dir + "|R" : dir + "|T";

    /// <summary>
    /// 读盘解析尚未缓存的启用来源并拼装 Faces。
    /// force=true 时先丢弃启用来源的旧缓存全部重扫（「重新扫描」按钮语义）。
    /// sources 应按优先级传入：系统 → 用户 → 自定义。
    /// </summary>
    public Task ScanAsync(IEnumerable<(string Dir, FontSource Src, bool Recursive)> sources,
        bool force = false, IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var wanted = sources.Where(s => Directory.Exists(s.Dir)).ToList();
            if (force)
                foreach (var d in wanted.Select(s => CacheKey(s.Dir, s.Recursive)))
                    _cache.Remove(d);

            var pending = new List<(string Dir, FontSource Src, bool Recursive)>();
            var seenDir = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in wanted)
                if (!seenDir.Add(CacheKey(s.Dir, s.Recursive))) continue;
                else if (!_cache.ContainsKey(CacheKey(s.Dir, s.Recursive))) pending.Add(s);

            // 展平为文件清单（同一文件只计最先命中的来源）
            var files = new List<(string Path, FontSource Src, string Dir)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dir, src, recursive) in pending)
            {
                try
                {
                    var opt = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = recursive, // 勾选「含子目录」才下钻
                    };
                    foreach (var f in Directory.EnumerateFiles(dir, "*", opt))
                    {
                        if (!FontExts.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                        if (!seen.Add(f)) continue;
                        files.Add((f, src, CacheKey(dir, recursive)));
                    }
                }
                catch { }
            }

            var perDirFail = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var perDirBag = new ConcurrentDictionary<string, ConcurrentBag<FaceInfo>>(StringComparer.OrdinalIgnoreCase);
            int done = 0;
            Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = ct }, i =>
            {
                try
                {
                    foreach (var f in SfntParser.ParseFile(files[i].Path))
                    {
                        f.Source = files[i].Src;
                        perDirBag.GetOrAdd(files[i].Dir, _ => new()).Add(f);
                    }
                }
                catch
                {
                    perDirFail.AddOrUpdate(files[i].Dir, 1, (_, n) => n + 1);
                }

                var d = Interlocked.Increment(ref done);
                if (d % 25 == 0 || d == files.Count)
                    progress?.Report((d, files.Count));
            });

            foreach (var p in pending)
            {
                var key = CacheKey(p.Dir, p.Recursive);
                _cache[key] = new SourceResult
                {
                    Faces = perDirBag.TryGetValue(key, out var b) ? b.ToList() : new(),
                    Failed = perDirFail.TryGetValue(key, out var n) ? n : 0,
                };
            }

            Reassemble(wanted);
        }, ct);
    }

    /// <summary>仅按启用来源重新拼装 Faces 与失败计数（零 IO）。用于扫描范围勾选切换。</summary>
    public void Reassemble(IEnumerable<(string Dir, FontSource Src, bool Recursive)> sources)
    {
        // 去重键必须含 FaceIndex：同一 TTC 的所有 face 共享 FilePath，仅按路径去重会丢失其余面
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var faces = new List<FaceInfo>();
        int failed = 0;
        foreach (var (dir, _, recursive) in sources)
        {
            if (!_cache.TryGetValue(CacheKey(dir, recursive), out var c)) continue;
            failed += c.Failed;
            foreach (var f in c.Faces)
                if (seen.Add(f.FilePath + "#" + f.FaceIndex)) faces.Add(f);
        }
        Faces = faces;
        FailedFiles = failed;
    }
}
