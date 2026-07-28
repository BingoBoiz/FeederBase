using System;
using System.Collections.Generic;
using System.Text;

namespace Feeder
{
    public static class FeederTextDiff
    {
        public const int DefaultContextLines = 3;

        private const int MaxEditDistance = 1500;

        private const int TabWidth = 4;
        private const int MaxHighlightLineLength = 400;
        private const float MaxHighlightRatio = 0.7f;

        public static FeederDiffFileResult Compute(string originalText, string newText)
        {
            return Compute(originalText, newText, DefaultContextLines);
        }

        public static FeederDiffFileResult Compute(string originalText, string newText, int contextLines)
        {
            FeederDiffFileResult result = new FeederDiffFileResult();
            if (contextLines < 0)
            {
                contextLines = 0;
            }

            string[] newLines = SplitLines(newText, out bool _);

            if (originalText == null)
            {
                List<FeederDiffLine> lines = new List<FeederDiffLine>(newLines.Length);
                for (int i = 0; i < newLines.Length; i++)
                {
                    lines.Add(MakeLine(FeederDiffOp.Added, -1, i + 1, newLines[i]));
                }

                AppendSingleHunk(result, lines, 0, 0, newLines.Length == 0 ? 0 : 1, newLines.Length);
                Finish(result);
                return result;
            }

            string[] oldLines = SplitLines(originalText, out bool _);

            if (!TryBuildChangeFlags(oldLines, newLines, out bool[] oldDeleted, out bool[] newInserted))
            {
                result.BailedOut = true;
                List<FeederDiffLine> lines = new List<FeederDiffLine>(oldLines.Length + newLines.Length);
                for (int i = 0; i < oldLines.Length; i++)
                {
                    lines.Add(MakeLine(FeederDiffOp.Removed, i + 1, -1, oldLines[i]));
                }

                for (int i = 0; i < newLines.Length; i++)
                {
                    lines.Add(MakeLine(FeederDiffOp.Added, -1, i + 1, newLines[i]));
                }

                AppendSingleHunk(result, lines,
                    oldLines.Length == 0 ? 0 : 1, oldLines.Length,
                    newLines.Length == 0 ? 0 : 1, newLines.Length);
                Finish(result);
                return result;
            }

            List<FeederDiffLine> all = BuildLines(oldLines, newLines, oldDeleted, newInserted);
            BuildHunks(result, all, contextLines);
            Finish(result);
            return result;
        }

