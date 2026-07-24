using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEngine;

namespace Feeder
{
    public sealed class FComponentReplacerTool : FTargetPrefabsToolBase
    {
        protected override string GetDescription()
        {
            return "Chọn danh sách prefab/scene, chọn Script A và Script B, bấm Replace để thay thế B bằng A và copy dữ liệu tương ứng.";
        }

        [Title("Component Types")]
        [LabelText("Replace With (Script A)")]
        [ValueDropdown(nameof(GetComponentTypeOptions))]
        [ShowInInspector]
        private Type replaceWithType
        {
            get => ResolveType(FToolPrefs.GetString(nameof(FComponentReplacerTool), nameof(replaceWithType), null));
            set => FToolPrefs.SetString(nameof(FComponentReplacerTool), nameof(replaceWithType), value?.AssemblyQualifiedName);
        }

        [LabelText("Find (Script B)")]
        [ValueDropdown(nameof(GetComponentTypeOptions))]
        [ShowInInspector]
        private Type findType
        {
            get => ResolveType(FToolPrefs.GetString(nameof(FComponentReplacerTool), nameof(findType), null));
            set => FToolPrefs.SetString(nameof(FComponentReplacerTool), nameof(findType), value?.AssemblyQualifiedName);
        }

        private static Type ResolveType(string assemblyQualifiedName)
            => string.IsNullOrEmpty(assemblyQualifiedName) ? null : Type.GetType(assemblyQualifiedName);

        [Button(ButtonSizes.Large)]
        public void ReplaceComponent()
        {
            var result = FComponentReplaceService.ReplaceComponents(ReplaceWithType, FindType, TargetPrefabs);
            Debug.Log($"<color=green>Replaced {result.ReplacedCount} component(s) in {result.ModifiedPrefabs} prefab(s), {result.ModifiedSceneObjects} scene object(s).</color>");
        }

        private IEnumerable<ValueDropdownItem<Type>> GetComponentTypeOptions()
        {
            return FComponentTypeOptionsProvider.GetComponentTypeOptions();
        }

        private Type ReplaceWithType => replaceWithType ?? throw new InvalidOperationException("replace type is null.");
        private Type FindType => findType ?? throw new InvalidOperationException("find type is null.");
    }
}
