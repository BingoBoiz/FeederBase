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
        private string _selectedEnumTypeName
        {
            get => FToolPrefs.GetString(nameof(FSortOrderTool), nameof(_selectedEnumTypeName), null);
            set => FToolPrefs.SetString(nameof(FSortOrderTool), nameof(_selectedEnumTypeName), value);
        }

        private const float DefaultThresholdPercent = 0.9f;

        [OnInspectorGUI]
        private void DrawMatchThreshold()
            => FMatchThresholdGUI.Draw(nameof(FSortOrderTool), DefaultThresholdPercent);

        [PropertyOrder(40)]
        [OnInspectorGUI]
        private void DrawGuide()
        {
            GUILayout.Space(2);
            FStylesUtils.DrawInfoBox(
                "Enum Type       chọn enum làm thứ tự chuẩn (member None bị bỏ qua)\n" +
                "Threshold       ngưỡng khớp tối thiểu (0–1), thường để 0.8–0.9. Bấm icon cạnh label để đổi\n" +
                "                sang ngưỡng theo số ký tự lệch (gợi ý 1–3) khi tên ngắn — % chia theo\n" +
                "                độ dài nên tên ngắn lệch 2 ký tự đã tụt xuống ~50%\n" +
                "Match Strategy  Fuzzy         tắt = chỉ gán khi tên asset giống hệt tên enum từng ký tự.\n" +
                "                              Dòng ngưỡng ẩn đi, Score hiện 'exact' — hoặc '≠case' nếu\n" +
                "                              chỉ khác hoa/thường / thứ tự token\n" +
                "                Unique Asset  mỗi asset chỉ gán cho 1 enum; tắt = nhiều enum dùng chung 1 asset\n" +
                "                Fill All      enum dưới ngưỡng vẫn nhận asset tốt nhất còn lại (giả định số\n" +
                "                              asset ≥ số enum). Ngưỡng thành mốc tin cậy: dòng gán ép có dấu *\n" +
                "Analyze         xem bảng map enum → asset kèm % khớp (hoặc Δn = số ký tự lệch)\n" +
                "Apply Sort      sắp xếp lại TargetAssets theo thứ tự enum (null = không khớp). Lưu ý: asset\n" +
                "                dư ngoài số enum bị loại khỏi TargetAssets"
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

            FMatchThreshold threshold = FMatchThreshold.Load(nameof(FSortOrderTool), DefaultThresholdPercent);

            _mappingRows ??= new List<SortOrderMappingRow>();
            _mappingRows.Clear();

            List<string> enumNames = FEnumTypeUtils.GetMatchableMemberNames(enumType);
            string[] assetNames = new string[TargetAssets.Count];
            for (int i = 0; i < TargetAssets.Count; i++)
                assetNames[i] = TargetAssets[i] != null ? TargetAssets[i].name : null;

            FMatchAssign.Entry[] entries = FMatchAssign.Run(enumNames, assetNames, threshold);

            for (int i = 0; i < enumNames.Count; i++)
            {
                FMatchAssign.Entry entry = entries[i];
                _mappingRows.Add(new SortOrderMappingRow
                {
                    EnumName = enumNames[i],
                    Score = entry.HasScore ? entry.Score.Text + (entry.Forced ? "*" : "") : "—",
                    Asset = entry.AssetIndex >= 0 ? TargetAssets[entry.AssetIndex] : null,
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
