using System;
using Sirenix.OdinInspector.Editor;
using UnityEngine;

namespace Feeder
{
    internal static class FMenuCatalog
    {
        private const string AssetRoot = "Asset/";
        private const string PrefabRoot = "Prefab/";
        private const string OptimizedRoot = "Optimized/";

        public const string AssetCollectorToolPath = "Asset Collector";
        public const string AssetGroupToolPath = "Asset Group Tool";

        public const string CompareStringToolPath = AssetRoot + "Compare String Tool";
        public const string BatchSortOrderToolPath = AssetRoot + "Sort Order Tool";
        public const string BatchRenameToolPath = AssetRoot + "Rename Tool";
        public const string AssetOrganizerToolPath = AssetRoot + "Asset Organizer Tool";
        public const string DataFillerToolPath = AssetRoot + "Data Filler Tool";
        public const string CharacterMeshUpdaterToolPath = AssetRoot + "Character Mesh Updater Tool";
        public const string DataClonerToolPath = AssetRoot + "Data Cloner Tool";
        
        public const string BatchComponentModifyToolPath = PrefabRoot + "Component Modify Tool";
        public const string BatchPrefabModifyToolPath = PrefabRoot + "Prefab Modify Tool";
        public const string BatchPrefabVariantCreatorToolPath = PrefabRoot + "Prefab Variant Creator Tool";
        public const string BatchNameOffsetToolPath = PrefabRoot + "Name Offset Tool";
        public const string ModelScaleToColliderToolPath = PrefabRoot + "Model Scale To Collider Tool";
        public const string MeshBoxColliderFitterToolPath = PrefabRoot + "Mesh Box Collider Fitter Tool";
        public const string BatchComponentReplacerToolPath = PrefabRoot + "Component Replacer Tool";
        public const string BatchMissingComponentToolPath = PrefabRoot + "Missing Component Handler Tool";

        public const string UnpackMeshToolPath = OptimizedRoot + "Unpack Mesh Tool";
        public const string UnpackAnimationToolPath = OptimizedRoot + "Unpack Animation Tool";
        public const string RepackModelsToolPath = OptimizedRoot + "Repack Models Tool";
        public const string DeduplicateMeshToolPath = OptimizedRoot + "Deduplicate Mesh Tool";
        public const string DeduplicateTextureToolPath = OptimizedRoot + "Deduplicate Texture Tool";
        public const string DeduplicateMaterialToolPath = OptimizedRoot + "Deduplicate Material Tool";
        public const string VfxFlipbookTrimToolPath = OptimizedRoot + "VFX Flipbook Trim Tool";

        public static void AddTools(OdinMenuTree tree)
        {
            if (tree == null) throw new ArgumentNullException(nameof(tree));
            tree.Add(AssetCollectorToolPath, ScriptableObject.CreateInstance<FAssetCollectorTool>(), FIconCatalog.AssetCollectorToolIcon);
            tree.Add(AssetGroupToolPath, ScriptableObject.CreateInstance<FAssetGroupTool>(), FIconCatalog.AssetGroupToolIcon);
            tree.Add(CompareStringToolPath, ScriptableObject.CreateInstance<FCompareStringTool>(), FIconCatalog.DefaultToolIcon);
            tree.Add(BatchSortOrderToolPath, ScriptableObject.CreateInstance<FSortOrderTool>(), FIconCatalog.SortOrderToolIcon);
            tree.Add(BatchRenameToolPath, ScriptableObject.CreateInstance<FRenameTool>(), FIconCatalog.RenameToolIcon);
            tree.Add(AssetOrganizerToolPath, ScriptableObject.CreateInstance<FAssetOrganizerTool>(), FIconCatalog.AssetOrganizerToolIcon);
            tree.Add(BatchComponentModifyToolPath, ScriptableObject.CreateInstance<FComponentModifyTool>(), FIconCatalog.ComponentModifyToolIcon);
            tree.Add(BatchPrefabModifyToolPath, ScriptableObject.CreateInstance<FPrefabModifyTool>(), FIconCatalog.PrefabModifyToolIcon);
            tree.Add(BatchPrefabVariantCreatorToolPath, ScriptableObject.CreateInstance<FPrefabVariantCreatorTool>(), FIconCatalog.PrefabVariantCreatorToolIcon);
            tree.Add(BatchNameOffsetToolPath, ScriptableObject.CreateInstance<FNameOffsetTool>(), FIconCatalog.NameOffsetToolIcon);
            tree.Add(ModelScaleToColliderToolPath, ScriptableObject.CreateInstance<FModelScaleToColliderTool>(), FIconCatalog.ModelScaleToColliderToolIcon);
            tree.Add(MeshBoxColliderFitterToolPath, ScriptableObject.CreateInstance<FMeshBoxColliderFitterTool>(), FIconCatalog.MeshBoxColliderFitterToolIcon);
            tree.Add(BatchComponentReplacerToolPath, ScriptableObject.CreateInstance<FComponentReplacerTool>(), FIconCatalog.ComponentReplacerToolIcon);
            tree.Add(BatchMissingComponentToolPath, ScriptableObject.CreateInstance<FMissingComponentHandlerTool>(), FIconCatalog.MissingScriptHandlerToolIcon);

            tree.Add(DataFillerToolPath, ScriptableObject.CreateInstance<FDataFillerTool>(), FIconCatalog.ScriptableObjectsFillerToolIcon);
            tree.Add(CharacterMeshUpdaterToolPath, ScriptableObject.CreateInstance<FCharacterMeshUpdateTool>(), FIconCatalog.CharacterMeshUpdaterToolIcon);
            tree.Add(DataClonerToolPath, ScriptableObject.CreateInstance<FDataClonerTool>(), FIconCatalog.DataClonerToolIcon);

            tree.Add(UnpackMeshToolPath, ScriptableObject.CreateInstance<FUnpackMeshTool>(), FIconCatalog.DefaultToolIcon);
            tree.Add(UnpackAnimationToolPath, ScriptableObject.CreateInstance<FUnpackAnimationTool>(), FIconCatalog.DefaultToolIcon);
            tree.Add(DeduplicateTextureToolPath, ScriptableObject.CreateInstance<FDeduplicateTextureTool>(), FIconCatalog.DefaultToolIcon);
            tree.Add(DeduplicateMaterialToolPath, ScriptableObject.CreateInstance<FDeduplicateMaterialTool>(), FIconCatalog.DefaultToolIcon);
            tree.Add(DeduplicateMeshToolPath, ScriptableObject.CreateInstance<FDeduplicateMeshTool>(), FIconCatalog.DefaultToolIcon);
            tree.Add(RepackModelsToolPath, ScriptableObject.CreateInstance<FRepackModelsTool>(), FIconCatalog.DefaultToolIcon);
            tree.Add(VfxFlipbookTrimToolPath, ScriptableObject.CreateInstance<FVfxFlipbookTrimTool>(), FIconCatalog.DefaultToolIcon);
        }
    }
}
