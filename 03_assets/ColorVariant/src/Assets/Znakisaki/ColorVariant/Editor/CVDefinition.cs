// Znakisaki ColorVariant v0.2.0-preview
// バリエーション定義（平文 JSON）の保存・読み込み・参照解決。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;

namespace Znakisaki.ColorVariant
{
    [Serializable]
    internal sealed class CVAssetRef
    {
        public string guid = "";
        public long localId;
        public string name = "";
        public bool IsEmpty => string.IsNullOrEmpty(guid);
    }

    [Serializable]
    internal sealed class CVScope
    {
        public string rendererPath = "";
        public int rendererComponentIndex;
        public int slotIndex;
        public string property = "";
    }

    [Serializable]
    internal sealed class CVTextureSwap
    {
        public CVAssetRef from = new CVAssetRef();  // scope 指定時は空（空スロット）でもよい
        public CVAssetRef to = new CVAssetRef();
        public bool hasScope;
        public CVScope scope = new CVScope();
        public bool hasBake;                    // 焼き付け（to が空なら元テクスチャのコピーに焼き付け）
        public CVBakeSettings bake = new CVBakeSettings();
    }

    [Serializable]
    internal sealed class CVColorOverride
    {
        public string property = "";
        public Color value = Color.white;
        public bool hasMaterialFilter;
        public CVAssetRef material = new CVAssetRef();
    }

    [Serializable]
    internal sealed class CVDefinition
    {
        public string format = "Znakisaki.ColorVariant";
        public int version = 1;
        public string toolVersion = CVInfo.Version;
        public string variantName = "";
        public List<CVTextureSwap> textureSwaps = new List<CVTextureSwap>();
        public List<CVColorOverride> colorOverrides = new List<CVColorOverride>();
    }

