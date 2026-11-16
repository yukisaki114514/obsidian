using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FolderLabeler
{
    internal sealed class WalkedFile
    {
        public string rel;          // フォルダからの相対パス ("/" 区切り)
        public string full;         // 絶対パス (メモリ上だけ。JSON には書かない)
        public long size;
        public long writeTicks;
        public string category;
        public string guid = "";
    }

    internal sealed class WalkResult
    {
        public readonly List<WalkedFile> files = new List<WalkedFile>();
        public readonly List<string> errors = new List<string>();  // 読めなかったサブフォルダ等 (相対パス + 理由)
        public bool truncated;
    }

    /// <summary>
    /// System.IO だけでフォルダ配下を列挙する (プロジェクト内外で同じ形式にするため)。読み取りのみ。
    /// </summary>
    internal static class FolderWalker
    {
        public const int MaxFiles = 200000;

        public static WalkResult Walk(string root, bool readMetaGuids)
        {
            var result = new WalkResult();
            var stack = new Stack<string>();
            stack.Push("");
            while (stack.Count > 0)
            {
                string rel = stack.Pop();
                string dir = rel.Length == 0 ? root : Path.Combine(root, rel);
                string[] subdirs, files;
                try
                {
                    subdirs = Directory.GetDirectories(dir);
                    files = Directory.GetFiles(dir);
                }
                catch (Exception e)
                {
                    result.errors.Add((rel.Length == 0 ? "(フォルダ自体)" : rel) + ": " + Describe(e));
                    continue;
                }
                Array.Sort(files, StringComparer.Ordinal);
                Array.Sort(subdirs, StringComparer.Ordinal);

                HashSet<string> metas = null;
                if (readMetaGuids)
                {
                    metas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string f in files)
                    {
                        if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) metas.Add(f);
                    }
                }

                foreach (string f in files)
                {
                    string name = Path.GetFileName(f);
                    if (FileCategories.IsIgnoredName(name, false)) continue;
                    if (result.files.Count >= MaxFiles)
                    {
                        result.truncated = true;
                        return result;
                    }
                    var w = new WalkedFile
                    {
                        rel = rel.Length == 0 ? name : rel + "/" + name,
                        full = f,
                        category = FileCategories.Of(name),
                    };
                    try
                    {
                        var info = new FileInfo(f);
                        w.size = info.Length;
                        w.writeTicks = info.LastWriteTimeUtc.Ticks;
                    }
                    catch (Exception e)
                    {
                        result.errors.Add(w.rel + ": " + Describe(e));
                    }
                    if (metas != null && metas.Contains(f + ".meta")) w.guid = ReadMetaGuid(f + ".meta");
                    result.files.Add(w);
                }

                for (int i = subdirs.Length - 1; i >= 0; i--)
                {
                    string name = Path.GetFileName(subdirs[i]);
                    if (FileCategories.IsIgnoredName(name, true)) continue;
                    stack.Push(rel.Length == 0 ? name : rel + "/" + name);
                }
            }
            return result;
        }

        /// <summary>.meta から "guid: xxxx" を読む。読めなければ空。</summary>
        public static string ReadMetaGuid(string metaPath)
        {
            try
            {
                using (var reader = new StreamReader(metaPath))
                {
                    for (int i = 0; i < 8; i++)
                    {
                        string line = reader.ReadLine();
                        if (line == null) break;
                        line = line.Trim();
                        if (line.StartsWith("guid:", StringComparison.Ordinal))
                        {
                            string g = line.Substring(5).Trim();
                            return IsGuid(g) ? g : "";
                        }
                    }
                }
            }
            catch (Exception) { }
            return "";
        }

        public static bool IsGuid(string g)
        {
            if (g == null || g.Length != 32) return false;
            foreach (char c in g)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        /// <summary>差分走査用: 相対パス・サイズ・更新日時から作るハッシュ。</summary>
        public static string Signature(IList<WalkedFile> files)
        {
            var sb = new StringBuilder(files.Count * 48);
            foreach (var f in files)
            {
                sb.Append(f.rel).Append('|').Append(f.size).Append('|').Append(f.writeTicks).Append('\n');
            }
            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) hex.Append(b.ToString("x2"));
                return files.Count + ":" + hex;
            }
        }

        public static string Describe(Exception e)
        {
            if (e is UnauthorizedAccessException) return "アクセスが拒否されました";
            if (e is PathTooLongException) return "パスが長すぎます";
            if (e is DirectoryNotFoundException) return "フォルダが見つかりません";
            if (e is IOException) return "読み取りエラー (" + e.Message + ")";
            return e.GetType().Name + ": " + e.Message;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.#") + " MB";
            return (mb / 1024.0).ToString("0.##") + " GB";
        }
    }
}
