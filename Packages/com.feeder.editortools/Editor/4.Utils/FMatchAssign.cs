using System.Collections.Generic;

namespace Feeder
{
    /// <summary>
    /// Gán asset cho key bằng khớp tên mờ, dùng chung cho mọi tool có bảng key → asset.
    /// Greedy toàn cục: score tất cả cặp, sắp theo <see cref="FMatchScore.Rank"/> giảm dần, gán từ
    /// cặp tốt nhất xuống. Nhờ vậy kết quả không phụ thuộc thứ tự key được truyền vào.
    /// Ba công tắc chiến lược đọc từ <see cref="FMatchThreshold"/>.
    /// </summary>
    public static class FMatchAssign
    {
        /// <summary>Kết quả cho một key, index trùng index của key trong list truyền vào.</summary>
        public readonly struct Entry
        {
            /// <summary>Index trong list asset, -1 = không gán được.</summary>
            public readonly int AssetIndex;

            /// <summary>Điểm của cặp đã gán, hoặc điểm cặp tốt nhất của key nếu không gán được.</summary>
            public readonly FMatchScore Score;

            /// <summary>false = key không có candidate nào (không có asset, hoặc key bị bỏ qua).</summary>
            public readonly bool HasScore;

            /// <summary>Gán nhờ Fill All Keys dù cặp không đạt ngưỡng — tức là gán ép, đáng để soi lại.</summary>
            public readonly bool Forced;

            internal Entry(int assetIndex, FMatchScore score, bool hasScore, bool forced)
            {
                AssetIndex = assetIndex;
                Score = score;
                HasScore = hasScore;
                Forced = forced;
            }
        }

        /// <param name="keyNames">Tên các key. Entry null/rỗng = key bỏ qua hoàn toàn (không match, không lấp).</param>
        /// <param name="assetNames">Tên các asset. Entry null/rỗng = slot rỗng (asset null).</param>
        /// <param name="threshold">Ngưỡng + ba công tắc chiến lược, lấy từ <see cref="FMatchThreshold.Load"/>.</param>
        /// <param name="allowForceFill">
        /// null = mọi key đều được Fill All Keys lấp. false tại index k = key k vẫn khớp theo ngưỡng
        /// bình thường nhưng không bị lấp ép (dùng cho member <c>None</c> của enum).
        /// </param>
        public static Entry[] Run(IReadOnlyList<string> keyNames, IReadOnlyList<string> assetNames,
                                  FMatchThreshold threshold, IReadOnlyList<bool> allowForceFill = null)
        {
            int keyCount = keyNames?.Count ?? 0;
            int assetCount = assetNames?.Count ?? 0;

            // default(Entry) có AssetIndex == 0, không phải -1 — nên phải khởi tạo tường minh,
            // nếu không key không có candidate nào sẽ trỏ nhầm vào asset đầu tiên.
            var entries = new Entry[keyCount];
            for (int k = 0; k < keyCount; k++)
                entries[k] = new Entry(-1, default, false, false);

            if (keyCount == 0 || assetCount == 0) return entries;

            var keys = new FMatchName[keyCount];
            for (int k = 0; k < keyCount; k++)
                keys[k] = new FMatchName(keyNames[k]);

            var assets = new FMatchName[assetCount];
            for (int a = 0; a < assetCount; a++)
                assets[a] = new FMatchName(assetNames[a]);

            // Score mọi cặp, đồng thời giữ lại cặp tốt nhất mỗi key để còn điểm hiển thị cho
            // những key cuối cùng không được gán.
            var candidates = new List<(int keyIdx, int assetIdx, FMatchScore score)>(keyCount * assetCount);
            for (int k = 0; k < keyCount; k++)
            {
                if (keys[k].IsEmpty) continue;
                for (int a = 0; a < assetCount; a++)
                {
                    if (assets[a].IsEmpty) continue;
                    FMatchScore score = threshold.Evaluate(keys[k], assets[a]);
                    candidates.Add((k, a, score));
                    if (!entries[k].HasScore || score.Rank > entries[k].Score.Rank)
                        entries[k] = new Entry(-1, score, true, false);
                }
            }

            // Ở cả ba chế độ, Accepted đúng bằng một ngưỡng cắt trên Rank (xem FMatchScore.Rank),
            // nên sắp Rank giảm dần đặt mọi cặp đạt ngưỡng trước mọi cặp bị loại. Vì thế một vòng
            // greedy duy nhất lo được cả hai pha: khớp theo ngưỡng, rồi Fill All Keys lấp phần dư.
            candidates.Sort((x, y) => y.score.Rank.CompareTo(x.score.Rank));

            var takenKeys = new HashSet<int>();
            var takenAssets = new HashSet<int>();

            foreach ((int k, int a, FMatchScore score) in candidates)
            {
                if (takenKeys.Contains(k)) continue;
                if (threshold.UniqueAssetPerKey && takenAssets.Contains(a)) continue;

                bool forced = !score.Accepted;
                if (forced && !(threshold.FillAllKeys && (allowForceFill == null || allowForceFill[k])))
                    continue;

                entries[k] = new Entry(a, score, true, forced);
                takenKeys.Add(k);
                if (threshold.UniqueAssetPerKey) takenAssets.Add(a);
            }

            return entries;
        }
    }
}
