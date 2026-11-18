using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HandGestureCreator
{
    /// <summary>ジェスチャー ⇔ AnimationClip、アバターへのポーズ適用</summary>
    public static class HandGestureAnimUtility
    {
        /// <summary>
        /// 指マッスルのみを含むHumanoid用AnimationClipを作成。
        /// VRChatの FX / Gesture レイヤーの各ジェスチャーステートにそのまま割り当てられる。
        /// </summary>
        public static AnimationClip CreateClip(HandGesture left, HandGesture right)
        {
            var clip = new AnimationClip { name = (left ?? right)?.name ?? "Gesture" };
            if (left != null) WriteHand(clip, HandSide.Left, left);
            if (right != null) WriteHand(clip, HandSide.Right, right);
            return clip;
        }

        static void WriteHand(AnimationClip clip, HandSide side, HandGesture g)
        {
            for (int f = 0; f < HandGesture.FingerCount; f++)
            for (int p = 0; p < HandGesture.MusclesPerFinger; p++)
            {
                float v = g.Get(f, p);
                var curve = new AnimationCurve(new Keyframe(0f, v), new Keyframe(1f / 60f, v));
                var binding = EditorCurveBinding.FloatCurve("", typeof(Animator),
                    HandGesture.AnimPropertyName(side, f, p));
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }
        }

        public static void SaveClipAsset(AnimationClip clip, string assetPath)
        {
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
            if (existing != null)
            {
                // 既存アセットを上書き(参照中のAnimatorControllerを壊さない)
                EditorUtility.CopySerialized(clip, existing);
                existing.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
                EditorUtility.SetDirty(existing);
            }
            else
            {
                AssetDatabase.CreateAsset(clip, assetPath);
            }
            AssetDatabase.SaveAssets();
        }

        /// <summary>既存のAnimationClip(他アバター用の手のアニメ等)からジェスチャーを読み取る</summary>
        public static HandGesture ReadFromClip(AnimationClip clip, HandSide side, out bool found)
        {
            var g = new HandGesture { name = clip.name };
            found = false;
            var bindings = AnimationUtility.GetCurveBindings(clip);
            for (int f = 0; f < HandGesture.FingerCount; f++)
            for (int p = 0; p < HandGesture.MusclesPerFinger; p++)
            {
                var prop = HandGesture.AnimPropertyName(side, f, p);
                var b = bindings.FirstOrDefault(x => x.type == typeof(Animator) && x.propertyName == prop);
                if (b.propertyName != prop) continue;
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                if (curve == null || curve.length == 0) continue;
                g.Set(f, p, curve.Evaluate(0f));
                found = true;
            }
            return g;
        }

        /// <summary>アバターの現在の指ポーズを読み取る(既存アバターから移植する用)</summary>
        public static HandGesture ReadFromAvatar(Animator animator, HandSide side)
        {
            var g = new HandGesture { name = $"{animator.name}_{side}" };
            if (!IsValidHumanoid(animator)) return g;
            using (var handler = new HumanPoseHandler(animator.avatar, animator.transform))
            {
                var pose = new HumanPose();
                handler.GetHumanPose(ref pose);
                for (int f = 0; f < HandGesture.FingerCount; f++)
                for (int p = 0; p < HandGesture.MusclesPerFinger; p++)
                {
                    int idx = HandGesture.MuscleIndex(side, f, p);
                    if (idx >= 0) g.Set(f, p, pose.muscles[idx]);
                }
            }
            return g;
        }

        /// <summary>左右の指ボーン(存在するもののみ)</summary>
        public static IEnumerable<Transform> FingerBones(Animator a)
            => FingerBones(a, HandSide.Left).Concat(FingerBones(a, HandSide.Right));

        public static IEnumerable<Transform> FingerBones(Animator a, HandSide side)
        {
            var first = side == HandSide.Left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal;
            var last = side == HandSide.Left ? HumanBodyBones.LeftLittleDistal : HumanBodyBones.RightLittleDistal;
            for (var b = first; b <= last; b++)
            {
                var t = a.GetBoneTransform(b);
                if (t != null) yield return t;
            }
        }

        public static bool IsValidHumanoid(Animator a)
            => a != null && a.avatar != null && a.avatar.isValid && a.avatar.isHuman;
    }
}
