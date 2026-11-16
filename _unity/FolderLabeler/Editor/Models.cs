using System;
using System.Collections.Generic;

namespace FolderLabeler
{
    // ======================================================================
    // 索引 (プロジェクト直下 FolderLabeler/index.json)
    //   ・平文 JSON。キーはフォルダの GUID
    //   ・manual (手動確定) と auto (自動判定) を分けて保持する
    //   ・formatVersion を必ず持つ。読めない版数のときは上書きしない
    // ======================================================================

    [Serializable]
    internal sealed class IndexFile
    {
        public const string FormatName = "FolderLabeler.Index";
        public const int CurrentVersion = 1;

        public string format = FormatName;
        public int formatVersion = CurrentVersion;
        public string toolVersion = "";
        public string projectName = "";     // プロジェクトのフォルダ名だけ (絶対パスは入れない)
        public string unityVersion = "";
        public string scannedAt = "";       // ISO 8601 (UTC)
        public string scanMode = "";        // full / diff
        public double scanSeconds;
        public bool sdkPresent;
        public List<FolderRecord> folders = new List<FolderRecord>();
    }

    [Serializable]
    internal sealed class FolderRecord
    {
        public string guid = "";            // 同一性のキー。Assets 直下のばら撒きファイルは "loose:Assets"
        public string path = "";            // 表示用。走査のたびに GUID から引き直す
        public string name = "";
        public string kind = Kinds.Unit;
        public string parentGuid = "";
        public int depth;
        public bool missing;                // GUID のフォルダが見つからない (手動ラベルだけ残している)
        public string error = "";           // 読み取れなかった理由。無ければ空
        public string signature = "";       // 差分走査用 (ファイル構成のハッシュ)
        public string inferenceSignature = "";
        public FolderStats stats = new FolderStats();
        public LabelResult inference = new LabelResult();       // 第 2 層: 中身からの推定
        public RegistryResult registry = new RegistryResult();  // 第 1 層: 辞書照合
        public LabelResult auto = new LabelResult();            // 自動判定の最終結果 (辞書→推定の順で決定)
        public ManualLabel manual = new ManualLabel();          // 手動確定。自動判定では絶対に書き換えない

        public bool IsLocked { get { return manual != null && manual.locked; } }

        /// <summary>画面に出す実効ラベルのキー。</summary>
        public string EffectiveLabel { get { return IsLocked ? manual.label : auto.label; } }

        public string EffectiveConfidence { get { return IsLocked ? "" : auto.confidence; } }

        public string EffectiveLayer { get { return IsLocked ? Layers.Manual : auto.layer; } }

        public bool HasCandidates
        {
            get { return registry != null && registry.candidates != null && registry.candidates.Count > 0 && auto.layer != Layers.RegistryExact; }
        }
    }

    [Serializable]
    internal sealed class FolderStats
    {
        public int fileCount;               // .meta を除くファイル数
        public long totalBytes;
        public string avatarPrefab = "";    // アバター本体と判断した根拠の Prefab 名 (相対パス)
        public List<CategoryCount> categories = new List<CategoryCount>();
    }

    [Serializable]
    internal sealed class CategoryCount
    {
        public string category = "";
        public int count;
        public long bytes;
        public List<string> examples = new List<string>();  // 最大 5 件 (フォルダからの相対パス)
    }

    [Serializable]
    internal sealed class LabelResult
    {
        public string label = "";
        public string confidence = "";
        public string layer = "";
        public bool undetermined;           // 根拠が足りず「不明」として扱っている
        public List<Signal> signals = new List<Signal>();
        public List<LabelScore> alternatives = new List<LabelScore>();
        public List<string> notes = new List<string>();
    }

    [Serializable]
    internal sealed class Signal
    {
        public string strength = "";        // strong / mid / weak / info
        public string label = "";           // このシグナルが指すラベル (情報だけのときは空)
        public string text = "";
    }

    [Serializable]
    internal sealed class LabelScore
    {
        public string label = "";
        public int score;
        public string strongest = "";
    }

    [Serializable]
    internal sealed class RegistryResult
    {
        public List<RegistryCandidate> candidates = new List<RegistryCandidate>();
        public string builtinRuleId = "";
    }

    [Serializable]
    internal sealed class RegistryCandidate
    {
        public string entryId = "";
        public string name = "";
        public string label = "";
        public bool exact;
        public float entryCoverage;     // 辞書側のファイルのうち、このフォルダにあった割合
        public float folderCoverage;    // このフォルダのファイルのうち、辞書側にあった割合
        public string key = "";         // 一致に使ったキー: guid / path / name_size
    }

    [Serializable]
    internal sealed class ManualLabel
    {
        public bool locked;
        public string label = "";
        public string lockedAt = "";
        public string note = "";
    }

    // ======================================================================
    // 辞書 (グローバル。既定 %AppData%/Znakisaki/FolderLabeler/registry.json)
    //   ・絶対パスを入れない。指紋は「登録フォルダからの相対パス」の集合
    //   ・将来の外部アプリが読めるよう、平文 JSON・版数つき
    // ======================================================================

    [Serializable]
    internal sealed class RegistryFile
    {
        public const string FormatName = "FolderLabeler.Registry";
        public const int CurrentVersion = 1;

        public string format = FormatName;
        public int formatVersion = CurrentVersion;
        public string updatedAt = "";
        public RegistrySettings settings = new RegistrySettings();
        public List<RegistryEntry> entries = new List<RegistryEntry>();
    }

    [Serializable]
    internal sealed class RegistrySettings
    {
        public float partialThreshold = 0.8f;   // 部分一致を候補にする最低一致率
        public int minFilesForPartial = 3;      // これより少ないファイル数の登録は完全一致のみ
        public int prefabTextLimit = 200;       // 1 フォルダで中身を文字列として確認する Prefab の上限
        public int prefabLoadLimit = 10;        // 1 フォルダで LoadAssetAtPath する Prefab の上限
        public int modelLoadLimit = 3;          // 1 フォルダで LoadAssetAtPath するモデルの上限
    }

    [Serializable]
    internal sealed class RegistryEntry
    {
        public string id = "";
        public string name = "";
        public string label = "";
        public string source = "";          // project / external_folder / unitypackage / builtin
        public string sourceName = "";      // 元のフォルダ名・ファイル名だけ (パスは入れない)
        public string registeredAt = "";
        public string note = "";
        public int fileCount;
        public long totalBytes;
        public List<FingerprintFile> files = new List<FingerprintFile>();

        // 同梱ルール用 (ユーザー登録では空)
        public List<string> namePatterns = new List<string>();
        public List<string> requireExtensions = new List<string>();

        [NonSerialized] public bool builtin;
    }

    [Serializable]
    internal sealed class FingerprintFile
    {
        public string p = "";   // 登録フォルダからの相対パス ("/" 区切り)
        public long s;          // バイト数
        public string g = "";   // .meta の GUID (取れたときだけ)
    }
}
