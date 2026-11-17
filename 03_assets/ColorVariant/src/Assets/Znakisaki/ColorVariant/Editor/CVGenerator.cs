// Znakisaki ColorVariant v0.2.0-preview
// 複製マテリアルの生成と割り当て。
// 元マテリアルは読み取りのみ。元アバター階層には書き込まない（アバターモードは複製へ適用）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Znakisaki.ColorVariant
{
    internal sealed class CVColorEntry
    {
        public string Property = "";
        public Color Value = Color.white;
        public Material MaterialFilter; // null = 全マテリアル（プロパティを持つもののみ）
    }

    internal enum CVApplyMode { DuplicateAvatar, SelectedRenderers }

    /// <summary>1 スロット（レンダラー × マテリアルスロット）分の変更計画。</summary>
    internal sealed class CVPlanEntry
    {
        public Renderer Renderer;
        public int RendererIndexInRoot = -1; // アバターモード：root 配下 GetComponentsInChildren での位置
        public int SlotIndex;
        public Material Source;
        public readonly List<(string prop, Texture tex)> Textures = new List<(string, Texture)>();
        public readonly List<(string prop, Color color)> Colors = new List<(string, Color)>();
        public string Signature;
    }

    internal sealed class CVPlan
    {
        public readonly List<CVPlanEntry> Entries = new List<CVPlanEntry>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public int UniqueMaterialCount;
    }

    internal static class CVGenerator
    {
        static readonly string[] ReservedFolderNames = { "Editor", "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };

        public static bool HasColorProperty(Material m, string prop)
        {
            if (m == null || m.shader == null) return false;
            int i = m.shader.FindPropertyIndex(prop);
            return i >= 0 && m.shader.GetPropertyType(i) == ShaderPropertyType.Color;
        }

        public static string SanitizeName(string name, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name)) { error = "バリエーション名が空です"; return null; }
            var sb = new StringBuilder();
            var invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name.Trim())
                sb.Append(Array.IndexOf(invalid, c) >= 0 || c == '/' || c == '\\' || c < 32 ? '_' : c);
            string s = sb.ToString().Trim(' ', '.');
            if (s.Length == 0) { error = "バリエーション名に使える文字がありません"; return null; }
            if (ReservedFolderNames.Any(r => string.Equals(r, s, StringComparison.OrdinalIgnoreCase)))
            { error = $"\"{s}\" はフォルダー名として使えません（予約名）"; return null; }
            return s;
        }

        public static bool ValidateOutputRoot(string root, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(root)) { error = "生成先フォルダーが空です"; return false; }
            root = root.Replace('\\', '/').TrimEnd('/');
            if (root != "Assets" && !root.StartsWith("Assets/", StringComparison.Ordinal))
            { error = "生成先フォルダーは Assets 配下を指定してください"; return false; }
            if (root.Split('/').Any(p => p == ".." || p == "." || p.Length == 0))
            { error = "生成先フォルダーのパスが不正です"; return false; }
            return true;
        }

        /// <summary>
        /// 変更計画を作る（読み取りのみ）。effectiveTexture(slot) は差し替え先（なければ null）。
        /// </summary>
        public static CVPlan BuildPlan(CVScanResult scan, IList<Renderer> targets, Transform root, CVApplyMode mode,
            Func<CVSlot, Texture> effectiveTexture, List<CVColorEntry> colors)
        {
            var plan = new CVPlan();
            Renderer[] rootRenderers = null;
            if (mode == CVApplyMode.DuplicateAvatar)
            {
                if (root == null) { plan.Errors.Add("対象アバターが指定されていません"); return plan; }
                rootRenderers = root.GetComponentsInChildren<Renderer>(true);
            }

            var map = new Dictionary<(Renderer, int), CVPlanEntry>();
            foreach (var s in scan.Slots)
            {
                var repl = effectiveTexture(s);
                if (repl == null || repl == s.Texture) continue;
                var sev = CVScanner.Validate(s.Texture, new[] { s.PropertyDimension }, repl, out string msg);
                string where = $"{s.Renderer.name} / スロット{s.SlotIndex} \"{s.Material.name}\" / {s.Property}";
                if (sev == CVSeverity.Error) { plan.Errors.Add(where + "：" + msg); continue; }
                if (sev == CVSeverity.Warning) plan.Warnings.Add(where + "：" + msg);
                GetEntry(map, plan, s.Renderer, s.SlotIndex, s.Material).Textures.Add((s.Property, repl));
            }

            // 色プロパティ：対象の全スロットのマテリアルに対して判定
            if (colors.Count > 0)
            {
                foreach (var r in targets)
                {
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    for (int si = 0; si < mats.Length; si++)
                    {
                        var m = mats[si];
                        if (m == null) continue;
                        foreach (var c in colors)
                        {
                            if (string.IsNullOrEmpty(c.Property)) continue;
                            if (c.MaterialFilter != null && c.MaterialFilter != m) continue;
                            if (!HasColorProperty(m, c.Property)) continue;
                            if (m.GetColor(c.Property) == c.Value) continue; // 変化なし
                            GetEntry(map, plan, r, si, m).Colors.Add((c.Property, c.Value));
                        }
                    }
                }
                foreach (var c in colors)
                {
                    if (string.IsNullOrEmpty(c.Property)) continue;
                    if (c.MaterialFilter == null && !scan.ColorProperties.Contains(c.Property))
                        plan.Notes.Add($"色プロパティ {c.Property} を持つマテリアルは対象内にありません（スキップ）");
                }
            }

            var sigs = new HashSet<string>();
            foreach (var e in plan.Entries)
            {
                if (mode == CVApplyMode.DuplicateAvatar)
                {
                    e.RendererIndexInRoot = Array.IndexOf(rootRenderers, e.Renderer);
                    if (e.RendererIndexInRoot < 0)
                        plan.Errors.Add($"レンダラー \"{e.Renderer.name}\" は対象アバター配下にありません（アバターモードでは配下のレンダラーのみ適用できます）");
                }
                var sb = new StringBuilder();
                sb.Append(e.Source.GetInstanceID());
                foreach (var t in e.Textures.OrderBy(t => t.prop, StringComparer.Ordinal)) sb.Append('|').Append(t.prop).Append('=').Append(t.tex.GetInstanceID());
                foreach (var c in e.Colors.OrderBy(c => c.prop, StringComparer.Ordinal)) sb.Append('|').Append(c.prop).Append('=').Append(c.color.ToString("R"));
                e.Signature = sb.ToString();
                sigs.Add(e.Signature);
            }
            plan.UniqueMaterialCount = sigs.Count;
            return plan;
        }

        static CVPlanEntry GetEntry(Dictionary<(Renderer, int), CVPlanEntry> map, CVPlan plan, Renderer r, int slot, Material src)
        {
            if (!map.TryGetValue((r, slot), out var e))
            {
                e = new CVPlanEntry { Renderer = r, SlotIndex = slot, Source = src };
                map.Add((r, slot), e);
                plan.Entries.Add(e);
            }
            return e;
        }

        /// <summary>
        /// 生成を実行する。plan.Errors が空であることを呼び出し側で確認しておくこと。
        /// </summary>
        public static void Execute(CVPlan plan, Transform root, CVApplyMode mode, string outputRoot, string variantFolderName, List<string> log)
        {
            var sw = Stopwatch.StartNew();
            string folder = outputRoot.Replace('\\', '/').TrimEnd('/') + "/" + variantFolderName + "/Materials";
            EnsureFolder(folder);

            // 元マテリアルの変更検知用（生成前後で dirty カウントを比較）
            var srcDirty = new Dictionary<Material, int>();
            foreach (var e in plan.Entries)
                if (!srcDirty.ContainsKey(e.Source)) srcDirty.Add(e.Source, EditorUtility.GetDirtyCount(e.Source));

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("ColorVariant: " + variantFolderName);

            Renderer[] dupRenderers = null;
            if (mode == CVApplyMode.DuplicateAvatar)
            {
                var src = root.gameObject;
                var dup = UnityEngine.Object.Instantiate(src, src.transform.parent);
                dup.name = src.name + "_" + variantFolderName;
                dup.transform.localPosition = src.transform.localPosition;
                dup.transform.localRotation = src.transform.localRotation;
                dup.transform.localScale = src.transform.localScale;
                dup.transform.SetSiblingIndex(src.transform.GetSiblingIndex() + 1);
                Undo.RegisterCreatedObjectUndo(dup, "ColorVariant: 複製アバター");
                dupRenderers = dup.GetComponentsInChildren<Renderer>(true);
                log.Add($"[複製] シーン上にアバターを複製しました：{dup.name}（元のアバターは変更していません）");
            }

            var created = new Dictionary<string, Material>();
            var pendingAssign = new Dictionary<Renderer, Material[]>();
            foreach (var e in plan.Entries)
            {
                if (!created.TryGetValue(e.Signature, out var nm))
                {
                    nm = new Material(e.Source.shader);
                    EditorUtility.CopySerialized(e.Source, nm); // 完全コピー（キーワード・RenderQueue・Scale/Offset 等を含む）
                    foreach (var t in e.Textures) nm.SetTexture(t.prop, t.tex); // Scale/Offset は変わらない
                    foreach (var c in e.Colors) nm.SetColor(c.prop, c.color);
                    string path = UniquePath(folder, e.Source.name + "_" + variantFolderName);
                    nm.name = Path.GetFileNameWithoutExtension(path);
                    AssetDatabase.CreateAsset(nm, path);
                    created.Add(e.Signature, nm);
                    var detail = new List<string>();
                    foreach (var t in e.Textures) detail.Add($"{t.prop}: {t.tex.name}");
                    foreach (var c in e.Colors) detail.Add($"{c.prop}: {ColorUtility.ToHtmlStringRGBA(c.color)}");
                    log.Add($"[作成] {path}  ← 元 \"{AssetDatabase.GetAssetPath(e.Source)}\"  （{string.Join(", ", detail)}）");
                }

                var target = mode == CVApplyMode.DuplicateAvatar ? dupRenderers[e.RendererIndexInRoot] : e.Renderer;
                if (!pendingAssign.TryGetValue(target, out var mats))
                {
                    mats = target.sharedMaterials;
                    pendingAssign.Add(target, mats);
                }
                mats[e.SlotIndex] = nm;
                log.Add($"[割当] {CVScanner.PathOf(target.transform, null)} スロット{e.SlotIndex} → {nm.name}");
            }

            foreach (var kv in pendingAssign)
            {
                Undo.RecordObject(kv.Key, "ColorVariant: マテリアル割り当て");
                kv.Key.sharedMaterials = kv.Value;
                PrefabUtility.RecordPrefabInstancePropertyModifications(kv.Key);
            }
            Undo.CollapseUndoOperations(group);

            foreach (var kv in srcDirty)
                if (EditorUtility.GetDirtyCount(kv.Key) != kv.Value)
                    log.Add($"[警告] 元マテリアル \"{kv.Key.name}\" の dirty カウントが変化しました。保存せずに内容を確認してください。");

            sw.Stop();
            log.Add($"[完了] マテリアル {created.Count} 件作成、スロット {plan.Entries.Count} 件割り当て（{sw.ElapsedMilliseconds} ms）。割り当ては Ctrl+Z で戻せます。作成した .mat ファイルは Undo では削除されません。");
        }

        public static string UniquePath(string folder, string baseName, string ext = ".mat")
        {
            baseName = SanitizeFile(baseName);
            string p = $"{folder}/{baseName}{ext}";
            int i = 1;
            while (Exists(p)) p = $"{folder}/{baseName}_{i++}{ext}";
            return p;
        }

        static bool Exists(string assetPath)
        {
            return File.Exists(assetPath) || Directory.Exists(assetPath) || AssetDatabase.LoadMainAssetAtPath(assetPath) != null;
        }

        static string SanitizeFile(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        public static void EnsureFolder(string folder)
        {
            var parts = folder.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
