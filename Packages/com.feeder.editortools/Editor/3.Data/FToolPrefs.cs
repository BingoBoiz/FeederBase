using System;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Lưu setting scalar (enum/bool/int/float/string) của các tool qua EditorPrefs.
    /// Dùng cho các field không phải Object ref — Object ref phải lưu qua <see cref="FDataContainer"/>.
    /// Key có prefix scope theo project vì EditorPrefs là global cho cả editor install.
    /// </summary>
    public static class FToolPrefs
    {
        private static readonly string ProjectId =
            Application.dataPath.GetHashCode().ToString("X8");

        private static string Key(string owner, string field) => $"Feeder.{ProjectId}.{owner}.{field}";

        public static bool GetBool(string owner, string field, bool def)
            => EditorPrefs.GetBool(Key(owner, field), def);

        public static void SetBool(string owner, string field, bool value)
            => EditorPrefs.SetBool(Key(owner, field), value);

        public static int GetInt(string owner, string field, int def)
            => EditorPrefs.GetInt(Key(owner, field), def);

        public static void SetInt(string owner, string field, int value)
            => EditorPrefs.SetInt(Key(owner, field), value);

        public static float GetFloat(string owner, string field, float def)
            => EditorPrefs.GetFloat(Key(owner, field), def);

        public static void SetFloat(string owner, string field, float value)
            => EditorPrefs.SetFloat(Key(owner, field), value);

        public static string GetString(string owner, string field, string def)
            => EditorPrefs.GetString(Key(owner, field), def);

        public static void SetString(string owner, string field, string value)
            => EditorPrefs.SetString(Key(owner, field), value ?? string.Empty);

        public static T GetEnum<T>(string owner, string field, T def) where T : struct, Enum
        {
            var saved = EditorPrefs.GetString(Key(owner, field), null);
            return Enum.TryParse(saved, out T value) ? value : def;
        }

        public static void SetEnum<T>(string owner, string field, T value) where T : struct, Enum
            => EditorPrefs.SetString(Key(owner, field), value.ToString());
    }
}
