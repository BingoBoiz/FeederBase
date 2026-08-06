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

            [TableColumnWidth(280)]
            [AssetSelector(Paths = "Assets")]
            public UnityEngine.Object Asset;
        }

        protected override string GetDescription()
        {
            return "Áp thứ tự TargetAssets vào thứ tự enum theo đúng vị trí (asset thứ i → enum thứ i). Analyze hiện bảng map; Apply Sort ghi đè TargetAssets theo thứ tự enum (null = thiếu asset).";
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

        [PropertyOrder(40)]
        [OnInspectorGUI]
        private void DrawGuide()
        {
            GUILayout.Space(2);
            FStylesUtils.DrawInfoBox(
                "Enum Type    chọn enum làm thứ tự chuẩn (member None bị bỏ qua)\n" +
                "Analyze      ghép theo vị trí: asset thứ i trong TargetAssets → enum thứ i.\n" +
                "             Không so tên, nên hãy kéo thả TargetAssets về đúng thứ tự trước\n" +
                "             (cột Asset trong bảng vẫn sửa tay được)\n" +
                "Apply Sort   ghi đè TargetAssets theo thứ tự enum. Lưu ý: asset dư ngoài số enum\n" +
                "             bị loại khỏi TargetAssets, enum thiếu asset thành null"
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

            List<string> enumNames = FEnumTypeUtils.GetMatchableMemberNames(enumType);
            for (int i = 0; i < enumNames.Count; i++)
            {
                _mappingRows.Add(new SortOrderMappingRow
                {
                    EnumName = enumNames[i],
                    Asset = i < TargetAssets.Count ? TargetAssets[i] : null,
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
