using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Feeder
{
    public sealed class FBuildSizeCapture : IPostprocessBuildWithReport
    {
        private const string LastBuildReportPath = "Library/LastBuild.buildreport";
        private const string TempImportPath = "Assets/FBuildSizeTempLastBuild.buildreport";
        private const int MaxLoggedViolations = 20;

        public int callbackOrder => int.MaxValue;

        public void OnPostprocessBuild(BuildReport report)
        {
            // Never throw here: a capture bug must not fail the build.
            try
            {
                FBuildSnapshot snapshot = CreateSnapshot(report);
                FBuildSizeHistory.instance.AddSnapshot(snapshot);
                EditorApplication.delayCall += () => FinalizeSnapshot(snapshot);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Feeder] Build size capture failed: {e.Message}");
            }
        }

        public static FBuildSnapshot CreateSnapshot(BuildReport report)
        {
            var byPath = new Dictionary<string, FBuildAssetRow>(StringComparer.OrdinalIgnoreCase);

            foreach (PackedAssets pack in report.packedAssets)
            {
                if (pack == null || pack.contents == null) continue;
                foreach (PackedAssetInfo info in pack.contents)
                {
                    string typeName = info.type != null ? info.type.Name : "Unknown";
                    string key = string.IsNullOrEmpty(info.sourceAssetPath)
                        ? FBuildSizeUtil.GeneratedPathPrefix + typeName
                        : info.sourceAssetPath;

                    if (!byPath.TryGetValue(key, out FBuildAssetRow row))
                    {
                        byPath[key] = row = new FBuildAssetRow
                        {
                            path = key,
                            fileName = FBuildSizeUtil.IsGeneratedPath(key) ? key : Path.GetFileName(key),
                            typeName = typeName,
                            category = FBuildSizeUtil.Categorize(typeName)
                        };
                    }

                    row.packedSize += (long)info.packedSize;
                }
            }

            List<FBuildAssetRow> rows = byPath.Values
                .OrderByDescending(r => r.packedSize)
                .ThenBy(r => r.path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            long packedTotal = rows.Sum(r => r.packedSize);
            foreach (FBuildAssetRow row in rows)
                row.percent = packedTotal > 0 ? (double)row.packedSize / packedTotal * 100d : 0d;

            BuildSummary summary = report.summary;
            long endedTicks = summary.buildEndedAt.Ticks > 0
                ? summary.buildEndedAt.ToUniversalTime().Ticks
                : DateTime.UtcNow.Ticks;

            return new FBuildSnapshot
            {
                buildEndedTicksUtc = endedTicks,
                platform = summary.platform.ToString(),
                result = summary.result.ToString(),
                outputPath = summary.outputPath,
                totalSize = (long)summary.totalSize,
                packedTotal = packedTotal,
                assetCount = rows.Count,
                buildSeconds = summary.totalTime.TotalSeconds,
                rows = rows
            };
        }

        // Inside OnPostprocessBuild the report is not finalized yet: summary.result can
        // still read Unknown and totalSize can be 0. Re-read once the editor is idle.
        private static void FinalizeSnapshot(FBuildSnapshot snapshot)
        {
            try
            {
                if (snapshot.totalSize == 0 || snapshot.result == nameof(BuildResult.Unknown))
                {
                    BuildReport latest = TryGetLatestReportViaReflection();
                    if (latest != null)
                    {
                        BuildSummary summary = latest.summary;
                        if (snapshot.totalSize == 0)
                            snapshot.totalSize = (long)summary.totalSize;
                        if (snapshot.result == nameof(BuildResult.Unknown))
                            snapshot.result = summary.result.ToString();
                        if (snapshot.buildSeconds <= 0)
                            snapshot.buildSeconds = summary.totalTime.TotalSeconds;
                        FBuildSizeHistory.instance.SaveHistory();
                    }
                }

                if (FBuildSizeHistory.instance.logWarningsAfterBuild)
                    LogBudgetWarnings(snapshot);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Feeder] Build size finalize failed: {e.Message}");
            }
        }

        public static bool ImportLastBuildReport(out string error)
        {
            error = null;

            BuildReport report = TryGetLatestReportViaReflection();
            if (report == null)
                report = TryLoadLastBuildReportViaCopy(out error);

            if (report == null)
            {
                if (string.IsNullOrEmpty(error))
                    error = File.Exists(LastBuildReportPath)
                        ? "Could not read Library/LastBuild.buildreport."
                        : "No build report found. Make a build first.";
                return false;
            }

            FBuildSnapshot snapshot = CreateSnapshot(report);
            FBuildSizeHistory.instance.AddSnapshot(snapshot);
            return true;
        }

        public static BuildReport TryGetLatestReportViaReflection()
        {
            try
            {
                MethodInfo method = typeof(BuildReport).GetMethod(
                    "GetLatestReport", BindingFlags.NonPublic | BindingFlags.Static);
                return method != null ? method.Invoke(null, null) as BuildReport : null;
            }
            catch
            {
                return null;
            }
        }

        private static BuildReport TryLoadLastBuildReportViaCopy(out string error)
        {
            error = null;
            if (!File.Exists(LastBuildReportPath))
                return null;

            try
            {
                File.Copy(LastBuildReportPath, TempImportPath, true);
                AssetDatabase.ImportAsset(TempImportPath, ImportAssetOptions.ForceSynchronousImport);
                return AssetDatabase.LoadAssetAtPath<BuildReport>(TempImportPath);
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
            finally
            {
                // Snapshot rows are already extracted by the caller before the next
                // domain reload, but the temp asset must never linger in Assets.
                EditorApplication.delayCall += () =>
                {
                    if (File.Exists(TempImportPath))
                        AssetDatabase.DeleteAsset(TempImportPath);
                };
            }
        }

        public static void LogBudgetWarnings(FBuildSnapshot snapshot)
        {
            if (snapshot == null) return;
            var history = FBuildSizeHistory.instance;

            if (history.AnyAssetBudgetEnabled() && snapshot.rows != null)
            {
                List<FBuildAssetRow> violations = snapshot.rows
                    .Where(history.IsOverBudget)
                    .OrderByDescending(r => r.packedSize - history.GetBudgetFor(r.category))
                    .ToList();

                if (violations.Count > 0)
                {
                    foreach (FBuildAssetRow row in violations.Take(MaxLoggedViolations))
                    {
                        long budget = history.GetBudgetFor(row.category);
                        Debug.LogWarning(
                            $"[Feeder] Asset over budget: {row.path} — {FBuildSizeUtil.FormatBytes(row.packedSize)} " +
                            $"(budget {FBuildSizeUtil.FormatBytes(budget)}, over by {FBuildSizeUtil.FormatBytes(row.packedSize - budget)})");
                    }

                    Debug.LogWarning(
                        $"[Feeder] Build size budget: {violations.Count} asset(s) over budget" +
                        (violations.Count > MaxLoggedViolations ? $" (showing first {MaxLoggedViolations})." : "."));
                }
            }

            if (history.totalBuildBudget > 0 && snapshot.DisplayTotal > history.totalBuildBudget)
            {
                Debug.LogWarning(
                    $"[Feeder] Total build size over budget: {FBuildSizeUtil.FormatBytes(snapshot.DisplayTotal)} " +
                    $"(budget {FBuildSizeUtil.FormatBytes(history.totalBuildBudget)}, over by " +
                    $"{FBuildSizeUtil.FormatBytes(snapshot.DisplayTotal - history.totalBuildBudget)})");
            }
        }
    }
}
