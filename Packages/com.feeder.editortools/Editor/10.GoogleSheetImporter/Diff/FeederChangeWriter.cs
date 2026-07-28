using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public sealed class FeederWriteReport
    {
        public int WrittenCount;
        public int SkippedCount;
        public List<string> WrittenPaths = new List<string>();
        public List<string> Problems = new List<string>();

        public bool AnyWritten => WrittenCount > 0;
    }

    public static class FeederChangeWriter
    {
        public static bool CanWriteNow(out string reason)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying)
            {
                reason = "Đang ở Play Mode — ghi file .cs sẽ kích hoạt reload và ngắt play. Thoát Play Mode rồi thử lại.";
                return false;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                reason = "Unity đang compile / cập nhật asset. Đợi xong rồi thử lại.";
                return false;
            }

            reason = null;
            return true;
        }

        public static FeederWriteReport Write(IList<FeederFileChange> files)
        {
            FeederWriteReport report = new FeederWriteReport();
            if (files == null || files.Count == 0)
            {
                return report;
            }

            List<FeederFileChange> ready = new List<FeederFileChange>();
            for (int i = 0; i < files.Count; i++)
            {
                FeederFileChange file = files[i];
                if (file == null || string.IsNullOrEmpty(file.AssetPath))
                {
                    continue;
                }

                if (!FeederEnumSourceEditor.IsUnderAssets(file.AssetPath))
                {
                    report.SkippedCount++;
                    report.Problems.Add($"{file.AssetPath}: nằm ngoài Assets/, bỏ qua.");
                    continue;
                }

                if (file.NewText == null || file.NewText == file.OriginalText)
                {
                    report.SkippedCount++;
                    continue;
                }

                string current = FeederEnumSourceEditor.TryReadText(file.AssetPath, out bool _);
                if (file.IsNewFile)
                {
                    if (current != null)
                    {
                        report.SkippedCount++;
                        report.Problems.Add($"{file.AssetPath}: file đã tồn tại, không ghi đè.");
                        continue;
                    }
                }
                else if (current != file.OriginalText)
                {
                    report.SkippedCount++;
                    report.Problems.Add($"{file.AssetPath}: file đã đổi từ lúc xem trước, hãy quét lại.");
                    continue;
                }

                if (!file.IsNewFile && !AssetDatabase.MakeEditable(file.AssetPath))
                {
                    report.SkippedCount++;
                    report.Problems.Add($"{file.AssetPath}: file chỉ đọc / chưa check out.");
                    continue;
                }

                if (file.IsNewFile && !EnsureFolder(GetFolder(file.AssetPath)))
                {
                    report.SkippedCount++;
                    report.Problems.Add($"{file.AssetPath}: không tạo được thư mục chứa.");
                    continue;
                }

                ready.Add(file);
            }

            if (ready.Count == 0)
            {
                return report;
            }

            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < ready.Count; i++)
                {
                    FeederFileChange file = ready[i];
                    try
                    {
                        File.WriteAllText(Path.GetFullPath(file.AssetPath), file.NewText,
                            new UTF8Encoding(file.OriginalHadBom));
                        report.WrittenCount++;
                        report.WrittenPaths.Add(file.AssetPath);
                    }
                    catch (Exception e)
                    {
                        report.SkippedCount++;
                        report.Problems.Add($"{file.AssetPath}: {e.Message}");
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            for (int i = 0; i < report.WrittenPaths.Count; i++)
            {
                AssetDatabase.ImportAsset(report.WrittenPaths[i], ImportAssetOptions.ForceUpdate);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return report;
        }

        private static string GetFolder(string assetPath)
        {
            string normalized = assetPath.Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            return slash < 0 ? string.Empty : normalized.Substring(0, slash);
        }

        public static bool EnsureFolder(string folderAssetPath)
        {
            if (string.IsNullOrEmpty(folderAssetPath) || AssetDatabase.IsValidFolder(folderAssetPath))
            {
                return true;
            }

            string[] parts = folderAssetPath.Split('/');
            if (parts.Length == 0 || !string.Equals(parts[0], "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string current = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]))
                {
                    continue;
                }

                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }

            return AssetDatabase.IsValidFolder(folderAssetPath);
        }
    }
}
