using System.Collections.Generic;

namespace FolderLabeler
{
    /// <summary>
    /// 同梱の初期辞書 (読み取り専用)。名前 + 構成で既知のインフラ・ツールを見分ける。
    /// ユーザー辞書 (指紋による照合) より低い優先度で使う。
    /// 名前だけ一致して構成が合わないときは「中」にとどめる (同名の別物を高信頼にしないため)。
    /// </summary>
    internal static class BuiltinRules
    {
        static List<RegistryEntry> entries;

        public static List<RegistryEntry> Entries
        {
            get
            {
                if (entries == null) entries = Build();
                return entries;
            }
        }

        static List<RegistryEntry> Build()
        {
            var list = new List<RegistryEntry>();
            // インフラ (消すとプロジェクトが動かなくなる系)
            Add(list, "builtin.vrcsdk", "VRChat SDK", Label.Infra, new[] { @"^VRCSDK\d*$", @"^VRChat\s*Examples$" }, ".dll", ".cs");
            Add(list, "builtin.udon", "Udon", Label.Infra, new[] { @"^Udon$" }, ".dll", ".cs", ".asset");
            Add(list, "builtin.udonsharp", "UdonSharp", Label.Infra, new[] { @"^UdonSharp$" }, ".dll", ".cs");
            Add(list, "builtin.liltoon", "lilToon", Label.Infra, new[] { @"^lilToon$" }, ".shader", ".hlsl", ".lilblock");
            Add(list, "builtin.poiyomi", "Poiyomi Shaders", Label.Infra, new[] { @"^_?Poiyomi.*$" }, ".shader", ".cginc");
            Add(list, "builtin.thry", "Thry Editor", Label.Infra, new[] { @"^_?ThryEditor$" }, ".cs", ".dll");
            Add(list, "builtin.modularavatar", "Modular Avatar", Label.Infra, new[] { @"^nadena\.dev\.modular-avatar$", @"^Modular\s*Avatar$" }, ".cs", ".dll", ".asmdef");
            Add(list, "builtin.ndmf", "NDMF", Label.Infra, new[] { @"^nadena\.dev\.ndmf$", @"^NDMF$", @"^ndmf$" }, ".cs", ".dll", ".asmdef");
            Add(list, "builtin.aao", "Avatar Optimizer", Label.Infra, new[] { @"^com\.anatawa12\.avatar-optimizer$", @"^Avatar\s*Optimizer$" }, ".cs", ".dll", ".asmdef");
            Add(list, "builtin.vrcfury", "VRCFury", Label.Infra, new[] { @"^VRCFury.*$", @"^com\.vrcfury\..*$" }, ".cs", ".dll", ".asmdef");
            Add(list, "builtin.gesturemanager", "Gesture Manager", Label.Infra, new[] { @"^Gesture\s*Manager$", @"^vrchat\.blackstartx\.gesture-manager$" }, ".cs", ".dll", ".asmdef");
            Add(list, "builtin.audiolink", "AudioLink", Label.Infra, new[] { @"^AudioLink$" }, ".cs", ".shader", ".asset");
            Add(list, "builtin.tmp", "TextMesh Pro", Label.Infra, new[] { @"^TextMesh\s*Pro$" }, ".asset", ".shader", ".cs");
            Add(list, "builtin.bakery", "Bakery", Label.Infra, new[] { @"^Bakery$" }, ".cs", ".dll", ".shader");
            Add(list, "builtin.dynamicbone", "Dynamic Bone", Label.Infra, new[] { @"^DynamicBone$" }, ".cs", ".dll");
            Add(list, "builtin.finalik", "Final IK", Label.Infra, new[] { @"^RootMotion$" }, ".cs", ".dll");
            Add(list, "builtin.univrm", "UniVRM", Label.Infra, new[] { @"^VRM$", @"^VRM10$", @"^UniGLTF$", @"^VRMShaders$", @"^UniGLTF\..*$" }, ".cs", ".dll", ".shader", ".asmdef");

            // ツール (Znakisaki 製)
            Add(list, "builtin.shapefit", "ShapeFit", Label.Tool, new[] { @"^ShapeFit$", @"^AvatarAppearancePresets$" }, ".cs", ".dll");
            Add(list, "builtin.handgesturefit", "Hand Gesture Fit", Label.Tool, new[] { @"^HandGestureFit$" }, ".cs", ".dll");
            Add(list, "builtin.colliderfit", "Collider Fit", Label.Tool, new[] { @"^ColliderFit$" }, ".cs", ".dll");
            Add(list, "builtin.folderlabeler", "Folder Labeler", Label.Tool, new[] { @"^FolderLabeler$" }, ".cs", ".dll");

            // よく見かけるツール
            Add(list, "builtin.lilavatarutils", "lilAvatarUtils", Label.Tool, new[] { @"^lilAvatarUtils$" }, ".cs", ".dll");
            Add(list, "builtin.anatawa12", "anatawa12 製ツール", Label.Tool, new[] { @"^com\.anatawa12\..*$" }, ".cs", ".dll", ".asmdef");
            return list;
        }

        static void Add(List<RegistryEntry> list, string id, string name, Label label, string[] patterns, params string[] requireExt)
        {
            var e = new RegistryEntry
            {
                id = id,
                name = name,
                label = Labels.Key(label),
                source = "builtin",
                sourceName = "同梱ルール",
                builtin = true,
            };
            e.namePatterns.AddRange(patterns);
            e.requireExtensions.AddRange(requireExt);
            list.Add(e);
        }
    }
}
