using System.IO;

namespace FontScope;

// 从 TTC 中抽取单个 face 为独立 sfnt 字体文件的字节流。
// 背景：个别非典型共享表 TTC（如方正楷体拼音合集）按索引访问时
// Skia/GDI+ 均光栅化为空，仅 face0 可用；抽取绕开该缺陷。
// 表数据按绝对文件偏移整段拷贝（TTC 共享表天然支持），仅重建目录。
internal static class TtcFaceExtractor
{
    public static bool IsCollection(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> hdr = stackalloc byte[4];
            return fs.Read(hdr) == 4 && hdr[0] == 't' && hdr[1] == 't' && hdr[2] == 'c' && hdr[3] == 'f';
        }
        catch { return false; }
    }

    /// <summary>失败返回 null。</summary>
    public static byte[]? Extract(string path, int faceIndex)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hdr = new byte[12];
            if (fs.Read(hdr) < 12) return null;
            if (hdr[0] != 't' || hdr[1] != 't' || hdr[2] != 'c' || hdr[3] != 'f') return null;

            var buf4 = new byte[4];
            fs.Position = 8;
            if (fs.Read(buf4, 0, 4) < 4) return null;
            int nFaces = (buf4[0] << 24) | (buf4[1] << 16) | (buf4[2] << 8) | buf4[3];
            if (faceIndex < 0 || faceIndex >= nFaces) return null;

            fs.Position = 12 + 4L * faceIndex;
            if (fs.Read(buf4, 0, 4) < 4) return null;
            long baseOff = (buf4[0] << 24) | (buf4[1] << 16) | (buf4[2] << 8) | buf4[3];

            fs.Position = baseOff;
            var fh = new byte[12];
            if (fs.Read(fh) < 12) return null;
            uint sfntVer = ((uint)fh[0] << 24) | ((uint)fh[1] << 16) | ((uint)fh[2] << 8) | fh[3];
            int numTables = (fh[4] << 8) | fh[5];

            // 读原目录：tag(4) checksum(4) offset(4) length(4)
            var recs = new byte[16 * numTables];
            fs.Position = baseOff + 12;
            if (fs.Read(recs) < recs.Length) return null;

            // 新文件布局：头 12 + 目录 16n，随后各表按 4 字节对齐顺序排布
            int dirSize = 12 + 16 * numTables;
            int outLen = dirSize;
            var placements = new (long off, int len)[numTables];
            for (int i = 0; i < numTables; i++)
            {
                int len = (recs[16 * i + 12] << 24) | (recs[16 * i + 13] << 16)
                        | (recs[16 * i + 14] << 8) | recs[16 * i + 15];
                placements[i] = (outLen, len);
                outLen += (len + 3) & ~3;
            }

            var output = new byte[outLen];
            output[0] = (byte)(sfntVer >> 24); output[1] = (byte)(sfntVer >> 16);
            output[2] = (byte)(sfntVer >> 8); output[3] = (byte)sfntVer;
            output[4] = (byte)(numTables >> 8); output[5] = (byte)numTables;
            // searchRange/entrySelector/rangeShift 不关键，置 0 由读取方自行容错

            for (int i = 0; i < numTables; i++)
            {
                int r = 16 * i;
                Array.Copy(recs, r, output, 12 + 16 * i, 16); // tag/checksum 原样
                long off = placements[i].off; int len = placements[i].len;
                output[12 + 16 * i + 8] = (byte)(off >> 24); output[12 + 16 * i + 9] = (byte)(off >> 16);
                output[12 + 16 * i + 10] = (byte)(off >> 8); output[12 + 16 * i + 11] = (byte)off;
                // length 原样已在拷贝的 16 字节里

                fs.Position = (recs[r + 8] << 24) | (recs[r + 9] << 16) | (recs[r + 10] << 8) | recs[r + 11];
                int done = 0;
                while (done < len)
                {
                    int k = fs.Read(output, (int)off + done, len - done);
                    if (k <= 0) return null;
                    done += k;
                }
            }
            return output;
        }
        catch
        {
            return null;
        }
    }
}
