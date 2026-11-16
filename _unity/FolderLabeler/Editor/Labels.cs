using System;

namespace FolderLabeler
{
    /// <summary>主ラベル。JSON には Key (英字の固定文字列) で保存する。表示名は変えても JSON は壊れない。</summary>
    internal enum Label
    {
        Avatar,
        Outfit,
        Hair,
        Accessory,
        TextureMaterial,
        Motion,
        GestureExpression,
        Tool,
        Shader,
        Infra,
        WorldGimmick,
        Other,
    }

    /// <summary>信頼度。割合 (%) では表示しない。</summary>
    internal enum Confidence
    {
        Low = 0,
        Mid = 1,
        High = 2,
    }

    /// <summary>根拠シグナルの強さ。</summary>
    internal enum Strength
    {
        Weak = 0,
        Mid = 1,
        Strong = 2,
    }

    internal static class Labels
    {
        public const int Count = 12;

        static readonly string[] Keys =
        {
            "avatar", "outfit", "hair", "accessory", "texture_material", "motion",
            "gesture_expression", "tool", "shader", "infra", "world_gimmick", "other",
        };

        static readonly string[] Names =
        {
            "アバター本体", "衣装", "髪型", "アクセサリー・小物", "テクスチャ・マテリアル", "モーション",
            "ジェスチャー・表情", "ツール・Editor拡張", "シェーダー", "インフラ", "ワールド・ギミック", "その他・不明",
        };

        public static readonly string[] DisplayNames = (string[])Names.Clone();

        public static string Key(Label label) { return Keys[(int)label]; }

        public static string Name(Label label) { return Names[(int)label]; }

        public static bool TryParse(string key, out Label label)
        {
            label = Label.Other;
            if (string.IsNullOrEmpty(key)) return false;
            for (int i = 0; i < Keys.Length; i++)
            {
                if (string.Equals(Keys[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    label = (Label)i;
                    return true;
                }
            }
            return false;
        }

        /// <summary>キーから表示名。未知のキーはそのまま見せる (黙って別ラベルにしない)。</summary>
        public static string NameOfKey(string key)
        {
            Label l;
            if (TryParse(key, out l)) return Name(l);
            return string.IsNullOrEmpty(key) ? "(なし)" : "(未知のラベル: " + key + ")";
        }

        public static string ConfidenceKey(Confidence c)
        {
            switch (c)
            {
                case Confidence.High: return "high";
                case Confidence.Mid: return "mid";
                default: return "low";
            }
        }

        public static Confidence ParseConfidence(string key)
        {
            if (key == "high") return Confidence.High;
            if (key == "mid") return Confidence.Mid;
            return Confidence.Low;
        }

        public static string ConfidenceName(string key)
        {
            if (key == "high") return "高";
            if (key == "mid") return "中";
            if (key == "low") return "低";
            return "-";
        }

        public static string StrengthKey(Strength s)
        {
            switch (s)
            {
                case Strength.Strong: return "strong";
                case Strength.Mid: return "mid";
                default: return "weak";
            }
        }

        public static string StrengthName(string key)
        {
            if (key == "strong") return "強";
            if (key == "mid") return "中";
            if (key == "weak") return "弱";
            return "情報";
        }

        public static string LayerName(string layer)
        {
            switch (layer)
            {
                case Layers.Manual: return "手動で確定";
                case Layers.RegistryExact: return "辞書と完全一致";
                case Layers.BuiltinRule: return "同梱ルール (既知のインフラ等)";
                case Layers.Inference: return "中身からの推定";
                case Layers.Undetermined: return "判定材料なし";
                case Layers.Error: return "読み取りエラー";
                case Layers.Group: return "子フォルダから集約";
                default: return string.IsNullOrEmpty(layer) ? "-" : layer;
            }
        }
    }

    /// <summary>どの層でラベルが決まったか (JSON に保存する固定文字列)。</summary>
    internal static class Layers
    {
        public const string Manual = "manual";
        public const string RegistryExact = "registry_exact";
        public const string BuiltinRule = "builtin_rule";
        public const string Inference = "inference";
        public const string Undetermined = "undetermined";
        public const string Error = "error";
        public const string Group = "group";
    }

    internal static class Kinds
    {
        public const string Unit = "unit";      // 1 製品として判定する単位
        public const string Group = "group";    // 作者名フォルダ等。子フォルダを単位にする
        public const string Loose = "loose";    // Assets 直下にばら撒かれたファイル
        public const string Manual = "extra";   // 手動ラベルを保持するために追跡している、走査単位外のフォルダ
    }
}
