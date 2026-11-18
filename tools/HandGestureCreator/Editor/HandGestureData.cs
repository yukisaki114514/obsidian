using System;
using UnityEngine;

namespace HandGestureCreator
{
    public enum HandSide { Left, Right }

    /// <summary>
    /// 片手分のジェスチャー。Humanoidのマッスル値(-1..1)で保持するため、
    /// ボーン長や向きが異なる別アバターにもそのまま移植できる。
    /// </summary>
    [Serializable]
    public class HandGesture
    {
        public const int FingerCount = 5;
        public const int MusclesPerFinger = 4;
        public const int MuscleCount = FingerCount * MusclesPerFinger;

        public static readonly string[] Fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
        public static readonly string[] FingersJa = { "親指", "人差し指", "中指", "薬指", "小指" };
        public static readonly string[] Parts = { "1 Stretched", "Spread", "2 Stretched", "3 Stretched" };
        public static readonly string[] PartsJa = { "第1関節 曲げ", "開き", "第2関節 曲げ", "第3関節 曲げ" };

        public string name = "New Gesture";
        public int version = 1;
        /// <summary>[finger * 4 + part] の順。Stretched: 1=伸ばす / -1=曲げる</summary>
        public float[] muscles = new float[MuscleCount];

        public float Get(int finger, int part) => muscles[finger * MusclesPerFinger + part];
        public void Set(int finger, int part, float v) => muscles[finger * MusclesPerFinger + part] = Mathf.Clamp(v, -1f, 1f);

        public HandGesture Clone()
        {
            return new HandGesture { name = name, version = version, muscles = (float[])muscles.Clone() };
        }

        public string ToJson() => JsonUtility.ToJson(this, true);

        public static HandGesture FromJson(string json)
        {
            var g = JsonUtility.FromJson<HandGesture>(json);
            if (g == null) return null;
            if (g.muscles == null || g.muscles.Length != MuscleCount)
            {
                var fixedArr = new float[MuscleCount];
                if (g.muscles != null) Array.Copy(g.muscles, fixedArr, Mathf.Min(g.muscles.Length, MuscleCount));
                g.muscles = fixedArr;
            }
            return g;
        }

        // ---- Humanoid名の変換 ----

        /// <summary>HumanTrait.MuscleName 形式 (例: "Left Index 1 Stretched")</summary>
        public static string MuscleName(HandSide side, int finger, int part)
            => $"{side} {Fingers[finger]} {Parts[part]}";

        /// <summary>AnimationClip のプロパティ名形式 (例: "LeftHand.Index.1 Stretched")</summary>
        public static string AnimPropertyName(HandSide side, int finger, int part)
            => $"{side}Hand.{Fingers[finger]}.{Parts[part]}";

        static int[][] _muscleIndexCache;

        /// <summary>HumanPose.muscles 配列内のインデックス</summary>
        public static int MuscleIndex(HandSide side, int finger, int part)
        {
            if (_muscleIndexCache == null)
            {
                _muscleIndexCache = new int[2][];
                for (int s = 0; s < 2; s++)
                {
                    _muscleIndexCache[s] = new int[MuscleCount];
                    for (int f = 0; f < FingerCount; f++)
                        for (int p = 0; p < MusclesPerFinger; p++)
                            _muscleIndexCache[s][f * MusclesPerFinger + p] =
                                Array.IndexOf(HumanTrait.MuscleName, MuscleName((HandSide)s, f, p));
                }
            }
            return _muscleIndexCache[(int)side][finger * MusclesPerFinger + part];
        }

        // ---- 組み込みプリセット ----

        static HandGesture Make(string name, float[] perFinger)
        {
            var g = new HandGesture { name = name };
            Array.Copy(perFinger, g.muscles, MuscleCount);
            return g;
        }

        // 各行: 1 Stretched, Spread, 2 Stretched, 3 Stretched
        public static HandGesture[] BuiltIn => new[]
        {
            Make("Neutral", new float[] {
                 0.0f, 0.0f, 0.0f, 0.0f,
                 0.0f, 0.0f, 0.0f, 0.0f,
                 0.0f, 0.0f, 0.0f, 0.0f,
                 0.0f, 0.0f, 0.0f, 0.0f,
                 0.0f, 0.0f, 0.0f, 0.0f }),
            Make("Open", new float[] {
                 0.6f, 0.6f, 0.8f, 0.8f,
                 1.0f, 0.4f, 1.0f, 1.0f,
                 1.0f, 0.0f, 1.0f, 1.0f,
                 1.0f,-0.3f, 1.0f, 1.0f,
                 1.0f,-0.6f, 1.0f, 1.0f }),
            Make("Fist", new float[] {
                -0.6f,-0.2f,-0.6f,-0.8f,
                -1.0f, 0.0f,-1.0f,-1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f }),
            Make("Point", new float[] {
                -0.6f,-0.2f,-0.6f,-0.8f,
                 1.0f, 0.0f, 1.0f, 1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f }),
            Make("Peace", new float[] {
                -0.6f,-0.2f,-0.6f,-0.8f,
                 1.0f, 0.6f, 1.0f, 1.0f,
                 1.0f,-0.6f, 1.0f, 1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f }),
            Make("ThumbsUp", new float[] {
                 1.0f, 0.6f, 1.0f, 1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f }),
            Make("RockNRoll", new float[] {
                -0.6f,-0.2f,-0.6f,-0.8f,
                 1.0f, 0.3f, 1.0f, 1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f,
                 1.0f,-0.5f, 1.0f, 1.0f }),
            Make("HandGun", new float[] {
                 1.0f, 0.6f, 1.0f, 1.0f,
                 1.0f, 0.0f, 1.0f, 1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f,
                -1.0f, 0.0f,-1.0f,-1.0f }),
        };
    }
}
