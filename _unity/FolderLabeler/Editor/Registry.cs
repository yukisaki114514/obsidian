using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace FolderLabeler
{
    /// <summary>
    /// 辞書 (レジストリ) の読み書き。ユーザー辞書はグローバルな JSON 1 ファイル。
    /// 同梱の初期辞書 (BuiltinRules) は読み取り専用で、ユーザー辞書より低い優先度で使う。
    /// </summary>
    internal sealed class RegistryStore
    {
        const string PathPrefKey = "Znakisaki.FolderLabeler.RegistryPath";

        public RegistryFile data = new RegistryFile();
        public string loadProblem;      // 読めなかった理由 (このときは上書き保存しない)
        public bool readOnly;
        public int revision;            // 変更のたびに増やす (照合のやり直し判定用)

        public static string DefaultPath
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(Path.Combine(Path.Combine(appData, "Znakisaki"), "FolderLabeler"), "registry.json");
            }
        }

        public static string CurrentPath
        {
            get
            {
                string p = EditorPrefs.GetString(PathPrefKey, "");
                return string.IsNullOrEmpty(p) ? DefaultPath : p;
            }
        }

        public static bool IsCustomPath { get { return !string.IsNullOrEmpty(EditorPrefs.GetString(PathPrefKey, "")); } }

        public static void SetPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == DefaultPath) EditorPrefs.DeleteKey(PathPrefKey);
            else EditorPrefs.SetString(PathPrefKey, path);
        }

        public RegistrySettings Settings { get { return data.settings ?? (data.settings = new RegistrySettings()); } }

        public void Load()
        {
            loadProblem = null;
            readOnly = false;
            revision++;
            string path = CurrentPath;
            data = new RegistryFile();
            if (!File.Exists(path)) return;
            try
            {
                string json = File.ReadAllText(path);
                var loaded = JsonUtility.FromJson<RegistryFile>(json);
                if (loaded == null || loaded.format != RegistryFile.FormatName)
                {
                    loadProblem = "辞書ファイルの形式が違うため読み込めませんでした (" + path + ")。上書きを防ぐため、辞書の保存を止めています。";
                    readOnly = true;
                    return;
                }
                if (loaded.formatVersion > RegistryFile.CurrentVersion)
                {
                    loadProblem = "辞書ファイルがこのツールより新しい版数 (" + loaded.formatVersion + ") です。上書きを防ぐため、読み取り専用で使います。";
                    readOnly = true;
                }
                Normalize(loaded);
                data = loaded;
            }
            catch (Exception e)
            {
                loadProblem = "辞書ファイルを読み込めませんでした: " + e.Message + "\n(" + path + ")\n上書きを防ぐため、辞書の保存を止めています。";
                readOnly = true;
            }
        }

        static void Normalize(RegistryFile f)
        {
            if (f.settings == null) f.settings = new RegistrySettings();
            if (f.entries == null) f.entries = new List<RegistryEntry>();
            f.entries.RemoveAll(e => e == null);
            foreach (var e in f.entries)
            {
                if (e.files == null) e.files = new List<FingerprintFile>();
                if (e.namePatterns == null) e.namePatterns = new List<string>();
                if (e.requireExtensions == null) e.requireExtensions = new List<string>();
                if (string.IsNullOrEmpty(e.id)) e.id = Guid.NewGuid().ToString("N");
                e.files.RemoveAll(x => x == null || !IsSafeRelative(x.p));
            }
        }

        /// <summary>保存。失敗したら理由を返す (成功なら null)。</summary>
        public string Save()
        {
            if (readOnly) return "辞書が読み取り専用の状態のため保存しませんでした。" + (loadProblem ?? "");
            revision++;
            try
            {
                data.updatedAt = DateTime.UtcNow.ToString("o");
                Normalize(data);
                JsonFile.WriteAtomic(CurrentPath, JsonUtility.ToJson(data, true));
                return null;
            }
            catch (Exception e)
            {
                return "辞書を保存できませんでした: " + e.Message;
            }
        }

        public List<RegistryEntry> UserEntries { get { return data.entries; } }

        public RegistryEntry Find(string id)
        {
            foreach (var e in data.entries) if (e.id == id) return e;
            foreach (var e in BuiltinRules.Entries) if (e.id == id) return e;
            return null;
        }

        public string Add(RegistryEntry entry)
        {
            data.entries.Add(entry);
            return Save();
        }

        public string Remove(string id)
        {
            data.entries.RemoveAll(e => e.id == id);
            return Save();
        }

        // ------------------------------------------------------------------
        // export / import
        // ------------------------------------------------------------------

        public string Export(string path)
        {
            try
            {
                var copy = new RegistryFile { settings = data.settings, entries = data.entries, updatedAt = DateTime.UtcNow.ToString("o") };
                JsonFile.WriteAtomic(path, JsonUtility.ToJson(copy, true));
                return null;
            }
            catch (Exception e)
            {
                return "書き出せませんでした: " + e.Message;
            }
        }

        /// <summary>同じ id は置き換え、新しい id は追加する。設定は読み込まない。</summary>
        public string Import(string path, out int added, out int replaced, out int skipped)
        {
            added = replaced = skipped = 0;
            if (readOnly) return "辞書が読み取り専用の状態のため読み込めません。";
            RegistryFile f;
            try
            {
                f = JsonUtility.FromJson<RegistryFile>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                return "読み込めませんでした: " + e.Message;
            }
            if (f == null || f.format != RegistryFile.FormatName) return "FolderLabeler の辞書ファイルではありません。";
            if (f.formatVersion > RegistryFile.CurrentVersion) return "このツールより新しい版数 (" + f.formatVersion + ") の辞書のため読み込めません。";
            Normalize(f);
            foreach (var e in f.entries)
            {
                if (string.IsNullOrEmpty(e.name) || (e.files.Count == 0 && e.namePatterns.Count == 0))
                {
                    skipped++;
                    continue;
                }
                int idx = data.entries.FindIndex(x => x.id == e.id);
                if (idx >= 0) { data.entries[idx] = e; replaced++; }
                else { data.entries.Add(e); added++; }
            }
            return Save();
        }

        /// <summary>辞書に絶対パス等を入れないための確認。</summary>
        public static bool IsSafeRelative(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            if (p.StartsWith("/", StringComparison.Ordinal) || p.StartsWith("\\", StringComparison.Ordinal)) return false;
            if (p.Contains(":")) return false;
            if (p.Contains("..")) return false;
            return true;
        }
    }

    /// <summary>辞書へ登録する指紋の作成。プロジェクト内・外・unitypackage で同じ形式にする。</summary>
    internal static class Fingerprints
    {
        public static RegistryEntry FromWalk(IList<WalkedFile> files, string name, string label, string source, string sourceName)
        {
            var e = NewEntry(name, label, source, sourceName);
            foreach (var f in files)
            {
                if (!RegistryStore.IsSafeRelative(f.rel)) continue;
                e.files.Add(new FingerprintFile { p = f.rel, s = f.size, g = f.guid ?? "" });
                e.totalBytes += f.size;
            }
            e.fileCount = e.files.Count;
            return e;
        }

        /// <summary>
        /// unitypackage の中身を、共通の親フォルダ単位で指紋にする。
        /// 共通の親が Assets そのもの (= 複数フォルダにまたがる) のときは、直下のフォルダごとに分ける。
        /// </summary>
        public static List<RegistryEntry> FromUnityPackage(string file, string name, string label, out string note)
        {
            note = null;
            var assets = UnityPackageReader.Read(file);
            var real = new List<UnityPackageReader.PackageAsset>();
            foreach (var a in assets)
            {
                if (a.size < 0) continue;   // フォルダ
                string leaf = a.path.Substring(a.path.LastIndexOf('/') + 1);
                if (FileCategories.IsIgnoredName(leaf, false)) continue;
                real.Add(a);
            }
            var result = new List<RegistryEntry>();
            if (real.Count == 0)
            {
                note = "unitypackage の中にアセットが見つかりませんでした。";
                return result;
            }

            string common = CommonDirectory(real);
            var groups = new Dictionary<string, List<UnityPackageReader.PackageAsset>>();
            if (common == "Assets" || common == "")
            {
                foreach (var a in real)
                {
                    string rest = common.Length == 0 ? a.path : a.path.Substring(common.Length + 1);
                    int slash = rest.IndexOf('/');
                    string top = slash > 0 ? rest.Substring(0, slash) : "";
                    string key = top.Length == 0 ? common : (common.Length == 0 ? top : common + "/" + top);
                    List<UnityPackageReader.PackageAsset> list;
                    if (!groups.TryGetValue(key, out list)) groups.Add(key, list = new List<UnityPackageReader.PackageAsset>());
                    list.Add(a);
                }
            }
            else
            {
                groups.Add(common, real);
            }

            var skippedRoot = 0;
            foreach (var kv in groups)
            {
                if (kv.Key == "Assets" || kv.Key == "")
                {
                    skippedRoot += kv.Value.Count;  // Assets 直下のばら撒きファイルは指紋にしない
                    continue;
                }
                string folderName = kv.Key.Substring(kv.Key.LastIndexOf('/') + 1);
                string entryName = groups.Count > 1 ? name + " (" + folderName + ")" : name;
                var e = NewEntry(entryName, label, "unitypackage", Path.GetFileName(file));
                e.note = "パッケージ内のフォルダ: " + folderName;
                foreach (var a in kv.Value)
                {
                    string rel = a.path.Substring(kv.Key.Length + 1);
                    if (!RegistryStore.IsSafeRelative(rel)) continue;
                    e.files.Add(new FingerprintFile { p = rel, s = a.size, g = a.guid });
                    e.totalBytes += a.size;
                }
                e.fileCount = e.files.Count;
                if (e.fileCount > 0) result.Add(e);
            }
            if (groups.Count > 1)
                note = "このパッケージは複数のフォルダにまたがっているため、フォルダごとに " + result.Count + " 件に分けて登録します。";
            if (skippedRoot > 0)
                note = (note == null ? "" : note + "\n") + "Assets 直下に置かれるファイル " + skippedRoot + " 件は指紋に含めません。";
            return result;
        }

        static string CommonDirectory(List<UnityPackageReader.PackageAsset> assets)
        {
            string common = null;
            foreach (var a in assets)
            {
                int last = a.path.LastIndexOf('/');
                string dir = last > 0 ? a.path.Substring(0, last) : "";
                if (common == null) { common = dir; continue; }
                while (common.Length > 0 && !(dir == common || dir.StartsWith(common + "/", StringComparison.Ordinal)))
                {
                    int cut = common.LastIndexOf('/');
                    common = cut > 0 ? common.Substring(0, cut) : "";
                }
                if (common.Length == 0) break;
            }
            return common ?? "";
        }

        static RegistryEntry NewEntry(string name, string label, string source, string sourceName)
        {
            return new RegistryEntry
            {
                id = Guid.NewGuid().ToString("N"),
                name = name,
                label = label,
                source = source,
                sourceName = sourceName,
                registeredAt = DateTime.UtcNow.ToString("o"),
            };
        }

        public static string SourceName(string source)
        {
            switch (source)
            {
                case "project": return "プロジェクト内";
                case "external_folder": return "外部フォルダ";
                case "unitypackage": return "unitypackage";
                case "builtin": return "同梱";
                default: return source;
            }
        }
    }

    /// <summary>走査したフォルダ 1 件分の、照合用の索引。</summary>
    internal sealed class MatchTarget
    {
        public int index;
        public string folderName;
        public int contentCount;
        public HashSet<string> extensions;
        public string[] ancestors;      // 祖先の GUID (入れ子の抑制用)
        public string guid;
    }

    /// <summary>辞書照合 (第 1 層)。</summary>
    internal static class RegistryMatcher
    {
        sealed class Hit { public int guid, path, nameSize; }

        /// <summary>
        /// targets[i] のファイル群と辞書を照合する。
        /// 完全一致: 辞書側の全ファイルがあり、フォルダ側にも余分が無い。
        /// 部分一致: 辞書側ファイルの threshold 以上が見つかる (ユーザーが自作ファイルを足した場合など)。
        /// </summary>
        public static List<RegistryCandidate>[] Match(IList<MatchTarget> targets, IList<IList<WalkedFile>> files,
            IList<RegistryEntry> entries, RegistrySettings settings)
        {
            var results = new List<RegistryCandidate>[targets.Count];
            for (int i = 0; i < results.Length; i++) results[i] = new List<RegistryCandidate>();

            var byGuid = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var byPath = new Dictionary<string, List<KeyValuePair<int, long>>>(StringComparer.OrdinalIgnoreCase);
            var byNameSize = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int t = 0; t < targets.Count; t++)
            {
                foreach (var f in files[t])
                {
                    if (!string.IsNullOrEmpty(f.guid)) AddTo(byGuid, f.guid, t);
                    List<KeyValuePair<int, long>> pl;
                    if (!byPath.TryGetValue(f.rel, out pl)) byPath.Add(f.rel, pl = new List<KeyValuePair<int, long>>());
                    pl.Add(new KeyValuePair<int, long>(t, f.size));
                    AddTo(byNameSize, NameSizeKey(f.rel, f.size), t);
                }
            }

            foreach (var entry in entries)
            {
                if (entry.files == null || entry.files.Count == 0) continue;
                var hits = new Dictionary<int, Hit>();
                int withGuid = 0;
                foreach (var ef in entry.files)
                {
                    if (!string.IsNullOrEmpty(ef.g))
                    {
                        withGuid++;
                        List<int> gl;
                        if (byGuid.TryGetValue(ef.g, out gl)) foreach (int t in Distinct(gl)) Get(hits, t).guid++;
                    }
                    List<KeyValuePair<int, long>> pl;
                    if (byPath.TryGetValue(ef.p, out pl))
                    {
                        bool lenient = FileCategories.IsReserializable(ef.p);
                        foreach (var kv in pl)
                        {
                            if (lenient || kv.Value == ef.s) Get(hits, kv.Key).path++;
                        }
                    }
                    List<int> nl;
                    if (byNameSize.TryGetValue(NameSizeKey(ef.p, ef.s), out nl)) foreach (int t in Distinct(nl)) Get(hits, t).nameSize++;
                }

                int n = entry.files.Count;
                var matched = new List<RegistryCandidate>();
                var matchedTargets = new List<int>();
                foreach (var kv in hits)
                {
                    var target = targets[kv.Key];
                    var h = kv.Value;
                    float guidCov = withGuid > 0 ? (float)h.guid / withGuid : 0f;
                    float pathCov = (float)h.path / n;
                    float nsCov = (float)h.nameSize / n;
                    string key = "guid";
                    float cov = guidCov;
                    int hitCount = h.guid;
                    if (pathCov > cov) { cov = pathCov; key = "path"; hitCount = h.path; }
                    if (nsCov > cov) { cov = nsCov; key = "name_size"; hitCount = h.nameSize; }
                    float folderCov = target.contentCount > 0 ? Math.Min(1f, (float)hitCount / target.contentCount) : 0f;
                    bool exact = cov >= 0.9999f && folderCov >= 0.9999f && key != "name_size";
                    bool partial = cov >= settings.partialThreshold && n >= settings.minFilesForPartial;
                    if (!exact && !partial) continue;
                    matched.Add(new RegistryCandidate
                    {
                        entryId = entry.id,
                        name = entry.name,
                        label = entry.label,
                        exact = exact,
                        entryCoverage = cov,
                        folderCoverage = folderCov,
                        key = key,
                    });
                    matchedTargets.Add(kv.Key);
                }

                // 入れ子の抑制: 子フォルダで当たっているなら、親 (作者名フォルダ等) には候補を出さない
                for (int i = 0; i < matched.Count; i++)
                {
                    var t = targets[matchedTargets[i]];
                    bool ancestorOfOther = false;
                    for (int j = 0; j < matched.Count && !ancestorOfOther; j++)
                    {
                        if (i == j) continue;
                        var other = targets[matchedTargets[j]];
                        if (other.ancestors != null && Array.IndexOf(other.ancestors, t.guid) >= 0
                            && matched[j].entryCoverage >= matched[i].entryCoverage - 0.0001f)
                            ancestorOfOther = true;
                    }
                    if (!ancestorOfOther) results[matchedTargets[i]].Add(matched[i]);
                }
            }

            foreach (var list in results)
            {
                list.Sort((a, b) =>
                {
                    if (a.exact != b.exact) return a.exact ? -1 : 1;
                    return b.entryCoverage.CompareTo(a.entryCoverage);
                });
            }
            return results;
        }

        /// <summary>同梱ルール (名前 + 構成)。当たればルールの id と、構成も合っているかを返す。</summary>
        public static RegistryEntry MatchBuiltin(string folderName, HashSet<string> extensions, out bool structureOk)
        {
            structureOk = false;
            foreach (var rule in BuiltinRules.Entries)
            {
                bool nameHit = false;
                foreach (string pattern in rule.namePatterns)
                {
                    if (Regex.IsMatch(folderName, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        nameHit = true;
                        break;
                    }
                }
                if (!nameHit) continue;
                if (rule.requireExtensions.Count == 0) structureOk = true;
                foreach (string ext in rule.requireExtensions)
                {
                    if (extensions.Contains(ext)) { structureOk = true; break; }
                }
                return rule;
            }
            return null;
        }

        static string NameSizeKey(string rel, long size)
        {
            int slash = rel.LastIndexOf('/');
            return (slash >= 0 ? rel.Substring(slash + 1) : rel) + "|" + size;
        }

        static void AddTo(Dictionary<string, List<int>> d, string key, int t)
        {
            List<int> l;
            if (!d.TryGetValue(key, out l)) d.Add(key, l = new List<int>());
            l.Add(t);
        }

        static IEnumerable<int> Distinct(List<int> l)
        {
            int prev = -1;
            foreach (int t in l)
            {
                if (t == prev) continue;    // 同じフォルダ内の重複 (同名同サイズの別ファイル) は 1 回だけ数える
                prev = t;
                yield return t;
            }
        }

        static Hit Get(Dictionary<int, Hit> d, int t)
        {
            Hit h;
            if (!d.TryGetValue(t, out h)) d.Add(t, h = new Hit());
            return h;
        }
    }

    internal static class JsonFile
    {
        /// <summary>一時ファイルに書いてから置き換える (書き込み途中で壊れた JSON を残さない)。</summary>
        public static void WriteAtomic(string path, string json)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
