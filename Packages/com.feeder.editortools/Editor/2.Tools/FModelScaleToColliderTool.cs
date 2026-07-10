using System.Collections.Generic;
using Sirenix.OdinInspector;
using Sirenix.Serialization;
using UnityEngine;

namespace Feeder
{
    public sealed class FModelScaleToColliderTool : FTargetPrefabsToolBase
    {
        protected override string GetDescription()
        {
            return "Chọn base prefab và đường dẫn model, thêm danh sách target, bấm Scale để scale model theo collider của base.";
        }

        [Title("Settings")]
        [LabelText("Base Prefab")]
        [ShowInInspector, OdinSerialize]
        private GameObject basePrefab;

        [LabelText("Target Path")]
        [ValueDropdown(nameof(GetTargetPathOptions), IsUniqueList = false, DropdownWidth = 400, DropdownHeight = 300, DrawDropdownForListElements = false)]
        [ShowInInspector, OdinSerialize]
        private string targetPath = FHierarchyPathOptionsProvider.RootToken;

        private readonly FHierarchyPathOptionsProvider pathOptionsProvider = new FHierarchyPathOptionsProvider();

        [Button(ButtonSizes.Large), GUIColor(0.3f, 0.8f, 1f)]
        public void ScaleModels()
        {
            int scaledCount = FModelScaleToColliderService.ScaleTargets(basePrefab, targetPath, TargetPrefabs);
            Debug.Log($"<color=green>Scaled {scaledCount} model(s).</color>");
        }

        private IEnumerable<ValueDropdownItem<string>> GetTargetPathOptions()
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
