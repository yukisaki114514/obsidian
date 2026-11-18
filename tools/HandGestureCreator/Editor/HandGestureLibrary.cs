using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HandGestureCreator
{
    /// <summary>
    /// プロジェクト外(ユーザーフォルダ)にJSONで保存するライブラリ。
    /// どのUnityプロジェクトから開いても同じジェスチャーが使える。
    /// </summary>
    public static class HandGestureLibrary
    {
        const string PrefKey = "HandGestureCreator.LibraryPath";

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRCHandGestureCreator", "Gestures");

        /// <summary>EditorPrefs はマシン全体で共有されるので、変更した保存先も全プロジェクト共通になる</summary>
        public static string LibraryPath
        {
            get => EditorPrefs.GetString(PrefKey, DefaultPath);
            set => EditorPrefs.SetString(PrefKey, value);
        }

        public static void EnsureDir() => Directory.CreateDirectory(LibraryPath);

        public static List<(string path, HandGesture gesture)> LoadAll()
        {
            var result = new List<(string, HandGesture)>();
            if (!Directory.Exists(LibraryPath)) return result;
            foreach (var file in Directory.GetFiles(LibraryPath, "*.json").OrderBy(f => f))
            {
                try
                {
                    var g = HandGesture.FromJson(File.ReadAllText(file));
                    if (g != null) result.Add((file, g));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[HandGestureCreator] 読み込み失敗: {file}\n{e.Message}");
                }
            }
            return result;
        }

        public static string Save(HandGesture g, bool overwrite)
        {
            EnsureDir();
            var safe = string.Concat((string.IsNullOrWhiteSpace(g.name) ? "Gesture" : g.name)
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(LibraryPath, safe + ".json");
            if (!overwrite)
            {
                int i = 1;
                while (File.Exists(path)) path = Path.Combine(LibraryPath, $"{safe}_{i++}.json");
            }
            File.WriteAllText(path, g.ToJson());
            return path;
        }

        public static void Delete(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
