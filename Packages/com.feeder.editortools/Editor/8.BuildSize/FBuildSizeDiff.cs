using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Feeder
{
    public enum FBuildDiffStatus
    {
        Added,
        Removed,
        Bigger,
        Smaller,
        Same
    }

    public class FBuildDiffRow
    {
        public string path;
        public string fileName;
        public string typeName;
        public FBuildAssetCategory category;
        public long sizeA;
        public long sizeB;
        public long delta;
        public FBuildDiffStatus status;
    }

    public struct FBuildDiffSummary
    {
        public long totalDelta;
        public int addedCount;
        public long addedSize;
        public int removedCount;
        public long removedSize;
        public int biggerCount;
        public int smallerCount;
        public int sameCount;
    }

    public static class FBuildSizeDiffEngine
    {
        public static List<FBuildDiffRow> Compute(FBuildSnapshot a, FBuildSnapshot b)
        {
            var result = new List<FBuildDiffRow>();
            if (a == null || b == null) return result;

            var remainingA = new Dictionary<string, FBuildAssetRow>(StringComparer.OrdinalIgnoreCase);
            if (a.rows != null)
                foreach (FBuildAssetRow row in a.rows)
                    remainingA[row.path] = row;

            if (b.rows != null)
            {
                foreach (FBuildAssetRow rowB in b.rows)
                {
                    if (remainingA.TryGetValue(rowB.path, out FBuildAssetRow rowA))
                    {
                        remainingA.Remove(rowB.path);
                        long delta = rowB.packedSize - rowA.packedSize;
                        result.Add(new FBuildDiffRow
                        {
                            path = rowB.path,
                            fileName = rowB.fileName,
                            typeName = rowB.typeName,
                            category = rowB.category,
                            sizeA = rowA.packedSize,
                            sizeB = rowB.packedSize,
                            delta = delta,
                            status = delta > 0 ? FBuildDiffStatus.Bigger
                                   : delta < 0 ? FBuildDiffStatus.Smaller
                                   : FBuildDiffStatus.Same
                        });
                    }
                    else
                    {
                        result.Add(new FBuildDiffRow
                        {
                            path = rowB.path,
                            fileName = rowB.fileName,
                            typeName = rowB.typeName,
                            category = rowB.category,
                            sizeA = 0,
                            sizeB = rowB.packedSize,
                            delta = rowB.packedSize,
                            status = FBuildDiffStatus.Added
                        });
                    }
                }
            }

            foreach (FBuildAssetRow rowA in remainingA.Values)
            {
                result.Add(new FBuildDiffRow
                {
                    path = rowA.path,
                    fileName = string.IsNullOrEmpty(rowA.fileName) ? Path.GetFileName(rowA.path) : rowA.fileName,
                    typeName = rowA.typeName,
                    category = rowA.category,
                    sizeA = rowA.packedSize,
                    sizeB = 0,
                    delta = -rowA.packedSize,
                    status = FBuildDiffStatus.Removed
                });
            }

            return result;
        }

        public static FBuildDiffSummary Summarize(List<FBuildDiffRow> rows)
        {
            var summary = new FBuildDiffSummary();
            if (rows == null) return summary;

            foreach (FBuildDiffRow row in rows)
            {
                summary.totalDelta += row.delta;
                switch (row.status)
                {
                    case FBuildDiffStatus.Added:
                        summary.addedCount++;
                        summary.addedSize += row.sizeB;
                        break;
                    case FBuildDiffStatus.Removed:
                        summary.removedCount++;
                        summary.removedSize += row.sizeA;
                        break;
                    case FBuildDiffStatus.Bigger:
                        summary.biggerCount++;
                        break;
                    case FBuildDiffStatus.Smaller:
                        summary.smallerCount++;
                        break;
                    default:
                        summary.sameCount++;
                        break;
                }
            }

            return summary;
        }
    }
}
