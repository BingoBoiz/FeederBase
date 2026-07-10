using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEngine;

namespace Feeder
{
    public sealed class FCompareStringTool : FTargetAssetsToolBase
    {
        [System.Serializable]
        private sealed class MatchRow
        {
            [TableColumnWidth(200, Resizable = true)]
            [ReadOnly]
            public string EnumName;

            [TableColumnWidth(80, Resizable = false)]
            [ReadOnly]
            [SuffixLabel("%")]
            [DisplayAsString]
            public string Score;

            [AssetSelector(Paths = "Assets")]
            public UnityEngine.Object Asset;
        }

        protected override string GetDescription()
        {
            return "Khớp mờ TargetAssets với các giá trị enum theo tên. Bỏ qua hoa/thường, ký tự đặc biệt, đảo thứ tự token và lỗi gõ. " +
                   "Ví dụ: enum 'Jewelry_Flower_Choker' khớp asset 'flowe_choker_jewelry' ở mức ~95%. " +
                   "Điểm số vẫn hiện khi dưới ngưỡng để bạn tinh chỉnh. Cột Asset sửa được để chỉnh tay.";
        }

        [PropertySpace(SpaceBefore = 8)]
        [LabelText("Enum Type")]
        [ValueDropdown(nameof(GetEnumTypeDropdown))]
        [ShowInInspector]
        [SerializeField]
        private string _selectedEnumTypeName;

        [PropertySpace(SpaceBefore = 6)]
        [LabelText("Match Threshold (0–1)")]
        [Range(0f, 1f)]
        [SerializeField]
        private float _matchThreshold = 0.9f;

        [PropertySpace(SpaceBefore = 10)]
        [ShowIf(nameof(HasRows))]
        [TableList(ShowIndexLabels = true, IsReadOnly = false, NumberOfItemsPerPage = 15, AlwaysExpanded = true, ShowPaging = true)]
        [LabelText("Enum → Asset mapping")]
        [SerializeField]
        private List<MatchRow> _rows = new List<MatchRow>();

        private bool HasRows => _rows?.Count > 0;

        [PropertySpace(SpaceBefore = 6)]
        [Button("Analyze", ButtonSizes.Large)]
        private void Analyze()
        {
            var enumType = FEnumTypeUtils.ResolveEnumType(_selectedEnumTypeName);
            if (enumType == null)
                throw new InvalidOperationException("Select an enum type first.");
            if (TargetAssets == null)
                throw new InvalidOperationException("TargetAssets is null.");

            _rows ??= new List<MatchRow>();
            _rows.Clear();

            var assetNormalized = new string[TargetAssets.Count];
            for (int i = 0; i < TargetAssets.Count; i++)
                assetNormalized[i] = TargetAssets[i] != null ? FuzzyMatchUtils.Normalize(TargetAssets[i].name) : null;

            var usedIndices = new HashSet<int>();
            var enumValues = System.Enum.GetValues(enumType);

            for (int i = 0; i < enumValues.Length; i++)
            {
                object enumVal = enumValues.GetValue(i);
                string enumName = enumVal?.ToString() ?? "";
                if (FEnumTypeUtils.ShouldSkipEnumMember(enumName))
                    continue;

                string normalizedEnum = FuzzyMatchUtils.Normalize(enumName);

                int bestIndex = -1;
                float bestScore = 0f;

                for (int j = 0; j < TargetAssets.Count; j++)
                {
                    if (usedIndices.Contains(j) || assetNormalized[j] == null) continue;
                    float score = FuzzyMatchUtils.Similarity(normalizedEnum, assetNormalized[j]);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = j;
                    }
                }

                UnityEngine.Object matchedAsset = null;
                if (bestIndex >= 0 && bestScore >= _matchThreshold)
                {
                    matchedAsset = TargetAssets[bestIndex];
                    usedIndices.Add(bestIndex);
                }

                _rows.Add(new MatchRow
                {
                    EnumName = enumName,
                    Score = bestScore > 0f ? $"{bestScore * 100f:F1}" : "—",
                    Asset = matchedAsset,
                });
            }
        }

        private IEnumerable<ValueDropdownItem<string>> GetEnumTypeDropdown()
            => FEnumTypeUtils.GetEnumTypeDropdown();
    }
}