    internal static class CVRefs
    {
        public static CVAssetRef Make(UnityEngine.Object o)
        {
            var r = new CVAssetRef();
            if (o == null) return r;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string g, out long l) && !string.IsNullOrEmpty(g))
            {
                r.guid = g; r.localId = l;
            }
            r.name = o.name;
            return r;
        }

        /// <summary>GUID＋ローカルID を解決する。失敗時は理由を返す。</summary>
        public static T Resolve<T>(CVAssetRef r, out string reason) where T : UnityEngine.Object
        {
            reason = null;
            if (r == null || r.IsEmpty) { reason = "参照が空です"; return null; }
            string path = AssetDatabase.GUIDToAssetPath(r.guid);
            if (string.IsNullOrEmpty(path) || AssetDatabase.GetMainAssetTypeAtPath(path) == null)
            {
                reason = $"アセット欠落：GUID {r.guid}（名前 \"{r.name}\"）がプロジェクト内に見つかりません";
                return null;
            }
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (o == null) continue;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string g, out long l) && l == r.localId)
                {
                    if (o is T t) return t;
                    reason = $"型不一致：{path} のローカルID {r.localId} は {o.GetType().Name} で、{typeof(T).Name} ではありません";
                    return null;
                }
            }
            reason = $"サブアセット欠落：{path} にローカルID {r.localId}（名前 \"{r.name}\"）がありません";
            return null;
        }
    }

    internal sealed class CVApplyIssue
    {
        public string Item;
        public string Reason;
    }

    internal static class CVDefinitionIO
    {
        public static string ToJson(CVDefinition d) => JsonUtility.ToJson(d, true);

        public static CVDefinition FromJson(string json, out string error)
        {
            error = null;
            CVDefinition d;
            try { d = JsonUtility.FromJson<CVDefinition>(json); }
            catch (Exception e) { error = "JSON の解析に失敗しました：" + e.Message; return null; }
            if (d == null || d.format != "Znakisaki.ColorVariant") { error = "ColorVariant の定義ファイルではありません（format 不一致）"; return null; }
            if (d.version != 1) { error = $"未対応の定義バージョンです：{d.version}"; return null; }
            if (d.textureSwaps == null) d.textureSwaps = new List<CVTextureSwap>();
            if (d.colorOverrides == null) d.colorOverrides = new List<CVColorOverride>();
            return d;
        }

        /// <summary>
        /// 定義を現在の走査結果へ反映する（ウィンドウ上の表を埋めるだけ。アセットやシーンは変更しない）。
        /// 解決できない項目は issues に理由つきで積む。
        /// </summary>
        public static void ApplyToScan(CVDefinition d, CVScanResult scan, IList<Renderer> targets, Transform root,
            Dictionary<CVSlot, Texture> slotOverrides, List<CVColorEntry> colors, List<CVApplyIssue> issues, out long elapsedMs)
        {
            var sw = Stopwatch.StartNew();

            // スコープ照合用の索引（相対パス|コンポーネント番号|スロット|プロパティ → スロット）
            var scopeIndex = new Dictionary<string, CVSlot>();
            foreach (var s in scan.Slots)
            {
                string key = ScopeKey(CVScanner.PathOf(s.Renderer.transform, root), CVScanner.RendererComponentIndex(s.Renderer), s.SlotIndex, s.Property);
                if (!scopeIndex.ContainsKey(key)) scopeIndex.Add(key, s);
            }

            foreach (var row in scan.Rows) { row.Replacement = null; row.Bake = new CVBakeSettings(); }
            slotOverrides.Clear();
            colors.Clear();

            int n = 0;
            foreach (var ts in d.textureSwaps)
            {
                n++;
                string label = $"テクスチャ差し替え #{n}：{Nm(ts.from)} → {(ts.hasBake && (ts.to == null || ts.to.IsEmpty) ? "（焼き付け）" : Nm(ts.to))}";
                bool bakeOnly = ts.hasBake && !ts.hasScope && (ts.to == null || ts.to.IsEmpty);
                Texture to = null;
                if (!bakeOnly)
                {
                    to = CVRefs.Resolve<Texture>(ts.to, out string reason);
                    if (to == null) { issues.Add(new CVApplyIssue { Item = label, Reason = "差し替え先：" + reason }); continue; }
                }

                if (ts.hasScope)
                {
                    var sc = ts.scope;
                    string key = ScopeKey(sc.rendererPath, sc.rendererComponentIndex, sc.slotIndex, sc.property);
                    if (!scopeIndex.TryGetValue(key, out var slot))
                    {
                        issues.Add(new CVApplyIssue { Item = label, Reason = $"適用先スロットが見つかりません：\"{sc.rendererPath}\"[{sc.rendererComponentIndex}] スロット{sc.slotIndex} {sc.property}" });
                        continue;
                    }
                    var sev = CVScanner.Validate(slot.Texture, new[] { slot.PropertyDimension }, to, out string msg);
                    if (sev == CVSeverity.Error) { issues.Add(new CVApplyIssue { Item = label, Reason = msg }); continue; }
                    slotOverrides[slot] = to;
                }
                else
                {
                    if (ts.from == null || ts.from.IsEmpty) { issues.Add(new CVApplyIssue { Item = label, Reason = "差し替え元が空で、適用先スロットの指定もありません" }); continue; }
                    string key = ts.from.guid + ":" + ts.from.localId;
                    if (!scan.RowIndex.TryGetValue(key, out var row))
                    {
                        // 差し替え元アセット自体が欠落しているかも確認して理由を詳しくする
                        CVRefs.Resolve<Texture>(ts.from, out string fromReason);
                        issues.Add(new CVApplyIssue { Item = label, Reason = fromReason != null ? "差し替え元：" + fromReason : "対象内に差し替え元テクスチャを使うスロットがありません" });
                        continue;
                    }
                    if (to != null)
                    {
                        var dims = new List<UnityEngine.Rendering.TextureDimension>();
                        foreach (var s in row.Slots) dims.Add(s.PropertyDimension);
                        var sev = CVScanner.Validate(row.Original, dims, to, out string msg);
                        if (sev == CVSeverity.Error) { issues.Add(new CVApplyIssue { Item = label, Reason = msg }); continue; }
                    }
                    if (ts.hasBake && ts.bake != null)
                    {
                        if (!CVBake.CanBake(to != null ? to : row.Original, out string bakeReason))
                        { issues.Add(new CVApplyIssue { Item = label, Reason = "焼き付け：" + bakeReason }); continue; }
                        row.Bake = ts.bake.Clone();
                        row.Bake.enabled = true;
                    }
                    row.Replacement = to;
                }
            }

            n = 0;
            foreach (var co in d.colorOverrides)
            {
                n++;
                string label = $"色プロパティ #{n}：{co.property}";
                if (string.IsNullOrEmpty(co.property)) { issues.Add(new CVApplyIssue { Item = label, Reason = "プロパティ名が空です" }); continue; }
                Material filter = null;
                if (co.hasMaterialFilter)
                {
                    filter = CVRefs.Resolve<Material>(co.material, out string reason);
                    if (filter == null) { issues.Add(new CVApplyIssue { Item = label, Reason = "対象マテリアル：" + reason }); continue; }
                    if (!scan.Materials.Contains(filter)) { issues.Add(new CVApplyIssue { Item = label, Reason = $"対象マテリアル \"{filter.name}\" が今回の対象内にありません" }); continue; }
                    if (!CVGenerator.HasColorProperty(filter, co.property)) { issues.Add(new CVApplyIssue { Item = label, Reason = $"マテリアル \"{filter.name}\" のシェーダーに Color 型プロパティ {co.property} がありません" }); continue; }
                }
                else if (!scan.ColorProperties.Contains(co.property))
                {
                    issues.Add(new CVApplyIssue { Item = label, Reason = $"対象内のどのシェーダーにも Color 型プロパティ {co.property} がありません" });
                    continue;
                }
                colors.Add(new CVColorEntry { Property = co.property, Value = co.value, MaterialFilter = filter });
            }
            sw.Stop();
            elapsedMs = sw.ElapsedMilliseconds;
        }

        /// <summary>現在の表から定義を作る。保存できない項目は issues に積む。</summary>
        public static CVDefinition Build(string variantName, CVScanResult scan, Dictionary<CVSlot, Texture> slotOverrides,
            List<CVColorEntry> colors, Transform root, List<CVApplyIssue> issues)
        {
            var d = new CVDefinition { variantName = variantName };
            foreach (var row in scan.Rows)
            {
                bool bake = row.Bake != null && row.Bake.HasEffect;
                if (row.Replacement == null && !bake) continue;
                string label = $"{row.Original.name} → {(row.Replacement != null ? row.Replacement.name : "（焼き付け）")}";
                if (!row.IsAsset) { issues.Add(new CVApplyIssue { Item = label, Reason = "差し替え元がアセットではないため保存できません" }); continue; }
                var to = CVRefs.Make(row.Replacement);
                if (row.Replacement != null && to.IsEmpty) { issues.Add(new CVApplyIssue { Item = label, Reason = "差し替え先がアセットではないため保存できません" }); continue; }
                d.textureSwaps.Add(new CVTextureSwap { from = CVRefs.Make(row.Original), to = to, hasBake = bake, bake = bake ? row.Bake.Clone() : new CVBakeSettings() });
            }
            foreach (var kv in slotOverrides)
            {
                var s = kv.Key;
                if (kv.Value == null) continue;
                string label = $"{s.Renderer.name} スロット{s.SlotIndex} {s.Property} → {kv.Value.name}";
                var to = CVRefs.Make(kv.Value);
                if (to.IsEmpty) { issues.Add(new CVApplyIssue { Item = label, Reason = "差し替え先がアセットではないため保存できません" }); continue; }
                d.textureSwaps.Add(new CVTextureSwap
                {
                    from = CVRefs.Make(s.Texture), to = to, hasScope = true,
                    scope = new CVScope
                    {
                        rendererPath = CVScanner.PathOf(s.Renderer.transform, root),
                        rendererComponentIndex = CVScanner.RendererComponentIndex(s.Renderer),
                        slotIndex = s.SlotIndex, property = s.Property,
                    }
                });
            }
            foreach (var c in colors)
            {
                if (string.IsNullOrEmpty(c.Property)) continue;
                var co = new CVColorOverride { property = c.Property, value = c.Value };
                if (c.MaterialFilter != null)
                {
                    co.hasMaterialFilter = true;
                    co.material = CVRefs.Make(c.MaterialFilter);
                    if (co.material.IsEmpty) { issues.Add(new CVApplyIssue { Item = "色プロパティ " + c.Property, Reason = "対象マテリアルがアセットではないため保存できません" }); continue; }
                }
                d.colorOverrides.Add(co);
            }
            return d;
        }

        static string ScopeKey(string path, int comp, int slot, string prop) => path + "|" + comp + "|" + slot + "|" + prop;
        static string Nm(CVAssetRef r) => r == null || string.IsNullOrEmpty(r.name) ? "(空)" : r.name;
    }
}
