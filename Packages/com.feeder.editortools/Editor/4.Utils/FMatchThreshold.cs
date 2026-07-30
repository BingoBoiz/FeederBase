using System;

namespace Feeder
{
    /// <summary>
    /// Cách phán quyết một cặp tên có khớp hay không.
    /// <see cref="Percent"/>/<see cref="CharDiff"/> là hai kiểu ngưỡng khớp mờ;
    /// <see cref="Exact"/> là khi tắt Fuzzy Match — chỉ nhận khi tên thô giống hệt.
    /// </summary>
    public enum FMatchMode
    {
        Percent,
        CharDiff,
        Exact,
    }

    /// <summary>
    /// Kết quả so một cặp tên, kèm phán quyết theo chế độ đang chọn.
    /// <see cref="Text"/> tính lazy để vòng lặp match không alloc string.
    /// </summary>
    public readonly struct FMatchScore
    {
        /// <summary>Độ giống 0..1 = 1 - Distance / max(len). Luôn tính, dùng cho log/hiển thị.</summary>
        public readonly float Similarity;

        /// <summary>Số ký tự lệch (Levenshtein), không phụ thuộc độ dài string.</summary>
        public readonly int Distance;

        /// <summary>Cặp này có qua ngưỡng của chế độ đang chọn hay không.</summary>
        public readonly bool Accepted;

        private readonly FMatchMode _mode;

        internal FMatchScore(float similarity, int distance, bool accepted, FMatchMode mode)
        {
            Similarity = similarity;
            Distance = distance;
            Accepted = accepted;
            _mode = mode;
        }

        /// <summary>
        /// Cao hơn = khớp tốt hơn. Chế độ %: chính là Similarity.
        /// Chế độ ký tự lệch: Similarity - Distance — vì Similarity ∈ [0,1] nên giá trị nằm gọn
        /// trong (-Distance, -Distance+1], các mức Distance không chồng lấn nhau. Tức là sắp theo
        /// Distance tăng dần trước, tie thì Similarity cao hơn (tên dài gần query hơn) thắng.
        /// KHÔNG được rank bằng Similarity rồi accept bằng Distance: tên asset dài thường có
        /// Similarity cao hơn dù lệch nhiều ký tự hơn, sẽ che mất candidate ngắn đáng lẽ khớp.
        /// Chế độ Exact: cặp được nhận luôn là 2f, còn lại xếp theo Similarity ≤ 1f. Phải tách band
        /// như vậy vì cặp chỉ khác hoa/thường hoặc thứ tự token cũng có Similarity == 1f, sẽ tie
        /// với cặp giống hệt nếu rank thẳng bằng Similarity.
        /// Nhờ cả ba chế độ đều là một ngưỡng cắt trên Rank mà <see cref="FMatchAssign"/> gán được
        /// cả hai pha (theo ngưỡng, rồi lấp phần dư) trong một vòng greedy duy nhất.
        /// </summary>
        public float Rank => _mode switch
        {
            FMatchMode.Exact => Accepted ? 2f : Similarity,
            FMatchMode.CharDiff => Similarity - Distance,
            _ => Similarity,
        };

        /// <summary>
        /// Hiển thị cho cột Score: "95.0%" ở chế độ %, "Δ2" ở chế độ ký tự lệch.
        /// Chế độ Exact: "exact" khi giống hệt, "≠case" khi normalize giống nhau nhưng tên thô khác
        /// (khác hoa/thường hoặc thứ tự token) — nói thẳng lý do bị loại thay vì hiện 100% gây hiểu nhầm.
        /// </summary>
        public string Text => _mode switch
        {
            FMatchMode.Exact => Accepted ? "exact" : Similarity >= 1f ? "≠case" : ScorePercentText,
            FMatchMode.CharDiff => Similarity <= 0f ? "—" : $"Δ{Distance}",
            _ => ScorePercentText,
        };

        private string ScorePercentText => Similarity <= 0f ? "—" : $"{Similarity * 100f:F1}%";
    }

