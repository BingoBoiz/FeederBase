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
            [DisplayAsString]
            public string Score;

            [AssetSelector(Paths = "Assets")]
            public UnityEngine.Object Asset;
        }

        protected override string GetDescription()
        {
            return "Khớp mờ TargetAssets với các giá trị enum theo tên. Bỏ qua hoa/thường, ký tự đặc biệt, đảo thứ tự token và lỗi gõ. " +
                   "Ví dụ: enum 'Jewelry_Flower_Choker' khớp asset 'flowe_choker_jewelry' ở mức ~95%. " +
                   "Ngưỡng % chia theo độ dài tên nên hụt với tên ngắn — bấm icon cạnh label để đổi sang ngưỡng theo số ký tự lệch (gợi ý 1–3), " +
                   "lúc đó cột Score hiện 'Δn' = số ký tự sai. " +
                   "Dải Match Strategy: tắt 'Fuzzy' để chỉ gán khi tên giống hệt từng ký tự (dòng ngưỡng ẩn đi); " +
                   "tắt 'Unique Asset' để nhiều enum dùng chung một asset; " +
                   "bật 'Fill All' thì enum dưới ngưỡng vẫn nhận asset tốt nhất còn lại và dòng bị gán ép có dấu * ở cột Score. " +
                   "Điểm số vẫn hiện khi dưới ngưỡng để bạn tinh chỉnh. Cột Asset sửa được để chỉnh tay.";
        }

        [PropertySpace(SpaceBefore = 8)]
        [LabelText("Enum Type")]
        [ValueDropdown(nameof(GetEnumTypeDropdown))]
        [ShowInInspector]
        private string _selectedEnumTypeName
        {
            get => FToolPrefs.GetString(nameof(FCompareStringTool), nameof(_selectedEnumTypeName), null);
            set => FToolPrefs.SetString(nameof(FCompareStringTool), nameof(_selectedEnumTypeName), value);
        }

        private const float DefaultThresholdPercent = 0.9f;

        [OnInspectorGUI]
        private void DrawMatchThreshold()
            => FMatchThresholdGUI.Draw(nameof(FCompareStringTool), DefaultThresholdPercent);

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

            FMatchThreshold threshold = FMatchThreshold.Load(nameof(FCompareStringTool), DefaultThresholdPercent);

            _rows ??= new List<MatchRow>();
            _rows.Clear();

            List<string> enumNames = FEnumTypeUtils.GetMatchableMemberNames(enumType);
            var assetNames = new string[TargetAssets.Count];
            for (int i = 0; i < TargetAssets.Count; i++)
                assetNames[i] = TargetAssets[i] != null ? TargetAssets[i].name : null;

            FMatchAssign.Entry[] entries = FMatchAssign.Run(enumNames, assetNames, threshold);

            for (int i = 0; i < enumNames.Count; i++)
            {
                FMatchAssign.Entry entry = entries[i];
                _rows.Add(new MatchRow
                {
                    EnumName = enumNames[i],
                    Score = entry.HasScore ? entry.Score.Text + (entry.Forced ? "*" : "") : "—",
                    Asset = entry.AssetIndex >= 0 ? TargetAssets[entry.AssetIndex] : null,
                });
            }
        }

        private IEnumerable<ValueDropdownItem<string>> GetEnumTypeDropdown()
            => FEnumTypeUtils.GetEnumTypeDropdown();
    }
}
