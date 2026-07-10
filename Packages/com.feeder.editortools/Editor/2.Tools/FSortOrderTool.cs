using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEngine;

namespace Feeder
{
    public sealed class FSortOrderTool : FTargetAssetsToolBase
    {
        [System.Serializable]
        private sealed class SortOrderMappingRow
        {
            [TableColumnWidth(180)]
            public string EnumName;

            [TableColumnWidth(80, Resizable = false)]
            [ReadOnly]
            [SuffixLabel("%")]
            [DisplayAsString]
            public string Score;

            [TableColumnWidth(280)]
            [AssetSelector(Paths = "Assets")]
            public UnityEngine.Object Asset;
        }

        protected override string GetDescription()
        {
            return "Sắp xếp TargetAssets theo thứ tự enum bằng cách khớp tên mờ. Analyze hiện bảng map; Apply Sort ghi đè TargetAssets theo thứ tự enum (null = không khớp).";
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

        [PropertyOrder(40)]
        [OnInspectorGUI]
        private void DrawGuide()
        {
            GUILayout.Space(2);
            FStylesUtils.DrawInfoBox(
                "Enum Type    chọn enum làm thứ tự chuẩn\n" +
                "Threshold    ngưỡng độ khớp tối thiểu (0–1), thường để 0.8–0.9\n" +
                "Analyze      xem bảng map enum → asset kèm % khớp\n" +
                "Apply Sort   sắp xếp lại TargetAssets theo thứ tự enum (null = không khớp)"
            );
            GUILayout.Space(4);
        }

        [PropertyOrder(50)]
        [PropertySpace(SpaceBefore = 10)]
        [ButtonGroup("SortActions")]
        [Button("Analyze", ButtonSizes.Medium)]
        private void Analyze()
        {
            Type enumType = FEnumTypeUtils.ResolveEnumType(_selectedEnumTypeName);
            if (enumType == null)
                throw new InvalidOperationException("Select an enum type first.");
            if (TargetAssets == null)
                throw new InvalidOperationException("TargetAssets is null.");

            _mappingRows ??= new List<SortOrderMappingRow>();
            _mappingRows.Clear();

            string[] assetNormalized = new string[TargetAssets.Count];
            for (int i = 0; i < TargetAssets.Count; i++)
                assetNormalized[i] = TargetAssets[i] != null ? FuzzyMatchUtils.Normalize(TargetAssets[i].name) : null;

            HashSet<int> usedIndices = new HashSet<int>();
            Array enumValues = System.Enum.GetValues(enumType);

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

                _mappingRows.Add(new SortOrderMappingRow
                {
                    EnumName = enumName,
                    Score = bestScore > 0f ? $"{bestScore * 100f:F1}" : "—",
                    Asset = matchedAsset,
                });
            }
        }

        [PropertyOrder(50)]
        [ButtonGroup("SortActions")]
        [Button("Apply Sort", ButtonSizes.Medium)]
        private void ApplySort()
        {
            if (_mappingRows == null || _mappingRows.Count == 0)
                throw new InvalidOperationException("Run Analyze first and ensure an enum type is selected.");
            FDataContainer data = GetDataContainer();
            data.TargetAssets.Clear();
            for (int i = 0; i < _mappingRows.Count; i++)
                data.TargetAssets.Add(_mappingRows[i].Asset);
            data.SyncAllFromAssets();
            FDataPersistenceService.SaveData(data);
        }

        [PropertyOrder(100)]
        [PropertySpace(SpaceBefore = 10)]
        [ShowIf(nameof(HasMapping))]
        [TableList(ShowIndexLabels = true, IsReadOnly = false, NumberOfItemsPerPage = 15, AlwaysExpanded = true, ShowPaging = true)]
        [LabelText("Enum → Asset mapping")]
        [SerializeField]
        private List<SortOrderMappingRow> _mappingRows = new List<SortOrderMappingRow>();

        private bool HasMapping => _mappingRows?.Count > 0;

        private IEnumerable<ValueDropdownItem<string>> GetEnumTypeDropdown()
            => FEnumTypeUtils.GetEnumTypeDropdown();
    }
}
