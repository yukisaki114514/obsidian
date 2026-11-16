using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FolderLabeler
{
    internal sealed class FolderLabelerWindow : EditorWindow
    {
        const string Title = "Folder Labeler";
        const float RowHeight = 20f;

        static readonly string[] TabNames = { "一覧", "レビュー", "辞書", "設定" };
        static readonly string[] ConfFilterNames = { "信頼度: すべて", "高", "中", "低・不明", "手動で確定", "辞書候補あり", "読み取りエラー" };
        static readonly string[] ColumnNames = { "フォルダ名", "ラベル", "信頼度", "サイズ", "ファイル数", "パス" };
        static readonly float[] ColumnWidths = { 200f, 190f, 52f, 76f, 64f, 0f };

        [SerializeField] int tab;
        [SerializeField] string search = "";
        [SerializeField] int labelFilter;
        [SerializeField] int confFilter;
        [SerializeField] int sortColumn = 5;
        [SerializeField] bool sortAscending = true;
        [SerializeField] string selectedGuid = "";
        [SerializeField] string selectedEntryId = "";
        [SerializeField] float detailHeight = 280f;

        [NonSerialized] Engine engine;
        [NonSerialized] string message;
        [NonSerialized] MessageType messageType = MessageType.Info;
        [NonSerialized] bool projectChanged;
        [NonSerialized] List<FolderRecord> visible = new List<FolderRecord>();
        [NonSerialized] bool resizing;

        // 手動ラベル・辞書登録フォーム
        [NonSerialized] string manualForGuid;
        [NonSerialized] int manualPick;
        [NonSerialized] bool registerOpen;
        [NonSerialized] string registerName = "";
        [NonSerialized] int registerLabel;

        // 外部登録 (確認待ち)
        [NonSerialized] List<RegistryEntry> pending;
        [NonSerialized] string pendingNote;
        [NonSerialized] string pendingName = "";
        [NonSerialized] int pendingLabel = (int)Label.Outfit;

        // 辞書の編集
        [NonSerialized] string editForId;
        [NonSerialized] string editName = "";
        [NonSerialized] int editLabel;
        [NonSerialized] string editNote = "";

        Vector2 listScroll, detailScroll, dictScroll, dictDetailScroll, settingsScroll;
        GUIStyle cellStyle, headerStyle, smallWrap;

        [MenuItem("Tools/Folder Labeler")]
        static void Open()
        {
            GetWindow<FolderLabelerWindow>(Title).minSize = new Vector2(720, 520);
        }

        void OnEnable()
        {
            EditorApplication.projectChanged += OnProjectChanged;
        }

        void OnDisable()
        {
            EditorApplication.projectChanged -= OnProjectChanged;
        }

        void OnProjectChanged()
        {
            projectChanged = true;
            if (engine != null) engine.RefreshPaths();  // 移動・改名に追随 (GUID からパスを引き直すだけ)
            Repaint();
        }

        void OnFocus()
        {
            if (engine != null) engine.RefreshPaths();
        }

        void EnsureEngine()
        {
            if (engine != null) return;
            engine = new Engine();
            try { engine.Load(); }
            catch (Exception e)
            {
                Debug.LogException(e);
                SetMessage("データの読み込み中にエラーが発生しました: " + e.Message, MessageType.Error);
            }
        }

        void EnsureStyles()
        {
            if (cellStyle != null) return;
            cellStyle = new GUIStyle(EditorStyles.label) { clipping = TextClipping.Clip, padding = new RectOffset(4, 4, 2, 2) };
            headerStyle = new GUIStyle(EditorStyles.toolbarButton) { alignment = TextAnchor.MiddleLeft };
            smallWrap = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { richText = false };
        }

        void SetMessage(string text, MessageType type)
        {
            message = text;
            messageType = type;
        }

        /// <summary>状態を変える操作は GUI の描画が終わってから行う (IMGUI のレイアウト崩れを防ぐ)。</summary>
        void Run(Action action)
        {
            EditorApplication.delayCall += () =>
            {
                try { action(); }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    SetMessage("エラーが発生しました: " + e.Message, MessageType.Error);
                }
                Repaint();
            };
        }

        // ------------------------------------------------------------------
        // GUI
        // ------------------------------------------------------------------

        void OnGUI()
        {
            EnsureEngine();
            EnsureStyles();

            EditorGUILayout.HelpBox(
                "Assets 内のフォルダを走査し、衣装・アバター・ツール等のラベルを、根拠と信頼度つきで提案します。"
                + "ラベルは自動判定による候補です。最終確認はご自身で行ってください。\n"
                + "走査・判定は読み取り専用で、シーン・アセット・プロジェクト設定は変更しません。"
                + "保存するのは、プロジェクト直下の FolderLabeler/index.json と辞書ファイルだけです。",
                MessageType.None);

            if (!VrcSdk.Present) EditorGUILayout.HelpBox(VrcSdk.AbsentMessage, MessageType.Warning);
            if (engine.indexProblem != null) EditorGUILayout.HelpBox(engine.indexProblem, MessageType.Error);
            if (engine.registry.loadProblem != null) EditorGUILayout.HelpBox(engine.registry.loadProblem, MessageType.Error);

            DrawScanBar();
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, messageType);
            foreach (string w in engine.lastScanWarnings) EditorGUILayout.HelpBox(w, MessageType.Warning);

            int reviewCount = 0;
            foreach (var r in engine.index.folders) if (NeedsReview(r)) reviewCount++;
            var tabLabels = new[]
            {
                TabNames[0] + " (" + engine.index.folders.Count + ")",
                TabNames[1] + " (" + reviewCount + ")",
                TabNames[2] + " (" + engine.registry.UserEntries.Count + ")",
                TabNames[3],
            };
            tab = GUILayout.Toolbar(Mathf.Clamp(tab, 0, 3), tabLabels);

            if (tab == 0 || tab == 1) DrawListTab(tab == 1);
            else if (tab == 2) DrawDictionaryTab();
            else DrawSettingsTab();
        }

        void DrawScanBar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = !EditorApplication.isCompiling && !EditorApplication.isUpdating;
                if (GUILayout.Button(new GUIContent("全走査", "すべてのフォルダを走査し、中身の推定をやり直します"), GUILayout.Height(24), GUILayout.Width(110)))
                    Run(() => DoScan(false));
                using (new EditorGUI.DisabledScope(engine.index.folders.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent("差分走査", "前回の走査からファイル構成が変わったフォルダだけ、中身の推定をやり直します"), GUILayout.Height(24), GUILayout.Width(110)))
                        Run(() => DoScan(true));
                    if (GUILayout.Button(new GUIContent("レポートを保存…", "一覧をテキスト (タブ区切り) で保存します"), GUILayout.Height(24), GUILayout.Width(120)))
                        Run(SaveReport);
                }
                GUI.enabled = true;
                GUILayout.FlexibleSpace();
            }
            if (engine.index.folders.Count == 0)
            {
                EditorGUILayout.HelpBox("まだ走査していません。「全走査」を押してください。", MessageType.Info);
            }
            else
            {
                string when = engine.index.scannedAt;
                DateTime t;
                if (DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) when = t.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
                EditorGUILayout.LabelField("前回の走査: " + when + " / " + engine.index.folders.Count + " フォルダ / "
                    + engine.index.scanSeconds.ToString("0.0") + " 秒" + (engine.index.sdkPresent ? "" : " (SDK なしで走査)"), EditorStyles.miniLabel);
                if (projectChanged)
                    EditorGUILayout.HelpBox("前回の走査のあとにプロジェクトが変更されました。「差分走査」で一覧を更新できます。", MessageType.Info);
            }
        }

        void DoScan(bool diff)
        {
            if (engine.indexReadOnly)
            {
                SetMessage("索引ファイルを読み込めなかったため、手動ラベルを守るために走査結果の保存を止めています。上のエラーを確認してください。", MessageType.Warning);
            }
            bool ok = engine.Scan(diff, true);
            projectChanged = false;
            SetMessage(engine.lastScanMessage, ok ? MessageType.Info : MessageType.Warning);
        }

        void SaveReport()
        {
            string path = EditorUtility.SaveFilePanel("レポートを保存", "", "FolderLabeler_report", "txt");
            if (string.IsNullOrEmpty(path)) return;
            string problem = engine.WriteReport(path);
            SetMessage(problem ?? "レポートを保存しました: " + path, problem == null ? MessageType.Info : MessageType.Error);
        }

        // ------------------------------------------------------------------
        // 一覧 / レビュー
        // ------------------------------------------------------------------

        static bool NeedsReview(FolderRecord r)
        {
            if (r.IsLocked || r.kind == Kinds.Group || r.kind == Kinds.Loose) return false;
            if (r.error.Length > 0 || r.auto.undetermined) return true;
            if (Labels.ParseConfidence(r.auto.confidence) == Confidence.Low) return true;
            return r.HasCandidates;
        }

        void DrawListTab(bool review)
        {
            if (review)
            {
                EditorGUILayout.HelpBox(
                    "信頼度「低」・「不明」・辞書の候補があるフォルダ・読み取りエラーのフォルダだけを表示しています。"
                    + "行を選び、下の詳細でラベルを選んで確定すると、手動ラベルとして固定されます (再走査しても上書きされません)。",
                    MessageType.None);
            }
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                search = GUILayout.TextField(search ?? "", EditorStyles.toolbarSearchField, GUILayout.MinWidth(160));
                var labelOptions = new string[Labels.Count + 1];
                labelOptions[0] = "ラベル: すべて";
                for (int i = 0; i < Labels.Count; i++) labelOptions[i + 1] = Labels.DisplayNames[i];
                labelFilter = EditorGUILayout.Popup(labelFilter, labelOptions, EditorStyles.toolbarPopup, GUILayout.Width(170));
                if (!review) confFilter = EditorGUILayout.Popup(confFilter, ConfFilterNames, EditorStyles.toolbarPopup, GUILayout.Width(130));
                GUILayout.FlexibleSpace();
            }

            BuildVisible(review);
            DrawHeader();

            listScroll = EditorGUILayout.BeginScrollView(listScroll, GUILayout.ExpandHeight(true));
            if (visible.Count == 0)
            {
                EditorGUILayout.LabelField(engine.index.folders.Count == 0 ? "" : (review ? "確認が必要なフォルダはありません。" : "条件に合うフォルダがありません。"), EditorStyles.centeredGreyMiniLabel);
            }
            for (int i = 0; i < visible.Count; i++) DrawRow(visible[i], i);
            EditorGUILayout.EndScrollView();

            DrawSplitter();
            detailScroll = EditorGUILayout.BeginScrollView(detailScroll, GUILayout.Height(detailHeight));
            var sel = string.IsNullOrEmpty(selectedGuid) ? null : engine.Find(selectedGuid);
            if (sel == null) EditorGUILayout.LabelField("行を選ぶと、ここに根拠と中身の内訳を表示します。", EditorStyles.centeredGreyMiniLabel);
            else DrawDetail(sel);
            EditorGUILayout.EndScrollView();
        }

        void BuildVisible(bool review)
        {
            visible.Clear();
            string q = (search ?? "").Trim();
            foreach (var r in engine.index.folders)
            {
                if (review && !NeedsReview(r)) continue;
                if (labelFilter > 0 && r.EffectiveLabel != Labels.Key((Label)(labelFilter - 1))) continue;
                if (!review && !PassConf(r)) continue;
                if (q.Length > 0)
                {
                    bool hit = r.path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                        || Labels.NameOfKey(r.EffectiveLabel).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit) continue;
                }
                visible.Add(r);
            }
            visible.Sort(Compare);
        }

        bool PassConf(FolderRecord r)
        {
            switch (confFilter)
            {
                case 1: return !r.IsLocked && r.auto.confidence == "high";
                case 2: return !r.IsLocked && r.auto.confidence == "mid";
                case 3: return !r.IsLocked && (r.auto.confidence == "low" || r.auto.undetermined);
                case 4: return r.IsLocked;
                case 5: return r.HasCandidates;
                case 6: return r.error.Length > 0 || r.missing;
                default: return true;
            }
        }

        int Compare(FolderRecord a, FolderRecord b)
        {
            int c;
            switch (sortColumn)
            {
                case 0: c = string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase); break;
                case 1: c = string.Compare(Labels.NameOfKey(a.EffectiveLabel), Labels.NameOfKey(b.EffectiveLabel), StringComparison.Ordinal); break;
                case 2: c = ConfRank(a).CompareTo(ConfRank(b)); break;
                case 3: c = a.stats.totalBytes.CompareTo(b.stats.totalBytes); break;
                case 4: c = a.stats.fileCount.CompareTo(b.stats.fileCount); break;
                default: c = string.Compare(a.path, b.path, StringComparison.OrdinalIgnoreCase); break;
            }
            if (c == 0) c = string.Compare(a.path, b.path, StringComparison.OrdinalIgnoreCase);
            return sortAscending ? c : -c;
        }

        static int ConfRank(FolderRecord r)
        {
            if (r.IsLocked) return 4;
            if (r.auto.undetermined) return 0;
            return 1 + (int)Labels.ParseConfidence(r.auto.confidence);
        }

        void DrawHeader()
        {
            Rect rect = GUILayoutUtility.GetRect(0, 20, GUILayout.ExpandWidth(true));
            float x = rect.x;
            for (int i = 0; i < ColumnNames.Length; i++)
            {
                float w = ColumnWidths[i] > 0 ? ColumnWidths[i] : Mathf.Max(80, rect.xMax - x);
                string text = ColumnNames[i] + (sortColumn == i ? (sortAscending ? " ▲" : " ▼") : "");
                if (GUI.Button(new Rect(x, rect.y, w, rect.height), text, headerStyle))
                {
                    if (sortColumn == i) sortAscending = !sortAscending;
                    else { sortColumn = i; sortAscending = i != 3 && i != 4; }
                }
                x += w;
            }
        }

        void DrawRow(FolderRecord r, int i)
        {
            Rect rect = GUILayoutUtility.GetRect(0, RowHeight, GUILayout.ExpandWidth(true));
            bool selected = r.guid == selectedGuid;
            if (Event.current.type == EventType.Repaint)
            {
                if (selected) EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin ? new Color(0.17f, 0.36f, 0.53f) : new Color(0.58f, 0.73f, 0.9f));
                else if (i % 2 == 1) EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin ? new Color(1f, 1f, 1f, 0.03f) : new Color(0f, 0f, 0f, 0.04f));
            }

            var oldColor = GUI.contentColor;
            if (r.error.Length > 0 || r.missing) GUI.contentColor = new Color(1f, 0.55f, 0.4f);
            else if (!r.IsLocked && (r.auto.undetermined || r.auto.confidence == "low")) GUI.contentColor = new Color(0.75f, 0.75f, 0.75f);

            float x = rect.x;
            string[] cells =
            {
                new string(' ', Math.Max(0, r.depth - 1) * 3) + (r.kind == Kinds.Group ? "▸ " : "") + r.name,
                LabelCell(r),
                ConfCell(r),
                FolderWalker.FormatBytes(r.stats.totalBytes),
                r.stats.fileCount.ToString(),
                r.kind == Kinds.Loose ? "Assets/ (直下のファイル)" : r.path,
            };
            for (int c = 0; c < cells.Length; c++)
            {
                float w = ColumnWidths[c] > 0 ? ColumnWidths[c] : Mathf.Max(80, rect.xMax - x);
                GUI.Label(new Rect(x, rect.y, w, rect.height), new GUIContent(cells[c], cells[c]), cellStyle);
                x += w;
            }
            GUI.contentColor = oldColor;

            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                if (selectedGuid != r.guid)
                {
                    selectedGuid = r.guid;
                    registerOpen = false;
                    manualForGuid = null;
                    detailScroll = Vector2.zero;
                }
                Ping(r);
                Event.current.Use();
                Repaint();
            }
        }

        static void Ping(FolderRecord r)
        {
            if (r.kind == Kinds.Loose || r.missing) return;
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(r.path);
            if (obj != null) EditorGUIUtility.PingObject(obj);
        }

        static string LabelCell(FolderRecord r)
        {
            if (r.error.Length > 0) return "読み取りエラー";
            string text;
            if (r.IsLocked) text = "[手動] " + Labels.NameOfKey(r.manual.label);
            else if (r.auto.undetermined) text = "不明";
            else text = Labels.NameOfKey(r.auto.label);
            if (r.kind == Kinds.Group && !r.IsLocked) text = "(まとめ) " + text;
            if (r.missing) text = "(見つかりません) " + text;
            if (r.HasCandidates && !r.IsLocked) text += "  *辞書候補";
            return text;
        }

        static string ConfCell(FolderRecord r)
        {
            if (r.IsLocked) return "手動";
            if (r.auto.undetermined) return "-";
            return Labels.ConfidenceName(r.auto.confidence);
        }

        void DrawSplitter()
        {
            Rect r = GUILayoutUtility.GetRect(0, 5, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(new Rect(r.x, r.y + 2, r.width, 1), new Color(0.5f, 0.5f, 0.5f, 0.6f));
            EditorGUIUtility.AddCursorRect(r, MouseCursor.ResizeVertical);
            var e = Event.current;
            if (e.type == EventType.MouseDown && r.Contains(e.mousePosition)) { resizing = true; e.Use(); }
            if (resizing && e.type == EventType.MouseDrag)
            {
                detailHeight = Mathf.Clamp(detailHeight - e.delta.y, 120f, Mathf.Max(140f, position.height - 260f));
                e.Use();
                Repaint();
            }
            if (e.type == EventType.MouseUp) resizing = false;
        }

        // ------------------------------------------------------------------
        // 詳細
        // ------------------------------------------------------------------

        void DrawDetail(FolderRecord r)
        {
            EditorGUILayout.LabelField(r.name, EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(r.kind == Kinds.Loose ? "Assets/ (直下のファイル)" : r.path, EditorStyles.miniLabel, GUILayout.Height(16));

            if (r.kind == Kinds.Group)
                EditorGUILayout.HelpBox("このフォルダは複数の製品をまとめたフォルダとして扱い、中のフォルダごとに判定しています。下のラベルは子フォルダの結果の集約です。", MessageType.None);
            if (r.kind == Kinds.Manual)
                EditorGUILayout.HelpBox("走査単位 (Assets 直下と 2 階層目) より深い位置にあるフォルダです。手動ラベルを保持するために追跡しています。", MessageType.None);
            if (r.kind == Kinds.Loose)
                EditorGUILayout.HelpBox("Assets 直下にフォルダに入れずに置かれたファイルです。製品の単位が分からないため判定しません。", MessageType.Info);
            if (r.error.Length > 0) EditorGUILayout.HelpBox(r.error, MessageType.Warning);
            if (r.missing)
            {
                EditorGUILayout.HelpBox("このフォルダが見つかりません (削除された可能性があります)。手動ラベルだけを残しています。", MessageType.Warning);
                if (GUILayout.Button("この手動ラベルを削除", GUILayout.Width(180)))
                    Run(() => { string p = engine.Unlock(r); selectedGuid = ""; if (p != null) SetMessage(p, MessageType.Error); });
                return;
            }

            // 現在のラベル
            string state;
            if (r.IsLocked) state = "手動で確定 (" + FormatTime(r.manual.lockedAt) + ")" + (string.IsNullOrEmpty(r.manual.note) ? "" : " ・ " + r.manual.note);
            else if (r.auto.undetermined) state = "不明 (" + Labels.LayerName(r.auto.layer) + ")";
            else state = "自動 ・ 信頼度 " + Labels.ConfidenceName(r.auto.confidence) + " ・ " + Labels.LayerName(r.auto.layer);
            EditorGUILayout.LabelField("ラベル", Labels.NameOfKey(r.EffectiveLabel) + "    " + state);
            if (r.IsLocked && !string.IsNullOrEmpty(r.auto.label))
            {
                EditorGUILayout.LabelField(" ", "自動判定では: " + (r.auto.undetermined ? "不明" : Labels.NameOfKey(r.auto.label) + " (信頼度 " + Labels.ConfidenceName(r.auto.confidence) + ")"), EditorStyles.miniLabel);
            }

            if (r.kind != Kinds.Loose) DrawManualControls(r);
            DrawCandidates(r);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("根拠", EditorStyles.boldLabel);
            if (r.auto.signals.Count == 0) EditorGUILayout.LabelField("(根拠シグナルはありません)", EditorStyles.miniLabel);
            foreach (var s in r.auto.signals)
            {
                string head = s.strength == "info" ? "・" : "[" + Labels.StrengthName(s.strength) + "] ";
                string tail = string.IsNullOrEmpty(s.label) || s.strength == "info" ? "" : "  → " + Labels.NameOfKey(s.label);
                EditorGUILayout.LabelField(head + s.text + tail, smallWrap);
            }
            foreach (string n in r.auto.notes) EditorGUILayout.LabelField("※ " + n, smallWrap);
            if (r.auto.alternatives.Count > 1)
            {
                var parts = new List<string>();
                foreach (var a in r.auto.alternatives) parts.Add(Labels.NameOfKey(a.label) + " (" + Labels.StrengthName(a.strongest) + ")");
                EditorGUILayout.LabelField("推定の候補: " + string.Join(" / ", parts.ToArray()), smallWrap);
            }
            if (!string.IsNullOrEmpty(r.stats.avatarPrefab)) EditorGUILayout.LabelField("代表 Prefab: " + r.stats.avatarPrefab, smallWrap);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("中身の内訳 (" + r.stats.fileCount + " ファイル / " + FolderWalker.FormatBytes(r.stats.totalBytes) + ")", EditorStyles.boldLabel);
            foreach (var c in r.stats.categories)
            {
                EditorGUILayout.LabelField(FileCategories.Name(c.category) + "  " + c.count + " 件 (" + FolderWalker.FormatBytes(c.bytes) + ")   例: "
                    + string.Join(", ", c.examples.ToArray()), smallWrap);
            }

            if (r.kind != Kinds.Loose && r.error.Length == 0)
            {
                EditorGUILayout.Space(6);
                DrawRegisterFromProject(r);
            }
        }

        void DrawManualControls(FolderRecord r)
        {
            if (manualForGuid != r.guid)
            {
                manualForGuid = r.guid;
                Label current;
                manualPick = Labels.TryParse(r.EffectiveLabel, out current) ? (int)current : (int)Label.Other;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(r.IsLocked ? "ラベルを変更" : "手動で確定");
                manualPick = EditorGUILayout.Popup(manualPick, Labels.DisplayNames, GUILayout.Width(180));
                if (GUILayout.Button("このラベルで確定", GUILayout.Width(120)))
                {
                    string key = Labels.Key((Label)manualPick);
                    Run(() => Report(engine.Lock(r, key, ""), "「" + r.name + "」を「" + Labels.NameOfKey(key) + "」で確定しました。"));
                }
                using (new EditorGUI.DisabledScope(!r.IsLocked))
                {
                    if (GUILayout.Button(new GUIContent("自動判定に戻す", "手動ラベルを破棄して、自動判定の結果を使います"), GUILayout.Width(110)))
                    {
                        Run(() =>
                        {
                            if (!EditorUtility.DisplayDialog(Title, "「" + r.name + "」の手動ラベルを破棄して、自動判定に戻しますか？", "自動に戻す", "キャンセル")) return;
                            manualForGuid = null;
                            Report(engine.Unlock(r), "「" + r.name + "」を自動判定に戻しました。");
                        });
                    }
                }
                GUILayout.FlexibleSpace();
            }
        }

        void DrawCandidates(FolderRecord r)
        {
            var cands = r.registry.candidates;
            if (cands.Count == 0) return;
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("辞書の候補", EditorStyles.boldLabel);
            if (!r.IsLocked && r.auto.layer != Layers.RegistryExact)
                EditorGUILayout.HelpBox("辞書の候補は自動では確定しません。内容が合っていれば「この候補で確定」を押してください。", MessageType.None);
            foreach (var c in cands)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string text = c.name + " ― " + Labels.NameOfKey(c.label)
                        + "   一致率 " + Math.Round(c.entryCoverage * 100) + "% (フォルダ側 " + Math.Round(c.folderCoverage * 100) + "%)"
                        + "   キー: " + KeyName(c.key) + (c.exact ? "   [完全一致]" : "   [部分一致]");
                    EditorGUILayout.LabelField(text, smallWrap);
                    if (GUILayout.Button("この候補で確定", GUILayout.Width(110)))
                    {
                        string label = c.label, name = c.name;
                        Run(() => Report(engine.Lock(r, label, "辞書の候補「" + name + "」から確定"), "「" + r.name + "」を辞書の候補「" + name + "」で確定しました。"));
                    }
                }
            }
        }

        static string KeyName(string key)
        {
            switch (key)
            {
                case "guid": return "GUID";
                case "path": return "相対パス";
                case "name_size": return "ファイル名とサイズ";
                default: return key;
            }
        }

        void DrawRegisterFromProject(FolderRecord r)
        {
            if (!registerOpen)
            {
                if (GUILayout.Button(new GUIContent("このフォルダを辞書に登録…", "中身の指紋 (相対パス・サイズ・GUID) を辞書に登録し、ほかのプロジェクトでも同じアセットを見分けられるようにします"), GUILayout.Width(200)))
                {
                    registerOpen = true;
                    registerName = r.name;
                    Label l;
                    registerLabel = Labels.TryParse(r.EffectiveLabel, out l) ? (int)l : (int)Label.Other;
                }
                return;
            }
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("辞書に登録", EditorStyles.boldLabel);
                if (r.kind == Kinds.Group)
                    EditorGUILayout.HelpBox("まとめフォルダ全体を 1 件として登録します。製品ごとに登録したい場合は、中のフォルダを選んで登録してください。", MessageType.Info);
                registerName = EditorGUILayout.TextField("表示名", registerName);
                registerLabel = EditorGUILayout.Popup("ラベル", registerLabel, Labels.DisplayNames);
                EditorGUILayout.LabelField("登録する内容", r.stats.fileCount + " ファイルの相対パス・サイズ・GUID (PC のフォルダの場所は保存しません)", smallWrap);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(registerName) || engine.registry.readOnly))
                    {
                        if (GUILayout.Button("登録する", GUILayout.Width(100)))
                        {
                            string name = registerName.Trim();
                            string label = Labels.Key((Label)registerLabel);
                            Run(() => RegisterProjectFolder(r, name, label));
                        }
                    }
                    if (GUILayout.Button("やめる", GUILayout.Width(80))) registerOpen = false;
                }
            }
        }

        void RegisterProjectFolder(FolderRecord r, string name, string label)
        {
            var files = engine.WalkedFiles(r.guid);
            if (files == null)
            {
                var w = FolderWalker.Walk(Path.Combine(Engine.ProjectRoot, r.path), false);
                files = w.files;
                foreach (var f in files) f.guid = AssetDatabase.AssetPathToGUID(r.path + "/" + f.rel) ?? "";
            }
            if (files.Count == 0)
            {
                SetMessage("ファイルが無いフォルダは登録できません。", MessageType.Warning);
                return;
            }
            var entry = Fingerprints.FromWalk(files, name, label, "project", r.name);
            string problem = engine.registry.Add(entry);
            registerOpen = false;
            if (problem != null) { SetMessage(problem, MessageType.Error); return; }
            engine.Rematch();
            SetMessage("「" + name + "」を辞書に登録しました (" + entry.fileCount + " ファイル)。", MessageType.Info);
        }

        void Report(string problem, string success)
        {
            if (problem != null) SetMessage(problem, MessageType.Error);
            else SetMessage(success, MessageType.Info);
        }

        static string FormatTime(string iso)
        {
            DateTime t;
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) return t.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
            return iso;
        }

        // ------------------------------------------------------------------
        // 辞書
        // ------------------------------------------------------------------

        void DrawDictionaryTab()
        {
            EditorGUILayout.HelpBox(
                "辞書に登録したアセットは、ほかのプロジェクトでも「登録済み」として見分けられます (完全一致は自動で確定、部分一致は候補として表示)。"
                + "辞書には各ファイルの相対パス・サイズ・GUID だけを保存し、PC 上のフォルダの場所 (ユーザー名などを含むパス) は保存しません。\n"
                + "辞書ファイル: " + RegistryStore.CurrentPath,
                MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(engine.registry.readOnly))
                {
                    if (GUILayout.Button(new GUIContent("外部フォルダを登録…", "プロジェクトの外にある元フォルダ (展開済みのアセット) を指定して登録します"), GUILayout.Width(150)))
                        Run(PickExternalFolder);
                    if (GUILayout.Button(new GUIContent("unitypackage から登録…", "unitypackage の中身の一覧 (パス・サイズ・GUID) を読み取って登録します。プロジェクトへは展開しません"), GUILayout.Width(170)))
                        Run(PickUnityPackage);
                    if (GUILayout.Button("読み込み…", GUILayout.Width(90))) Run(ImportRegistry);
                }
                using (new EditorGUI.DisabledScope(engine.registry.UserEntries.Count == 0))
                {
                    if (GUILayout.Button("書き出し…", GUILayout.Width(90))) Run(ExportRegistry);
                }
                GUILayout.FlexibleSpace();
            }

            if (pending != null) DrawPending();

            // 一覧
            Rect head = GUILayoutUtility.GetRect(0, 20, GUILayout.ExpandWidth(true));
            string[] cols = { "名称", "ラベル", "登録元", "ファイル数" };
            float[] widths = { Mathf.Max(160, head.width - 170 - 160 - 80), 170, 160, 80 };
            float hx = head.x;
            for (int i = 0; i < cols.Length; i++)
            {
                GUI.Label(new Rect(hx, head.y, widths[i], head.height), cols[i], headerStyle);
                hx += widths[i];
            }

            var all = new List<RegistryEntry>(engine.registry.UserEntries);
            all.AddRange(BuiltinRules.Entries);
            dictScroll = EditorGUILayout.BeginScrollView(dictScroll, GUILayout.ExpandHeight(true));
            if (engine.registry.UserEntries.Count == 0)
                EditorGUILayout.LabelField("まだ登録がありません。一覧でフォルダを選んで「このフォルダを辞書に登録…」を押すか、上のボタンから登録してください。", EditorStyles.centeredGreyMiniLabel);
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                Rect rect = GUILayoutUtility.GetRect(0, RowHeight, GUILayout.ExpandWidth(true));
                bool sel = e.id == selectedEntryId;
                if (Event.current.type == EventType.Repaint)
                {
                    if (sel) EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin ? new Color(0.17f, 0.36f, 0.53f) : new Color(0.58f, 0.73f, 0.9f));
                    else if (i % 2 == 1) EditorGUI.DrawRect(rect, EditorGUIUtility.isProSkin ? new Color(1f, 1f, 1f, 0.03f) : new Color(0f, 0f, 0f, 0.04f));
                }
                var old = GUI.contentColor;
                if (e.builtin) GUI.contentColor = new Color(0.7f, 0.7f, 0.7f);
                string[] cells =
                {
                    e.name,
                    Labels.NameOfKey(e.label),
                    Fingerprints.SourceName(e.source) + (string.IsNullOrEmpty(e.sourceName) || e.builtin ? "" : " (" + e.sourceName + ")"),
                    e.builtin ? "名前ルール" : e.fileCount.ToString(),
                };
                float x = rect.x;
                for (int c = 0; c < cells.Length; c++)
                {
                    GUI.Label(new Rect(x, rect.y, widths[c], rect.height), new GUIContent(cells[c], cells[c]), cellStyle);
                    x += widths[c];
                }
                GUI.contentColor = old;
                if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
                {
                    selectedEntryId = e.id;
                    editForId = null;
                    Event.current.Use();
                    Repaint();
                }
            }
            EditorGUILayout.EndScrollView();

            var selected = string.IsNullOrEmpty(selectedEntryId) ? null : engine.registry.Find(selectedEntryId);
            if (selected != null)
            {
                dictDetailScroll = EditorGUILayout.BeginScrollView(dictDetailScroll, GUILayout.Height(170));
                DrawEntryEditor(selected);
                EditorGUILayout.EndScrollView();
            }
        }

        void DrawEntryEditor(RegistryEntry e)
        {
            if (e.builtin)
            {
                EditorGUILayout.LabelField(e.name + " (同梱ルール)", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("同梱ルールは編集・削除できません。フォルダ名と中身の構成で見分けます。合わない場合は、一覧でそのフォルダのラベルを手動で確定してください。", MessageType.None);
                EditorGUILayout.LabelField("フォルダ名のパターン: " + string.Join("  ", e.namePatterns.ToArray()), smallWrap);
                EditorGUILayout.LabelField("構成の確認に使う拡張子: " + string.Join(" ", e.requireExtensions.ToArray()), smallWrap);
                return;
            }
            if (editForId != e.id)
            {
                editForId = e.id;
                editName = e.name;
                editNote = e.note ?? "";
                Label l;
                editLabel = Labels.TryParse(e.label, out l) ? (int)l : (int)Label.Other;
            }
            EditorGUILayout.LabelField("登録内容の編集", EditorStyles.boldLabel);
            editName = EditorGUILayout.TextField("表示名", editName);
            editLabel = EditorGUILayout.Popup("ラベル", editLabel, Labels.DisplayNames);
            editNote = EditorGUILayout.TextField("メモ", editNote);
            EditorGUILayout.LabelField("登録日時", FormatTime(e.registeredAt) + "   ファイル " + e.fileCount + " 件 / " + FolderWalker.FormatBytes(e.totalBytes)
                + "   GUID あり " + CountGuids(e) + " 件", smallWrap);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(engine.registry.readOnly))
                {
                    if (GUILayout.Button("変更を保存", GUILayout.Width(100)))
                    {
                        string name = editName.Trim(), label = Labels.Key((Label)editLabel), note = editNote;
                        Run(() =>
                        {
                            if (name.Length == 0) { SetMessage("表示名を入力してください。", MessageType.Warning); return; }
                            e.name = name; e.label = label; e.note = note;
                            string p = engine.registry.Save();
                            if (p == null) engine.Rematch();
                            Report(p, "辞書の「" + name + "」を更新しました。");
                        });
                    }
                    if (GUILayout.Button("削除…", GUILayout.Width(80)))
                    {
                        Run(() =>
                        {
                            if (!EditorUtility.DisplayDialog(Title, "辞書から「" + e.name + "」を削除しますか？\n(プロジェクトのフォルダやアセットは削除されません)", "削除", "キャンセル")) return;
                            string p = engine.registry.Remove(e.id);
                            selectedEntryId = "";
                            if (p == null) engine.Rematch();
                            Report(p, "辞書から「" + e.name + "」を削除しました。");
                        });
                    }
                }
                GUILayout.FlexibleSpace();
            }
        }

        static int CountGuids(RegistryEntry e)
        {
            int n = 0;
            foreach (var f in e.files) if (!string.IsNullOrEmpty(f.g)) n++;
            return n;
        }

        void PickExternalFolder()
        {
            string dir = EditorUtility.OpenFolderPanel("登録する元フォルダを選択 (プロジェクトの外)", "", "");
            if (string.IsNullOrEmpty(dir)) return;
            string full = Path.GetFullPath(dir);
            if (full.StartsWith(Path.GetFullPath(Application.dataPath), StringComparison.OrdinalIgnoreCase))
            {
                SetMessage("プロジェクト内のフォルダは、一覧でフォルダを選んで「このフォルダを辞書に登録…」から登録してください。", MessageType.Info);
                return;
            }
            var w = FolderWalker.Walk(full, true);
            var notes = new List<string>();
            if (w.truncated) notes.Add("ファイル数が多すぎるため、先頭 " + FolderWalker.MaxFiles + " 件だけを登録します。");
            if (w.errors.Count > 0) notes.Add("読み取れなかった場所が " + w.errors.Count + " 件あります (例: " + w.errors[0] + ")。");
            int packages = 0;
            foreach (var f in w.files) if (f.rel.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)) packages++;
            if (packages > 0) notes.Add("このフォルダには unitypackage が " + packages + " 件あります。アセットの中身で登録するには「unitypackage から登録…」を使ってください。");
            if (Directory.Exists(Path.Combine(full, "Assets"))) notes.Add("このフォルダの中に「Assets」フォルダがあります。プロジェクトの Assets 内に入るのと同じ階層のフォルダ (製品のフォルダ) を選ぶと、正しく照合できます。");
            if (w.files.Count == 0)
            {
                SetMessage("このフォルダにはファイルがありません。" + string.Join("\n", notes.ToArray()), MessageType.Warning);
                return;
            }
            string name = Path.GetFileName(full.TrimEnd('/', '\\'));
            pending = new List<RegistryEntry> { Fingerprints.FromWalk(w.files, name, Labels.Key(Label.Outfit), "external_folder", name) };
            pendingName = name;
            pendingLabel = (int)Label.Outfit;
            pendingNote = notes.Count > 0 ? string.Join("\n", notes.ToArray()) : null;
        }

        void PickUnityPackage()
        {
            string file = EditorUtility.OpenFilePanel("登録する unitypackage を選択", "", "unitypackage");
            if (string.IsNullOrEmpty(file)) return;
            string name = Path.GetFileNameWithoutExtension(file);
            string note;
            List<RegistryEntry> entries;
            try
            {
                entries = Fingerprints.FromUnityPackage(file, name, Labels.Key(Label.Outfit), out note);
            }
            catch (Exception e)
            {
                SetMessage("unitypackage を読み取れませんでした: " + e.Message, MessageType.Error);
                return;
            }
            if (entries.Count == 0)
            {
                SetMessage(note ?? "登録できる内容がありませんでした。", MessageType.Warning);
                return;
            }
            pending = entries;
            pendingName = name;
            pendingLabel = (int)Label.Outfit;
            pendingNote = note;
        }

        void DrawPending()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("登録の確認", EditorStyles.boldLabel);
                int files = 0;
                foreach (var e in pending) files += e.fileCount;
                EditorGUILayout.LabelField("読み取った内容", pending.Count + " 件 / 合計 " + files + " ファイル (元: " + pending[0].sourceName + ")", smallWrap);
                if (pending.Count > 1)
                {
                    foreach (var e in pending) EditorGUILayout.LabelField("・" + e.note + " (" + e.fileCount + " ファイル)", smallWrap);
                }
                if (!string.IsNullOrEmpty(pendingNote)) EditorGUILayout.HelpBox(pendingNote, MessageType.Info);
                pendingName = EditorGUILayout.TextField("表示名", pendingName);
                pendingLabel = EditorGUILayout.Popup("ラベル", pendingLabel, Labels.DisplayNames);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(pendingName)))
                    {
                        if (GUILayout.Button("登録する", GUILayout.Width(100)))
                        {
                            var list = pending;
                            string name = pendingName.Trim(), label = Labels.Key((Label)pendingLabel);
                            Run(() => CommitPending(list, name, label));
                        }
                    }
                    if (GUILayout.Button("やめる", GUILayout.Width(80))) pending = null;
                }
            }
        }

        void CommitPending(List<RegistryEntry> list, string name, string label)
        {
            foreach (var e in list)
            {
                e.label = label;
                if (list.Count == 1) e.name = name;
                else
                {
                    int p = e.name.LastIndexOf(" (", StringComparison.Ordinal);
                    e.name = name + (p >= 0 ? e.name.Substring(p) : "");
                }
                engine.registry.UserEntries.Add(e);
            }
            string problem = engine.registry.Save();
            pending = null;
            if (problem != null) { SetMessage(problem, MessageType.Error); return; }
            engine.Rematch();
            SetMessage("辞書に " + list.Count + " 件登録しました。" + (engine.index.folders.Count > 0 ? "現在の一覧で照合し直しました。" : ""), MessageType.Info);
        }

        void ExportRegistry()
        {
            string path = EditorUtility.SaveFilePanel("辞書を書き出し", "", "FolderLabeler_registry", "json");
            if (string.IsNullOrEmpty(path)) return;
            string problem = engine.registry.Export(path);
            Report(problem, "辞書を書き出しました (" + engine.registry.UserEntries.Count + " 件): " + path);
        }

        void ImportRegistry()
        {
            string path = EditorUtility.OpenFilePanel("辞書を読み込み", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            int added, replaced, skipped;
            string problem = engine.registry.Import(path, out added, out replaced, out skipped);
            if (problem == null) engine.Rematch();
            Report(problem, "辞書を読み込みました: 追加 " + added + " 件 / 同じ ID を置き換え " + replaced + " 件"
                + (skipped > 0 ? " / 内容が無く読み飛ばし " + skipped + " 件" : ""));
        }

        // ------------------------------------------------------------------
        // 設定
        // ------------------------------------------------------------------

        void DrawSettingsTab()
        {
            settingsScroll = EditorGUILayout.BeginScrollView(settingsScroll);
            var s = engine.registry.Settings;

            EditorGUILayout.LabelField("辞書ファイル", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(RegistryStore.CurrentPath, EditorStyles.textField, GUILayout.Height(18));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("場所を変更…", GUILayout.Width(110))) Run(ChangeRegistryPath);
                using (new EditorGUI.DisabledScope(!RegistryStore.IsCustomPath))
                {
                    if (GUILayout.Button("既定の場所に戻す", GUILayout.Width(130)))
                    {
                        Run(() =>
                        {
                            RegistryStore.SetPath(null);
                            engine.registry.Load();
                            engine.Rematch();
                            SetMessage("辞書ファイルの場所を既定に戻しました。", MessageType.Info);
                        });
                    }
                }
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.HelpBox("辞書ファイルの場所はこの PC の Unity の設定 (EditorPrefs) に保存します。辞書はすべてのプロジェクトで共通です。", MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("判定の設定 (辞書ファイルに保存・全プロジェクト共通)", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            float threshold = EditorGUILayout.Slider(new GUIContent("部分一致の基準 (%)",
                "辞書に登録したファイルのうち、この割合以上がフォルダにあれば「候補」として表示します。自作のファイルを足したフォルダでも当たるようにするための値です"),
                Mathf.Round(s.partialThreshold * 100f), 50f, 100f);
            int minFiles = EditorGUILayout.IntSlider(new GUIContent("部分一致に必要なファイル数", "これより少ないファイル数の登録は、完全一致のときだけ当てます (少数ファイルでの誤マッチ防止)"), s.minFilesForPartial, 1, 20);
            int prefabText = EditorGUILayout.IntSlider(new GUIContent("Prefab の確認上限 (文字列)", "1 フォルダで、中身を文字列として確認する Prefab の数の上限"), s.prefabTextLimit, 10, 2000);
            int prefabLoad = EditorGUILayout.IntSlider(new GUIContent("Prefab の読み込み上限", "1 フォルダで、Descriptor らしい Prefab を読み込んで型を確認する数の上限"), s.prefabLoadLimit, 1, 50);
            int modelLoad = EditorGUILayout.IntSlider(new GUIContent("モデルの読み込み上限", "1 フォルダで、Skinned Mesh の有無を確かめるために読み込むモデルの数の上限"), s.modelLoadLimit, 1, 20);
            if (EditorGUI.EndChangeCheck())
            {
                s.partialThreshold = threshold / 100f;
                s.minFilesForPartial = minFiles;
                s.prefabTextLimit = prefabText;
                s.prefabLoadLimit = prefabLoad;
                s.modelLoadLimit = modelLoad;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(engine.registry.readOnly))
                {
                    if (GUILayout.Button("設定を保存", GUILayout.Width(110)))
                        Run(() => { string p = engine.registry.Save(); if (p == null) engine.Rematch(); Report(p, "設定を保存しました。読み込み上限の変更は次の全走査から反映されます。"); });
                }
                if (GUILayout.Button("初期値に戻す", GUILayout.Width(110)))
                {
                    Run(() => { engine.registry.data.settings = new RegistrySettings(); string p = engine.registry.Save(); Report(p, "設定を初期値に戻しました。"); });
                }
                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("索引 (このプロジェクトの走査結果と手動ラベル)", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(Engine.IndexPath, EditorStyles.textField, GUILayout.Height(18));
            EditorGUILayout.HelpBox("索引は Assets の外 (プロジェクト直下の FolderLabeler フォルダ) に保存します。削除すると初回の状態に戻ります (手動ラベルも消えます。辞書は消えません)。", MessageType.None);
            if (GUILayout.Button("索引を削除して初期状態に戻す…", GUILayout.Width(220)))
            {
                Run(() =>
                {
                    int locked = 0;
                    foreach (var r in engine.index.folders) if (r.IsLocked) locked++;
                    if (!EditorUtility.DisplayDialog(Title, "索引 (FolderLabeler/index.json) を削除して初期状態に戻しますか？"
                            + (locked > 0 ? "\n手動で確定したラベル " + locked + " 件も消えます。" : ""), "削除", "キャンセル")) return;
                    string p = engine.DeleteIndex();
                    selectedGuid = "";
                    Report(p, "索引を削除しました。");
                });
            }
            EditorGUILayout.EndScrollView();
        }

        void ChangeRegistryPath()
        {
            string current = RegistryStore.CurrentPath;
            string path = EditorUtility.SaveFilePanel("辞書ファイルの場所", Path.GetDirectoryName(current), "registry", "json");
            if (string.IsNullOrEmpty(path)) return;
            var entries = engine.registry.UserEntries;
            bool copy = false;
            if (!File.Exists(path) && entries.Count > 0)
            {
                copy = EditorUtility.DisplayDialog(Title, "新しい場所にはまだ辞書がありません。今の辞書 (" + entries.Count + " 件) を新しい場所にコピーしますか？", "コピーする", "空の辞書で始める");
            }
            var oldData = engine.registry.data;
            RegistryStore.SetPath(path);
            engine.registry.Load();
            if (copy)
            {
                engine.registry.data = oldData;
                string p = engine.registry.Save();
                if (p != null) { SetMessage(p, MessageType.Error); return; }
            }
            engine.Rematch();
            SetMessage("辞書ファイルの場所を変更しました: " + path, MessageType.Info);
        }
    }
}
