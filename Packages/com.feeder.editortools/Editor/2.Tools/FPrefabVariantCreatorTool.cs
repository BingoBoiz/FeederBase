using System.Collections.Generic;
using Sirenix.OdinInspector;
using Sirenix.Serialization;
using UnityEngine;

namespace Feeder
{
    public sealed class FPrefabVariantCreatorTool : FTargetPrefabsToolBase
    {
        protected override string GetDescription()
        {
            return "Chọn base prefab, vị trí gắn model và thư mục lưu; kéo danh sách model; bấm Create để tạo các variant mới.";
        }

        [Title("Settings")]
        [LabelText("Base Prefab")]
        [OnValueChanged(nameof(OnBasePrefabChanged))]
        [ShowInInspector, OdinSerialize]
        private GameObject basePrefab;

        [LabelText("Locate Model")]
        [ValueDropdown(nameof(GetBasePrefabTransforms), IsUniqueList = false, DropdownWidth = 400, DropdownHeight = 300, DrawDropdownForListElements = false)]
        [ShowInInspector, OdinSerialize]
        private Transform locateModel;

        [PropertySpace(SpaceBefore = 6, SpaceAfter = 6)]
        [LabelText("Save Folder")]
        [FolderPath(AbsolutePath = false, RequireExistingPath = true)]
        [ShowInInspector, OdinSerialize]
        private string saveFolderPath;

        

        private readonly FPrefabTransformDropdownProvider transformDropdownProvider = new FPrefabTransformDropdownProvider();

        [Button(ButtonSizes.Large), GUIColor(0.3f, 0.8f, 1f)]
        public void CreateVariants()
        {
            var config = new PrefabVariantCreatorConfig(
                basePrefab,
                locateModel,
                saveFolderPath);

            var created = new List<GameObject>();
            int createdCount = FPrefabVariantCreatorService.CreatePrefabVariantsFromModels(config, TargetPrefabs, created);
            FSelectionUtils.SelectAndPing(created);
            Debug.Log($"<color=green>Created {createdCount} prefab variant(s).</color>");
        }

        private void OnBasePrefabChanged()
        {
            locateModel = null;
            transformDropdownProvider.Reset();
        }

        private IEnumerable<ValueDropdownItem<Transform>> GetBasePrefabTransforms()
        {
            return transformDropdownProvider.Get(basePrefab);
        }
    }
}
