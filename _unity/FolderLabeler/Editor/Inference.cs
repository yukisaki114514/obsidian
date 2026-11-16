using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace FolderLabeler
{
    internal sealed class Evidence
    {
        public Label label;
        public Strength strength;
        public string text;
    }

    /// <summary>
    /// 第 2 層: フォルダの中身からのラベル推定。読み取り専用。
    ///   ・Prefab はまずテキスト (YAML) として読み、Descriptor らしい物だけを LoadAssetAtPath で確認する
    ///   ・Instantiate しない。シーンに置かない。SetDirty しない
    /// </summary>
    internal static class Inference
    {
        const long MaxTextBytes = 32L * 1024 * 1024;

        static readonly string[] StructuralNames =
        {
            "texture", "textures", "tex", "material", "materials", "mat", "mats", "prefab", "prefabs",
            "fbx", "model", "models", "mesh", "meshes", "animation", "animations", "anim", "anims", "animator",
            "controller", "controllers", "editor", "runtime", "script", "scripts", "shader", "shaders",
            "resources", "doc", "docs", "document", "documents", "sample", "samples", "scene", "scenes",
            "sound", "sounds", "audio", "menu", "menus", "expression", "expressions", "parameter", "parameters",
            "psd", "image", "images", "icon", "icons", "plugins", "src", "lib", "unitypackage", "blend",
            "画像", "テクスチャ", "マテリアル", "プレハブ", "アニメーション", "説明書", "モデル", "メッシュ",
        };

        // フォルダ名・ファイル名のキーワード (弱シグナル)。単語境界の無い日本語は部分一致。
        static readonly KeyValuePair<Label, string>[] Keywords =
        {
            K(Label.Outfit, @"outfit|costume|cloth|clothes|clothing|dress|wear|shirt|skirt|jacket|hoodie|parka|coat|uniform|swimsuit|bikini|kimono|yukata|pants|shorts|boots|shoes|sneaker|socks|tights|衣装|服|ドレス|シャツ|スカート|ジャケット|パーカー|コート|制服|水着|着物|浴衣|ワンピ|靴|ブーツ|ソックス|タイツ|ニーハイ"),
            K(Label.Hair, @"hair|ponytail|twintail|bangs|wig|髪|ヘア|ポニー|ツイン|前髪|後ろ髪|ウィッグ"),
            K(Label.Accessory, @"accessor|acce|ring|earring|piercing|necklace|choker|glasses|megane|hat|cap|ribbon|bag|mask|headphone|wing|tail|horn|halo|アクセ|指輪|ピアス|イヤリング|ネックレス|チョーカー|眼鏡|メガネ|帽子|リボン|バッグ|マスク|ヘッドホン|翼|羽|しっぽ|尻尾|角|ヘイロー|小物"),
            K(Label.TextureMaterial, @"texture|material|matcap|normal\s*map|テクスチャ|マテリアル|カラバリ|色変え"),
            K(Label.Motion, @"motion|dance|pose|afk|idle|walk|emote|locomotion|モーション|ダンス|ポーズ|待機"),
            K(Label.GestureExpression, @"gesture|face|expression|emotion|hand|fist|ジェスチャー|表情|ハンド|フェイス"),
            K(Label.Tool, @"tool|editor|utility|utils|extension|ツール|拡張"),
            K(Label.Shader, @"shader|シェーダー"),
            K(Label.WorldGimmick, @"world|gimmick|udon|ワールド|ギミック"),
            K(Label.Avatar, @"avatar|アバター"),
        };

        static KeyValuePair<Label, string> K(Label l, string pattern) { return new KeyValuePair<Label, string>(l, pattern); }

        internal sealed class Context
        {
            public string assetPath;            // "Assets/..."
            public string folderName;
            public IList<WalkedFile> files;
            public RegistrySettings settings;
            public bool sdkPresent;
            public Dictionary<string, bool> prefabDescriptorCache;  // guid -> その Prefab 自体に Descriptor の記述があるか
        }

        internal sealed class Output
        {
            public readonly List<Evidence> evidence = new List<Evidence>();
            public readonly List<string> notes = new List<string>();
            public string avatarPrefab = "";
            public string undeterminedReason;
        }

        public static Output Run(Context ctx)
        {
            var o = new Output();
            var counts = new Dictionary<string, int>();
            int content = 0;
            foreach (var f in ctx.files)
            {
                int c;
                counts.TryGetValue(f.category, out c);
                counts[f.category] = c + 1;
                if (FileCategories.IsContent(f.category)) content++;
            }
            Func<string, int> n = cat => { int v; return counts.TryGetValue(cat, out v) ? v : 0; };

            if (ctx.files.Count == 0)
            {
                o.undeterminedReason = "フォルダが空です (判定材料がありません)";
                return o;
            }
            if (content == 0)
            {
                o.undeterminedReason = "説明書・圧縮ファイルしか無く、判定材料がありません";
                return o;
            }

            // 主体の種類
            string top = null;
            int topCount = 0;
            foreach (var kv in counts)
            {
                if (!FileCategories.IsContent(kv.Key)) continue;
                if (kv.Value > topCount) { top = kv.Key; topCount = kv.Value; }
            }
            float topShare = (float)topCount / content;
            o.notes.Add("主な中身: " + FileCategories.Name(top) + " " + topCount + " / " + content + " 件");
            if (content >= 20 && topShare < 0.35f) o.notes.Add("中身の種類が混在しています (主体と言える種類がありません)");

            CheckAvatar(ctx, o, n(FileCategories.Prefab));
            CheckCode(ctx, o, n(FileCategories.Code), content);
            CheckShader(o, n(FileCategories.Shader), n(FileCategories.Image), n(FileCategories.Model), content);
            CheckPackageJson(ctx, o);
            bool hasAvatarStrong = o.evidence.Exists(e => e.label == Label.Avatar && e.strength >= Strength.Mid);
            if (!hasAvatarStrong) CheckModels(ctx, o, n(FileCategories.Model));
            CheckAnimation(ctx, o, n(FileCategories.Animation) + n(FileCategories.Animator), content);
            CheckImages(o, n(FileCategories.Image) + n(FileCategories.Material), n(FileCategories.Model), n(FileCategories.Prefab), content);
            CheckWorld(ctx, o, n(FileCategories.Scene), n(FileCategories.Prefab));
            if (n(FileCategories.Audio) * 2 > content)
                o.evidence.Add(new Evidence { label = Label.Other, strength = Strength.Weak, text = "音声ファイルが中身の大半です" });

            // フォルダ名キーワード (弱)
            foreach (var kw in Keywords)
            {
                if (Regex.IsMatch(ctx.folderName, kw.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    o.evidence.Add(new Evidence { label = kw.Key, strength = Strength.Weak, text = "フォルダ名にキーワード (" + Labels.Name(kw.Key) + ")" });
            }

            if (o.evidence.Count == 0)
                o.undeterminedReason = "決め手になる中身が見つかりませんでした (主な中身: " + FileCategories.Name(top) + ")";
            return o;
        }

        // ------------------------------------------------------------------
        // アバター本体 (VRC Avatar Descriptor)
        // ------------------------------------------------------------------

        static void CheckAvatar(Context ctx, Output o, int prefabCount)
        {
            if (prefabCount == 0) return;
            var prefabs = new List<WalkedFile>();
            foreach (var f in ctx.files) if (f.category == FileCategories.Prefab) prefabs.Add(f);
            // 浅い階層・大きいファイルを優先 (アバターの Prefab は大きく、フォルダの上の方に置かれやすい)
            prefabs.Sort((a, b) =>
            {
                int da = Depth(a.rel), db = Depth(b.rel);
                if (da != db) return da.CompareTo(db);
                return b.size.CompareTo(a.size);
            });

            int limit = Math.Max(1, ctx.settings.prefabTextLimit);
            int checkedText = 0, binary = 0;
            var own = new List<WalkedFile>();           // その Prefab 自体に Descriptor の記述がある
            var viaExternal = new List<string>();       // 他フォルダのアバター Prefab を元にした Variant
            var viaInternal = new List<WalkedFile>();
            var binaries = new List<WalkedFile>();
            var folderGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in ctx.files) if (!string.IsNullOrEmpty(f.guid)) folderGuids.Add(f.guid);

            foreach (var p in prefabs)
            {
                if (checkedText >= limit) break;
                if (p.size > MaxTextBytes) continue;
                checkedText++;
                string text;
                try { text = File.ReadAllText(p.full); }
                catch (Exception e)
                {
                    o.notes.Add("Prefab を読めませんでした: " + p.rel + " (" + FolderWalker.Describe(e) + ")");
                    continue;
                }
                if (!text.StartsWith("%YAML", StringComparison.Ordinal))
                {
                    binary++;
                    binaries.Add(p);
                    continue;
                }
                if (HasDescriptorFields(text))
                {
                    own.Add(p);
                    if (!string.IsNullOrEmpty(p.guid)) ctx.prefabDescriptorCache[p.guid] = true;
                    continue;
                }
                foreach (string src in SourcePrefabGuids(text))
                {
                    if (!BaseHasDescriptor(ctx, src, 0)) continue;
                    if (folderGuids.Contains(src)) viaInternal.Add(p);
                    else viaExternal.Add(p.rel);
                    break;
                }
            }
            if (prefabs.Count > checkedText)
                o.notes.Add("Prefab " + prefabs.Count + " 件のうち " + checkedText + " 件を確認しました (上限 " + limit + " 件)");
            if (binary > 0)
                o.notes.Add("バイナリ形式の Prefab が " + binary + " 件あります (テキストでの確認ができないため、SDK がある場合のみ読み込んで確認します)");

            var candidates = new List<WalkedFile>(own);
            candidates.AddRange(viaInternal);
            if (ctx.sdkPresent) candidates.AddRange(binaries);

            if (ctx.sdkPresent && candidates.Count > 0)
            {
                int loadLimit = Math.Max(1, ctx.settings.prefabLoadLimit);
                int loaded = 0;
                string confirmed = null;
                foreach (var p in candidates)
                {
                    if (loaded >= loadLimit) break;
                    loaded++;
                    string assetPath = ctx.assetPath + "/" + p.rel;
                    try
                    {
                        var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                        if (go == null) continue;
                        foreach (var c in go.GetComponentsInChildren<Component>(true))
                        {
                            if (VrcSdk.IsDescriptor(c)) { confirmed = p.rel; break; }
                        }
                    }
                    catch (Exception e)
                    {
                        o.notes.Add("Prefab を読み込めませんでした: " + p.rel + " (" + e.Message + ")");
                    }
                    if (confirmed != null) break;
                }
                if (confirmed != null)
                {
                    o.avatarPrefab = confirmed;
                    o.evidence.Add(new Evidence { label = Label.Avatar, strength = Strength.Strong, text = "VRC Avatar Descriptor 付きの Prefab: " + confirmed });
                    return;
                }
                if (own.Count > 0)
                {
                    o.avatarPrefab = own[0].rel;
                    o.evidence.Add(new Evidence { label = Label.Avatar, strength = Strength.Mid, text = "Avatar Descriptor らしい記述のある Prefab (読み込みでは型を確認できず): " + own[0].rel });
                }
            }
            else if (own.Count > 0 || viaInternal.Count > 0)
            {
                string rel = own.Count > 0 ? own[0].rel : viaInternal[0].rel;
                o.avatarPrefab = rel;
                o.evidence.Add(new Evidence { label = Label.Avatar, strength = Strength.Mid, text = "Avatar Descriptor らしい記述のある Prefab (SDK が無いため型は未確認): " + rel });
            }

            if (viaExternal.Count > 0 && o.avatarPrefab.Length == 0)
            {
                o.evidence.Add(new Evidence
                {
                    label = Label.Outfit,
                    strength = Strength.Mid,
                    text = "ほかのフォルダのアバターを元にした Prefab Variant があります (着せ替え済み Prefab の可能性): " + viaExternal[0],
                });
            }
        }

        static bool HasDescriptorFields(string text)
        {
            int hits = 0;
            foreach (string field in VrcSdk.DescriptorYamlFields)
            {
                if (text.IndexOf("  " + field, StringComparison.Ordinal) >= 0) hits++;
            }
            return hits >= VrcSdk.DescriptorYamlMinHits;
        }

        static readonly Regex SourcePrefabRegex = new Regex(@"m_SourcePrefab:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-fA-F]{32})", RegexOptions.CultureInvariant);

        static IEnumerable<string> SourcePrefabGuids(string text)
        {
            var seen = new HashSet<string>();
            foreach (Match m in SourcePrefabRegex.Matches(text))
            {
                string g = m.Groups[1].Value.ToLowerInvariant();
                if (seen.Add(g)) yield return g;
            }
        }

        /// <summary>元 Prefab (Variant の親) をたどって Descriptor の記述があるか。テキストだけで判断する。</summary>
        static bool BaseHasDescriptor(Context ctx, string guid, int depth)
        {
            bool cached;
            if (ctx.prefabDescriptorCache.TryGetValue(guid, out cached)) return cached;
            ctx.prefabDescriptorCache[guid] = false;    // 循環参照の防止
            if (depth > 6) return false;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return false;
            string full = Path.Combine(Path.GetDirectoryName(Application.dataPath), path);
            bool result = false;
            try
            {
                var info = new FileInfo(full);
                if (info.Exists && info.Length <= MaxTextBytes)
                {
                    string text = File.ReadAllText(full);
                    if (text.StartsWith("%YAML", StringComparison.Ordinal))
                    {
                        if (HasDescriptorFields(text)) result = true;
                        else
                        {
                            foreach (string src in SourcePrefabGuids(text))
                            {
                                if (BaseHasDescriptor(ctx, src, depth + 1)) { result = true; break; }
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            ctx.prefabDescriptorCache[guid] = result;
            return result;
        }

        // ------------------------------------------------------------------
        // ツール / シェーダー / パッケージ
        // ------------------------------------------------------------------

        static void CheckCode(Context ctx, Output o, int codeCount, int content)
        {
            if (codeCount == 0) return;
            int editorCode = 0;
            bool editorAsmdef = false;
            bool udon = false;
            int sampled = 0;
            foreach (var f in ctx.files)
            {
                if (f.category != FileCategories.Code) continue;
                if (InEditorFolder(f.rel)) editorCode++;
                string ext = Path.GetExtension(f.rel).ToLowerInvariant();
                if (ext == ".asmdef" && f.size < 64 * 1024)
                {
                    try
                    {
                        string text = File.ReadAllText(f.full);
                        if (Regex.IsMatch(text, "\"includePlatforms\"\\s*:\\s*\\[\\s*\"Editor\"\\s*\\]")) editorAsmdef = true;
                    }
                    catch (Exception) { }
                }
                if (ext == ".cs" && sampled < 30 && f.size < 512 * 1024)
                {
                    sampled++;
                    try
                    {
                        string text = File.ReadAllText(f.full);
                        if (text.IndexOf("UdonSharpBehaviour", StringComparison.Ordinal) >= 0) udon = true;
                    }
                    catch (Exception) { }
                }
            }
            float share = (float)codeCount / content;
            bool editorMajor = editorAsmdef || editorCode * 2 >= codeCount;
            if (udon)
            {
                o.evidence.Add(new Evidence { label = Label.WorldGimmick, strength = Strength.Mid, text = "UdonSharp のスクリプトを含みます" });
            }
            if (share >= 0.5f && editorMajor && !udon)
            {
                o.evidence.Add(new Evidence
                {
                    label = Label.Tool,
                    strength = Strength.Strong,
                    text = "Editor 用のスクリプト・DLL が中身の大半です (" + codeCount + " / " + content + " 件"
                        + (editorAsmdef ? "、Editor 専用 asmdef あり" : "") + ")",
                });
            }
            else if (share >= 0.5f && !udon)
            {
                o.evidence.Add(new Evidence { label = Label.Tool, strength = Strength.Mid, text = "スクリプト・DLL が中身の大半です (Editor 専用かは不明)" });
            }
            else if (editorCode > 0 || editorAsmdef)
            {
                o.evidence.Add(new Evidence { label = Label.Tool, strength = Strength.Weak, text = "Editor スクリプトを含みます (" + editorCode + " 件)" });
            }
        }

        static bool InEditorFolder(string rel)
        {
            foreach (string seg in rel.Split('/'))
            {
                if (string.Equals(seg, "Editor", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static void CheckShader(Output o, int shaders, int images, int models, int content)
        {
            if (shaders == 0) return;
            float share = (float)shaders / content;
            if (share >= 0.3f || (shaders >= 5 && shaders >= images && shaders >= models))
                o.evidence.Add(new Evidence { label = Label.Shader, strength = Strength.Strong, text = "シェーダーファイルが主体です (" + shaders + " / " + content + " 件)" });
            else
                o.evidence.Add(new Evidence { label = Label.Shader, strength = Strength.Weak, text = "シェーダーファイルを含みます (" + shaders + " 件)" });
        }

        static void CheckPackageJson(Context ctx, Output o)
        {
            foreach (var f in ctx.files)
            {
                if (f.rel != "package.json") continue;
                try
                {
                    string text = File.ReadAllText(f.full);
                    var m = Regex.Match(text, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                    string name = m.Success ? m.Groups[1].Value : "(名前なし)";
                    bool vpm = text.IndexOf("\"vpmDependencies\"", StringComparison.Ordinal) >= 0
                        || text.IndexOf("\"vrchatVersion\"", StringComparison.Ordinal) >= 0;
                    o.evidence.Add(new Evidence
                    {
                        label = Label.Tool,
                        strength = Strength.Mid,
                        text = (vpm ? "VPM パッケージの定義" : "パッケージ定義") + " (package.json: " + name + ") があります",
                    });
                }
                catch (Exception e)
                {
                    o.notes.Add("package.json を読めませんでした (" + FolderWalker.Describe(e) + ")");
                }
                return;
            }
        }

        // ------------------------------------------------------------------
        // 衣装 / 髪型 / 小物 (Skinned Mesh)
        // ------------------------------------------------------------------

        static void CheckModels(Context ctx, Output o, int modelCount)
        {
            if (modelCount == 0) return;
            var models = new List<WalkedFile>();
            foreach (var f in ctx.files)
            {
                if (f.category != FileCategories.Model) continue;
                string ext = Path.GetExtension(f.rel).ToLowerInvariant();
                if (ext == ".fbx" || ext == ".obj" || ext == ".dae" || ext == ".vrm" || ext == ".glb" || ext == ".gltf") models.Add(f);
            }
            if (models.Count == 0)
            {
                o.notes.Add("Unity で読み込めない形式のモデル (blend 等) だけがあります");
                return;
            }
            models.Sort((a, b) => b.size.CompareTo(a.size));

            int limit = Math.Max(1, ctx.settings.modelLoadLimit);
            bool skinned = false;
            int loaded = 0;
            var names = new StringBuilder();
            names.Append(ctx.folderName).Append(' ');
            foreach (var m in models) names.Append(Path.GetFileNameWithoutExtension(m.rel)).Append(' ');
            foreach (var f in ctx.files)
            {
                if (f.category == FileCategories.Prefab) names.Append(Path.GetFileNameWithoutExtension(f.rel)).Append(' ');
            }

            foreach (var m in models)
            {
                if (loaded >= limit) break;
                loaded++;
                try
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(ctx.assetPath + "/" + m.rel);
                    if (go == null) continue;
                    foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        skinned = true;
                        names.Append(smr.name).Append(' ');
                    }
                }
                catch (Exception e)
                {
                    o.notes.Add("モデルを読み込めませんでした: " + m.rel + " (" + e.Message + ")");
                }
            }
            if (models.Count > loaded) o.notes.Add("モデル " + models.Count + " 件のうち " + loaded + " 件を読み込んで確認しました");

            if (!skinned)
            {
                o.evidence.Add(new Evidence { label = Label.Accessory, strength = Strength.Weak, text = "ボーン付きでないモデルがあります (小物・ワールド素材の可能性)" });
                return;
            }

            string all = names.ToString();
            int hair = CountMatches(all, Label.Hair);
            int acc = CountMatches(all, Label.Accessory);
            int outfit = CountMatches(all, Label.Outfit);
            Label pick = Label.Outfit;
            string why;
            if (hair > outfit && hair >= acc) { pick = Label.Hair; why = "名前に髪型のキーワード"; }
            else if (acc > outfit && acc > hair) { pick = Label.Accessory; why = "名前に小物のキーワード"; }
            else if (outfit > 0) why = "名前に衣装のキーワード";
            else why = "名前から種類を決められないため衣装として推定 (髪型・小物の可能性もあります)";
            o.evidence.Add(new Evidence
            {
                label = pick,
                strength = Strength.Mid,
                text = "Skinned Mesh を持つモデルがあり、Avatar Descriptor はありません。" + why,
            });
        }

        static int CountMatches(string text, Label label)
        {
            foreach (var kw in Keywords)
            {
                if (kw.Key != label) continue;
                return Regex.Matches(text, kw.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count;
            }
            return 0;
        }

        // ------------------------------------------------------------------
        // アニメーション / 画像 / ワールド
        // ------------------------------------------------------------------

        static void CheckAnimation(Context ctx, Output o, int animCount, int content)
        {
            if (animCount == 0) return;
            float share = (float)animCount / content;
            if (share < 0.4f) return;

            int face = 0, hand = 0, body = 0, sampled = 0;
            foreach (var f in ctx.files)
            {
                if (f.category != FileCategories.Animation || sampled >= 20 || f.size > 4 * 1024 * 1024) continue;
                sampled++;
                try
                {
                    string text = File.ReadAllText(f.full);
                    if (!text.StartsWith("%YAML", StringComparison.Ordinal)) continue;
                    bool hasFace = text.IndexOf("attribute: blendShape.", StringComparison.Ordinal) >= 0;
                    bool hasHand = Regex.IsMatch(text, @"attribute: (Left|Right)Hand\.");
                    bool hasBody = Regex.IsMatch(text, @"attribute: (Spine|Chest|Neck|Head|Left Upper|Right Upper|Left Lower|Right Lower|RootT|RootQ|Motion)[ .]");
                    if (hasFace) face++;
                    if (hasHand && !hasBody) hand++;
                    if (hasBody) body++;
                }
                catch (Exception) { }
            }
            if (face + hand > body)
            {
                o.evidence.Add(new Evidence
                {
                    label = Label.GestureExpression,
                    strength = Strength.Mid,
                    text = "アニメーションが主体で、表情 (BlendShape) や指の動きが中心です (確認 " + sampled + " 件: 表情 " + face + " / 指 " + hand + " / 全身 " + body + ")",
                });
            }
            else
            {
                o.evidence.Add(new Evidence
                {
                    label = Label.Motion,
                    strength = Strength.Mid,
                    text = "アニメーションが主体です (" + animCount + " / " + content + " 件)",
                });
            }
        }

        static void CheckImages(Output o, int images, int models, int prefabs, int content)
        {
            if (images == 0) return;
            float share = (float)images / content;
            if (share >= 0.6f && models == 0)
                o.evidence.Add(new Evidence { label = Label.TextureMaterial, strength = Strength.Mid, text = "画像・マテリアルが中身の大半で、モデルがありません (" + images + " / " + content + " 件)" });
            else if (share >= 0.4f && models == 0 && prefabs == 0)
                o.evidence.Add(new Evidence { label = Label.TextureMaterial, strength = Strength.Weak, text = "画像・マテリアルが多めです" });
        }

        static void CheckWorld(Context ctx, Output o, int scenes, int prefabs)
        {
            if (scenes > 0)
                o.evidence.Add(new Evidence { label = Label.WorldGimmick, strength = Strength.Weak, text = "シーンファイルを含みます (" + scenes + " 件)" });

            // アバターギミック: Expression Menu / Parameters を持つ
            int sampled = 0;
            bool menu = false;
            foreach (var f in ctx.files)
            {
                if (sampled >= 30) break;
                if (!f.rel.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) || f.size > 1024 * 1024) continue;
                sampled++;
                try
                {
                    string text = File.ReadAllText(f.full);
                    if (CountFields(text, VrcSdk.ExpressionMenuYamlFields) >= 3 || CountFields(text, VrcSdk.ExpressionParamsYamlFields) >= 3)
                    {
                        menu = true;
                        break;
                    }
                }
                catch (Exception) { }
            }
            if (menu && prefabs > 0)
                o.evidence.Add(new Evidence { label = Label.WorldGimmick, strength = Strength.Weak, text = "Expression Menu / Parameters を含みます (アバターギミックの可能性)" });
        }

        static int CountFields(string text, string[] fields)
        {
            int hits = 0;
            foreach (string f in fields) if (text.IndexOf(f, StringComparison.Ordinal) >= 0) hits++;
            return hits;
        }

        static int Depth(string rel)
        {
            int d = 0;
            foreach (char c in rel) if (c == '/') d++;
            return d;
        }

        // ------------------------------------------------------------------
        // 走査単位 (グループ判定)
        // ------------------------------------------------------------------

        /// <summary>
        /// 作者名フォルダのように「複数の製品フォルダをまとめているだけ」のフォルダか。
        /// そうなら子フォルダを判定単位にする。
        /// </summary>
        public static bool LooksLikeGroup(string fullPath, string name, out string reason)
        {
            reason = null;
            string[] dirs, files;
            try
            {
                dirs = Directory.GetDirectories(fullPath);
                files = Directory.GetFiles(fullPath);
            }
            catch (Exception)
            {
                return false;
            }
            var subdirs = new List<string>();
            foreach (string d in dirs)
            {
                string n = Path.GetFileName(d);
                if (!FileCategories.IsIgnoredName(n, true)) subdirs.Add(n);
            }
            if (subdirs.Count == 0) return false;

            if (name == "Plugins" || name == "Resources" || name == "StreamingAssets")
            {
                reason = "Unity の特殊フォルダ (" + name + ") のため、中のフォルダごとに判定します";
                return true;
            }
            if (subdirs.Count < 2) return false;

            int directContent = 0;
            foreach (string f in files)
            {
                string n = Path.GetFileName(f);
                if (FileCategories.IsIgnoredName(n, false)) continue;
                if (n == "package.json" || n.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)) return false;
                if (FileCategories.IsContent(FileCategories.Of(n))) directContent++;
            }
            if (directContent > 3) return false;

            int structural = 0;
            foreach (string d in subdirs)
            {
                string key = Regex.Replace(d.ToLowerInvariant(), @"^[_\-\d\s.]+|[_\-\d\s.]+$", "");
                if (Array.IndexOf(StructuralNames, key) >= 0) structural++;
            }
            if (structural * 2 >= subdirs.Count) return false;

            reason = "複数のフォルダをまとめたフォルダ (作者名フォルダ等) と判断し、中のフォルダごとに判定します";
            return true;
        }

        // ------------------------------------------------------------------
        // 根拠からラベルと信頼度を決める
        // ------------------------------------------------------------------

        public static LabelResult Decide(Output o)
        {
            var r = new LabelResult { layer = Layers.Inference };
            r.notes.AddRange(o.notes);
            foreach (var e in o.evidence)
                r.signals.Add(new Signal { strength = Labels.StrengthKey(e.strength), label = Labels.Key(e.label), text = e.text });

            if (o.undeterminedReason != null || o.evidence.Count == 0)
            {
                r.label = Labels.Key(Label.Other);
                r.confidence = Labels.ConfidenceKey(Confidence.Low);
                r.layer = Layers.Undetermined;
                r.undetermined = true;
                r.signals.Add(new Signal { strength = "info", text = "根拠不足: " + (o.undeterminedReason ?? "判定材料がありません") });
                return r;
            }

            var score = new int[Labels.Count];
            var strongest = new int[Labels.Count];
            for (int i = 0; i < strongest.Length; i++) strongest[i] = -1;
            foreach (var e in o.evidence)
            {
                int w = e.strength == Strength.Strong ? 10 : e.strength == Strength.Mid ? 4 : 1;
                score[(int)e.label] += w;
                strongest[(int)e.label] = Math.Max(strongest[(int)e.label], (int)e.strength);
            }
            int best = -1;
            for (int i = 0; i < Labels.Count; i++)
            {
                if (strongest[i] < 0) continue;
                if (best < 0 || strongest[i] > strongest[best] || (strongest[i] == strongest[best] && score[i] > score[best])) best = i;
            }
            var bestStrength = (Strength)strongest[best];
            int strongLabels = 0, sameLevel = 0;
            for (int i = 0; i < Labels.Count; i++)
            {
                if (strongest[i] == (int)Strength.Strong) strongLabels++;
                if (i != best && strongest[i] == strongest[best] && score[i] == score[best]) sameLevel++;
            }

            Confidence conf;
            if (bestStrength == Strength.Strong) conf = strongLabels > 1 ? Confidence.Mid : Confidence.High;
            else if (bestStrength == Strength.Mid) conf = Confidence.Mid;
            else conf = Confidence.Low;
            if (strongLabels > 1) r.notes.Add("強いシグナルが複数のラベルを指しているため、信頼度を下げています");
            if (sameLevel > 0 && conf > Confidence.Low && strongLabels <= 1)
            {
                conf = (Confidence)((int)conf - 1);
                r.notes.Add("同じ強さの候補が複数あるため、信頼度を下げています");
            }

            r.label = Labels.Key((Label)best);
            r.confidence = Labels.ConfidenceKey(conf);
            var alts = new List<LabelScore>();
            for (int i = 0; i < Labels.Count; i++)
            {
                if (strongest[i] < 0) continue;
                alts.Add(new LabelScore { label = Labels.Key((Label)i), score = score[i], strongest = Labels.StrengthKey((Strength)strongest[i]) });
            }
            alts.Sort((a, b) => b.score.CompareTo(a.score));
            r.alternatives = alts;
            return r;
        }
    }
}
