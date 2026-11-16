using System;
using UnityEngine;

namespace FolderLabeler
{
    /// <summary>
    /// VRChat SDK の型へは、型名の文字列だけで触る (using VRC.* は使わない)。
    /// このファイル以外に SDK の型名を書かない。
    /// </summary>
    internal static class VrcSdk
    {
        public const string DescriptorTypeName = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";
        public const string DescriptorShortName = "VRCAvatarDescriptor";
        public const string LegacyDescriptorShortName = "VRC_AvatarDescriptor";

        /// <summary>
        /// Prefab のテキスト (YAML) に現れる VRC Avatar Descriptor のフィールド名。
        /// SDK が無くても、Prefab の中にこれらが揃っていれば Descriptor らしいと推定できる。
        /// </summary>
        public static readonly string[] DescriptorYamlFields =
        {
            "ViewPosition:",
            "lipSync:",
            "VisemeSkinnedMesh:",
            "customizeAnimationLayers:",
            "baseAnimationLayers:",
            "expressionsMenu:",
            "expressionParameters:",
            "customEyeLookSettings:",
        };

        /// <summary>上のフィールド名のうち、いくつ揃えば Descriptor らしいとみなすか。</summary>
        public const int DescriptorYamlMinHits = 4;

        /// <summary>Expression Menu / Parameters の .asset に現れるフィールド名。</summary>
        public static readonly string[] ExpressionMenuYamlFields = { "controls:", "subMenu:", "parameter:" };
        public static readonly string[] ExpressionParamsYamlFields = { "parameters:", "valueType:", "saved:" };

        static bool resolved;
        static Type descriptorType;

        public static bool Present
        {
            get { Resolve(); return descriptorType != null; }
        }

        public const string AbsentMessage =
            "VRChat SDK (Avatars) が見つかりません。SDK がないためアバター判定は弱くなります "
            + "(Prefab の記述内容だけで推定し、アバター本体の信頼度は「中」までになります)。ほかの判定はそのまま動作します。";

        static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            descriptorType = FindType(DescriptorTypeName);
        }

        static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName, false); }
                catch (Exception) { }
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>コンポーネントが VRC Avatar Descriptor (または旧 SDK の同等品) か。型名で判定する。</summary>
        public static bool IsDescriptor(Component c)
        {
            if (c == null) return false;    // Missing Script
            for (Type t = c.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                if (t.Name == DescriptorShortName || t.Name == LegacyDescriptorShortName) return true;
            }
            return false;
        }

        /// <summary>SDK の状態が変わったとき (導入・削除) に呼ぶ。</summary>
        public static void Reset() { resolved = false; descriptorType = null; }
    }
}
