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
        HandGesture _gesture = HandGesture.BuiltIn[0].Clone();
        string _loadedPath;

        readonly bool[] _fingerFold = { true, true, true, true, true };
        Vector2 _scroll, _libScroll;
        List<(string path, HandGesture gesture)> _library = new List<(string, HandGesture)>();
        string _libFilter = "";

        // プレビュー前のトランスフォームを保持して、終了時に完全に元へ戻す
        Animator _snapshotOwner;
        readonly List<(Transform t, Vector3 p, Quaternion r, Vector3 s)> _snapshot =
            new List<(Transform, Vector3, Quaternion, Vector3)>();

        [MenuItem("Tools/Hand Gesture Creator")]
        public static void Open()
        {
            var w = GetWindow<HandGestureWindow>("Hand Gesture Creator");
            w.minSize = new Vector2(380, 520);
        }

        void OnEnable()
        {
            RefreshLibrary();
            if (_animator == null && Selection.activeGameObject != null)
                _animator = Selection.activeGameObject.GetComponentInParent<Animator>();
        }

        void OnDisable() => RestoreSnapshot();

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
                            _gesture = HandGestureAnimUtility.ReadFromAvatar(_animator,
                                _target == Target.Left ? HandSide.Left : HandSide.Right);
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
            EditorGUILayout.LabelField("指の編集  (曲げ: 1=伸ばす / -1=曲げる)", EditorStyles.boldLabel);
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
                    }

                    if (!_fingerFold[f]) continue;
                    EditorGUI.indentLevel++;
                    for (int p = 0; p < HandGesture.MusclesPerFinger; p++)
                        _gesture.Set(f, p, EditorGUILayout.Slider(HandGesture.PartsJa[p], _gesture.Get(f, p), -1f, 1f));
                    EditorGUI.indentLevel--;
                }
            }
            if (EditorGUI.EndChangeCheck()) ApplyPreview();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("全て開く")) SetAllFold(true);
                if (GUILayout.Button("全て閉じる")) SetAllFold(false);
                if (GUILayout.Button("値をリセット"))
                {
                    _gesture.muscles = new float[HandGesture.MuscleCount];
                    ApplyPreview();
                }
            }
        }

        void SetAllFold(bool v) { for (int i = 0; i < _fingerFold.Length; i++) _fingerFold[i] = v; }

        void DrawSaveExportSection()
        {
            EditorGUILayout.LabelField("保存 / 書き出し", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _gesture.name = EditorGUILayout.TextField("ジェスチャー名", _gesture.name);

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
            foreach (var t in _animator.GetComponentsInChildren<Transform>(true))
                _snapshot.Add((t, t.localPosition, t.localRotation, t.localScale));
        }

        void RestoreSnapshot()
        {
            foreach (var (t, p, r, s) in _snapshot)
            {
                if (t == null) continue;
                t.localPosition = p; t.localRotation = r; t.localScale = s;
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
            HandGestureAnimUtility.ApplyToAvatar(_animator,
                _target != Target.Right ? _gesture : null,
                _target != Target.Left ? _gesture : null);
            SceneView.RepaintAll();
        }

        void FocusHand()
        {
            var bone = _animator.GetBoneTransform(_target == Target.Left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            var sv = SceneView.lastActiveSceneView;
            if (bone == null || sv == null) return;
            var b = new Bounds(bone.position, Vector3.one * 0.15f);
            foreach (var t in bone.GetComponentsInChildren<Transform>()) b.Encapsulate(t.position);
            sv.Frame(b, false);
        }

        // ================= Import / Export =================

        void ExportAnim()
        {
            var suffix = _target == Target.Left ? "_L" : _target == Target.Right ? "_R" : "_LR";
            var path = EditorUtility.SaveFilePanelInProject("AnimationClip を書き出し",
                _gesture.name + suffix, "anim", "VRChatの FX / Gesture レイヤーに割り当てられます");
            if (string.IsNullOrEmpty(path)) return;
            var clip = HandGestureAnimUtility.CreateClip(
                _target != Target.Right ? _gesture : null,
                _target != Target.Left ? _gesture : null);
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
            var first = _target == Target.Left ? HandSide.Left : HandSide.Right;
            var g = HandGestureAnimUtility.ReadFromClip(clip, first, out bool found);
            if (!found) g = HandGestureAnimUtility.ReadFromClip(clip, first == HandSide.Left ? HandSide.Right : HandSide.Left, out found);
            if (!found)
            {
                EditorUtility.DisplayDialog("エラー", "このクリップには指のマッスルカーブがありません。", "OK");
                return;
            }
            _gesture = g;
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
            var g = HandGesture.FromJson(File.ReadAllText(path));
            if (g == null) return;
            _gesture = g;
            _loadedPath = null;
            ApplyPreview();
        }
    }
}
