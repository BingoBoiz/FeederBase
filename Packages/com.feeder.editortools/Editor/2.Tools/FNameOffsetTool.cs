using System.Collections.Generic;
using Sirenix.OdinInspector;
using Sirenix.Serialization;
using UnityEngine;

namespace Feeder
{
    public sealed class FNameOffsetTool : FTargetPrefabsToolBase
    {
        protected override string GetDescription()
        {
            return "Chọn đường dẫn holder và offset Y, thêm target, bấm Adjust để dời vị trí tên theo offset đã chọn.";
        }

        [Title("Settings")]
        [LabelText("Holder Path")]
        [ValueDropdown(nameof(GetHolderPathOptions), IsUniqueList = false, DropdownWidth = 400, DropdownHeight = 300, DrawDropdownForListElements = false)]
        [ShowInInspector, OdinSerialize]
        private string holderPath = FHierarchyPathOptionsProvider.RootToken;

        [LabelText("Offset Y")]
        [ShowInInspector, OdinSerialize]
        private float offsetY = 0.1f;

        private readonly FHierarchyPathOptionsProvider pathOptionsProvider = new FHierarchyPathOptionsProvider();

        [Button(ButtonSizes.Large), GUIColor(0.3f, 0.8f, 1f)]
        public void AdjustOffset()
        {
            int adjustedCount = FNameOffsetAdjusterService.AdjustOffset(TargetPrefabs, holderPath, offsetY);
            Debug.Log($"<color=green>Adjusted {adjustedCount} target(s).</color>");
        }

        private IEnumerable<ValueDropdownItem<string>> GetHolderPathOptions()
        {
            if (!(TargetPrefabs?.Count > 0))
                return new List<ValueDropdownItem<string>>(0);

            var first = GetFirstNonNullTarget();
            return first == null
                ? new List<ValueDropdownItem<string>>(0)
                : pathOptionsProvider.Build(first);
        }

        private GameObject GetFirstNonNullTarget()
        {
            for (int i = 0; i < TargetPrefabs.Count; i++)
            {
                var t = TargetPrefabs[i];
                if (t != null) return t;
            }
            return null;
        }
    }
}
