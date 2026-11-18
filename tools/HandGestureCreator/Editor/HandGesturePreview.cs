using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HandGestureCreator
{
    /// <summary>
    /// 非破壊プレビュー: アバターの「Transform と描画用レンダラーだけ」を写したコピーを作り、そこに指の形を付けて表示する。
    /// (Hand Gesture Fit のシーンプレビューと同じ決まりに従う)
    ///   ・実アバターには一切書き込まない (読むだけ)
    ///   ・コピーは全オブジェクトに HideAndDontSave (Hierarchy に出ず、シーン・ビルドに保存されない)
    ///   ・Undo に登録せず、シーンを変更済みにする操作も呼ばない。アセットも作らない
    ///   ・Play モード開始前 / スクリプト再読み込み前 / シーン保存前 / Unity 終了 / シーン切り替えでコピーを破棄
    /// </summary>
    internal sealed class HandGesturePreview
    {
        /// <summary>コピーのルート名の目印。異常終了で取り残されたコピーを見分けて片付けるのに使う。</summary>
        public const string RootMarker = "[HandGestureCreator Preview] ";

        // 表示位置: シーン原点の真上。実アバターや他のオブジェクトと重ならないようにする
        static readonly Vector3 Anchor = new Vector3(0f, 100f, 0f);

        public bool Active { get; private set; }
        public string Problem { get; private set; }
        public string Notice { get; private set; }

        Animator _source;
        GameObject _root;
        HumanPoseHandler _handler;
        readonly Dictionary<Transform, Transform> _map = new Dictionary<Transform, Transform>();
        readonly List<(Transform t, Vector3 p, Quaternion r, Vector3 s)> _rest = new List<(Transform, Vector3, Quaternion, Vector3)>();
        int _rendererCount;

        int _sceneHandle;
        bool _sceneWasDirty, _dirtyWarned;
        bool _pausedForPrefab;

        SceneView _cameraView;
        Vector3 _savedPivot; Quaternion _savedRotation; float _savedSize; bool _savedOrtho; bool _cameraSaved;
        bool _frameRequested;

        // ---------------- 開始・終了 ----------------

        public void Start()
        {
            if (Active) return;
            Problem = Notice = null;
            _dirtyWarned = false;
            _pausedForPrefab = false;
            _frameRequested = true;
            var scene = SceneManager.GetActiveScene();
            _sceneHandle = scene.handle;
            _sceneWasDirty = scene.isDirty;
            Hook(true);
            Active = true;
        }

        public void Stop(string reason = null)
        {
            if (!Active) { if (reason != null) Problem = reason; return; }
            Active = false;
            Hook(false);
            ReleaseCopy();
            RestoreCamera();
            Problem = reason;
            Notice = null;
            SceneView.RepaintAll();
        }

        public void RequestFrame() => _frameRequested = true;

        void Hook(bool on)
        {
            EditorApplication.playModeStateChanged -= OnPlayMode;
            AssemblyReloadEvents.beforeAssemblyReload -= OnReload;
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorApplication.quitting -= OnReload;
            SceneView.duringSceneGui -= OnSceneGui;
            if (!on) return;
            EditorApplication.playModeStateChanged += OnPlayMode;
            AssemblyReloadEvents.beforeAssemblyReload += OnReload;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorApplication.quitting += OnReload;
            SceneView.duringSceneGui += OnSceneGui;
        }

        void OnPlayMode(PlayModeStateChange c)
        {
            // 非保存オブジェクトは Play モードへ持ち越されるので、入る前に必ず破棄する
            if (c == PlayModeStateChange.ExitingEditMode) Stop("Play モードに入るため、プレビューを終了しました。");
        }

        void OnReload() => Stop();

        // 保存されない設定だが、念のため保存直前に破棄。次の更新で作り直す
        void OnSceneSaving(Scene s, string p) => ReleaseCopy();

        void ReleaseCopy()
        {
            _handler?.Dispose();
            _handler = null;
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
            _root = null;
            _source = null;
            _map.Clear();
            _rest.Clear();
        }

        /// <summary>異常終了などで取り残されたコピーを破棄する。プレビューしていないときに呼ぶ。</summary>
        public static int SweepOrphans()
        {
            int count = 0;
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || go.transform.parent != null || EditorUtility.IsPersistent(go)) continue;
                if (go.name == null || !go.name.StartsWith(RootMarker)) continue;
                if ((go.hideFlags & HideFlags.DontSave) != HideFlags.DontSave) continue;
                UnityEngine.Object.DestroyImmediate(go);
                count++;
            }
            return count;
        }

        // ---------------- 更新 ----------------

        /// <summary>手の形を表示する。left/right が null の手はアバター本来の形のまま。表示が変わったら true。</summary>
        public bool Update(Animator avatar, HandGesture left, HandGesture right, string label)
        {
            if (!Active) return false;
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    Stop("Play モードのため、プレビューを終了しました。");
                    return true;
                }
                if (SceneManager.GetActiveScene().handle != _sceneHandle)
                {
                    Stop("シーンが切り替わったため、プレビューを終了しました。");
                    return true;
                }
                if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                {
                    if (_pausedForPrefab) return false;
                    ReleaseCopy();
                    _pausedForPrefab = true;
                    Notice = "プレハブの編集画面を開いているあいだはプレビューできません。閉じると再開します。";
                    return true;
                }
                if (_pausedForPrefab) { _pausedForPrefab = false; _frameRequested = true; Notice = null; }

                if (!HandGestureAnimUtility.IsValidHumanoid(avatar))
                {
                    ReleaseCopy();
                    Notice = "Humanoid のアバターを指定してください。";
                    return true;
                }

                if (_root == null || _source != avatar) CreateCopy(avatar);
                Pose(left, right);
                _caption = "Hand Gesture Creator プレビュー (表示専用・保存されません)" + (string.IsNullOrEmpty(label) ? "" : "\n" + label);

                if (_frameRequested)
                {
                    Notice = _rendererCount == 0 ? "アバターに表示中のメッシュがありません。" : null;
                    if (!FrameCamera(left != null, right != null))
                        Notice = "Scene ビューが開かれていません。開いてから「カメラを合わせ直す」を押してください。";
                    _frameRequested = false;
                }

                if (!_sceneWasDirty && !_dirtyWarned && SceneManager.GetActiveScene().isDirty)
                {
                    _dirtyWarned = true;
                    Debug.LogWarning("[Hand Gesture Creator] プレビュー表示中にシーンが「未保存の変更あり」になりました。"
                        + "プレビュー用コピーは保存されませんが、原因が本ツールにある場合はお知らせください。");
                }
                SceneView.RepaintAll();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Stop("プレビューを表示できませんでした: " + e.Message);
                return true;
            }
        }

        void CreateCopy(Animator avatar)
        {
            ReleaseCopy();
            _source = avatar;
            _root = new GameObject(RootMarker + avatar.gameObject.name) { hideFlags = HideFlags.HideAndDontSave };
            _root.transform.localScale = avatar.transform.lossyScale;
            _map[avatar.transform] = _root.transform;
            CopyChildren(avatar.transform, _root.transform);
            _rendererCount = AttachRenderers(avatar);
            CheckNotSaved(_root.transform);

            _root.transform.SetPositionAndRotation(Anchor, Quaternion.identity);
            foreach (var t in _root.GetComponentsInChildren<Transform>(true))
                _rest.Add((t, t.localPosition, t.localRotation, t.localScale));
            _handler = new HumanPoseHandler(avatar.avatar, _root.transform);
        }

        void Pose(HandGesture left, HandGesture right)
        {
            // 毎回コピーの初期状態から付け直す (実アバターの現在の姿勢を基準にする)
            foreach (var (t, p, r, s) in _rest) { t.localPosition = p; t.localRotation = r; t.localScale = s; }
            if (left == null && right == null) return;

            var fingers = new HashSet<Transform>();
            if (left != null) fingers.UnionWith(MappedFingers(HandSide.Left));
            if (right != null) fingers.UnionWith(MappedFingers(HandSide.Right));

            var pose = new HumanPose();
            _handler.GetHumanPose(ref pose);
            if (left != null) Write(ref pose, HandSide.Left, left);
            if (right != null) Write(ref pose, HandSide.Right, right);
            _handler.SetHumanPose(ref pose);

            // 指以外は初期状態へ戻す
            foreach (var (t, p, r, s) in _rest)
                if (!fingers.Contains(t)) { t.localPosition = p; t.localRotation = r; t.localScale = s; }
        }

        IEnumerable<Transform> MappedFingers(HandSide side)
        {
            foreach (var b in HandGestureAnimUtility.FingerBones(_source, side))
                if (_map.TryGetValue(b, out var m)) yield return m;
        }

        static void Write(ref HumanPose pose, HandSide side, HandGesture g)
        {
            for (int f = 0; f < HandGesture.FingerCount; f++)
            for (int p = 0; p < HandGesture.MusclesPerFinger; p++)
            {
                int idx = HandGesture.MuscleIndex(side, f, p);
                if (idx >= 0) pose.muscles[idx] = g.Get(f, p);
            }
        }

        void CopyChildren(Transform from, Transform to)
        {
            for (int i = 0; i < from.childCount; i++)
            {
                var child = from.GetChild(i);
                var t = new GameObject(child.name) { hideFlags = HideFlags.HideAndDontSave }.transform;
                t.SetParent(to, false);
                t.localPosition = child.localPosition;
                t.localRotation = child.localRotation;
                t.localScale = child.localScale;
                _map[child] = t;
                CopyChildren(child, t);
            }
        }

        /// <summary>表示中のメッシュを描画専用として写す。メッシュ・マテリアルは参照のみ(複製・変更しない)。</summary>
        int AttachRenderers(Animator avatar)
        {
            int count = 0;
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(false))
            {
                if (renderer == null || !renderer.enabled) continue;
                if (!_map.TryGetValue(renderer.transform, out var owner)) continue;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    if (skinned.sharedMesh == null) continue;
                    var copy = owner.gameObject.AddComponent<SkinnedMeshRenderer>();
                    copy.sharedMesh = skinned.sharedMesh;
                    copy.sharedMaterials = skinned.sharedMaterials;
                    var bones = skinned.bones;
                    var mapped = new Transform[bones.Length];
                    for (int i = 0; i < bones.Length; i++)
                        mapped[i] = bones[i] != null && _map.TryGetValue(bones[i], out var b) ? b : null;
                    copy.bones = mapped;
                    if (skinned.rootBone != null && _map.TryGetValue(skinned.rootBone, out var rb)) copy.rootBone = rb;
                    copy.localBounds = skinned.localBounds;
                    copy.updateWhenOffscreen = true;
                    // 編集モードでもボーンの変更を毎回メッシュに反映させる
                    copy.forceMatrixRecalculationPerRender = true;
                    for (int i = 0; i < skinned.sharedMesh.blendShapeCount; i++)
                        copy.SetBlendShapeWeight(i, skinned.GetBlendShapeWeight(i));
                    count++;
                }
                else if (renderer is MeshRenderer)
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    owner.gameObject.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                    owner.gameObject.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
                    count++;
                }
            }
            return count;
        }

        static void CheckNotSaved(Transform t)
        {
            if ((t.gameObject.hideFlags & HideFlags.DontSave) != HideFlags.DontSave)
                throw new InvalidOperationException($"プレビュー用コピーに保存しない設定が付いていません ({t.name})。シーンを汚さないよう中止しました。");
            for (int i = 0; i < t.childCount; i++) CheckNotSaved(t.GetChild(i));
        }

        // ---------------- カメラ ----------------

        bool FrameCamera(bool left, bool right)
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null || _root == null) return false;
            if (!_cameraSaved)
            {
                _cameraView = view;
                _savedPivot = view.pivot; _savedRotation = view.rotation;
                _savedSize = view.size; _savedOrtho = view.orthographic;
                _cameraSaved = true;
            }
            Bounds? b = null;
            foreach (var (side, bone) in new[] { (HandSide.Left, HumanBodyBones.LeftHand), (HandSide.Right, HumanBodyBones.RightHand) })
            {
                if (side == HandSide.Left && !left && right) continue;
                if (side == HandSide.Right && !right && left) continue;
                var src = _source.GetBoneTransform(bone);
                if (src == null || !_map.TryGetValue(src, out var hand)) continue;
                var bb = b ?? new Bounds(hand.position, Vector3.one * 0.05f);
                foreach (var t in hand.GetComponentsInChildren<Transform>()) bb.Encapsulate(t.position);
                b = bb;
            }
            if (!b.HasValue) return true;
            // アバターの正面やや上から見る
            view.LookAt(b.Value.center, Quaternion.Euler(15f, 180f, 0f), Mathf.Max(0.08f, b.Value.extents.magnitude * 1.6f), view.orthographic, true);
            return true;
        }

        void RestoreCamera()
        {
            if (_cameraSaved && _cameraView != null)
                _cameraView.LookAt(_savedPivot, _savedRotation, _savedSize, _savedOrtho, true);
            _cameraSaved = false;
            _cameraView = null;
        }

        string _caption = "";

        void OnSceneGui(SceneView view)
        {
            if (!Active) return;
            Handles.BeginGUI();
            GUI.Label(new Rect(12f, 8f, 560f, 40f), _caption, EditorStyles.whiteBoldLabel);
            Handles.EndGUI();
        }
    }
}
