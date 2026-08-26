using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace FontScope;

// 手写解析 sfnt 结构（ttf/otf/ttc）：name / cmap / OS/2 表。
// seek 式读取：只把需要的表读进内存，跳过占文件绝大部分体积的字形数据，扫描速度大幅提升。
internal static class SfntParser
{
    const int MaxTableRead = 8 * 1024 * 1024; // 防御伪造的表长度

    static ushort U16(byte[] b, int i) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i));
    static uint U32(byte[] b, int i) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i));

    static int ReadExact(FileStream fs, Span<byte> buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = fs.Read(buf[total..]);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    static byte[]? ReadTable(FileStream fs, long offset, int length)
    {
        if (offset < 0 || length <= 0 || length > MaxTableRead || offset + length > fs.Length) return null;
        var buf = new byte[length];
        fs.Position = offset;
        return ReadExact(fs, buf) == length ? buf : null;
    }

    public static List<FaceInfo> ParseFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Span<byte> hdr = stackalloc byte[12];
        if (ReadExact(fs, hdr) < 12) return new List<FaceInfo>();
        uint tag = BinaryPrimitives.ReadUInt32BigEndian(hdr);

        List<long> faceOffsets;
        bool isCollection = false;
        if (tag == 0x74746366) // 'ttcf'
        {
            isCollection = true;
            var cnt = new byte[4];
            fs.Position = 8;
            if (ReadExact(fs, cnt) < 4) throw new InvalidDataException("ttc 头不完整");
            int n = (int)BinaryPrimitives.ReadUInt32BigEndian(cnt);
            if (n < 0 || n > 1024) throw new InvalidDataException("ttc face 数异常");
            var offs = new byte[4 * n];
            fs.Position = 12;
            if (ReadExact(fs, offs) < offs.Length) throw new InvalidDataException("ttc 目录不完整");
            faceOffsets = new List<long>(n);
            for (int i = 0; i < n; i++)
                faceOffsets.Add(BinaryPrimitives.ReadUInt32BigEndian(offs.AsSpan(4 * i)));
        }
        else if (tag == 0x00010000 || tag == 0x4F54544F /* OTTO */ || tag == 0x74727565 /* true */)
        {
            faceOffsets = new List<long> { 0 };
        }
        else throw new InvalidDataException("不是可识别的字体文件");

        var faces = new List<FaceInfo>(faceOffsets.Count);
        for (int fi = 0; fi < faceOffsets.Count; fi++)
        {
            var face = ParseFace(fs, faceOffsets[fi]);
            if (face == null) continue;
            face.FilePath = path;
            face.FaceIndex = fi;
            face.IsCollection = isCollection;
            faces.Add(face);
        }
        return faces;
    }

    static FaceInfo? ParseFace(FileStream fs, long faceOffset)
    {
        // 表目录：numTables 在 +4，表记录 16 字节/条，起始于 +12
        var nt = new byte[2];
        fs.Position = faceOffset + 4;
        if (ReadExact(fs, nt) < 2) return null;
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(nt);

        var records = new byte[16 * numTables];
        fs.Position = faceOffset + 12;
        if (ReadExact(fs, records) < records.Length) return null;

        long nameOff = -1, cmapOff = -1, os2Off = -1, headOff = -1, hheaOff = -1;
        int nameLen = 0, cmapLen = 0, os2Len = 0, headLen = 0, hheaLen = 0;
        bool hasColor = false;
        var face = new FaceInfo();
        for (int i = 0; i < numTables; i++)
        {
            uint t = U32(records, 16 * i);
            int to = (int)U32(records, 16 * i + 8);
            int tl = (int)U32(records, 16 * i + 12);
            if (t == 0x6E616D65) { nameOff = to; nameLen = tl; }        // 'name'
            else if (t == 0x636D6170) { cmapOff = to; cmapLen = tl; }   // 'cmap'
            else if (t == 0x4F532F32) { os2Off = to; os2Len = tl; }     // 'OS/2'
            else if (t == 0x68656164) { headOff = to; headLen = tl; }   // 'head'
            else if (t == 0x68686561) { hheaOff = to; hheaLen = tl; }   // 'hhea'
            else if (t == 0x676C7966) face.Outline = "TrueType";        // 'glyf'
            else if (t == 0x43464620 || t == 0x43464632)
                face.Outline = t == 0x43464620 ? "CFF" : "CFF2";        // 'CFF '/'CFF2'
            else if (t == 0x434F4C52 || t == 0x43424454 || t == 0x73626978 || t == 0x53564720)
                hasColor = true;                                       // 'COLR'/'CBDT'/'sbix'/'SVG ' 彩色字形表
        }
        face.IsColorFont = hasColor;

        if (nameOff >= 0)
        {
            var tb = ReadTable(fs, nameOff, nameLen);
            if (tb != null) ParseName(tb, face);
        }
        if (cmapOff >= 0)
        {
            var tb = ReadTable(fs, cmapOff, cmapLen);
            if (tb != null) ParseCmap(tb, face);
        }
        bool os2Read = false;
        if (os2Off >= 0 && os2Len >= 64)
        {
            var tb = ReadTable(fs, os2Off, Math.Min(os2Len, 96));
            if (tb != null && tb.Length >= 64)
            {
                os2Read = true;
                face.WeightClass = U16(tb, 4);              // usWeightClass
                if (tb.Length >= 10)
                {
                    face.WidthClass = U16(tb, 6);           // usWidthClass
                    face.FsType = U16(tb, 8);               // fsType 嵌入许可位
                }
                face.IsItalic = (U16(tb, 62) & 1) != 0;     // fsSelection bit0
                if (tb.Length >= 72)
                {
                    face.TypoAscender = (short)U16(tb, 68); // sTypoAscender
                    face.TypoDescender = (short)U16(tb, 70);
                }
            }
        }

        // OS/2 缺失时退回 head.macStyle 判斜体
        if (!os2Read && headOff >= 0 && headLen >= 46)
        {
            var tb = ReadTable(fs, headOff, Math.Min(headLen, 46));
            if (tb != null)
                face.IsItalic = (U16(tb, 44) & 2) != 0;     // macStyle bit1
        }

        if (headOff >= 0 && headLen >= 20)
        {
            var tb = ReadTable(fs, headOff, Math.Min(headLen, 54));
            if (tb != null && tb.Length >= 20)
                face.UnitsPerEm = U16(tb, 18);              // unitsPerEm
        }

        // OS/2 typo 度量缺失时用 hhea 兜底
        if (face.TypoAscender == 0 && face.TypoDescender == 0 && hheaOff >= 0 && hheaLen >= 8)
        {
            var tb = ReadTable(fs, hheaOff, Math.Min(hheaLen, 36));
            if (tb != null)
            {
                face.TypoAscender = (short)U16(tb, 4);      // ascender
                face.TypoDescender = (short)U16(tb, 6);     // descender
            }
        }

        // 兜底：无名或无 cmap 的 face 无价值
        if (face.CodePoints.Count == 0 && string.IsNullOrEmpty(face.FamilyEn)) return null;
        return face;
    }

    // 以下各表均以独立数组传入，偏移从 0 起

    static void ParseName(byte[] b, FaceInfo face)
    {
        if (b.Length < 6) return;
        int count = U16(b, 2);
        int strOff = U16(b, 4);

        string? n1en = null, n1zh = null, n2en = null, n4en = null, n4zh = null, n16en = null, n16zh = null, n17en = null;

        // 标准项（nameID 0–17，15 保留）全部收集进 NameTable
        void Put(ushort id, bool en, bool zh, string s)
        {
            (string En, string Zh) t = face.NameTable.TryGetValue(id, out var v) ? v : ("", "");
            if (en && t.En.Length == 0) t.En = s;
            if (zh && t.Zh.Length == 0) t.Zh = s;
            face.NameTable[id] = t;
        }

        // 第一遍：只取 Unicode 编码的记录（platform 0 任意，platform 3 的 1/10）
        // 第二遍：platform 1（Macintosh）ASCII 记录仅补空位——
        //         个别字体（如某些 TTC 合集）只有 Mac 名记录，没有 Unicode 记录
        var macRecs = new List<(int rec, int len, int so)>();
        for (int i = 0; i < count; i++)
        {
            int rec = 6 + 12 * i;
            if (rec + 12 > b.Length) break;
            ushort plat = U16(b, rec), enc = U16(b, rec + 2), lang = U16(b, rec + 4), nameId = U16(b, rec + 6);
            int len = U16(b, rec + 8), so = U16(b, rec + 10);
            if (strOff + so + len > b.Length || len == 0) continue;

            bool unicode = plat == 0 || (plat == 3 && (enc == 1 || enc == 10));
            if (unicode)
                HandleRecord(plat, lang, nameId, () => Encoding.BigEndianUnicode.GetString(b, strOff + so, len));
            else if (plat == 1 && enc == 0)
                macRecs.Add((rec, len, so));
        }
        foreach (var (rec, len, so) in macRecs)
        {
            ushort nameId = U16(b, rec + 6);
            HandleRecord(1, 0, nameId, () => Encoding.Latin1.GetString(b, strOff + so, len));
        }

        void HandleRecord(uint plat, uint lang, ushort nameId, Func<string> read)
        {
            var s = read();
            bool en = plat == 0 ? lang == 0 : plat == 3 ? lang == 0x0409 : true;
            bool zh = lang == 0x0804 || lang == 0x0404;
            if (nameId <= 17 && nameId != 15) Put(nameId, en, zh, s);
            switch (nameId)
            {
                case 1: if (en && n1en == null) n1en = s; if (zh && n1zh == null) n1zh = s; break;
                case 2: if (en && n2en == null) n2en = s; break;
                case 4: if (en && n4en == null) n4en = s; if (zh && n4zh == null) n4zh = s; break;
                case 16: if (en && n16en == null) n16en = s; if (zh && n16zh == null) n16zh = s; break;
                case 17: if (en && n17en == null) n17en = s; break;
            }
        }

        face.FamilyEn = n16en ?? n1en ?? "";
        face.FamilyEnLegacy = n1en ?? n16en ?? "";
        face.FamilyZh = n16zh ?? n1zh ?? "";
        face.SubFamily = n17en ?? n2en ?? "Regular";
        face.FullNameEn = n4en ?? string.Join(" ", new[] { face.FamilyEn, face.SubFamily }.Where(x => x.Length > 0));
        face.FullNameZh = n4zh ?? "";
    }

    static void ParseCmap(byte[] b, FaceInfo face)
    {
        if (b.Length < 4) return;
        int numTables = U16(b, 2);

        // subtable 优先级：UCS4 > UCS2(BMP) > 其他 Unicode
        int best = -1, bestRank = -1;
        for (int i = 0; i < numTables; i++)
        {
            int rec = 4 + 8 * i;
            if (rec + 8 > b.Length) break;
            ushort plat = U16(b, rec), enc = U16(b, rec + 2);
            int sub = (int)U32(b, rec + 4);
            int rank = (plat, enc) switch
            {
                (3, 10) => 5, (0, 6) => 4, (0, 4) => 4,
                (3, 1) => 3, (0, 3) => 2, (0, 2) => 2, (0, 1) => 2, (0, 0) => 2,
                _ => -1
            };
            if (rank > bestRank) { bestRank = rank; best = sub; }
        }
        if (bestRank < 0 || best < 0 || best + 2 > b.Length) return;

        switch (U16(b, best))
        {
            case 0: ParseFormat0(b, best, face); break;
            case 4: ParseFormat4(b, best, face); break;
            case 6: ParseFormat6(b, best, face); break;
            case 12: ParseFormat12(b, best, face); break;
        }
    }

    static void ParseFormat0(byte[] b, int off, FaceInfo face)
    {
        if (off + 262 > b.Length) return;
        for (int c = 0; c < 256; c++)
            if (b[off + 6 + c] != 0) face.CodePoints.Add((uint)c);
    }

    static void ParseFormat4(byte[] b, int off, FaceInfo face)
    {
        if (off + 14 > b.Length) return;
        int segCount = U16(b, off + 6) / 2;
        int endBase = off + 14;
        int startBase = endBase + segCount * 2 + 2;
        int deltaBase = startBase + segCount * 2;
        int rangeBase = deltaBase + segCount * 2;
        if (rangeBase + segCount * 2 > b.Length) return;

        for (int s = 0; s < segCount; s++)
        {
            ushort end = U16(b, endBase + 2 * s);
            ushort start = U16(b, startBase + 2 * s);
            if (start == 0xFFFF) break; // 收尾段
            short delta = (short)U16(b, deltaBase + 2 * s);
            ushort rangeOff = U16(b, rangeBase + 2 * s);

            for (uint c = start; c <= end && c < 0xFFFF; c++)
            {
                ushort glyph;
                if (rangeOff == 0)
                {
                    glyph = (ushort)(c + delta);
                }
                else
                {
                    int addr = rangeBase + 2 * s + rangeOff + 2 * (int)(c - start);
                    if (addr + 2 > b.Length) break;
                    glyph = U16(b, addr);
                    if (glyph != 0) glyph = (ushort)(glyph + delta);
                }
                if (glyph != 0) face.CodePoints.Add(c);
            }
        }
    }

    static void ParseFormat6(byte[] b, int off, FaceInfo face)
    {
        if (off + 10 > b.Length) return;
        ushort first = U16(b, off + 6), count = U16(b, off + 8);
        if (off + 10 + 2 * count > b.Length) return;
        for (int i = 0; i < count; i++)
            if (U16(b, off + 10 + 2 * i) != 0) face.CodePoints.Add((uint)(first + i));
    }

    static void ParseFormat12(byte[] b, int off, FaceInfo face)
    {
        if (off + 16 > b.Length) return;
        uint nGroups = U32(b, off + 12);
        if (off + 16 + 12L * nGroups > b.Length)
            nGroups = (uint)((b.Length - off - 16) / 12);
        for (uint g = 0; g < nGroups; g++)
        {
            int rec = off + 16 + (int)(12 * g);
            uint start = U32(b, rec), end = U32(b, rec + 4), gid = U32(b, rec + 8);
            for (uint c = start; c <= end && c <= 0x10FFFF; c++)
            {
                uint glyph = gid + (c - start);
                if (glyph != 0) face.CodePoints.Add(c);
            }
            if (end == 0x10FFFF) break;
        }
    }

}
