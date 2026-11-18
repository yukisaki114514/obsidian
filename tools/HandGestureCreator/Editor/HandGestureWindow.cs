using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HandGestureCreator
{
    public class HandGestureWindow : EditorWindow
    {
        enum Target { Left, Right, Both }

        Animator _animator;
        Target _target = Target.Right;
        bool _livePreview = true;
        // 両手モードで「左右同じ」をOFFにすると左右別々に編集できる
        [SerializeField] HandGesture _gestureR = HandGesture.BuiltIn[0].Clone();
        [SerializeField] HandGesture _gestureL = HandGesture.BuiltIn[0].Clone();
        [SerializeField] bool _symmetric = true;
        [SerializeField] HandSide _editSide = HandSide.Right;
        string _loadedPath;

        /// <summary>今スライダーで編集している手</summary>
        HandSide EditingSide => _target == Target.Left ? HandSide.Left
            : _target == Target.Right ? HandSide.Right
            : _symmetric ? HandSide.Right : _editSide;

        HandGesture _gesture
        {
            get => EditingSide == HandSide.Left ? _gestureL : _gestureR;
            set
            {
                if (EditingSide == HandSide.Left) _gestureL = value; else _gestureR = value;
                if (_target == Target.Both && _symmetric) { _gestureR = value; _gestureL = value.Clone(); }
            }
        }

        HandGesture LeftForApply => _target == Target.Right ? null
            : _target == Target.Both && _symmetric ? _gestureR : _gestureL;
        HandGesture RightForApply => _target == Target.Left ? null : _gestureR;

        readonly bool[] _fingerFold = { true, true, true, true, true };
        Vector2 _scroll, _libScroll;
        List<(string path, HandGesture gesture)> _library = new List<(string, HandGesture)>();
        string _libFilter = "";

        // プレビュー前の「指ボーンだけ」のTransformを保持し、終了時に元へ戻す。
        // [SerializeField] によりドメインリロード後も保持される。
        [System.Serializable]
        struct BoneState { public Transform t; public Vector3 p; public Quaternion r; public Vector3 s; }
        [SerializeField] Animator _snapshotOwner;
        [SerializeField] List<BoneState> _snapshot = new List<BoneState>();

        [MenuItem("Tools/Hand Gesture Creator")]
        public static void Open()
        {
            var w = GetWindow<HandGestureWindow>($"{HandGestureInfo.ProductName} v{HandGestureInfo.Version}");
            w.minSize = new Vector2(380, 520);
        }

        void OnEnable()
        {
            AssemblyReloadEvents.beforeAssemblyReload += RestoreSnapshot;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.quitting += RestoreSnapshot;
            RefreshLibrary();
            if (_animator == null && Selection.activeGameObject != null)
                _animator = Selection.activeGameObject.GetComponentInParent<Animator>();
        }

        void OnDisable()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= RestoreSnapshot;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.quitting -= RestoreSnapshot;
            RestoreSnapshot();
        }

        void OnPlayModeChanged(PlayModeStateChange s)
        {
            if (s == PlayModeStateChange.ExitingEditMode) RestoreSnapshot();
        }

        void OnDestroy() => RestoreSnapshot();

        void RefreshLibrary() => _library = HandGestureLibrary.LoadAll();

        // ================= GUI =================

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawAvatarSection();
            EditorGUILayout.Space();
            DrawPresetSection();
            EditorGUILayout.Space();
            DrawFingerSection();
            EditorGUILayout.Space();
            DrawSaveExportSection();
            EditorGUILayout.Space();
            DrawLibrarySection();
            EditorGUILayout.EndScrollView();
        }

        void DrawAvatarSection()
        {
            EditorGUILayout.LabelField("アバター", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                var a = (Animator)EditorGUILayout.ObjectField("Animator", _animator, typeof(Animator), true);
                if (EditorGUI.EndChangeCheck())
                {
                    RestoreSnapshot();
                    _animator = a;
                    ApplyPreview();
                }

                if (_animator != null && !HandGestureAnimUtility.IsValidHumanoid(_animator))
                    EditorGUILayout.HelpBox("Humanoid の Avatar が設定された Animator を指定してください。", MessageType.Error);

                EditorGUI.BeginChangeCheck();
                _target = (Target)GUILayout.Toolbar((int)_target, new[] { "左手", "右手", "両手" });
                if (_target == Target.Both)
                {
                    bool sym = EditorGUILayout.Toggle("左右同じ形", _symmetric);
                    if (sym != _symmetric)
                    {
                        _symmetric = sym;
                        if (sym) _gestureL = _gestureR.Clone();
                    }
                    if (!_symmetric)
                        _editSide = (HandSide)GUILayout.Toolbar((int)_editSide, new[] { "左手を編集", "右手を編集" });
                }
                bool live = EditorGUILayout.Toggle("ライブプレビュー", _livePreview);
                if (EditorGUI.EndChangeCheck())
                {
                    RestoreSnapshot();
                    _livePreview = live;
                    ApplyPreview();
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!HandGestureAnimUtility.IsValidHumanoid(_animator)))
                    {
                        if (GUILayout.Button("手にフォーカス")) FocusHand();
                        if (GUILayout.Button("アバターの現在の指を読み取る"))
                        {
                            RestoreSnapshot();
                            if (_target != Target.Right) _gestureL = HandGestureAnimUtility.ReadFromAvatar(_animator, HandSide.Left);
                            if (_target != Target.Left) _gestureR = HandGestureAnimUtility.ReadFromAvatar(_animator, HandSide.Right);
                            if (_target == Target.Both) _symmetric = false; // 読み取った左右差を保持
                            _loadedPath = null;
                            ApplyPreview();
                        }
                    }
                    if (GUILayout.Button("ポーズを元に戻す")) RestoreSnapshot();
                }
            }
        }

        void DrawPresetSection()
        {
            EditorGUILayout.LabelField("クイックプリセット", EditorStyles.boldLabel);
            var presets = HandGesture.BuiltIn;
            int perRow = Mathf.Max(1, (int)((position.width - 20) / 90));
            for (int i = 0; i < presets.Length; i += perRow)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int j = i; j < Mathf.Min(i + perRow, presets.Length); j++)
                    {
                        if (GUILayout.Button(presets[j].name))
                        {
                            _gesture = presets[j].Clone();
                            _loadedPath = null;
                            ApplyPreview();
                        }
                    }
                }
            }
        }

        void DrawFingerSection()
        {
            var editing = _target == Target.Both && _symmetric ? "両手" : EditingSide == HandSide.Left ? "左手" : "右手";
            EditorGUILayout.LabelField($"指の編集 [{editing}]  (曲げ: 1=伸ばす / -1=曲げる)", EditorStyles.boldLabel);
            bool changed = false;
            EditorGUI.BeginChangeCheck();
            for (int f = 0; f < HandGesture.FingerCount; f++)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    _fingerFold[f] = EditorGUILayout.Foldout(_fingerFold[f], HandGesture.FingersJa[f], true);

                    // 指全体の曲げを一括調整(1〜3関節の平均)
                    float avg = (_gesture.Get(f, 0) + _gesture.Get(f, 2) + _gesture.Get(f, 3)) / 3f;
                    EditorGUI.BeginChangeCheck();
                    float nAvg = EditorGUILayout.Slider("  全体の曲げ", avg, -1f, 1f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        float d = nAvg - avg;
                        foreach (var p in new[] { 0, 2, 3 }) _gesture.Set(f, p, _gesture.Get(f, p) + d);
                        changed = true;
                    }

                    if (!_fingerFold[f]) continue;
                    EditorGUI.indentLevel++;
                    for (int p = 0; p < HandGesture.MusclesPerFinger; p++)
                        _gesture.Set(f, p, EditorGUILayout.Slider(HandGesture.PartsJa[p], _gesture.Get(f, p), -1f, 1f));
                    EditorGUI.indentLevel--;
                }
            }
            if (EditorGUI.EndChangeCheck() || changed) { SyncSymmetric(); ApplyPreview(); }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("全て開く")) SetAllFold(true);
                if (GUILayout.Button("全て閉じる")) SetAllFold(false);
                if (GUILayout.Button("値をリセット"))
                {
                    _gesture.muscles = new float[HandGesture.MuscleCount];
                    SyncSymmetric();
                    ApplyPreview();
                }
            }
        }

        void SyncSymmetric()
        {
            if (_target == Target.Both && _symmetric) _gestureL = _gestureR.Clone();
        }

        void SetAllFold(bool v) { for (int i = 0; i < _fingerFold.Length; i++) _fingerFold[i] = v; }

        void DrawSaveExportSection()
        {
            EditorGUILayout.LabelField("保存 / 書き出し", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _gesture.name = EditorGUILayout.TextField("ジェスチャー名", _gesture.name);
                if (_target == Target.Both && !_symmetric)
                    EditorGUILayout.HelpBox("ライブラリ/JSONには編集中の手の形のみ保存されます。左右別の形は .anim 書き出しで保存してください。", MessageType.Info);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("ライブラリに新規保存"))
                    {
                        _loadedPath = HandGestureLibrary.Save(_gesture, false);
                        RefreshLibrary();
                        ShowNotification(new GUIContent("保存しました"));
                    }
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_loadedPath)))
                    {
                        if (GUILayout.Button("上書き保存"))
                        {
                            File.WriteAllText(_loadedPath, _gesture.ToJson());
                            RefreshLibrary();
                            ShowNotification(new GUIContent("上書きしました"));
                        }
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(".anim として書き出し")) ExportAnim();
                    if (GUILayout.Button(".anim から読み込み")) ImportAnim();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("JSON 書き出し")) ExportJson();
                    if (GUILayout.Button("JSON 読み込み")) ImportJson();
                }
            }
        }

        void DrawLibrarySection()
        {
            EditorGUILayout.LabelField("ライブラリ (全プロジェクト共通)", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.SelectableLabel(HandGestureLibrary.LibraryPath, EditorStyles.miniLabel,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    if (GUILayout.Button("変更", GUILayout.Width(44)))
                    {
                        var p = EditorUtility.OpenFolderPanel("ライブラリの保存先", HandGestureLibrary.LibraryPath, "");
                        if (!string.IsNullOrEmpty(p)) { HandGestureLibrary.LibraryPath = p; RefreshLibrary(); }
                    }
                    if (GUILayout.Button("開く", GUILayout.Width(44)))
                    {
                        HandGestureLibrary.EnsureDir();
                        EditorUtility.RevealInFinder(HandGestureLibrary.LibraryPath);
                    }
                    if (GUILayout.Button("更新", GUILayout.Width(44))) RefreshLibrary();
                }

                _libFilter = EditorGUILayout.TextField("検索", _libFilter);

                if (_library.Count == 0)
                    EditorGUILayout.LabelField("保存されたジェスチャーはありません。", EditorStyles.centeredGreyMiniLabel);

                _libScroll = EditorGUILayout.BeginScrollView(_libScroll, GUILayout.MaxHeight(220));
                string toDelete = null;
                foreach (var (path, g) in _library)
                {
                    if (!string.IsNullOrEmpty(_libFilter) &&
                        g.name.IndexOf(_libFilter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var style = path == _loadedPath ? EditorStyles.boldLabel : EditorStyles.label;
                        EditorGUILayout.LabelField(g.name, style);
                        if (GUILayout.Button("読込", GUILayout.Width(44)))
                        {
                            _gesture = g.Clone();
                            _loadedPath = path;
                            ApplyPreview();
                        }
                        if (GUILayout.Button(".anim", GUILayout.Width(50)))
                        {
                            _gesture = g.Clone();
                            _loadedPath = path;
                            ApplyPreview();
                            ExportAnim();
                        }
                        if (GUILayout.Button("×", GUILayout.Width(24)) &&
                            EditorUtility.DisplayDialog("削除", $"「{g.name}」を削除しますか？", "削除", "キャンセル"))
                            toDelete = path;
                    }
                }
                EditorGUILayout.EndScrollView();

                if (toDelete != null)
                {
                    HandGestureLibrary.Delete(toDelete);
                    if (_loadedPath == toDelete) _loadedPath = null;
                    RefreshLibrary();
                }
            }
        }

        // ================= Preview =================

        void TakeSnapshot()
        {
            if (_snapshotOwner == _animator && _snapshot.Count > 0) return;
            RestoreSnapshot();
            _snapshotOwner = _animator;
            foreach (var t in HandGestureAnimUtility.FingerBones(_animator))
                _snapshot.Add(new BoneState { t = t, p = t.localPosition, r = t.localRotation, s = t.localScale });
        }

        void RestoreSnapshot()
        {
            if (_snapshot == null) _snapshot = new List<BoneState>();
            foreach (var b in _snapshot)
            {
                if (b.t == null) continue;
                b.t.localPosition = b.p; b.t.localRotation = b.r; b.t.localScale = b.s;
            }
            _snapshot.Clear();
            _snapshotOwner = null;
            SceneView.RepaintAll();
        }

        void ApplyPreview()
        {
            if (!_livePreview || !HandGestureAnimUtility.IsValidHumanoid(_animator)) return;
            if (EditorApplication.isPlaying) return;
            TakeSnapshot();
            HandGestureAnimUtility.ApplyToAvatar(_animator, LeftForApply, RightForApply);
            SceneView.RepaintAll();
        }

        void FocusHand()
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null) return;
            Bounds? b = null;
            foreach (var hb in new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand })
            {
                if (_target == Target.Left && hb == HumanBodyBones.RightHand) continue;
                if (_target == Target.Right && hb == HumanBodyBones.LeftHand) continue;
                var bone = _animator.GetBoneTransform(hb);
                if (bone == null) continue;
                var bb = b ?? new Bounds(bone.position, Vector3.one * 0.15f);
                foreach (var t in bone.GetComponentsInChildren<Transform>()) bb.Encapsulate(t.position);
                b = bb;
            }
            if (b.HasValue) sv.Frame(b.Value, false);
        }

        // ================= Import / Export =================

        void ExportAnim()
        {
            var suffix = _target == Target.Left ? "_L" : _target == Target.Right ? "_R" : "_LR";
            var path = EditorUtility.SaveFilePanelInProject("AnimationClip を書き出し",
                _gesture.name + suffix, "anim", "VRChatの FX / Gesture レイヤーに割り当てられます");
            if (string.IsNullOrEmpty(path)) return;
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null &&
                !EditorUtility.DisplayDialog("上書き確認", $"{path} は既に存在します。上書きしますか？\n(このクリップを参照しているAnimatorの設定は保持されます)", "上書き", "キャンセル"))
                return;
            var clip = HandGestureAnimUtility.CreateClip(LeftForApply, RightForApply);
            HandGestureAnimUtility.SaveClipAsset(clip, path);
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<AnimationClip>(path));
        }

        void ImportAnim()
        {
            var abs = EditorUtility.OpenFilePanel("AnimationClip を読み込み", Application.dataPath, "anim");
            if (string.IsNullOrEmpty(abs)) return;
            var dataPath = Application.dataPath.Replace('\\', '/');
            abs = abs.Replace('\\', '/');
            if (!abs.StartsWith(dataPath))
            {
                EditorUtility.DisplayDialog("エラー", "プロジェクトの Assets 内の .anim を選択してください。", "OK");
                return;
            }
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets" + abs.Substring(dataPath.Length));
            if (clip == null) return;

            // 選択中の手 → 無ければもう片方の手 の順に探す
            if (_target == Target.Both)
            {
                var l = HandGestureAnimUtility.ReadFromClip(clip, HandSide.Left, out bool fl);
                var r = HandGestureAnimUtility.ReadFromClip(clip, HandSide.Right, out bool fr);
                if (fl || fr)
                {
                    _gestureL = fl ? l : r.Clone();
                    _gestureR = fr ? r : l.Clone();
                    _symmetric = !(fl && fr);
                    _loadedPath = null;
                    ApplyPreview();
                    return;
                }
            }
            var first = _target == Target.Left ? HandSide.Left : HandSide.Right;
            var g = HandGestureAnimUtility.ReadFromClip(clip, first, out bool found);
            if (!found) g = HandGestureAnimUtility.ReadFromClip(clip, first == HandSide.Left ? HandSide.Right : HandSide.Left, out found);
            if (!found)
            {
                EditorUtility.DisplayDialog("エラー", "このクリップには指のマッスルカーブがありません。", "OK");
                return;
            }
            _gesture = g;
            SyncSymmetric();
            _loadedPath = null;
            ApplyPreview();
        }

        void ExportJson()
        {
            var path = EditorUtility.SaveFilePanel("JSON 書き出し", "", _gesture.name, "json");
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, _gesture.ToJson());
        }

        void ImportJson()
        {
            var path = EditorUtility.OpenFilePanel("JSON 読み込み", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            HandGesture g = null;
            try { g = HandGesture.FromJson(File.ReadAllText(path)); }
            catch (System.Exception e) { Debug.LogWarning($"[HandGestureCreator] JSON読み込み失敗: {e.Message}"); }
            if (g == null)
            {
                EditorUtility.DisplayDialog("エラー", "ジェスチャーのJSONとして読み込めませんでした。", "OK");
                return;
            }
            _gesture = g;
            SyncSymmetric();
            _loadedPath = null;
            ApplyPreview();
        }
    }
}