        private static void Finish(FeederDiffFileResult result)
        {
            for (int h = 0; h < result.Hunks.Count; h++)
            {
                List<FeederDiffLine> lines = result.Hunks[h].Lines;
                ApplyWordHighlight(lines);
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Op == FeederDiffOp.Added)
                    {
                        result.AddedCount++;
                    }
                    else if (lines[i].Op == FeederDiffOp.Removed)
                    {
                        result.RemovedCount++;
                    }

                    int length = lines[i].Text != null ? lines[i].Text.Length : 0;
                    if (length > result.MaxLineChars)
                    {
                        result.MaxLineChars = length;
                    }
                }
            }
        }

        public static string[] SplitLines(string text, out bool endsWithNewline)
        {
            endsWithNewline = false;
            if (string.IsNullOrEmpty(text))
            {
                return Array.Empty<string>();
            }

            List<string> lines = new List<string>();
            int start = 0;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\r')
                {
                    lines.Add(text.Substring(start, i - start));
                    i += (i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
                    start = i;
                }
                else if (c == '\n')
                {
                    lines.Add(text.Substring(start, i - start));
                    i++;
                    start = i;
                }
                else
                {
                    i++;
                }
            }

            if (start < text.Length)
            {
                lines.Add(text.Substring(start));
            }
            else
            {
                endsWithNewline = lines.Count > 0;
            }

            return lines.ToArray();
        }

        private static FeederDiffLine MakeLine(FeederDiffOp op, int oldLine, int newLine, string raw)
        {
            return new FeederDiffLine
            {
                Op = op,
                OldLine = oldLine,
                NewLine = newLine,
                RawText = raw,
                Text = ExpandTabs(raw),
                HlStart = 0,
                HlEnd = 0,
            };
        }

        private static string ExpandTabs(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('\t') < 0)
            {
                return value ?? string.Empty;
            }

            return value.Replace("\t", new string(' ', TabWidth));
        }

        private static bool TryBuildChangeFlags(string[] oldLines, string[] newLines,
            out bool[] oldDeleted, out bool[] newInserted)
        {
            oldDeleted = new bool[oldLines.Length];
            newInserted = new bool[newLines.Length];

            Dictionary<string, int> pool = new Dictionary<string, int>(StringComparer.Ordinal);
            int[] oldIds = InternAll(oldLines, pool);
            int[] newIds = InternAll(newLines, pool);

            int prefix = 0;
            int maxPrefix = Math.Min(oldIds.Length, newIds.Length);
            while (prefix < maxPrefix && oldIds[prefix] == newIds[prefix])
            {
                prefix++;
            }

            int suffix = 0;
            int maxSuffix = Math.Min(oldIds.Length, newIds.Length) - prefix;
            while (suffix < maxSuffix &&
                   oldIds[oldIds.Length - 1 - suffix] == newIds[newIds.Length - 1 - suffix])
            {
                suffix++;
            }

            int oldMid = oldIds.Length - prefix - suffix;
            int newMid = newIds.Length - prefix - suffix;

            int[] a = new int[oldMid];
            Array.Copy(oldIds, prefix, a, 0, oldMid);
            int[] b = new int[newMid];
            Array.Copy(newIds, prefix, b, 0, newMid);

            if (!TryMyers(a, b, out bool[] aDeleted, out bool[] bInserted))
            {
                return false;
            }

            for (int i = 0; i < oldMid; i++)
            {
                oldDeleted[prefix + i] = aDeleted[i];
            }

            for (int i = 0; i < newMid; i++)
            {
                newInserted[prefix + i] = bInserted[i];
            }

            SlideRuns(oldIds, oldDeleted);
            SlideRuns(newIds, newInserted);
            return true;
        }

        private static int[] InternAll(string[] lines, Dictionary<string, int> pool)
        {
            int[] ids = new int[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                string key = lines[i] ?? string.Empty;
                if (!pool.TryGetValue(key, out int id))
                {
                    id = pool.Count;
                    pool.Add(key, id);
                }

                ids[i] = id;
            }

            return ids;
        }

        private static void SlideRuns(int[] ids, bool[] changed)
        {
            int n = ids.Length;
            int i = 0;
            while (i < n)
            {
                if (!changed[i])
                {
                    i++;
                    continue;
                }

                int start = i;
                int end = i;
                while (end < n && changed[end])
                {
                    end++;
                }

                while (end < n && !changed[end] && ids[start] == ids[end])
                {
                    changed[start] = false;
                    changed[end] = true;
                    start++;
                    end++;
                }

                i = end;
            }
        }

        private static bool TryMyers(int[] a, int[] b, out bool[] aDeleted, out bool[] bInserted)
        {
            int n = a.Length;
            int m = b.Length;
            aDeleted = new bool[n];
            bInserted = new bool[m];

            if (n == 0 && m == 0)
            {
                return true;
            }

            if (n == 0)
            {
                for (int j = 0; j < m; j++)
                {
                    bInserted[j] = true;
                }

                return true;
            }

            if (m == 0)
            {
                for (int i = 0; i < n; i++)
                {
                    aDeleted[i] = true;
                }

                return true;
            }

            int max = n + m;
            int offset = max + 2;
            int[] v = new int[2 * max + 5];
            List<int[]> trace = new List<int[]>();
            v[offset + 1] = 0;

            int limit = Math.Min(MaxEditDistance, max);
            for (int d = 0; d <= limit; d++)
            {
                trace.Add(Snapshot(v, offset, d));
                for (int k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                    {
                        x = v[offset + k + 1];
                    }
                    else
                    {
                        x = v[offset + k - 1] + 1;
                    }

                    int y = x - k;
                    while (x < n && y < m && a[x] == b[y])
                    {
                        x++;
                        y++;
                    }

                    v[offset + k] = x;
                    if (x >= n && y >= m)
                    {
                        Backtrack(trace, n, m, aDeleted, bInserted);
                        return true;
                    }
                }
            }

            return false;
        }

        private static int[] Snapshot(int[] v, int offset, int d)
        {
            int size = 2 * (d + 1) + 1;
            int[] snapshot = new int[size];
            for (int k = -(d + 1); k <= d + 1; k++)
            {
                snapshot[k + d + 1] = v[offset + k];
            }

            return snapshot;
        }

        private static int SnapshotAt(int[] snapshot, int d, int k)
        {
            return snapshot[k + d + 1];
        }

        private static void Backtrack(List<int[]> trace, int n, int m, bool[] aDeleted, bool[] bInserted)
        {
            int x = n;
            int y = m;
            for (int d = trace.Count - 1; d >= 0; d--)
            {
                int[] v = trace[d];
                int k = x - y;
                int previousK;
                if (k == -d || (k != d && SnapshotAt(v, d, k - 1) < SnapshotAt(v, d, k + 1)))
                {
                    previousK = k + 1;
                }
                else
                {
                    previousK = k - 1;
                }

                int previousX = SnapshotAt(v, d, previousK);
                int previousY = previousX - previousK;

                while (x > previousX && y > previousY)
                {
                    x--;
                    y--;
                }

                if (d > 0)
                {
                    if (x > previousX && x > 0)
                    {
                        aDeleted[x - 1] = true;
                        x--;
                    }
                    else if (y > previousY && y > 0)
                    {
                        bInserted[y - 1] = true;
                        y--;
                    }
                }

                x = previousX;
                y = previousY;
            }
        }

        private static List<FeederDiffLine> BuildLines(string[] oldLines, string[] newLines,
            bool[] oldDeleted, bool[] newInserted)
        {
            List<FeederDiffLine> lines = new List<FeederDiffLine>(oldLines.Length + newLines.Length);
            int i = 0;
            int j = 0;
            while (i < oldLines.Length || j < newLines.Length)
            {
                if (i < oldLines.Length && oldDeleted[i])
                {
                    lines.Add(MakeLine(FeederDiffOp.Removed, i + 1, -1, oldLines[i]));
                    i++;
                }
                else if (j < newLines.Length && newInserted[j])
                {
                    lines.Add(MakeLine(FeederDiffOp.Added, -1, j + 1, newLines[j]));
                    j++;
                }
                else if (i < oldLines.Length && j < newLines.Length)
                {
                    lines.Add(MakeLine(FeederDiffOp.Equal, i + 1, j + 1, oldLines[i]));
                    i++;
                    j++;
                }
                else if (i < oldLines.Length)
                {
                    lines.Add(MakeLine(FeederDiffOp.Removed, i + 1, -1, oldLines[i]));
                    i++;
                }
                else
                {
                    lines.Add(MakeLine(FeederDiffOp.Added, -1, j + 1, newLines[j]));
                    j++;
                }
            }

            return lines;
        }

        private static void BuildHunks(FeederDiffFileResult result, List<FeederDiffLine> all, int context)
        {
            List<int> changed = new List<int>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Op != FeederDiffOp.Equal)
                {
                    changed.Add(i);
                }
            }

            if (changed.Count == 0)
            {
                return;
            }

            int groupStart = 0;
            for (int g = 0; g < changed.Count; g++)
            {
                bool last = g == changed.Count - 1;
                bool split = !last && changed[g + 1] - changed[g] - 1 > context * 2;
                if (!last && !split)
                {
                    continue;
                }

                int firstChange = changed[groupStart];
                int lastChange = changed[g];
                int lo = Math.Max(0, firstChange - context);
                int hi = Math.Min(all.Count, lastChange + context + 1);
                result.Hunks.Add(CreateHunk(all, lo, hi));
                groupStart = g + 1;
            }
        }

        private static FeederDiffHunk CreateHunk(List<FeederDiffLine> all, int lo, int hi)
        {
            FeederDiffHunk hunk = new FeederDiffHunk();
            int oldStart = -1;
            int newStart = -1;
            for (int i = lo; i < hi; i++)
            {
                FeederDiffLine line = all[i];
                hunk.Lines.Add(line);
                if (line.OldLine > 0)
                {
                    if (oldStart < 0)
                    {
                        oldStart = line.OldLine;
                    }

                    hunk.OldCount++;
                }

                if (line.NewLine > 0)
                {
                    if (newStart < 0)
                    {
                        newStart = line.NewLine;
                    }

                    hunk.NewCount++;
                }
            }

            hunk.OldStart = oldStart >= 0 ? oldStart : PreviousNumber(all, lo, true);
            hunk.NewStart = newStart >= 0 ? newStart : PreviousNumber(all, lo, false);
            hunk.Header = BuildHeader(hunk);
            return hunk;
        }

        private static int PreviousNumber(List<FeederDiffLine> all, int lo, bool old)
        {
            for (int i = lo - 1; i >= 0; i--)
            {
                int number = old ? all[i].OldLine : all[i].NewLine;
                if (number > 0)
                {
                    return number;
                }
            }

            return 0;
        }

        private static void AppendSingleHunk(FeederDiffFileResult result, List<FeederDiffLine> lines,
            int oldStart, int oldCount, int newStart, int newCount)
        {
            if (lines.Count == 0)
            {
                return;
            }

            FeederDiffHunk hunk = new FeederDiffHunk
            {
                OldStart = oldStart,
                OldCount = oldCount,
                NewStart = newStart,
                NewCount = newCount,
                Lines = lines,
            };
            hunk.Header = BuildHeader(hunk);
            result.Hunks.Add(hunk);
        }

        private static string BuildHeader(FeederDiffHunk hunk)
        {
            return $"@@ -{Range(hunk.OldStart, hunk.OldCount)} +{Range(hunk.NewStart, hunk.NewCount)} @@";
        }

        private static string Range(int start, int count)
        {
            return count == 1 ? start.ToString() : $"{start},{count}";
        }

        private static void ApplyWordHighlight(List<FeederDiffLine> lines)
        {
            int i = 0;
            while (i < lines.Count)
            {
                if (lines[i].Op != FeederDiffOp.Removed)
                {
                    i++;
                    continue;
                }

                int removedStart = i;
                while (i < lines.Count && lines[i].Op == FeederDiffOp.Removed)
                {
                    i++;
                }

                int removedCount = i - removedStart;
                if (i >= lines.Count || lines[i].Op != FeederDiffOp.Added)
                {
                    continue;
                }

                int addedStart = i;
                while (i < lines.Count && lines[i].Op == FeederDiffOp.Added)
                {
                    i++;
                }

                if (i - addedStart != removedCount)
                {
                    continue;
                }

                for (int t = 0; t < removedCount; t++)
                {
                    HighlightPair(lines, removedStart + t, addedStart + t);
                }
            }
        }

        private static void HighlightPair(List<FeederDiffLine> lines, int oldIndex, int newIndex)
        {
            FeederDiffLine oldLine = lines[oldIndex];
            FeederDiffLine newLine = lines[newIndex];
            string a = oldLine.Text ?? string.Empty;
            string b = newLine.Text ?? string.Empty;
            if (a.Length > MaxHighlightLineLength || b.Length > MaxHighlightLineLength || a == b)
            {
                return;
            }

            int prefix = 0;
            int maxCommon = Math.Min(a.Length, b.Length);
            while (prefix < maxCommon && a[prefix] == b[prefix])
            {
                prefix++;
            }

            int suffix = 0;
            while (suffix < maxCommon - prefix &&
                   a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
            {
                suffix++;
            }

            int oldLength = a.Length - prefix - suffix;
            int newLength = b.Length - prefix - suffix;

            if (oldLength > a.Length * MaxHighlightRatio || newLength > b.Length * MaxHighlightRatio)
            {
                return;
            }

            oldLine.HlStart = prefix;
            oldLine.HlEnd = prefix + oldLength;
            newLine.HlStart = prefix;
            newLine.HlEnd = prefix + newLength;
            lines[oldIndex] = oldLine;
            lines[newIndex] = newLine;
        }

        public static string ToUnifiedText(string assetPath, bool isNewFile, FeederDiffFileResult result)
        {
            StringBuilder builder = new StringBuilder();
            string path = string.IsNullOrEmpty(assetPath) ? "file" : assetPath;
            builder.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n');
            if (isNewFile)
            {
                builder.Append("new file mode 100644\n");
                builder.Append("--- /dev/null\n");
            }
            else
            {
                builder.Append("--- a/").Append(path).Append('\n');
            }

            builder.Append("+++ b/").Append(path).Append('\n');

            for (int h = 0; h < result.Hunks.Count; h++)
            {
                FeederDiffHunk hunk = result.Hunks[h];
                builder.Append(hunk.Header).Append('\n');
                for (int i = 0; i < hunk.Lines.Count; i++)
                {
                    FeederDiffLine line = hunk.Lines[i];
                    builder.Append(SignOf(line.Op)).Append(line.RawText ?? string.Empty).Append('\n');
                }
            }

            return builder.ToString();
        }

        public static char SignOf(FeederDiffOp op)
        {
            switch (op)
            {
                case FeederDiffOp.Added:
                    return '+';
                case FeederDiffOp.Removed:
                    return '-';
                default:
                    return ' ';
            }
        }
    }
}