    /// <summary>
    /// Toàn bộ cấu hình khớp tên của một tool, đọc từ <see cref="FToolPrefs"/>: ngưỡng nhận một cặp
    /// và ba công tắc chiến lược gán (<see cref="Fuzzy"/>, <see cref="UniqueAssetPerKey"/>,
    /// <see cref="FillAllKeys"/>). Ngưỡng có hai kiểu: theo tỷ lệ % (chia theo độ dài — hụt với tên
    /// ngắn) hoặc theo số ký tự lệch tối đa (độc lập độ dài).
    /// Vẽ UI bằng <see cref="FMatchThresholdGUI"/>, gán bằng <see cref="FMatchAssign"/>.
    /// </summary>
    public readonly struct FMatchThreshold
    {
        // Giữ đúng chuỗi key cũ để không reset giá trị người dùng đã set.
        internal const string PercentKey = "_matchThreshold";
        internal const string ModeKey = "_matchByCharDiff";
        internal const string CharDiffKey = "_maxCharDiff";
        internal const string FuzzyKey = "_matchFuzzy";
        internal const string UniqueKey = "_matchUniqueAsset";
        internal const string FillAllKey = "_matchFillAll";
        internal const int CharDiffDefault = 2;
        internal const int CharDiffMax = 10;

        public readonly bool ByCharDiff;
        public readonly float MinSimilarity;
        public readonly int MaxCharDiff;

        /// <summary>Bật: nhận cặp đạt ngưỡng %/Δ. Tắt: chỉ nhận khi tên thô giống hệt từng ký tự.</summary>
        public readonly bool Fuzzy;

        /// <summary>Bật: mỗi asset chỉ gán cho một key. Tắt: nhiều key dùng chung một asset được.</summary>
        public readonly bool UniqueAssetPerKey;

        /// <summary>
        /// Bật: key không đạt ngưỡng vẫn nhận asset tốt nhất còn lại (giả định số asset ≥ số key),
        /// và ngưỡng chuyển vai thành mốc tin cậy — cặp dưới ngưỡng bị đánh dấu là gán ép.
        /// </summary>
        public readonly bool FillAllKeys;

        private FMatchThreshold(bool byCharDiff, float minSimilarity, int maxCharDiff,
                                bool fuzzy, bool uniqueAssetPerKey, bool fillAllKeys)
        {
            ByCharDiff = byCharDiff;
            MinSimilarity = minSimilarity;
            MaxCharDiff = maxCharDiff;
            Fuzzy = fuzzy;
            UniqueAssetPerKey = uniqueAssetPerKey;
            FillAllKeys = fillAllKeys;
        }

        public static FMatchThreshold Load(string owner, float defaultPercent) => new FMatchThreshold(
            FToolPrefs.GetBool(owner, ModeKey, false),
            FToolPrefs.GetFloat(owner, PercentKey, defaultPercent),
            FToolPrefs.GetInt(owner, CharDiffKey, CharDiffDefault),
            FToolPrefs.GetBool(owner, FuzzyKey, true),
            FToolPrefs.GetBool(owner, UniqueKey, true),
            FToolPrefs.GetBool(owner, FillAllKey, true));

        /// <summary>
        /// Chế độ phán quyết đang có hiệu lực. <see cref="ByCharDiff"/> vẫn được lưu khi tắt
        /// <see cref="Fuzzy"/>, nên bật Fuzzy lại là khôi phục đúng kiểu ngưỡng người dùng đã chọn.
        /// </summary>
        public FMatchMode Mode => !Fuzzy ? FMatchMode.Exact
            : ByCharDiff ? FMatchMode.CharDiff
            : FMatchMode.Percent;

        /// <summary>
        /// So một cặp tên. Chạy Levenshtein trên dạng normalize một lần rồi suy ra cả Distance lẫn
        /// Similarity — hai giá trị này luôn được tính kể cả ở chế độ Exact, vì cột Score vẫn hiện
        /// điểm để người dùng tinh chỉnh và Fill All Keys cần chúng để xếp hạng phần lấp dư.
        /// </summary>
        public FMatchScore Evaluate(FMatchName a, FMatchName b)
        {
            int dist = FuzzyMatchUtils.Distance(a.Normalized, b.Normalized);
            int maxLen = Math.Max(a.Normalized?.Length ?? 0, b.Normalized?.Length ?? 0);
            float similarity = maxLen == 0 ? 1f : 1f - (float)dist / maxLen;
            FMatchMode mode = Mode;
            bool accepted = mode switch
            {
                FMatchMode.Exact => string.Equals(a.Raw, b.Raw, StringComparison.Ordinal),
                FMatchMode.CharDiff => dist <= MaxCharDiff,
                _ => similarity >= MinSimilarity,
            };
            return new FMatchScore(similarity, dist, accepted, mode);
        }
    }
}
