// Znakisaki ColorVariant v0.1.0-preview
// 走査結果のデータモデルと、読み取り専用の走査処理。
// ここでは一切の書き込み（SetDirty / Undo 記録 / アセット変更）を行わない。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Znakisaki.ColorVariant
{
    internal static class CVInfo
    {
        public const string Version = "0.1.0-preview";
        public const string DefaultOutputRoot = "Assets/Znakisaki/ColorVariant";
    }

    internal enum CVSeverity { None, Ok, Warning, Error }

    /// <summary>1 つの「レンダラー × マテリアルスロット × テクスチャプロパティ」。</summary>
    internal sealed class CVSlot
    {
        public int TargetIndex;          // 対象レンダラー一覧での位置
        public Renderer Renderer;
        public int SlotIndex;
        public Material Material;
        public string Property;
        public TextureDimension PropertyDimension;
        public bool Hidden;              // [HideInInspector] 付き
        public Texture Texture;          // 現在の参照（null 可）
        public string TextureKey;        // 照合キー（null テクスチャは null）
    }

    /// <summary>元テクスチャ単位でまとめた差し替え行。</summary>
    internal sealed class CVTextureRow
    {
        public string Key;
        public Texture Original;
        public string Guid;
        public long LocalId;
        public bool IsAsset;
        public readonly List<CVSlot> Slots = new List<CVSlot>();
        public Texture Replacement;
    }

    internal sealed class CVScanResult
    {
        public readonly List<CVSlot> Slots = new List<CVSlot>();
        public readonly List<CVTextureRow> Rows = new List<CVTextureRow>();
        public readonly Dictionary<string, CVTextureRow> RowIndex = new Dictionary<string, CVTextureRow>();
        public readonly SortedSet<string> ColorProperties = new SortedSet<string>(StringComparer.Ordinal);
        public readonly HashSet<Material> Materials = new HashSet<Material>();
        public long ElapsedMs;
    }

    internal static class CVScanner
    {
        public static string KeyOf(UnityEngine.Object o, out string guid, out long localId, out bool isAsset)
        {
            guid = null; localId = 0; isAsset = false;
            if (o == null) return null;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out guid, out localId) && !string.IsNullOrEmpty(guid))
            {
                isAsset = true;
                return guid + ":" + localId;
            }
            guid = null; localId = 0;
            return "inst:" + o.GetInstanceID();
        }

        /// <summary>
        /// 対象レンダラーを走査する。sharedMaterials のみを読む（renderer.materials は
        /// インスタンス化を起こすため使わない）。テクスチャの width/height には触れない。
        /// </summary>
        public static CVScanResult Scan(IList<Renderer> targets)
        {
            var sw = Stopwatch.StartNew();
            var res = new CVScanResult();
            var shaderCache = new Dictionary<Shader, List<(string name, TextureDimension dim, bool hidden)>>();

            for (int ti = 0; ti < targets.Count; ti++)
            {
                var r = targets[ti];
                if (r == null) continue;
                var mats = r.sharedMaterials;
                for (int si = 0; si < mats.Length; si++)
                {
                    var m = mats[si];
                    if (m == null || m.shader == null) continue;
                    res.Materials.Add(m);
                    var sh = m.shader;
                    if (!shaderCache.TryGetValue(sh, out var props))
                    {
                        props = new List<(string, TextureDimension, bool)>();
                        int n = sh.GetPropertyCount();
                        for (int pi = 0; pi < n; pi++)
                        {
                            var type = sh.GetPropertyType(pi);
                            if (type == ShaderPropertyType.Texture)
                            {
                                bool hidden = (sh.GetPropertyFlags(pi) & ShaderPropertyFlags.HideInInspector) != 0;
                                props.Add((sh.GetPropertyName(pi), sh.GetPropertyTextureDimension(pi), hidden));
                            }
                            else if (type == ShaderPropertyType.Color)
                            {
                                res.ColorProperties.Add(sh.GetPropertyName(pi));
                            }
                        }
                        shaderCache[sh] = props;
                    }
                    else
                    {
                        // 色プロパティはシェーダー初回時に登録済み
                    }

                    foreach (var p in props)
                    {
                        var tex = m.GetTexture(p.name);
                        var slot = new CVSlot
                        {
                            TargetIndex = ti, Renderer = r, SlotIndex = si, Material = m,
                            Property = p.name, PropertyDimension = p.dim, Hidden = p.hidden, Texture = tex,
                        };
                        if (tex != null)
                        {
                            slot.TextureKey = KeyOf(tex, out var g, out var l, out var isAsset);
                            if (!res.RowIndex.TryGetValue(slot.TextureKey, out var row))
                            {
                                row = new CVTextureRow { Key = slot.TextureKey, Original = tex, Guid = g, LocalId = l, IsAsset = isAsset };
                                res.RowIndex.Add(row.Key, row);
                                res.Rows.Add(row);
                            }
                            row.Slots.Add(slot);
                        }
                        res.Slots.Add(slot);
                    }
                }
            }
            res.Rows.Sort((a, b) => string.Compare(a.Original.name, b.Original.name, StringComparison.OrdinalIgnoreCase));
            sw.Stop();
            res.ElapsedMs = sw.ElapsedMilliseconds;
            return res;
        }

        /// <summary>
        /// 差し替え可否の判定。次元タイプ不一致は Error（拒否）、
        /// 解像度・アスペクト比の違いは Warning（生成は可能）。
        /// width/height はこの判定を行う行（差し替え先が指定された行）だけで参照する。
        /// </summary>
        public static CVSeverity Validate(Texture original, IEnumerable<TextureDimension> propertyDims, Texture replacement, out string message)
        {
            message = "";
            if (replacement == null) return CVSeverity.None;
            if (original != null && replacement.dimension != original.dimension)
            {
                message = $"次元タイプ不一致：元 {DimName(original.dimension)} / 差し替え先 {DimName(replacement.dimension)}（差し替え不可）";
                return CVSeverity.Error;
            }
            foreach (var pd in propertyDims)
            {
                if (pd != TextureDimension.Any && pd != TextureDimension.Unknown && pd != replacement.dimension)
                {
                    message = $"次元タイプ不一致：プロパティは {DimName(pd)} / 差し替え先 {DimName(replacement.dimension)}（差し替え不可）";
                    return CVSeverity.Error;
                }
            }
            if (original != null)
            {
                int ow = original.width, oh = original.height, rw = replacement.width, rh = replacement.height;
                bool aspect = (long)ow * rh != (long)oh * rw;
                if (aspect)
                {
                    message = $"アスペクト比が異なります：{ow}x{oh} → {rw}x{rh}（生成は可能）";
                    return CVSeverity.Warning;
                }
                if (ow != rw || oh != rh)
                {
                    message = $"解像度が異なります：{ow}x{oh} → {rw}x{rh}（生成は可能）";
                    return CVSeverity.Warning;
                }
            }
            message = "OK";
            return CVSeverity.Ok;
        }

        public static string DimName(TextureDimension d)
        {
            switch (d)
            {
                case TextureDimension.Tex2D: return "Texture2D";
                case TextureDimension.Tex3D: return "Texture3D";
                case TextureDimension.Cube: return "Cube";
                case TextureDimension.Tex2DArray: return "Texture2DArray";
                case TextureDimension.CubeArray: return "CubeArray";
                case TextureDimension.Any: return "Any";
                default: return d.ToString();
            }
        }

        /// <summary>root からの相対パス（root 自身は ""）。root が null なら完全パス。</summary>
        public static string PathOf(Transform t, Transform root)
        {
            var parts = new List<string>();
            while (t != null && t != root)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        public static int RendererComponentIndex(Renderer r)
        {
            var list = r.GetComponents<Renderer>();
            return Array.IndexOf(list, r);
        }
    }
}
