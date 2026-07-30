using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Feeder
{
    /// <summary>
    /// Một tên tham gia khớp, giữ sẵn cả hai dạng: <see cref="Raw"/> cho chế độ so tuyệt đối,
    /// <see cref="Normalized"/> cho khớp mờ. Normalize một lần ở đây để vòng lặp so cặp
    /// (O(key × asset)) không phải normalize lại.
    /// </summary>
    public readonly struct FMatchName
    {
        public readonly string Raw;
        public readonly string Normalized;

        public FMatchName(string raw)
        {
            Raw = raw ?? "";
            Normalized = FuzzyMatchUtils.Normalize(raw);
        }

        /// <summary>Tên rỗng = slot không tham gia khớp (asset null, key bị skip).</summary>
        public bool IsEmpty => string.IsNullOrEmpty(Raw);
    }

    public static class FuzzyMatchUtils
    {
        // Splits PascalCase / camelCase boundaries: "FlowerChoker" → ["Flower", "Choker"]
        private static readonly Regex CamelSplit = new Regex(
            @"(?<=[a-z\d])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])",
            RegexOptions.Compiled);

        // Splits on common separators: _, -, space, dot
        private static readonly Regex SepSplit = new Regex(
            @"[_\-\s\.]+",
            RegexOptions.Compiled);

        /// <summary>
        /// Normalizes a name for fuzzy comparison:
        /// splits by delimiters and camelCase, lowercases all tokens,
        /// sorts tokens alphabetically (neutralizes reordering), then concatenates.
        /// </summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var byDelimiter = SepSplit.Split(s);
            var tokens = new List<string>();
            for (int i = 0; i < byDelimiter.Length; i++)
            {
                var part = byDelimiter[i];
                if (string.IsNullOrEmpty(part)) continue;
                var subParts = CamelSplit.Split(part);
                for (int j = 0; j < subParts.Length; j++)
                {
                    if (!string.IsNullOrEmpty(subParts[j]))
                        tokens.Add(subParts[j].ToLowerInvariant());
                }
            }
            tokens.Sort(StringComparer.Ordinal);
            return string.Concat(tokens);
        }

        /// <summary>
        /// Số ký tự lệch (Levenshtein distance) giữa hai string đã normalize. 0 = giống hệt.
        /// Tỷ lệ giống 0..1 (1 - dist / max(len)) tính trong <see cref="FMatchThreshold.Evaluate"/>.
        /// </summary>
        public static int Distance(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;
            return LevenshteinDistance(a, b);
        }

        // Two-row rolling Levenshtein (O(n) space instead of O(m*n))
        private static int LevenshteinDistance(string a, string b)
        {
            int m = a.Length, n = b.Length;
            var prev = new int[n + 1];
            var curr = new int[n + 1];
            for (int j = 0; j <= n; j++) prev[j] = j;
            for (int i = 1; i <= m; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= n; j++)
                {
                    curr[j] = a[i - 1] == b[j - 1]
                        ? prev[j - 1]
                        : 1 + Math.Min(prev[j - 1], Math.Min(prev[j], curr[j - 1]));
                }
                var tmp = prev; prev = curr; curr = tmp;
            }
            return prev[n];
        }
    }
}
