using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace HandGestureCreator
{
    /// <summary>
    /// アバターの既存コントローラーから「どのジェスチャーでどのクリップが出るか」を読み取る。
    /// VRChat SDK には直接依存せず、Avatar Descriptor は SerializedObject 経由で読むだけ(書き換えない)。
    /// (Hand Gesture Fit の GestureControllerReader と同じ解釈)
    /// </summary>
    internal static class GestureControllerImport
    {
        public const int GestureCount = 8;

        /// <summary>VRChat の GestureLeft / GestureRight の値 (0-7) と同じ並び。</summary>
        public static readonly string[] GestureLabels =
        {
            "待機 (Neutral)", "グー (Fist)", "パー (HandOpen)", "指差し (FingerPoint)",
            "ピース (Victory)", "ロック (RockNRoll)", "銃 (HandGun)", "サムズアップ (ThumbsUp)",
        };
        public static readonly string[] GestureIds =
        {
            "Neutral", "Fist", "HandOpen", "FingerPoint", "Victory", "RockNRoll", "HandGun", "ThumbsUp",
        };

        /// <summary>Gesture レイヤー → FX レイヤーの順で、独自のコントローラーが設定されていれば返す。</summary>
        public static AnimatorController FindController(GameObject avatar)
        {
            if (avatar == null) return null;
            Component descriptor = null;
            foreach (var c in avatar.GetComponents<Component>())
                if (c != null && c.GetType().Name == "VRCAvatarDescriptor") { descriptor = c; break; }
            if (descriptor == null) return null;

            var layers = new SerializedObject(descriptor).FindProperty("baseAnimationLayers");
            if (layers == null || !layers.isArray) return null;
            foreach (var wanted in new[] { "Gesture", "FX" })
            {
                for (int i = 0; i < layers.arraySize; i++)
                {
                    var e = layers.GetArrayElementAtIndex(i);
                    var type = e.FindPropertyRelative("type");
                    if (type == null) continue;
                    int idx = type.enumValueIndex;
                    if (idx < 0 || idx >= type.enumNames.Length || type.enumNames[idx] != wanted) continue;
                    var isDefault = e.FindPropertyRelative("isDefault");
                    if (isDefault != null && isDefault.boolValue) continue;
                    if (e.FindPropertyRelative("animatorController")?.objectReferenceValue is AnimatorController ac)
                        return ac;
                }
            }
            return null;
        }

        /// <param name="left">GestureLeft の値 (0-7) ごとのクリップ。見つからなければ null のまま。</param>
        public static void Read(AnimatorController controller, AnimationClip[] left, AnimationClip[] right)
        {
            foreach (var layer in controller.layers)
            {
                var machine = layer.stateMachine;
                if (machine == null) continue;
                var transitions = new List<AnimatorStateTransition>();
                Collect(machine, transitions);

                bool usesLeft = false, usesRight = false;
                foreach (var tr in transitions)
                {
                    var clip = MainClip(tr.destinationState != null ? tr.destinationState.motion : null);
                    foreach (var cond in tr.conditions)
                    {
                        bool isLeft = cond.parameter == "GestureLeft";
                        bool isRight = cond.parameter == "GestureRight";
                        if (!isLeft && !isRight) continue;
                        if (isLeft) usesLeft = true; else usesRight = true;
                        if (cond.mode != AnimatorConditionMode.Equals || clip == null) continue;
                        int g = Mathf.RoundToInt(cond.threshold);
                        if (g < 0 || g >= GestureCount) continue;
                        var slots = isLeft ? left : right;
                        if (slots[g] == null) slots[g] = clip;
                    }
                }

                // 待機が遷移から分からなければ、そのレイヤーの初期ステートを使う
                var idle = MainClip(machine.defaultState != null ? machine.defaultState.motion : null);
                if (idle != null)
                {
                    if (usesLeft && left[0] == null) left[0] = idle;
                    if (usesRight && right[0] == null) right[0] = idle;
                }
            }
        }

        static void Collect(AnimatorStateMachine m, List<AnimatorStateTransition> list)
        {
            list.AddRange(m.anyStateTransitions);
            foreach (var c in m.states) if (c.state != null) list.AddRange(c.state.transitions);
            foreach (var c in m.stateMachines) if (c.stateMachine != null) Collect(c.stateMachine, list);
        }

        /// <summary>ブレンドツリーなら最後の子(トリガーを握り切った側)のクリップを採用する。</summary>
        static AnimationClip MainClip(Motion motion)
        {
            for (int depth = 0; depth < 8 && motion != null; depth++)
            {
                if (motion is AnimationClip clip) return clip;
                if (!(motion is BlendTree tree)) return null;
                var children = tree.children;
                motion = null;
                for (int i = children.Length - 1; i >= 0; i--)
                    if (children[i].motion != null) { motion = children[i].motion; break; }
            }
            return null;
        }

        /// <summary>
        /// コントローラーの 8 ジェスチャー×左右から指の形を取り出す。
        /// 左右で同じクリップ・同じ形のものは 1 つにまとめる。戻り値は (表示名, ジェスチャー)。
        /// </summary>
        public static List<HandGesture> Extract(AnimatorController controller, string prefix)
        {
            var left = new AnimationClip[GestureCount];
            var right = new AnimationClip[GestureCount];
            Read(controller, left, right);

            var result = new List<HandGesture>();
            for (int g = 0; g < GestureCount; g++)
            {
                HandGesture l = null, r = null;
                if (left[g] != null)
                {
                    var x = HandGestureAnimUtility.ReadFromClip(left[g], HandSide.Left, out bool f);
                    if (f) l = x;
                }
                if (right[g] != null)
                {
                    var x = HandGestureAnimUtility.ReadFromClip(right[g], HandSide.Right, out bool f);
                    if (f) r = x;
                }
                if (l != null && r != null && SameShape(l, r))
                {
                    r.name = $"{prefix}_{GestureIds[g]}";
                    result.Add(r);
                    continue;
                }
                if (l != null) { l.name = $"{prefix}_{GestureIds[g]}_L"; result.Add(l); }
                if (r != null) { r.name = $"{prefix}_{GestureIds[g]}_R"; result.Add(r); }
            }
            return result;
        }

        static bool SameShape(HandGesture a, HandGesture b)
        {
            for (int i = 0; i < HandGesture.MuscleCount; i++)
                if (Mathf.Abs(a.muscles[i] - b.muscles[i]) > 0.001f) return false;
            return true;
        }
    }
}
