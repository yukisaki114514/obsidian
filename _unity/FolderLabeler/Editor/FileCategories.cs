using System;
using System.Collections.Generic;
using System.IO;

namespace FolderLabeler
{
    /// <summary>拡張子からファイルの種類を決める。</summary>
    internal static class FileCategories
    {
        public const string Model = "model";
        public const string Image = "image";
        public const string Material = "material";
        public const string Animation = "animation";
        public const string Animator = "animator";
        public const string Prefab = "prefab";
        public const string Scene = "scene";
        public const string Shader = "shader";
        public const string Code = "code";
        public const string Audio = "audio";
        public const string Video = "video";
        public const string Document = "document";
        public const string Asset = "asset";
        public const string Package = "package";
        public const string Other = "other";

        public static readonly string[] Order =
        {
            Prefab, Model, Image, Material, Animation, Animator, Shader, Code, Scene, Audio, Video, Asset, Package, Document, Other,
        };

        static readonly Dictionary<string, string> ByExt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".fbx", Model }, { ".obj", Model }, { ".blend", Model }, { ".dae", Model }, { ".3ds", Model },
            { ".max", Model }, { ".ma", Model }, { ".mb", Model }, { ".vrm", Model }, { ".gltf", Model }, { ".glb", Model },
            { ".png", Image }, { ".jpg", Image }, { ".jpeg", Image }, { ".tga", Image }, { ".psd", Image },
            { ".tif", Image }, { ".tiff", Image }, { ".bmp", Image }, { ".exr", Image }, { ".hdr", Image },
            { ".gif", Image }, { ".clip", Image }, { ".webp", Image },
            { ".mat", Material }, { ".physicmaterial", Material }, { ".cubemap", Material },
            { ".anim", Animation },
            { ".controller", Animator }, { ".overridecontroller", Animator }, { ".mask", Animator }, { ".playable", Animator },
            { ".prefab", Prefab },
            { ".unity", Scene }, { ".lighting", Scene },
            { ".shader", Shader }, { ".cginc", Shader }, { ".hlsl", Shader }, { ".compute", Shader },
            { ".shadergraph", Shader }, { ".shadersubgraph", Shader }, { ".glsl", Shader }, { ".shadervariants", Shader },
            { ".cs", Code }, { ".dll", Code }, { ".asmdef", Code }, { ".asmref", Code }, { ".so", Code }, { ".bundle", Code },
            { ".wav", Audio }, { ".mp3", Audio }, { ".ogg", Audio }, { ".aif", Audio }, { ".aiff", Audio }, { ".flac", Audio },
            { ".mp4", Video }, { ".mov", Video }, { ".webm", Video }, { ".avi", Video },
            { ".txt", Document }, { ".md", Document }, { ".pdf", Document }, { ".html", Document }, { ".htm", Document },
            { ".url", Document }, { ".rtf", Document }, { ".docx", Document },
            { ".asset", Asset }, { ".json", Asset }, { ".bytes", Asset }, { ".fontsettings", Asset },
            { ".ttf", Asset }, { ".otf", Asset }, { ".spriteatlas", Asset },
            { ".unitypackage", Package }, { ".zip", Package }, { ".7z", Package },
        };

        public static string Of(string fileName)
        {
            string ext = Path.GetExtension(fileName);
            string cat;
            if (!string.IsNullOrEmpty(ext) && ByExt.TryGetValue(ext, out cat)) return cat;
            return Other;
        }

        public static string Name(string category)
        {
            switch (category)
            {
                case Model: return "3D モデル";
                case Image: return "画像";
                case Material: return "マテリアル";
                case Animation: return "アニメーション";
                case Animator: return "アニメーター";
                case Prefab: return "Prefab";
                case Scene: return "シーン";
                case Shader: return "シェーダー";
                case Code: return "スクリプト・DLL";
                case Audio: return "音声";
                case Video: return "動画";
                case Document: return "説明書・テキスト";
                case Asset: return "その他のアセット";
                case Package: return "パッケージ・圧縮ファイル";
                default: return "その他";
            }
        }

        /// <summary>判定の材料に数えない種類 (説明書など)。</summary>
        public static bool IsContent(string category)
        {
            return category != Document && category != Package;
        }

        /// <summary>Unity が無視する名前 (隠しファイル・"~" で終わるフォルダ等) と OS のゴミファイル。</summary>
        public static bool IsIgnoredName(string name, bool isDirectory)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (name[0] == '.') return true;
            if (name.EndsWith("~", StringComparison.Ordinal)) return true;
            if (isDirectory)
            {
                return string.Equals(name, "cvs", StringComparison.OrdinalIgnoreCase);
            }
            if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>シリアライズし直されてサイズが変わりやすい (= サイズ一致を求めない) テキストアセット。</summary>
        public static bool IsReserializable(string relPath)
        {
            string ext = Path.GetExtension(relPath).ToLowerInvariant();
            switch (ext)
            {
                case ".prefab": case ".mat": case ".anim": case ".controller": case ".overridecontroller":
                case ".asset": case ".unity": case ".mask": case ".physicmaterial": case ".playable":
                    return true;
                default:
                    return false;
            }
        }
    }
}
