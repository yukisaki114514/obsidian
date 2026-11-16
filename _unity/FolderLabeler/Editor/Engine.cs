using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FolderLabeler
{
    /// <summary>
    /// 走査・判定・索引の保存をまとめる。画面を持たないので、レポート出力だけでも動作確認できる。
    /// 書き込むのは FolderLabeler/index.json (プロジェクト直下、Assets の外) と辞書 JSON だけ。
    /// </summary>
    internal sealed class Engine
    {
        public const string ToolVersion = "1.0.0";
        public const string LooseGuid = "loose:Assets";

        public IndexFile index = new IndexFile();
        public readonly RegistryStore registry = new RegistryStore();
        public string indexProblem;
        public bool indexReadOnly;
        public string lastScanMessage;
        public readonly List<string> lastScanWarnings = new List<string>();

        // 直近の走査で列挙したファイル (メモリ上だけ。辞書登録に使う)
        readonly Dictionary<string, List<WalkedFile>> walked = new Dictionary<string, List<WalkedFile>>();

        public static string ProjectRoot { get { return Path.GetDirectoryName(Application.dataPath); } }
        public static string DataDir { get { return Path.Combine(ProjectRoot, "FolderLabeler"); } }
        public static string IndexPath { get { return Path.Combine(DataDir, "index.json"); } }
        public static string ReportPath { get { return Path.Combine(DataDir, "report.txt"); } }

        public void Load()
        {
            registry.Load();
            LoadIndex();
        }

        void LoadIndex()
        {
            indexProblem = null;
            indexReadOnly = false;
            index = new IndexFile();
            if (!File.Exists(IndexPath)) return;
            try
            {
                var loaded = JsonUtility.FromJson<IndexFile>(File.ReadAllText(IndexPath));
                if (loaded == null || loaded.format != IndexFile.FormatName)
                {
                    indexProblem = "索引ファイル (FolderLabeler/index.json) の形式が違うため読み込めませんでした。手動ラベルを守るため、上書き保存を止めています。"
                        + "ファイルを確認するか、不要なら削除してから走査してください。";
                    indexReadOnly = true;
                    return;
                }
                if (loaded.formatVersion > IndexFile.CurrentVersion)
                {
                    indexProblem = "索引ファイルがこのツールより新しい版数 (" + loaded.formatVersion + ") です。上書きを防ぐため保存を止めています。";
                    indexReadOnly = true;
                }
                if (loaded.folders == null) loaded.folders = new List<FolderRecord>();
                loaded.folders.RemoveAll(f => f == null || string.IsNullOrEmpty(f.guid));
                foreach (var f in loaded.folders) Repair(f);
                index = loaded;
                RefreshPaths();
            }
            catch (Exception e)
            {
                indexProblem = "索引ファイルを読み込めませんでした: " + e.Message + "\n手動ラベルを守るため、上書き保存を止めています。";
                indexReadOnly = true;
            }
        }

        static void Repair(FolderRecord f)
        {
            if (f.stats == null) f.stats = new FolderStats();
            if (f.stats.categories == null) f.stats.categories = new List<CategoryCount>();
            if (f.inference == null) f.inference = new LabelResult();
            if (f.auto == null) f.auto = new LabelResult();
            if (f.registry == null) f.registry = new RegistryResult();
            if (f.registry.candidates == null) f.registry.candidates = new List<RegistryCandidate>();
            if (f.manual == null) f.manual = new ManualLabel();
            foreach (var r in new[] { f.inference, f.auto })
            {
                if (r.signals == null) r.signals = new List<Signal>();
                if (r.notes == null) r.notes = new List<string>();
                if (r.alternatives == null) r.alternatives = new List<LabelScore>();
            }
        }

        /// <summary>GUID を正としてパスを引き直す (移動・改名に追随)。</summary>
        public void RefreshPaths()
        {
            foreach (var f in index.folders)
            {
                if (f.guid == LooseGuid) continue;
                string path = AssetDatabase.GUIDToAssetPath(f.guid);
                if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
                {
                    f.path = path;
                    f.name = Path.GetFileName(path);
                    f.missing = false;
                }
                else
                {
                    f.missing = true;
                }
            }
        }

        public string SaveIndex()
        {
            if (indexReadOnly) return "索引が読み取り専用の状態のため保存しませんでした。";
            try
            {
                index.format = IndexFile.FormatName;
                index.formatVersion = IndexFile.CurrentVersion;
                index.toolVersion = ToolVersion;
                index.projectName = Path.GetFileName(ProjectRoot);
                index.unityVersion = Application.unityVersion;
                JsonFile.WriteAtomic(IndexPath, JsonUtility.ToJson(index, true));
                return null;
            }
            catch (Exception e)
            {
                return "索引を保存できませんでした: " + e.Message;
            }
        }

        public FolderRecord Find(string guid)
        {
            foreach (var f in index.folders) if (f.guid == guid) return f;
            return null;
        }

        public List<WalkedFile> WalkedFiles(string guid)
        {
            List<WalkedFile> list;
            return walked.TryGetValue(guid, out list) ? list : null;
        }

        // ------------------------------------------------------------------
        // 走査
        // ------------------------------------------------------------------

        sealed class Unit
        {
            public string guid, path, name, kind, parentGuid, groupReason;
            public int depth;
            public FolderRecord record;
            public List<WalkedFile> files;
            public string walkError;
            public List<string> walkWarnings;
        }

        /// <summary>
        /// 走査する。diff = true なら、ファイル構成が前回から変わっていないフォルダは中身の推定をやり直さない。
        /// 中断されたら false (索引は変更しない)。
        /// </summary>
        public bool Scan(bool diff, bool interactive)
        {
            var sw = Stopwatch.StartNew();
            lastScanWarnings.Clear();
            VrcSdk.Reset();
            bool sdk = VrcSdk.Present;
            if (diff && index.sdkPresent != sdk) diff = false;  // SDK の有無が変わったら全部やり直す

            var old = new Dictionary<string, FolderRecord>();
            foreach (var f in index.folders) old[f.guid] = f;

            List<Unit> units;
            try
            {
                units = PlanUnits(old);
            }
            catch (Exception e)
            {
                lastScanMessage = "Assets フォルダを列挙できませんでした: " + e.Message;
                Debug.LogException(e);
                return false;
            }

            var settings = registry.Settings;
            var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            int reused = 0, judged = 0, errors = 0;
            walked.Clear();
            try
            {
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (interactive && EditorUtility.DisplayCancelableProgressBar("Folder Labeler",
                            "走査中 (" + (i + 1) + " / " + units.Count + "): " + u.path, (float)i / Math.Max(1, units.Count)))
                    {
                        lastScanMessage = "走査を中断しました。索引は前回のままです。";
                        return false;
                    }
                    FolderRecord prev;
                    old.TryGetValue(u.guid, out prev);
                    u.record = BuildRecord(u, prev);
                    if (u.kind == Kinds.Loose || u.record.error.Length > 0 || u.kind == Kinds.Group) continue;

                    if (diff && prev != null && prev.inferenceSignature == u.record.signature && prev.inference != null
                        && !string.IsNullOrEmpty(prev.inference.label))
                    {
                        u.record.inference = prev.inference;
                        u.record.stats.avatarPrefab = prev.stats != null ? prev.stats.avatarPrefab : "";
                        reused++;
                        continue;
                    }
                    try
                    {
                        var output = Inference.Run(new Inference.Context
                        {
                            assetPath = u.path,
                            folderName = u.name,
                            files = u.files,
                            settings = settings,
                            sdkPresent = sdk,
                            prefabDescriptorCache = cache,
                        });
                        u.record.inference = Inference.Decide(output);
                        u.record.stats.avatarPrefab = output.avatarPrefab;
                        u.record.inferenceSignature = u.record.signature;
                        judged++;
                    }
                    catch (Exception e)
                    {
                        u.record.error = "判定中に例外が発生しました: " + e.GetType().Name + ": " + e.Message;
                        Debug.LogException(e);
                    }
                }
            }
            finally
            {
                if (interactive) EditorUtility.ClearProgressBar();
                // 判定のために読み込んだアセットをメモリから外す (アセット自体は変更しない)
                EditorUtility.UnloadUnusedAssetsImmediate();
            }

            // 第 1 層: 辞書照合 (ユーザー辞書の指紋 → 同梱ルール)
            ApplyRegistry(units);

            // グループ (作者名フォルダ等) は子の結果を集約
            foreach (var u in units)
            {
                if (u.kind == Kinds.Group) AggregateGroup(u, units);
                if (u.record.error.Length > 0) errors++;
            }

            var newList = new List<FolderRecord>();
            var seen = new HashSet<string>();
            foreach (var u in units)
            {
                newList.Add(u.record);
                seen.Add(u.guid);
                walked[u.guid] = u.files ?? new List<WalkedFile>();
            }
            // 走査単位から外れたが手動ラベルのあるフォルダ (削除された等) は残して知らせる
            foreach (var f in index.folders)
            {
                if (seen.Contains(f.guid) || !f.IsLocked) continue;
                f.missing = true;
                newList.Add(f);
            }

            index.folders = newList;
            index.scannedAt = DateTime.UtcNow.ToString("o");
            index.scanMode = diff ? "diff" : "full";
            index.sdkPresent = sdk;
            sw.Stop();
            index.scanSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);

            string saveProblem = SaveIndex();
            var msg = new StringBuilder();
            msg.Append(diff ? "差分走査" : "全走査").Append("が終わりました: ")
                .Append(units.Count).Append(" フォルダ / ").Append(index.scanSeconds.ToString("0.0")).Append(" 秒");
            if (diff) msg.Append(" (推定し直し ").Append(judged).Append(" 件・前回の結果を使用 ").Append(reused).Append(" 件)");
            if (errors > 0) msg.Append("\n読み取れなかったフォルダが ").Append(errors).Append(" 件あります (一覧に理由つきで表示しています)。");
            if (saveProblem != null) msg.Append("\n").Append(saveProblem);
            lastScanMessage = msg.ToString();
            return true;
        }

        List<Unit> PlanUnits(Dictionary<string, FolderRecord> old)
        {
            var units = new List<Unit>();
            string assets = Application.dataPath;
            var dirs = Directory.GetDirectories(assets);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string d in dirs)
            {
                string name = Path.GetFileName(d);
                if (FileCategories.IsIgnoredName(name, true)) continue;
                string path = "Assets/" + name;
                var top = NewUnit(path, name, 1, "");
                if (top == null) continue;
                string reason;
                if (Inference.LooksLikeGroup(d, name, out reason))
                {
                    top.kind = Kinds.Group;
                    top.groupReason = reason;
                    units.Add(top);
                    string[] children;
                    try { children = Directory.GetDirectories(d); }
                    catch (Exception e)
                    {
                        top.walkError = "中のフォルダを列挙できませんでした: " + FolderWalker.Describe(e);
                        continue;
                    }
                    Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                    foreach (string c in children)
                    {
                        string cn = Path.GetFileName(c);
                        if (FileCategories.IsIgnoredName(cn, true)) continue;
                        var child = NewUnit(path + "/" + cn, cn, 2, top.guid);
                        if (child != null) units.Add(child);
                    }
                }
                else
                {
                    units.Add(top);
                }
            }

            // 走査単位より深いフォルダに付けた手動ラベル (グループ判定が変わった場合など) も追跡を続ける
            var planned = new HashSet<string>();
            foreach (var u in units) planned.Add(u.guid);
            foreach (var kv in old)
            {
                if (planned.Contains(kv.Key) || !kv.Value.IsLocked || kv.Key == LooseGuid) continue;
                string p = AssetDatabase.GUIDToAssetPath(kv.Key);
                if (string.IsNullOrEmpty(p) || !AssetDatabase.IsValidFolder(p) || p == "Assets") continue;
                var extra = NewUnit(p, Path.GetFileName(p), p.Split('/').Length - 1, "");
                if (extra == null) continue;
                extra.kind = Kinds.Manual;
                units.Add(extra);
            }

            // Assets 直下のファイル
            var loose = new Unit { guid = LooseGuid, path = "Assets", name = "(Assets 直下のファイル)", kind = Kinds.Loose, depth = 1 };
            loose.files = new List<WalkedFile>();
            foreach (string f in Directory.GetFiles(assets))
            {
                string n = Path.GetFileName(f);
                if (FileCategories.IsIgnoredName(n, false)) continue;
                var info = new FileInfo(f);
                loose.files.Add(new WalkedFile { rel = n, full = f, size = info.Length, writeTicks = info.LastWriteTimeUtc.Ticks, category = FileCategories.Of(n) });
            }
            if (loose.files.Count > 0) units.Add(loose);
            return units;
        }

        Unit NewUnit(string path, string name, int depth, string parent)
        {
            string guid = AssetDatabase.AssetPathToGUID(path);
            var u = new Unit { path = path, name = name, depth = depth, parentGuid = parent, kind = Kinds.Unit };
            if (string.IsNullOrEmpty(guid))
            {
                // .meta が無い (Unity がまだ取り込んでいない) フォルダ。黙って飛ばさず理由つきで残す
                u.guid = "path:" + path;
                u.walkError = "Unity に取り込まれていないフォルダです (.meta がありません)。Unity にフォーカスを戻して取り込みが終わってから走査してください。";
                return u;
            }
            u.guid = guid;
            return u;
        }

        FolderRecord BuildRecord(Unit u, FolderRecord prev)
        {
            var r = new FolderRecord
            {
                guid = u.guid,
                path = u.path,
                name = u.name,
                kind = u.kind,
                parentGuid = u.parentGuid ?? "",
                depth = u.depth,
                manual = prev != null && prev.manual != null ? prev.manual : new ManualLabel(),
            };
            if (prev != null) r.inferenceSignature = prev.inferenceSignature ?? "";

            if (u.walkError != null && u.kind != Kinds.Group)
            {
                r.error = u.walkError;
                u.files = new List<WalkedFile>();
                return r;
            }
            if (u.kind != Kinds.Loose)
            {
                WalkResult w;
                try
                {
                    w = FolderWalker.Walk(Path.Combine(ProjectRoot, u.path), false);
                }
                catch (Exception e)
                {
                    r.error = "フォルダを読み取れませんでした: " + FolderWalker.Describe(e);
                    u.files = new List<WalkedFile>();
                    return r;
                }
                u.files = w.files;
                u.walkWarnings = w.errors;
                if (w.truncated) r.auto.notes.Add("ファイル数が多すぎるため、先頭 " + FolderWalker.MaxFiles + " 件だけを数えています");
                foreach (var f in u.files)
                {
                    f.guid = AssetDatabase.AssetPathToGUID(u.path + "/" + f.rel) ?? "";
                }
                if (w.errors.Count > 0 && w.files.Count == 0)
                {
                    r.error = "フォルダの中を読み取れませんでした: " + w.errors[0];
                }
            }
            r.signature = FolderWalker.Signature(u.files);
            FillStats(r.stats, u.files);
            if (u.walkError != null) r.error = u.walkError;
            return r;
        }

        static void FillStats(FolderStats stats, List<WalkedFile> files)
        {
            var byCat = new Dictionary<string, CategoryCount>();
            foreach (var f in files)
            {
                stats.fileCount++;
                stats.totalBytes += f.size;
                CategoryCount c;
                if (!byCat.TryGetValue(f.category, out c)) byCat.Add(f.category, c = new CategoryCount { category = f.category });
                c.count++;
                c.bytes += f.size;
                if (c.examples.Count < 5) c.examples.Add(f.rel);
            }
            foreach (string cat in FileCategories.Order)
            {
                CategoryCount c;
                if (byCat.TryGetValue(cat, out c)) stats.categories.Add(c);
            }
        }

        // ------------------------------------------------------------------
        // 辞書照合と最終ラベル
        // ------------------------------------------------------------------

        void ApplyRegistry(List<Unit> units)
        {
            var targets = new List<MatchTarget>();
            var files = new List<IList<WalkedFile>>();
            var map = new List<Unit>();
            var parentOf = new Dictionary<string, string>();
            foreach (var u in units) parentOf[u.guid] = u.parentGuid ?? "";
            foreach (var u in units)
            {
                if (u.kind == Kinds.Loose || u.files == null || u.record.error.Length > 0) continue;
                int content = 0;
                var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in u.files)
                {
                    content++;
                    exts.Add(Path.GetExtension(f.rel));
                }
                var anc = new List<string>();
                string p;
                if (parentOf.TryGetValue(u.guid, out p) && !string.IsNullOrEmpty(p)) anc.Add(p);
                targets.Add(new MatchTarget { index = targets.Count, folderName = u.name, contentCount = content, extensions = exts, ancestors = anc.ToArray(), guid = u.guid });
                files.Add(u.files);
                map.Add(u);
            }

            List<RegistryCandidate>[] matches;
            try
            {
                matches = RegistryMatcher.Match(targets, files, registry.UserEntries, registry.Settings);
            }
            catch (Exception e)
            {
                lastScanWarnings.Add("辞書照合で例外が発生したため、辞書による判定を行いませんでした: " + e.Message);
                Debug.LogException(e);
                matches = new List<RegistryCandidate>[targets.Count];
                for (int i = 0; i < matches.Length; i++) matches[i] = new List<RegistryCandidate>();
            }

            for (int i = 0; i < map.Count; i++)
            {
                var u = map[i];
                u.record.registry.candidates = matches[i];
                bool structureOk;
                var rule = RegistryMatcher.MatchBuiltin(u.name, targets[i].extensions, out structureOk);
                u.record.registry.builtinRuleId = rule != null ? rule.id : "";
                if (u.kind != Kinds.Group) u.record.auto = Combine(u.record, rule, structureOk);
            }
            foreach (var u in units)
            {
                if (u.kind == Kinds.Loose) u.record.auto = LooseResult(u);
                else if (u.record.error.Length > 0) u.record.auto = ErrorResult(u.record.error);
            }
        }

        static LabelResult Combine(FolderRecord r, RegistryEntry rule, bool ruleStructureOk)
        {
            var inf = r.inference ?? new LabelResult();
            var cands = r.registry.candidates;
            int exactCount = 0;
            RegistryCandidate exact = null;
            foreach (var c in cands) if (c.exact) { exactCount++; exact = c; }

            if (exactCount == 1)
            {
                var res = CopyOf(inf);
                res.label = exact.label;
                res.confidence = Labels.ConfidenceKey(Confidence.High);
                res.layer = Layers.RegistryExact;
                res.undetermined = false;
                res.signals.Insert(0, new Signal
                {
                    strength = "strong",
                    label = exact.label,
                    text = "辞書に登録済み: " + exact.name + " (完全一致・キー: " + KeyName(exact.key) + ")",
                });
                return res;
            }

            if (rule != null)
            {
                var res = CopyOf(inf);
                res.label = rule.label;
                res.layer = Layers.BuiltinRule;
                res.undetermined = false;
                res.confidence = Labels.ConfidenceKey(ruleStructureOk ? Confidence.High : Confidence.Mid);
                res.signals.Insert(0, new Signal
                {
                    strength = ruleStructureOk ? "strong" : "mid",
                    label = rule.label,
                    text = "同梱ルール「" + rule.name + "」に一致" + (ruleStructureOk ? " (名前と構成)" : " (名前のみ。構成が想定と違います)"),
                });
                if (exactCount > 1) res.notes.Insert(0, "辞書の完全一致が複数あるため、自動では確定していません");
                return res;
            }

            var result = CopyOf(inf);
            if (exactCount > 1) result.notes.Insert(0, "辞書の完全一致が " + exactCount + " 件あるため、自動では確定していません。詳細から選んで確定してください");
            else if (cands.Count > 0) result.notes.Insert(0, "辞書の部分一致 (候補) が " + cands.Count + " 件あります。詳細から選んで確定できます");
            if (string.IsNullOrEmpty(result.label))
            {
                result.label = Labels.Key(Label.Other);
                result.confidence = "low";
                result.layer = Layers.Undetermined;
                result.undetermined = true;
            }
            return result;
        }

        static LabelResult CopyOf(LabelResult src)
        {
            var r = new LabelResult
            {
                label = src.label,
                confidence = src.confidence,
                layer = src.layer,
                undetermined = src.undetermined,
            };
            if (src.signals != null) r.signals.AddRange(src.signals);
            if (src.notes != null) r.notes.AddRange(src.notes);
            if (src.alternatives != null) r.alternatives.AddRange(src.alternatives);
            return r;
        }

        static LabelResult LooseResult(Unit u)
        {
            var r = new LabelResult
            {
                label = Labels.Key(Label.Other),
                confidence = "low",
                layer = Layers.Undetermined,
                undetermined = true,
            };
            r.signals.Add(new Signal { strength = "info", text = "Assets 直下に置かれたファイル " + u.files.Count + " 件です (フォルダに入っていないため製品を判定できません)" });
            return r;
        }

        static LabelResult ErrorResult(string error)
        {
            var r = new LabelResult
            {
                label = Labels.Key(Label.Other),
                confidence = "low",
                layer = Layers.Error,
                undetermined = true,
            };
            r.signals.Add(new Signal { strength = "info", text = "判定できませんでした: " + error });
            return r;
        }

        static void AggregateGroup(Unit g, List<Unit> units)
        {
            var r = new LabelResult { layer = Layers.Group };
            r.signals.Add(new Signal { strength = "info", text = g.groupReason ?? "子フォルダごとに判定しています" });
            RegistryCandidate exact = null;
            int exactCount = 0;
            foreach (var c in g.record.registry.candidates) if (c.exact) { exact = c; exactCount++; }
            if (exactCount == 1)
            {
                // まとめフォルダごと辞書に登録されている場合
                r.label = exact.label;
                r.confidence = "high";
                r.layer = Layers.RegistryExact;
                r.signals.Insert(0, new Signal { strength = "strong", label = exact.label, text = "辞書に登録済み: " + exact.name + " (完全一致・キー: " + KeyName(exact.key) + ")" });
                g.record.auto = r;
                g.record.inference = new LabelResult();
                return;
            }
            string common = null;
            bool same = true;
            int worst = 2, children = 0;
            foreach (var u in units)
            {
                if (u.parentGuid != g.guid) continue;
                children++;
                string l = u.record.EffectiveLabel;
                if (common == null) common = l;
                else if (common != l) same = false;
                int c = u.record.IsLocked ? 2 : (int)Labels.ParseConfidence(u.record.auto.confidence);
                worst = Math.Min(worst, c);
            }
            if (children > 0 && same && common != null)
            {
                r.label = common;
                r.confidence = Labels.ConfidenceKey((Confidence)worst);
                r.signals.Add(new Signal { strength = "info", label = common, text = "子フォルダ " + children + " 件がすべて「" + Labels.NameOfKey(common) + "」" });
            }
            else
            {
                r.label = Labels.Key(Label.Other);
                r.confidence = "low";
                r.signals.Add(new Signal { strength = "info", text = "子フォルダ " + children + " 件のラベルが混在しています" });
            }
            g.record.auto = r;
            g.record.inference = new LabelResult();
            if (g.walkError != null) g.record.error = g.walkError;
        }

        static string KeyName(string key)
        {
            switch (key)
            {
                case "guid": return "GUID";
                case "path": return "相対パス";
                case "name_size": return "ファイル名とサイズ";
                default: return key;
            }
        }

        /// <summary>辞書を変えたあと、中身の推定はそのままで照合だけやり直す。</summary>
        public void Rematch()
        {
            if (index.folders.Count == 0) return;
            var units = new List<Unit>();
            foreach (var r in index.folders)
            {
                if (r.missing) continue;
                List<WalkedFile> files;
                if (!walked.TryGetValue(r.guid, out files))
                {
                    // ウィンドウを開き直した直後などで一覧がメモリに無いときは、ファイルだけ数え直す
                    files = new List<WalkedFile>();
                    if (r.kind != Kinds.Loose && r.error.Length == 0)
                    {
                        try
                        {
                            files = FolderWalker.Walk(Path.Combine(ProjectRoot, r.path), false).files;
                            foreach (var f in files) f.guid = AssetDatabase.AssetPathToGUID(r.path + "/" + f.rel) ?? "";
                        }
                        catch (Exception) { }
                    }
                    walked[r.guid] = files;
                }
                units.Add(new Unit { guid = r.guid, path = r.path, name = r.name, kind = r.kind, parentGuid = r.parentGuid, depth = r.depth, record = r, files = files });
            }
            ApplyRegistry(units);
            foreach (var u in units) if (u.kind == Kinds.Group) AggregateGroup(u, units);
            SaveIndex();
        }

        // ------------------------------------------------------------------
        // 手動ラベル
        // ------------------------------------------------------------------

        public string Lock(FolderRecord r, string labelKey, string note)
        {
            r.manual = new ManualLabel { locked = true, label = labelKey, lockedAt = DateTime.UtcNow.ToString("o"), note = note ?? "" };
            return SaveIndex();
        }

        /// <summary>「自動判定に戻す」。ユーザーが明示したときだけ呼ぶ。</summary>
        public string Unlock(FolderRecord r)
        {
            r.manual = new ManualLabel();
            if (r.missing) index.folders.Remove(r);
            return SaveIndex();
        }

        /// <summary>索引を削除して初期状態に戻す。辞書は消さない。</summary>
        public string DeleteIndex()
        {
            try
            {
                if (File.Exists(IndexPath)) File.Delete(IndexPath);
                if (File.Exists(ReportPath)) File.Delete(ReportPath);
                if (Directory.Exists(DataDir) && Directory.GetFileSystemEntries(DataDir).Length == 0) Directory.Delete(DataDir);
            }
            catch (Exception e)
            {
                return "索引を削除できませんでした: " + e.Message;
            }
            index = new IndexFile();
            indexProblem = null;
            indexReadOnly = false;
            walked.Clear();
            return null;
        }

        // ------------------------------------------------------------------
        // レポート (画面なしでの確認用)
        // ------------------------------------------------------------------

        public string BuildReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Folder Labeler v" + ToolVersion + " 走査レポート");
            sb.AppendLine("走査日時 (UTC): " + index.scannedAt + "   方式: " + index.scanMode + "   所要時間: " + index.scanSeconds + " 秒");
            sb.AppendLine("VRChat SDK: " + (index.sdkPresent ? "あり" : "なし (アバター判定は弱くなります)"));
            sb.AppendLine("※ ラベルは自動判定による候補です。最終確認はご自身で行ってください。");
            sb.AppendLine();
            int[] perConf = new int[4];
            foreach (var r in index.folders)
            {
                if (r.IsLocked) perConf[3]++;
                else perConf[(int)Labels.ParseConfidence(r.auto.confidence)]++;
            }
            sb.AppendLine("フォルダ数: " + index.folders.Count + "  (手動 " + perConf[3] + " / 高 " + perConf[2] + " / 中 " + perConf[1] + " / 低・不明 " + perConf[0] + ")");
            sb.AppendLine();
            sb.AppendLine("パス\tラベル\t区分\t信頼度\t決定層\tファイル数\tサイズ\t根拠");
            foreach (var r in index.folders)
            {
                var signals = new List<string>();
                foreach (var s in r.auto.signals) signals.Add("[" + Labels.StrengthName(s.strength) + "] " + s.text);
                foreach (var c in r.registry.candidates) signals.Add("[辞書候補] " + c.name + " " + Math.Round(c.entryCoverage * 100) + "%");
                if (r.error.Length > 0) signals.Add("[エラー] " + r.error);
                if (r.missing) signals.Add("[見つかりません] 手動ラベルだけ残しています");
                sb.Append(r.kind == Kinds.Loose ? "Assets/*" : r.path).Append('\t')
                    .Append(Labels.NameOfKey(r.EffectiveLabel)).Append(r.auto.undetermined && !r.IsLocked ? " (不明)" : "").Append('\t')
                    .Append(r.IsLocked ? "手動" : "自動").Append('\t')
                    .Append(r.IsLocked ? "-" : Labels.ConfidenceName(r.auto.confidence)).Append('\t')
                    .Append(Labels.LayerName(r.EffectiveLayer)).Append('\t')
                    .Append(r.stats.fileCount).Append('\t')
                    .Append(FolderWalker.FormatBytes(r.stats.totalBytes)).Append('\t')
                    .Append(string.Join(" / ", signals.ToArray())).AppendLine();
            }
            return sb.ToString();
        }

        public string WriteReport(string path)
        {
            try
            {
                JsonFile.WriteAtomic(path, BuildReport());
                return null;
            }
            catch (Exception e)
            {
                return "レポートを書き出せませんでした: " + e.Message;
            }
        }
    }

    /// <summary>
    /// コマンドラインから画面なしで走査する入口:
    ///   Unity.exe -batchmode -projectPath &lt;project&gt; -executeMethod FolderLabeler.Batch.ScanAndReport -quit
    /// 結果は FolderLabeler/index.json と FolderLabeler/report.txt に書かれる。
    /// </summary>
    public static class Batch
    {
        public static void ScanAndReport()
        {
            var engine = new Engine();
            engine.Load();
            bool ok = engine.Scan(false, false);
            Debug.Log("[Folder Labeler] " + engine.lastScanMessage);
            if (!ok) return;
            string problem = engine.WriteReport(Engine.ReportPath);
            Debug.Log(problem ?? "[Folder Labeler] レポートを書き出しました: FolderLabeler/report.txt");
        }
    }
}
