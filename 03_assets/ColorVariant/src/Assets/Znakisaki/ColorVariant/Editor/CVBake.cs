// Znakisaki ColorVariant v0.2.0-preview
// テクスチャの「コピー」に色を焼き付ける（色相・彩度・明度、乗算色、グラデーション）。
// 元テクスチャのアセット・インポート設定は読み取りのみで、一切変更しない。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Znakisaki.ColorVariant
{
    [Serializable]
    internal sealed class CVGradKey
    {
        public Color color = Color.white;
        public float time;
    }

    internal enum CVGradDirection { Vertical = 0, Horizontal = 1, Luminance = 2 }
    internal enum CVGradBlend { Multiply = 0, Colorize = 1, Normal = 2 }

    /// <summary>焼き付け設定（JSON にもこの形で保存する）。</summary>
    [Serializable]
    internal sealed class CVBakeSettings
    {
        public bool enabled;
        public float hueShift;          // -180..180 度
        public float saturation = 1f;   // 0..2 倍
        public float brightness = 1f;   // 0..2 倍
        public Color tint = Color.white;
        public bool useGradient;
        public List<CVGradKey> gradientColors = new List<CVGradKey>
        {
            new CVGradKey { color = new Color(1f, 0.55f, 0.75f), time = 0f },
            new CVGradKey { color = new Color(0.45f, 0.55f, 1f), time = 1f },
        };
        public List<CVGradKey> gradientAlphas = new List<CVGradKey>
        {
            new CVGradKey { color = Color.white, time = 0f },
            new CVGradKey { color = Color.white, time = 1f },
        };
        public int direction = (int)CVGradDirection.Vertical;
        public int blend = (int)CVGradBlend.Colorize;
        public float strength = 1f;

        public bool HasEffect =>
            enabled && (Mathf.Abs(hueShift) > 0.001f || Mathf.Abs(saturation - 1f) > 0.001f || Mathf.Abs(brightness - 1f) > 0.001f
                        || tint != Color.white || (useGradient && strength > 0f));

        public CVBakeSettings Clone() => JsonUtility.FromJson<CVBakeSettings>(JsonUtility.ToJson(this));

        public Gradient ToGradient()
        {
            var g = new Gradient();
            var ck = new GradientColorKey[Mathf.Clamp(gradientColors.Count, 1, 8)];
            for (int i = 0; i < ck.Length; i++) ck[i] = gradientColors.Count > i ? new GradientColorKey(gradientColors[i].color, gradientColors[i].time) : new GradientColorKey(Color.white, 0);
            var ak = new GradientAlphaKey[Mathf.Clamp(gradientAlphas.Count, 1, 8)];
            for (int i = 0; i < ak.Length; i++) ak[i] = gradientAlphas.Count > i ? new GradientAlphaKey(gradientAlphas[i].color.a, gradientAlphas[i].time) : new GradientAlphaKey(1, 0);
            g.SetKeys(ck, ak);
            return g;
        }

        public void FromGradient(Gradient g)
        {
            gradientColors.Clear();
            foreach (var k in g.colorKeys) gradientColors.Add(new CVGradKey { color = k.color, time = k.time });
            gradientAlphas.Clear();
            foreach (var k in g.alphaKeys) gradientAlphas.Add(new CVGradKey { color = new Color(1, 1, 1, k.alpha), time = k.time });
        }

        public string Describe()
        {
            var parts = new List<string>();
            if (Mathf.Abs(hueShift) > 0.001f) parts.Add($"色相{hueShift:+0;-0}°");
            if (Mathf.Abs(saturation - 1f) > 0.001f) parts.Add($"彩度×{saturation:0.00}");
            if (Mathf.Abs(brightness - 1f) > 0.001f) parts.Add($"明度×{brightness:0.00}");
            if (tint != Color.white) parts.Add("乗算色#" + ColorUtility.ToHtmlStringRGB(tint));
            if (useGradient && strength > 0f) parts.Add($"グラデーション({DirName((CVGradDirection)direction)}/{BlendName((CVGradBlend)blend)}/{strength:0.00})");
            return parts.Count == 0 ? "変更なし" : string.Join(" ", parts);
        }

        public static string DirName(CVGradDirection d) => d == CVGradDirection.Vertical ? "縦" : d == CVGradDirection.Horizontal ? "横" : "明るさ";
        public static string BlendName(CVGradBlend b) => b == CVGradBlend.Multiply ? "乗算" : b == CVGradBlend.Colorize ? "色付け" : "通常";
    }

    internal static class CVBake
    {
        /// <summary>焼き付け可能か判定する。不可なら理由を返す。</summary>
        public static bool CanBake(Texture tex, out string reason)
        {
            reason = null;
            if (tex == null) { reason = "テクスチャがありません"; return false; }
            if (!(tex is Texture2D)) { reason = $"焼き付けは Texture2D のみ対応です（{CVScanner.DimName(tex.dimension)}）"; return false; }
            string path = AssetDatabase.GetAssetPath(tex);
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                if (ti.textureType == TextureImporterType.NormalMap) { reason = "ノーマルマップには焼き付けできません"; return false; }
                if (!ti.sRGBTexture) { reason = "カラーテクスチャではありません（sRGB がオフ：マスク・データ用テクスチャの可能性）"; return false; }
            }
            return true;
        }

        /// <summary>
        /// 元画像を読み込む。PNG/JPG はファイルを直接読み、元の解像度・無圧縮で扱う。
        /// それ以外は GPU でインポート済みテクスチャを読み出す（インポート時の解像度・圧縮が反映される）。
        /// 返したテクスチャは呼び出し側で破棄する。
        /// </summary>
        public static Texture2D LoadSource(Texture2D src, int maxSize, out string note)
        {
            note = null;
            string path = AssetDatabase.GetAssetPath(src);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (maxSize <= 0 && (ext == ".png" || ext == ".jpg" || ext == ".jpeg") && File.Exists(path))
            {
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (t.LoadImage(File.ReadAllBytes(path), false)) return t;
                UnityEngine.Object.DestroyImmediate(t);
            }
            int w = src.width, h = src.height;
            if (maxSize > 0)
            {
                float s = Mathf.Min(1f, maxSize / (float)Mathf.Max(w, h));
                w = Mathf.Max(1, Mathf.RoundToInt(w * s)); h = Mathf.Max(1, Mathf.RoundToInt(h * s));
            }
            else note = $"{(string.IsNullOrEmpty(ext) ? "非アセット" : ext)} 形式のため、インポート後のテクスチャ（{w}x{h}、圧縮後の画質）から作成しました";
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var t = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                t.Apply();
                return t;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>ピクセル配列に焼き付け処理を行う（アルファは保持）。</summary>
        public static void Process(Color32[] px, int w, int h, CVBakeSettings s, Action<float> progress = null)
        {
            bool hsv = Mathf.Abs(s.hueShift) > 0.001f || Mathf.Abs(s.saturation - 1f) > 0.001f || Mathf.Abs(s.brightness - 1f) > 0.001f;
            bool tint = s.tint != Color.white;
            bool grad = s.useGradient && s.strength > 0f;
            var lut = new Color[256];
            if (grad) { var g = s.ToGradient(); for (int i = 0; i < 256; i++) lut[i] = g.Evaluate(i / 255f); }
            float hue = s.hueShift / 360f;
            var dir = (CVGradDirection)s.direction;
            var blend = (CVGradBlend)s.blend;

            for (int y = 0; y < h; y++)
            {
                if (progress != null && (y & 63) == 0) progress(y / (float)h);
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    var p = px[row + x];
                    float r = p.r / 255f, gg = p.g / 255f, b = p.b / 255f;
                    if (hsv)
                    {
                        Color.RGBToHSV(new Color(r, gg, b), out float H, out float S, out float V);
                        H = Mathf.Repeat(H + hue, 1f);
                        S = Mathf.Clamp01(S * s.saturation);
                        V = Mathf.Clamp01(V * s.brightness);
                        var c = Color.HSVToRGB(H, S, V);
                        r = c.r; gg = c.g; b = c.b;
                    }
                    if (tint) { r *= s.tint.r; gg *= s.tint.g; b *= s.tint.b; }
                    if (grad)
                    {
                        float t;
                        if (dir == CVGradDirection.Vertical) t = h > 1 ? 1f - y / (float)(h - 1) : 0f;  // 画像の上端 = 0
                        else if (dir == CVGradDirection.Horizontal) t = w > 1 ? x / (float)(w - 1) : 0f;
                        else t = 0.299f * r + 0.587f * gg + 0.114f * b;
                        var gc = lut[Mathf.Clamp(Mathf.RoundToInt(t * 255f), 0, 255)];
                        float nr, ng, nb;
                        if (blend == CVGradBlend.Multiply) { nr = r * gc.r; ng = gg * gc.g; nb = b * gc.b; }
                        else if (blend == CVGradBlend.Colorize)
                        {
                            float lum = 0.299f * r + 0.587f * gg + 0.114f * b;
                            float gl = Mathf.Max(0.0001f, 0.299f * gc.r + 0.587f * gc.g + 0.114f * gc.b);
                            float k = lum / gl;
                            nr = Mathf.Clamp01(gc.r * k); ng = Mathf.Clamp01(gc.g * k); nb = Mathf.Clamp01(gc.b * k);
                        }
                        else { nr = gc.r; ng = gc.g; nb = gc.b; }
                        float a = s.strength * gc.a;
                        r += (nr - r) * a; gg += (ng - gg) * a; b += (nb - b) * a;
                    }
                    px[row + x] = new Color32((byte)Mathf.Clamp(Mathf.RoundToInt(r * 255f), 0, 255),
                                              (byte)Mathf.Clamp(Mathf.RoundToInt(gg * 255f), 0, 255),
                                              (byte)Mathf.Clamp(Mathf.RoundToInt(b * 255f), 0, 255), p.a);
                }
            }
            progress?.Invoke(1f);
        }

        /// <summary>プレビュー用（縮小）。返したテクスチャは呼び出し側で破棄する。</summary>
        public static Texture2D MakePreview(Texture2D src, CVBakeSettings s, int size)
        {
            var t = LoadSource(src, size, out _);
            var px = t.GetPixels32();
            Process(px, t.width, t.height, s);
            t.SetPixels32(px);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        /// <summary>
        /// コピーを作って焼き付け、PNG として folder に保存する。元のインポート設定を複製する。
        /// 戻り値は作成したテクスチャアセット。
        /// </summary>
        public static Texture2D BakeToAsset(Texture2D src, CVBakeSettings s, string folder, string baseName, List<string> log)
        {
            var sw = Stopwatch.StartNew();
            int srcDirty = EditorUtility.GetDirtyCount(src);
            Texture2D work = null;
            try
            {
                work = LoadSource(src, 0, out string note);
                if (note != null) log.Add("[焼き付け/情報] " + src.name + "：" + note);
                var px = work.GetPixels32();
                string title = "ColorVariant：焼き付け中 " + src.name;
                Process(px, work.width, work.height, s, p => EditorUtility.DisplayProgressBar(title, $"{work.width}x{work.height}", p));
                work.SetPixels32(px);
                byte[] png = work.EncodeToPNG();

                CVGenerator.EnsureFolder(folder);
                string path = CVGenerator.UniquePath(folder, baseName, ".png");
                File.WriteAllBytes(path, png);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                CopyImporterSettings(AssetDatabase.GetAssetPath(src), path);
                var result = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                sw.Stop();
                log.Add($"[作成] {path}  ← 元 \"{AssetDatabase.GetAssetPath(src)}\"（{s.Describe()}、{work.width}x{work.height}、{sw.ElapsedMilliseconds} ms）");
                if (EditorUtility.GetDirtyCount(src) != srcDirty)
                    log.Add($"[警告] 元テクスチャ \"{src.name}\" の dirty カウントが変化しました。保存せずに内容を確認してください。");
                return result;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (work != null) UnityEngine.Object.DestroyImmediate(work);
            }
        }

        static void CopyImporterSettings(string srcPath, string dstPath)
        {
            var si = AssetImporter.GetAtPath(srcPath) as TextureImporter;
            var di = AssetImporter.GetAtPath(dstPath) as TextureImporter;
            if (si == null || di == null) return;
            var settings = new TextureImporterSettings();
            si.ReadTextureSettings(settings);
            di.SetTextureSettings(settings);
            di.SetPlatformTextureSettings(si.GetDefaultPlatformTextureSettings());
            foreach (var platform in new[] { "Standalone", "Android", "iPhone" })
            {
                var ps = si.GetPlatformTextureSettings(platform);
                if (ps.overridden) di.SetPlatformTextureSettings(ps);
            }
            di.SaveAndReimport();
        }
    }
}
