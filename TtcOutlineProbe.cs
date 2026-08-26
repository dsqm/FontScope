using System.IO;

namespace FontScope;

// 直接按字节读取 TTC 中指定 face 的 cmap + loca，判断某码点是否映射到非空字形轮廓。
// 用途：共享表缺陷集合的占坑探测——不经抽取、不经 Skia，O(小常数) 完成。
// 结构不支持或解析失败返回 null（不确定），调用方回退到 Skia 度量路径。
internal static class TtcOutlineProbe
{
    /// <summary>true=有墨迹；false=码点未映射或字形为空；null=无法判定。</summary>
    public static bool? HasOutline(string path, int faceIndex, uint codepoint)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var b4 = new byte[4];

            fs.Position = 8;
            if (fs.Read(b4, 0, 4) < 4) return null;
            int nFaces = (int)BE(b4);
            if (faceIndex < 0 || faceIndex >= nFaces) return null;

            fs.Position = 12 + 4L * faceIndex;
            if (fs.Read(b4, 0, 4) < 4) return null;
            long faceOff = BE(b4);

            fs.Position = faceOff + 4;
            var b2 = new byte[2];
            if (fs.Read(b2, 0, 2) < 2) return null;
            int numTables = (b2[0] << 8) | b2[1];

            long cmapOff = -1, locaOff = -1, headOff = -1;
            bool hasGlyf = false;
            ushort numGlyphs = 0;
            var subs = new List<(int pid, int eid, long off)>();

            var rec = new byte[16];
            for (int i = 0; i < numTables; i++)
            {
                fs.Position = faceOff + 12 + 16L * i;
                if (fs.Read(rec) < 16) return null;
                var tag = new string((char)rec[0], 1) + (char)rec[1] + (char)rec[2] + (char)rec[3];
                long off = BE(rec, 8), len = BE(rec, 12);
                switch (tag)
                {
                    case "cmap": cmapOff = off; break;
                    case "loca": locaOff = off; break;
                    case "head": headOff = off; break;
                    case "glyf": hasGlyf = true; break;
                    case "maxp":
                        fs.Position = off + 4;
                        if (fs.Read(b2, 0, 2) < 2) return null;
                        numGlyphs = (ushort)((b2[0] << 8) | b2[1]);
                        break;
                }
            }
            // 无 loca/glyf/head 的轮廓体系（CFF 等）交给上层回退处理
            if (cmapOff < 0 || locaOff < 0 || headOff < 0 || !hasGlyf || numGlyphs == 0) return null;

            fs.Position = headOff + 50;
            if (fs.Read(b2, 0, 2) < 2) return null;
            bool longLoca = ((b2[0] << 8) | b2[1]) == 1;

            fs.Position = cmapOff + 2;
            if (fs.Read(b2, 0, 2) < 2) return null;
            int nSub = (b2[0] << 8) | b2[1];
            for (int i = 0; i < nSub; i++)
            {
                fs.Position = cmapOff + 4 + 8L * i;
                var e = new byte[8];
                if (fs.Read(e) < 8) return null;
                subs.Add(((e[0] << 8) | e[1], (e[2] << 8) | e[3], cmapOff + BE(e, 4)));
            }

            // 任一子表映射到非空字形即有墨迹；全部子表都判空才判空
            bool? verdict = false;
            foreach (var (_, _, soff) in subs)
            {
                fs.Position = soff;
                if (fs.Read(b2, 0, 2) < 2) continue;
                int fmt = (b2[0] << 8) | b2[1];
                uint gid;
                switch (fmt)
                {
                    case 4: gid = LookupFmt4(fs, soff, codepoint); break;
                    case 12: gid = LookupFmt12(fs, soff, codepoint); break;
                    default: continue;
                }
                if (gid == 0xFFFFFFFF) { verdict = null; continue; } // 该子表查不到，不能下结论
                if (gid >= numGlyphs) return false;
                long s, e2;
                if (longLoca)
                {
                    fs.Position = locaOff + 4L * gid;
                    var q = new byte[8];
                    if (fs.Read(q) < 8) return null;
                    s = BE(q); e2 = BE(q, 4);
                }
                else
                {
                    fs.Position = locaOff + 2L * gid;
                    var w = new byte[4];
                    if (fs.Read(w) < 4) return null;
                    s = BE(w) * 2L; e2 = BE(w, 2) * 2L;
                }
                if (e2 > s) return true; // 非空轮廓（复合字形也按有墨处理）
            }
            return verdict;
        }
        catch { return null; }
    }

    // 返回 0xFFFFFFFF 表示该子表不含此码点
    static uint LookupFmt4(FileStream fs, long base_, uint cp)
    {
        if (cp > 0xFFFF) return 0xFFFFFFFF;
        Span<byte> b = stackalloc byte[2];
        fs.Position = base_ + 6;
        ReadExact(fs, b);
        int segX2 = (b[0] << 8) | b[1];
        long endO = base_ + 14, startO = endO + segX2 + 2, deltaO = startO + segX2, rangeO = deltaO + segX2;
        var w = new byte[2];
        for (int s = 0; s < segX2 / 2; s++)
        {
            fs.Position = endO + 2L * s; ReadExact(fs, w);
            uint end = (uint)((w[0] << 8) | w[1]);
            if (cp > end) continue;
            fs.Position = startO + 2L * s; ReadExact(fs, w);
            uint start = (uint)((w[0] << 8) | w[1]);
            if (cp < start) break;
            fs.Position = rangeO + 2L * s; ReadExact(fs, w);
            uint ro = (uint)((w[0] << 8) | w[1]);
            if (ro == 0)
            {
                fs.Position = deltaO + 2L * s; ReadExact(fs, w);
                return (cp + (uint)(short)((w[0] << 8) | w[1])) & 0xFFFF;
            }
            fs.Position = rangeO + 2L * s + ro + (cp - start) * 2; ReadExact(fs, w);
            uint g = (uint)((w[0] << 8) | w[1]);
            if (g == 0) return 0;
            fs.Position = deltaO + 2L * s; ReadExact(fs, w);
            return (g + (uint)(short)((w[0] << 8) | w[1])) & 0xFFFF;
        }
        return 0xFFFFFFFF;
    }

    static uint LookupFmt12(FileStream fs, long base_, uint cp)
    {
        Span<byte> b = stackalloc byte[4];
        fs.Position = base_ + 12;
        ReadExact(fs, b);
        uint nGroups = BE(b);
        long lo = 0, hi = nGroups - 1;
        var g = new byte[12];
        while (lo <= hi)
        {
            long mid = (lo + hi) / 2;
            fs.Position = base_ + 16 + 12L * mid;
            ReadExact(fs, g);
            uint startC = BE(g), endC = BE(g, 4);
            if (cp < startC) { hi = mid - 1; continue; }
            if (cp > endC) { lo = mid + 1; continue; }
            return BE(g, 8) + (cp - startC);
        }
        return 0xFFFFFFFF;
    }

    static uint BE(ReadOnlySpan<byte> b, int pos = 0) =>
        (uint)(b[pos] << 24 | b[pos + 1] << 16 | b[pos + 2] << 8 | b[pos + 3]);

    static void ReadExact(FileStream fs, Span<byte> buf)
    {
        int done = 0;
        while (done < buf.Length)
        {
            int k = fs.Read(buf[done..]);
            if (k <= 0) throw new EndOfStreamException();
            done += k;
        }
    }
}
