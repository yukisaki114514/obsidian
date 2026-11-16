using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FolderLabeler
{
    /// <summary>
    /// .unitypackage (gzip 圧縮の tar) から、各アセットのパス・サイズ・GUID だけを読み出す。
    /// 中身をプロジェクトへ展開することはしない。
    /// </summary>
    internal static class UnityPackageReader
    {
        internal sealed class PackageAsset
        {
            public string guid;
            public string path;     // "Assets/..." (パッケージに書かれたままのパス)
            public long size = -1;  // asset 本体が無い (= フォルダ) ときは -1
        }

        public static List<PackageAsset> Read(string file)
        {
            var byGuid = new Dictionary<string, PackageAsset>(StringComparer.OrdinalIgnoreCase);
            using (var fs = File.OpenRead(file))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            {
                var header = new byte[512];
                string longName = null;
                while (true)
                {
                    if (!ReadExactly(gz, header, 512)) break;
                    if (IsZeroBlock(header)) break;

                    string name = ReadString(header, 0, 100);
                    string prefix = ReadString(header, 345, 155);
                    if (!string.IsNullOrEmpty(prefix) && ReadString(header, 257, 5) == "ustar") name = prefix + "/" + name;
                    long size = ReadOctal(header, 124, 12);
                    char type = (char)header[156];

                    if (longName != null)
                    {
                        name = longName;
                        longName = null;
                    }

                    long padded = (size + 511) / 512 * 512;

                    if (type == 'L')
                    {
                        // GNU の長いファイル名
                        byte[] data = ReadBytes(gz, padded);
                        longName = Encoding.UTF8.GetString(data, 0, (int)Math.Min(size, data.Length)).TrimEnd('\0');
                        continue;
                    }

                    name = name.Replace('\\', '/');
                    if (name.StartsWith("./", StringComparison.Ordinal)) name = name.Substring(2);
                    int slash = name.IndexOf('/');
                    string guid = slash > 0 ? name.Substring(0, slash) : "";
                    string leaf = slash > 0 ? name.Substring(slash + 1) : name;

                    if ((type == '0' || type == '\0') && FolderWalker.IsGuid(guid))
                    {
                        PackageAsset a;
                        if (!byGuid.TryGetValue(guid, out a))
                        {
                            a = new PackageAsset { guid = guid.ToLowerInvariant() };
                            byGuid.Add(guid, a);
                        }
                        if (leaf == "pathname")
                        {
                            byte[] data = ReadBytes(gz, padded);
                            string text = Encoding.UTF8.GetString(data, 0, (int)Math.Min(size, data.Length));
                            int nl = text.IndexOf('\n');
                            a.path = (nl >= 0 ? text.Substring(0, nl) : text).Trim().TrimEnd('\0').Replace('\\', '/');
                            continue;
                        }
                        if (leaf == "asset") a.size = size;
                    }
                    Skip(gz, padded);
                }
            }
            var list = new List<PackageAsset>();
            foreach (var a in byGuid.Values)
            {
                if (!string.IsNullOrEmpty(a.path)) list.Add(a);
            }
            return list;
        }

        static bool ReadExactly(Stream s, byte[] buf, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        static byte[] ReadBytes(Stream s, long count)
        {
            if (count > 16 * 1024 * 1024) throw new InvalidDataException("unitypackage の項目が想定より大きいため読めません");
            var buf = new byte[count];
            if (!ReadExactly(s, buf, (int)count)) throw new EndOfStreamException("unitypackage が途中で終わっています");
            return buf;
        }

        static void Skip(Stream s, long count)
        {
            var buf = new byte[81920];
            while (count > 0)
            {
                int n = s.Read(buf, 0, (int)Math.Min(buf.Length, count));
                if (n <= 0) throw new EndOfStreamException("unitypackage が途中で終わっています");
                count -= n;
            }
        }

        static bool IsZeroBlock(byte[] b)
        {
            for (int i = 0; i < b.Length; i++) if (b[i] != 0) return false;
            return true;
        }

        static string ReadString(byte[] b, int offset, int length)
        {
            int end = offset;
            while (end < offset + length && b[end] != 0) end++;
            return Encoding.UTF8.GetString(b, offset, end - offset);
        }

        static long ReadOctal(byte[] b, int offset, int length)
        {
            // GNU tar の巨大サイズ (base-256) にも対応
            if ((b[offset] & 0x80) != 0)
            {
                long v = 0;
                for (int i = offset + 1; i < offset + length; i++) v = (v << 8) | b[i];
                return v;
            }
            long value = 0;
            for (int i = offset; i < offset + length; i++)
            {
                byte c = b[i];
                if (c == 0 || c == ' ') { if (value > 0) break; continue; }
                if (c < '0' || c > '7') break;
                value = value * 8 + (c - '0');
            }
            return value;
        }
    }
}
