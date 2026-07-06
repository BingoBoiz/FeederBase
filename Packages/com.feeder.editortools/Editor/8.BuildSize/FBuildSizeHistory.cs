using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    [FilePath("UserSettings/Feeder/FBuildSizeHistory.asset",
              FilePathAttribute.Location.ProjectFolder)]
    public sealed class FBuildSizeHistory : ScriptableSingleton<FBuildSizeHistory>
    {
        [SerializeField] public List<FBuildSnapshot> snapshots = new List<FBuildSnapshot>();
        [SerializeField] public int maxSnapshots = 10;

        // Budgets in bytes, 0 = disabled.
        [SerializeField] public long defaultAssetBudget;
        [SerializeField] public long textureBudget;
        [SerializeField] public long meshBudget;
        [SerializeField] public long audioBudget;
        [SerializeField] public long otherBudget;
        [SerializeField] public long totalBuildBudget;
        [SerializeField] public bool logWarningsAfterBuild = true;

        public bool HasData => snapshots != null && snapshots.Count > 0;

        public FBuildSnapshot Latest => HasData ? snapshots[snapshots.Count - 1] : null;

        public void AddSnapshot(FBuildSnapshot snapshot)
        {
            if (snapshot == null) return;
            if (snapshots == null)
                snapshots = new List<FBuildSnapshot>();

            snapshots.RemoveAll(s =>
                s.buildEndedTicksUtc == snapshot.buildEndedTicksUtc &&
                string.Equals(s.platform, snapshot.platform, StringComparison.Ordinal));

            snapshots.Add(snapshot);
            snapshots.Sort((a, b) => a.buildEndedTicksUtc.CompareTo(b.buildEndedTicksUtc));
            TrimToMax();
            SaveHistory();
        }

        public void RemoveSnapshotAt(int index)
        {
            if (snapshots == null || index < 0 || index >= snapshots.Count) return;
            snapshots.RemoveAt(index);
            SaveHistory();
        }

        public void ClearHistory()
        {
            if (snapshots == null)
                snapshots = new List<FBuildSnapshot>();
            else
                snapshots.Clear();
            SaveHistory();
        }

        public void TrimToMax()
        {
            maxSnapshots = Mathf.Clamp(maxSnapshots, 2, 30);
            while (snapshots != null && snapshots.Count > maxSnapshots)
                snapshots.RemoveAt(0);
        }

        public void SaveHistory()
        {
            if (snapshots == null)
                snapshots = new List<FBuildSnapshot>();
            Save(true);
        }

        public long GetBudgetFor(FBuildAssetCategory category)
        {
            long budget;
            switch (category)
            {
                case FBuildAssetCategory.Texture: budget = textureBudget; break;
                case FBuildAssetCategory.Mesh: budget = meshBudget; break;
                case FBuildAssetCategory.Audio: budget = audioBudget; break;
                default: budget = otherBudget; break;
            }

            return budget > 0 ? budget : defaultAssetBudget;
        }

        public bool IsOverBudget(FBuildAssetRow row)
        {
            if (row == null) return false;
            long budget = GetBudgetFor(row.category);
            return budget > 0 && row.packedSize > budget;
        }

        public bool AnyAssetBudgetEnabled()
        {
            return defaultAssetBudget > 0 || textureBudget > 0 || meshBudget > 0 ||
                   audioBudget > 0 || otherBudget > 0;
        }
    }

    public enum FBuildAssetCategory
    {
        Texture,
        Mesh,
        Audio,
        Other
    }

    [Serializable]
    public class FBuildAssetRow
    {
        public string path;
        public string fileName;
        public string typeName;
        public FBuildAssetCategory category;
        public long packedSize;
        public double percent;
    }

    [Serializable]
    public class FBuildSnapshot
    {
        public long buildEndedTicksUtc;
        public string platform;
        public string result;
        public string outputPath;
        public long totalSize;
        public long packedTotal;
        public int assetCount;
        public double buildSeconds;
        public List<FBuildAssetRow> rows = new List<FBuildAssetRow>();

        public DateTime BuildEndedLocal =>
            new DateTime(buildEndedTicksUtc, DateTimeKind.Utc).ToLocalTime();

        public long DisplayTotal => totalSize > 0 ? totalSize : packedTotal;

        public string Label =>
            $"{BuildEndedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} | {platform} | {FBuildSizeUtil.FormatBytes(DisplayTotal)}";
    }

    public static class FBuildSizeUtil
    {
        public const string GeneratedPathPrefix = "<generated> ";

        public static bool IsGeneratedPath(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   path.StartsWith(GeneratedPathPrefix, StringComparison.Ordinal);
        }

        public static FBuildAssetCategory Categorize(string typeName)
        {
            switch (typeName)
            {
                case "Texture2D":
                case "Texture3D":
                case "Texture2DArray":
                case "Cubemap":
                case "CubemapArray":
                case "RenderTexture":
                case "Sprite":
                    return FBuildAssetCategory.Texture;
                case "Mesh":
                    return FBuildAssetCategory.Mesh;
                case "AudioClip":
                    return FBuildAssetCategory.Audio;
                default:
                    return FBuildAssetCategory.Other;
            }
        }

        public static string FormatBytes(long bytes)
        {
            bool negative = bytes < 0;
            long abs = Math.Abs(bytes);
            string text;

            if (abs < 1024)
            {
                text = $"{abs} B";
            }
            else
            {
                double value = abs / 1024d;
                if (value < 1024) text = $"{value:0.##} KB";
                else
                {
                    value /= 1024d;
                    if (value < 1024) text = $"{value:0.##} MB";
                    else
                    {
                        value /= 1024d;
                        text = $"{value:0.##} GB";
                    }
                }
            }

            return negative ? "-" + text : text;
        }

        public static string FormatSignedBytes(long bytes)
        {
            if (bytes > 0) return "+" + FormatBytes(bytes);
            return FormatBytes(bytes);
        }
    }
}
