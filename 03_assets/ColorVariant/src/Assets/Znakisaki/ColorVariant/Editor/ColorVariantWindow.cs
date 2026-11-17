// Znakisaki ColorVariant v0.1.1-preview
// メインウィンドウ：Znakisaki/ColorVariant/Open Window
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Znakisaki.ColorVariant
{
    internal sealed class ColorVariantWindow : EditorWindow
    {
        sealed class TargetEntry { public Renderer Renderer; public bool Include = true; }

        [MenuItem("Znakisaki/ColorVariant/Open Window")]
        static void Open()
        {
            var w = GetWindow<ColorVariantWindow>();
            w.titleContent = new GUIContent("ColorVariant");
            w.minSize = new Vector2(640, 480);
            w.Show();
        }

        [MenuItem("Znakisaki/ColorVariant/About")]
        static void About()
        {
            EditorUtility.DisplayDialog("ColorVariant", $"Znakisaki ColorVariant v{CVInfo.Version}\nUnity 2022.3 / Built-in Render Pipeline", "OK");
        }

        // ---- 状態（ドメインリロードで初期化される） ----
        [NonSerialized] CVApplyMode mode = CVApplyMode.DuplicateAvatar;
        [NonSerialized] GameObject avatarRoot;
        [NonSerialized] readonly List<TargetEntry> targets = new List<TargetEntry>();
        [NonSerialized] CVScanResult scan;
        [NonSerialized] readonly Dictionary<CVSlot, Texture> slotOverrides = new Dictionary<CVSlot, Texture>();
        [NonSerialized] readonly List<CVColorEntry> colors = new List<CVColorEntry>();
        [NonSerialized] readonly List<CVApplyIssue> issues = new List<CVApplyIssue>();
        [NonSerialized] readonly List<string> log = new List<string>();
        [NonSerialized] string variantName = "Variant";
        [NonSerialized] string outputRoot = CVInfo.DefaultOutputRoot;
        [NonSerialized] bool slotView;
        [NonSerialized] bool showHidden;
        [NonSerialized] bool foldTargets = false, foldColors = true, foldIssues = true, foldLog = true;
        [NonSerialized] Vector2 scrollMain;
        [NonSerialized] long lastDefMs = -1;
        // 検証結果キャッシュ（差し替え先が変わったときのみ再計算＝width/height 参照もその時だけ）
        [NonSerialized] readonly Dictionary<object, (Texture repl, CVSeverity sev, string msg)> validateCache = new Dictionary<object, (Texture, CVSeverity, string)>();

        [NonSerialized] bool foldAbout;
        [NonSerialized] bool onlyAssigned;
        [NonSerialized] string search = "";
        [NonSerialized] GUIStyle sectionTitle, okStyle, warnStyle, errStyle;

        void InitStyles()
        {
            if (sectionTitle != null) return;
            sectionTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
            okStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            okStyle.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.5f, 0.85f, 0.5f) : new Color(0.1f, 0.5f, 0.1f);
            warnStyle = new GUIStyle(okStyle); warnStyle.normal.textColor = EditorGUIUtility.isProSkin ? new Color(1f, 0.8f, 0.3f) : new Color(0.6f, 0.4f, 0f);
            errStyle = new GUIStyle(okStyle); errStyle.normal.textColor = EditorGUIUtility.isProSkin ? new Color(1f, 0.45f, 0.45f) : new Color(0.75f, 0.1f, 0.1f);
        }

        IDisposable Section(string title)
        {
            EditorGUILayout.Space(6);
            var scope = new EditorGUILayout.VerticalScope(EditorStyles.helpBox);
            EditorGUILayout.LabelField(title, sectionTitle);
            return scope;
        }

        void OnGUI()
        {
            InitStyles();
            DrawHeader();
            scrollMain = EditorGUILayout.BeginScrollView(scrollMain);
            using (Section("① 対象を選ぶ")) DrawTargets();
            using (Section("② テクスチャを差し替える")) DrawSwapTable();
            using (Section("③ 色を変える（任意）")) DrawColors();
            using (Section("④ 生成の設定")) DrawGenerate();
            DrawIssues();
            DrawLog();
            EditorGUILayout.EndScrollView();
            DrawFooter();
        }

        // 画面下に固定：集計と生成ボタン
        void DrawFooter()
        {
            int assigned = 0, warn = 0, err = 0;
            if (scan != null)
            {
                foreach (var s in scan.Slots)
                {
                    var t = EffectiveTexture(s);
                    if (t == null || t == s.Texture) continue;
                    assigned++;
                    var (sev, _) = Check(s, s.Texture, new[] { s.PropertyDimension }, t);
                    if (sev == CVSeverity.Error) err++; else if (sev == CVSeverity.Warning) warn++;
                }
            }
            string safe = CVGenerator.SanitizeName(variantName, out _);
            bool rootOk = CVGenerator.ValidateOutputRoot(outputRoot, out _);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label($"差し替え {assigned} スロット　色 {colors.Count(c => !string.IsNullOrEmpty(c.Property))} 件", EditorStyles.boldLabel);
                    if (warn > 0) GUILayout.Label($"⚠ {warn}", warnStyle);
                    if (err > 0) GUILayout.Label($"✕ {err}（生成不可）", errStyle);
                    GUILayout.FlexibleSpace();
                }
                using (new EditorGUI.DisabledScope(scan == null || safe == null || !rootOk || err > 0))
                    if (GUILayout.Button($"色違い「{safe ?? "?"}」を生成", GUILayout.Height(30))) Generate(safe);
            }
        }

        // ================= 対象 =================
        void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label($"ColorVariant v{CVInfo.Version}", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                foldAbout = GUILayout.Toggle(foldAbout, "このツールについて", EditorStyles.toolbarButton);
                using (new EditorGUI.DisabledScope(scan == null))
                {
                    if (GUILayout.Button("定義を保存…", EditorStyles.toolbarButton)) SaveDefinition();
                    if (GUILayout.Button("定義を読み込み…", EditorStyles.toolbarButton)) LoadDefinition();
                }
            }
            if (foldAbout)
                EditorGUILayout.HelpBox("衣装や髪の「色違い」を作るツールです。\n" +
                    "テクスチャを差し替えた“複製マテリアル”を新しく作り、アバターの複製（または選んだレンダラー）に割り当てます。\n" +
                    "元のマテリアルと元のアバターは変更しません。\n" +
                    "定義（JSON）を保存すると、同じ色違いを別のアバター・衣装にも再適用できます。", MessageType.Info);
            if (lastDefMs >= 0) EditorGUILayout.LabelField($"直近の定義読み込み：{lastDefMs} ms", EditorStyles.miniLabel);
            var newMode = (CVApplyMode)GUILayout.Toolbar((int)mode, new[] { "アバターモード（複製へ適用）", "レンダラーモード（選択レンダラーへ割り当て）" });
            if (newMode != mode)
            {
                mode = newMode;
                targets.Clear();
                ClearScan();
            }
        }

        void DrawTargets()
        {
            if (mode == CVApplyMode.DuplicateAvatar)
            {
                EditorGUI.BeginChangeCheck();
                var go = (GameObject)EditorGUILayout.ObjectField("対象アバター", avatarRoot, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck())
                {
                    if (go != null && EditorUtility.IsPersistent(go))
                    {
                        EditorUtility.DisplayDialog("ColorVariant", "シーン上のオブジェクトを指定してください（Prefab アセットは指定できません）。", "OK");
                    }
                    else
                    {
                        avatarRoot = go;
                        CollectFromRoot();
                        Rescan();
                    }
                }
                EditorGUILayout.HelpBox("アバター直下の全レンダラー（非アクティブ含む）を走査します。生成時はアバターをシーン上に複製し、複製側に割り当てます。元のアバターは変更しません。", MessageType.None);
            }
            else
            {
                EditorGUILayout.HelpBox("ヒエラルキーで選択したレンダラーを追加します。生成時はそのレンダラーへ割り当てます（Ctrl+Z で戻せます）。", MessageType.None);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(mode == CVApplyMode.DuplicateAvatar ? "選択中のレンダラーを再追加" : "選択中のレンダラーを追加")) AddSelection();
                if (GUILayout.Button("再走査")) { if (mode == CVApplyMode.DuplicateAvatar) RefreshRootList(); Rescan(); }
                if (mode == CVApplyMode.SelectedRenderers && GUILayout.Button("すべて外す")) { targets.Clear(); Rescan(); }
            }

            foldTargets = EditorGUILayout.Foldout(foldTargets, $"対象レンダラー（{targets.Count(t => t.Include)} / {targets.Count}）", true);
            if (!foldTargets) return;
            EditorGUI.indentLevel++;
            bool changed = false;
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool inc = EditorGUILayout.ToggleLeft(t.Renderer != null ? CVScanner.PathOf(t.Renderer.transform, avatarRoot != null ? avatarRoot.transform.parent : null) : "(削除済み)", t.Include);
                    if (inc != t.Include) { t.Include = inc; changed = true; }
                    if (t.Renderer != null && GUILayout.Button("選択", GUILayout.Width(44))) EditorGUIUtility.PingObject(t.Renderer);
                    if (mode == CVApplyMode.SelectedRenderers && GUILayout.Button("×", GUILayout.Width(22))) { targets.RemoveAt(i); i--; changed = true; }
                }
            }
            EditorGUI.indentLevel--;
            if (changed) Rescan();
        }

        void CollectFromRoot()
        {
            targets.Clear();
            if (avatarRoot == null) return;
            foreach (var r in avatarRoot.GetComponentsInChildren<Renderer>(true)) targets.Add(new TargetEntry { Renderer = r });
        }

        void RefreshRootList()
        {
            if (avatarRoot == null) { targets.Clear(); return; }
            var excluded = new HashSet<Renderer>(targets.Where(t => !t.Include).Select(t => t.Renderer));
            CollectFromRoot();
            foreach (var t in targets) t.Include = !excluded.Contains(t.Renderer);
        }

        void AddSelection()
        {
            var sel = Selection.GetFiltered<Renderer>(SelectionMode.Editable | SelectionMode.ExcludePrefab);
            if (sel.Length == 0) { ShowNotification(new GUIContent("シーン上のレンダラーを選択してください")); return; }
            int rejected = 0;
            foreach (var r in sel)
            {
                var existing = targets.FirstOrDefault(t => t.Renderer == r);
                if (existing != null) { existing.Include = true; continue; }
                if (mode == CVApplyMode.DuplicateAvatar)
                {
                    // アバターモードでは配下のレンダラーのみ（外部のレンダラーを変更しないため）
                    rejected++;
                    continue;
                }
                targets.Add(new TargetEntry { Renderer = r });
            }
            if (rejected > 0) ShowNotification(new GUIContent($"{rejected} 件はアバター配下ではないため追加しませんでした"));
            Rescan();
        }

        List<Renderer> IncludedRenderers() => targets.Where(t => t.Include && t.Renderer != null).Select(t => t.Renderer).ToList();
        Transform ScopeRoot() => mode == CVApplyMode.DuplicateAvatar && avatarRoot != null ? avatarRoot.transform : null;

        void ClearScan()
        {
            scan = null;
            slotOverrides.Clear();
            validateCache.Clear();
        }

        void Rescan()
        {
            // 差し替え指定は可能な範囲で引き継ぐ
            var keep = new Dictionary<string, Texture>();
            var keepSlot = new Dictionary<string, Texture>();
            if (scan != null)
            {
                foreach (var row in scan.Rows) if (row.Replacement != null) keep[row.Key] = row.Replacement;
                foreach (var kv in slotOverrides) if (kv.Key.Renderer != null) keepSlot[SlotId(kv.Key)] = kv.Value;
            }
            slotOverrides.Clear();
            validateCache.Clear();
            scan = CVScanner.Scan(IncludedRenderers());
            foreach (var row in scan.Rows) if (keep.TryGetValue(row.Key, out var t)) row.Replacement = t;
            foreach (var s in scan.Slots) if (keepSlot.TryGetValue(SlotId(s), out var t)) slotOverrides[s] = t;
        }

        static string SlotId(CVSlot s) => s.Renderer.GetInstanceID() + "|" + s.SlotIndex + "|" + s.Property;

        // ================= 差し替え表 =================
        void DrawSwapTable()
        {
            if (scan == null) { EditorGUILayout.HelpBox("対象を指定してください。", MessageType.Info); return; }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"走査：{scan.Slots.Count} スロット / テクスチャ {scan.Rows.Count} 種 / {scan.ElapsedMs} ms", EditorStyles.miniLabel);
                slotView = GUILayout.Toolbar(slotView ? 1 : 0, new[] { "元テクスチャ単位", "スロット単位" }, GUILayout.Width(220)) == 1;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField);
                onlyAssigned = GUILayout.Toggle(onlyAssigned, "指定済みのみ", GUILayout.Width(90));
                if (slotView) showHidden = GUILayout.Toggle(showHidden, "非表示プロパティ", GUILayout.Width(110));
            }

            if (!slotView) DrawRows(); else DrawSlots();
        }

        void DrawRows()
        {
            if (scan.Rows.Count == 0) { EditorGUILayout.HelpBox("テクスチャが設定されたスロットがありません。", MessageType.Info); return; }
            foreach (var row in scan.Rows)
            {
                if (onlyAssigned && row.Replacement == null) continue;
                if (!Match(row.Original.name)) continue;
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(row.Original.name, EditorStyles.label, GUILayout.MinWidth(120))) EditorGUIUtility.PingObject(row.Original);
                        GUILayout.Label($"{CVScanner.DimName(row.Original.dimension)}  使用 {row.Slots.Count} 箇所" + (row.IsAsset ? "" : "  (非アセット)"), EditorStyles.miniLabel, GUILayout.Width(200));
                        GUILayout.Label("→", GUILayout.Width(16));
                        row.Replacement = (Texture)EditorGUILayout.ObjectField(row.Replacement, typeof(Texture), false, GUILayout.MinWidth(150));
                    }
                    var (sev, msg) = Check(row, row.Original, row.Slots.Select(s => s.PropertyDimension), row.Replacement);
                    DrawStatus(sev, msg);
                }
            }
        }

        void DrawSlots()
        {
            Renderer lastR = null; Material lastM = null; int lastSlot = -1;
            foreach (var s in scan.Slots)
            {
                if (s.Hidden && !showHidden && !slotOverrides.ContainsKey(s)) continue;
                if (onlyAssigned && EffectiveTexture(s) == null) continue;
                if (!Match(s.Renderer.name) && !Match(s.Material.name) && !Match(s.Property) && !(s.Texture != null && Match(s.Texture.name))) continue;
                if (s.Renderer != lastR) { EditorGUILayout.LabelField(s.Renderer.name, EditorStyles.boldLabel); lastR = s.Renderer; lastSlot = -1; }
                if (s.SlotIndex != lastSlot || s.Material != lastM) { EditorGUILayout.LabelField($"  スロット{s.SlotIndex}: {s.Material.name}  ({s.Material.shader.name})", EditorStyles.miniBoldLabel); lastSlot = s.SlotIndex; lastM = s.Material; }
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(24);
                    GUILayout.Label(s.Property + (s.Hidden ? " (hidden)" : ""), GUILayout.Width(150));
                    GUILayout.Label((s.Texture != null ? s.Texture.name : "(なし)") + "  " + CVScanner.DimName(s.PropertyDimension), EditorStyles.miniLabel, GUILayout.Width(200));
                    GUILayout.Label("→", GUILayout.Width(16));
                    slotOverrides.TryGetValue(s, out var ov);
                    var newOv = (Texture)EditorGUILayout.ObjectField(ov, typeof(Texture), false, GUILayout.MinWidth(150));
                    if (newOv != ov) { if (newOv == null) slotOverrides.Remove(s); else slotOverrides[s] = newOv; }
                }
                Texture eff = EffectiveTexture(s);
                if (eff != null)
                {
                    var (sev, msg) = Check(s, s.Texture, new[] { s.PropertyDimension }, eff);
                    string src = slotOverrides.ContainsKey(s) ? "個別指定" : "元テクスチャ単位の指定";
                    DrawStatus(sev, $"[{src}] {msg}");
                }
            }
        }

        (CVSeverity, string) Check(object key, Texture original, IEnumerable<TextureDimension> dims, Texture repl)
        {
            if (repl == null) { validateCache.Remove(key); return (CVSeverity.None, null); }
            if (validateCache.TryGetValue(key, out var c) && c.repl == repl) return (c.sev, c.msg);
            var sev = CVScanner.Validate(original, dims, repl, out string msg);
            validateCache[key] = (repl, sev, msg);
            return (sev, msg);
        }

        bool Match(string name) => string.IsNullOrEmpty(search) || (name != null && name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);

        void DrawStatus(CVSeverity sev, string msg)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(28);
                switch (sev)
                {
                    case CVSeverity.Error: GUILayout.Label("✕ " + msg, errStyle); break;
                    case CVSeverity.Warning: GUILayout.Label("⚠ " + msg, warnStyle); break;
                    case CVSeverity.Ok: GUILayout.Label("✓ " + msg, okStyle); break;
                }
            }
        }

        Texture EffectiveTexture(CVSlot s)
        {
            if (slotOverrides.TryGetValue(s, out var ov) && ov != null) return ov;
            if (s.TextureKey != null && scan.RowIndex.TryGetValue(s.TextureKey, out var row)) return row.Replacement;
            return null;
        }

        // ================= 色プロパティ =================
        void DrawColors()
        {
            EditorGUILayout.HelpBox("対象内のシェーダーが持つ Color 型プロパティから選びます。そのプロパティを持たないマテリアルは変更しません。マテリアル指定を空にすると、プロパティを持つ全マテリアルが対象です。", MessageType.None);
            var names = scan != null ? scan.ColorProperties.ToArray() : new string[0];
            for (int i = 0; i < colors.Count; i++)
            {
                var c = colors[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    int idx = Array.IndexOf(names, c.Property);
                    var opts = names.ToList();
                    if (idx < 0) { opts.Insert(0, string.IsNullOrEmpty(c.Property) ? "(選択)" : c.Property + " (対象内になし)"); idx = 0; }
                    int sel = EditorGUILayout.Popup(idx, opts.ToArray(), GUILayout.Width(180));
                    if (sel != idx) c.Property = opts[sel];
                    c.Value = EditorGUILayout.ColorField(GUIContent.none, c.Value, true, true, false, GUILayout.Width(120));
                    c.MaterialFilter = (Material)EditorGUILayout.ObjectField(c.MaterialFilter, typeof(Material), false);
                    if (GUILayout.Button("×", GUILayout.Width(22))) { colors.RemoveAt(i); i--; }
                }
            }
            using (new EditorGUI.DisabledScope(names.Length == 0))
                if (GUILayout.Button("色プロパティを追加", GUILayout.Width(160))) colors.Add(new CVColorEntry());
        }


        void SaveDefinition()
        {
            issues.Clear();
            var d = CVDefinitionIO.Build(variantName, scan, slotOverrides, colors, ScopeRoot(), issues);
            string name = CVGenerator.SanitizeName(variantName, out _) ?? "Variant";
            string defaultDir = Path.GetFullPath(outputRoot.Replace('\\', '/') + "/" + name);
            if (!Directory.Exists(defaultDir)) defaultDir = Path.GetFullPath("Assets");
            string path = EditorUtility.SaveFilePanel("バリエーション定義を保存", defaultDir, name + ".colorvariant", "json");
            if (string.IsNullOrEmpty(path)) return;
            if (File.Exists(path) && !EditorUtility.DisplayDialog("ColorVariant", $"既存のファイルを上書きしますか？\n{path}", "上書き", "キャンセル")) return;
            File.WriteAllText(path, CVDefinitionIO.ToJson(d), new UTF8Encoding(false));
            log.Add($"[定義保存] {path}（テクスチャ差し替え {d.textureSwaps.Count} / 色 {d.colorOverrides.Count}）");
            if (issues.Count > 0) log.Add($"[定義保存] 保存できなかった項目が {issues.Count} 件あります（下の一覧を参照）");
            string rel = ToAssetPath(path);
            if (rel != null) AssetDatabase.ImportAsset(rel);
        }

        void LoadDefinition()
        {
            string path = EditorUtility.OpenFilePanel("バリエーション定義を読み込む", Path.GetFullPath("Assets"), "json");
            if (string.IsNullOrEmpty(path)) return;
            var d = CVDefinitionIO.FromJson(File.ReadAllText(path, Encoding.UTF8), out string err);
            if (d == null) { EditorUtility.DisplayDialog("ColorVariant", err, "OK"); return; }
            issues.Clear();
            validateCache.Clear();
            CVDefinitionIO.ApplyToScan(d, scan, IncludedRenderers(), ScopeRoot(), slotOverrides, colors, issues, out lastDefMs);
            if (!string.IsNullOrEmpty(d.variantName)) variantName = d.variantName;
            foldIssues = true;
            log.Add($"[定義読込] {path}：未適用 {issues.Count} 件（{lastDefMs} ms）");
        }

        static string ToAssetPath(string full)
        {
            string proj = Path.GetFullPath(".").Replace('\\', '/').TrimEnd('/') + "/";
            full = Path.GetFullPath(full).Replace('\\', '/');
            if (!full.StartsWith(proj, StringComparison.OrdinalIgnoreCase)) return null;
            string rel = full.Substring(proj.Length);
            return rel.StartsWith("Assets/") ? rel : null;
        }

        static string ToFolderAssetPath(string full)
        {
            string proj = Path.GetFullPath(".").Replace('\\', '/').TrimEnd('/') + "/";
            full = Path.GetFullPath(full).Replace('\\', '/').TrimEnd('/');
            if (!full.StartsWith(proj, StringComparison.OrdinalIgnoreCase)) return null;
            string rel = full.Substring(proj.Length);
            return rel == "Assets" || rel.StartsWith("Assets/") ? rel : null;
        }

        // ================= 生成 =================
        void DrawGenerate()
        {
            variantName = EditorGUILayout.TextField("バリエーション名", variantName);
            using (new EditorGUILayout.HorizontalScope())
            {
                outputRoot = EditorGUILayout.TextField("生成先の親フォルダー", outputRoot);
                if (GUILayout.Button("…", GUILayout.Width(24)))
                {
                    string p = EditorUtility.OpenFolderPanel("生成先の親フォルダー", Path.GetFullPath("Assets"), "");
                    string rel = string.IsNullOrEmpty(p) ? null : ToFolderAssetPath(p);
                    if (rel != null) outputRoot = rel; else if (!string.IsNullOrEmpty(p)) ShowNotification(new GUIContent("Assets 配下を選んでください"));
                }
            }
            string safe = CVGenerator.SanitizeName(variantName, out string nameErr);
            bool rootOk = CVGenerator.ValidateOutputRoot(outputRoot, out string rootErr);
            if (nameErr != null) EditorGUILayout.HelpBox(nameErr, MessageType.Error);
            if (!rootOk) EditorGUILayout.HelpBox(rootErr, MessageType.Error);
            if (safe != null && rootOk) EditorGUILayout.LabelField("生成先：" + outputRoot.TrimEnd('/') + "/" + safe + "/Materials/", EditorStyles.miniLabel);
            EditorGUILayout.HelpBox("・作成した .mat ファイルの削除は Undo（Ctrl+Z）の対象外です。不要になった場合は生成先フォルダーから手動で削除してください。\n・同名ファイルがある場合は上書きせず、末尾に _1, _2… を付けます。\n・シーンは保存しません。", MessageType.Warning);

        }

        void Generate(string safeName)
        {
            if (mode == CVApplyMode.DuplicateAvatar && avatarRoot == null) { ShowNotification(new GUIContent("対象アバターを指定してください")); return; }
            var plan = CVGenerator.BuildPlan(scan, IncludedRenderers(), ScopeRoot(), mode, EffectiveTexture, colors);
            if (plan.Errors.Count > 0)
            {
                issues.Clear();
                foreach (var e in plan.Errors) issues.Add(new CVApplyIssue { Item = "生成を中止", Reason = e });
                foldIssues = true;
                EditorUtility.DisplayDialog("ColorVariant", $"生成できない指定が {plan.Errors.Count} 件あります（次元タイプ不一致など）。一覧を確認してください。\n\n" + string.Join("\n", plan.Errors.Take(5)), "OK");
                return;
            }
            if (plan.Entries.Count == 0) { EditorUtility.DisplayDialog("ColorVariant", "変更点がありません。差し替え先または色を指定してください。", "OK"); return; }

            var sb = new StringBuilder();
            sb.AppendLine($"新規マテリアル {plan.UniqueMaterialCount} 件を作成し、{plan.Entries.Count} スロットに割り当てます。");
            sb.AppendLine($"作成先：{outputRoot.TrimEnd('/')}/{safeName}/Materials/");
            sb.AppendLine(mode == CVApplyMode.DuplicateAvatar ? $"適用先：\"{avatarRoot.name}\" の複製（元のアバターは変更しません）" : "適用先：選択したレンダラー（Ctrl+Z で戻せます）");
            sb.AppendLine();
            sb.AppendLine("※作成した .mat ファイルは Ctrl+Z では削除されません。");
            if (plan.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"⚠ 警告 {plan.Warnings.Count} 件：");
                foreach (var w in plan.Warnings.Take(6)) sb.AppendLine("・" + w);
                if (plan.Warnings.Count > 6) sb.AppendLine($"…ほか {plan.Warnings.Count - 6} 件");
            }
            foreach (var n in plan.Notes) sb.AppendLine("・" + n);
            if (!EditorUtility.DisplayDialog("ColorVariant：生成の確認", sb.ToString(), "生成する", "キャンセル")) return;

            log.Add($"---- 生成 \"{safeName}\" ----");
            foreach (var w in plan.Warnings) log.Add("[警告] " + w);
            foreach (var n in plan.Notes) log.Add("[情報] " + n);
            try
            {
                CVGenerator.Execute(plan, ScopeRoot(), mode, outputRoot, safeName, log);
            }
            catch (Exception e)
            {
                log.Add("[エラー] " + e.Message);
                Debug.LogException(e);
            }
            foldLog = true;
            if (mode == CVApplyMode.SelectedRenderers) Rescan();
        }

        // ================= 一覧・ログ =================
        void DrawIssues()
        {
            if (issues.Count == 0) return;
            EditorGUILayout.Space();
            foldIssues = EditorGUILayout.Foldout(foldIssues, $"未適用・未保存の項目（{issues.Count}）", true);
            if (!foldIssues) return;
            foreach (var i in issues) EditorGUILayout.HelpBox($"{i.Item}\n理由：{i.Reason}", MessageType.Warning);
            if (GUILayout.Button("一覧を消去", GUILayout.Width(100))) issues.Clear();
        }

        void DrawLog()
        {
            if (log.Count == 0) return;
            EditorGUILayout.Space();
            foldLog = EditorGUILayout.Foldout(foldLog, $"ログ（{log.Count}）", true);
            if (!foldLog) return;
            string text = string.Join("\n", log);
            EditorGUILayout.SelectableLabel(text, EditorStyles.textArea, GUILayout.Height(Mathf.Min(400, 16 * log.Count + 8)));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("ログをコピー", GUILayout.Width(100))) EditorGUIUtility.systemCopyBuffer = text;
                if (GUILayout.Button("ログを消去", GUILayout.Width(100))) log.Clear();
            }
        }
    }
}
