using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public static class FPrefabVariantCreatorService
    {
        public static int CreatePrefabVariantsFromModels(
            PrefabVariantCreatorConfig config,
            IReadOnlyList<GameObject> models,
            List<GameObject> createdVariants = null)
        {
            ValidateInputArguments(config, models);

            int createdCount = 0;

            foreach (var model in models)
            {
                if (model == null)
                {
                    Debug.LogWarning("[FPrefabVariantCreatorService] Skipping null model.");
                    continue;
                }

                var saved = CreateAndSaveVariantPrefab(config, model);
                createdVariants?.Add(saved);
                createdCount++;
            }

            AssetDatabase.Refresh();
            return createdCount;
        }

        // Validation
        private static void ValidateInputArguments(
            PrefabVariantCreatorConfig config,
            IReadOnlyList<GameObject> models)
        {
            if (config.BasePrefab == null)
                throw new InvalidOperationException("Base prefab is null.");

            if (string.IsNullOrEmpty(config.SaveFolderPath))
                throw new InvalidOperationException("Save folder path is empty.");

            if (!AssetDatabase.IsValidFolder(config.SaveFolderPath))
                throw new InvalidOperationException("Save folder path is invalid.");

            if (models == null || models.Count == 0)
                throw new InvalidOperationException("Models list is empty.");
        }

        // Core Flow
        private static GameObject CreateAndSaveVariantPrefab(
            PrefabVariantCreatorConfig config,
            GameObject model)
        {
            string modelName = GetValidModelName(model);
            string savePath = BuildVariantAssetPath(config.SaveFolderPath, modelName);

            var tempInstance = InstantiateBasePrefab(config.BasePrefab);

            try
            {
                AddModelToPrefabInstance(config, model, modelName, tempInstance);
                return SavePrefabAssetAndDestroyTempInstance(savePath, tempInstance);
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(tempInstance);
                throw;
            }
        }

        // Step 1 - Instantiate
        private static GameObject InstantiateBasePrefab(GameObject basePrefab)
        {
            var instance = PrefabUtility.InstantiatePrefab(basePrefab) as GameObject;

            if (instance == null)
                throw new InvalidOperationException("Failed to instantiate base prefab.");

            return instance;
        }

        // Step 2 - Add Model
        private static void AddModelToPrefabInstance(
            PrefabVariantCreatorConfig config,
            GameObject model,
            string modelName,
            GameObject prefabInstance)
        {
            Transform parent = ResolveTargetParentTransform(config, prefabInstance);

            if (config.LocateModel == null)
            {
                // Merge mode: content must be unpacked before merging into the base root.
                GameObject clonedModel = ClonePlainUnderParent(model, modelName, parent);
                MoveChildrenToParentAndDestroyRootObject(
                    clonedModel.transform,
                    parent);
            }
            else
            {
                CloneModelUnderParent(model, modelName, parent);
            }
        }

        private static Transform ResolveTargetParentTransform(
            PrefabVariantCreatorConfig config,
            GameObject prefabInstance)
        {
            if (config.LocateModel == null)
                return prefabInstance.transform;

            string holderPath = AnimationUtility.CalculateTransformPath(
                config.LocateModel,
                config.BasePrefab.transform);

            if (string.IsNullOrEmpty(holderPath))
            {
                if (config.LocateModel == config.BasePrefab.transform)
                    return prefabInstance.transform;

                throw new InvalidOperationException("Holder path is empty.");
            }

            var holder = prefabInstance.transform.Find(holderPath);

            if (holder == null)
                throw new InvalidOperationException("Holder not found in base prefab.");

            return holder;
        }

        private static GameObject CloneModelUnderParent(
            GameObject model,
            string modelName,
            Transform parent)
        {
            GameObject clone;

            if (EditorUtility.IsPersistent(model))
            {
                clone = (GameObject)PrefabUtility.InstantiatePrefab(model, parent);
            }
            else if (PrefabUtility.IsPartOfPrefabInstance(model)
                     && PrefabUtility.GetOutermostPrefabInstanceRoot(model) == model)
            {
                // Scene prefab instance: re-instantiate the source asset to keep the
                // prefab link, then carry over the instance's property overrides.
                var sourceAsset = PrefabUtility.GetCorrespondingObjectFromSource(model);
                clone = (GameObject)PrefabUtility.InstantiatePrefab(sourceAsset, parent);

                var modifications = PrefabUtility.GetPropertyModifications(model);
                if (modifications != null)
                    PrefabUtility.SetPropertyModifications(clone, modifications);
            }
            else
            {
                clone = UnityEngine.Object.Instantiate(model, parent, true);
            }

            clone.name = modelName;
            clone.transform.localPosition = Vector3.zero;
            return clone;
        }

        private static GameObject ClonePlainUnderParent(
            GameObject model,
            string modelName,
            Transform parent)
        {
            var clone = UnityEngine.Object.Instantiate(model, parent, true);
            clone.name = modelName;
            clone.transform.localPosition = Vector3.zero;
            return clone;
        }

        // Step 3 - Save
        private static GameObject SavePrefabAssetAndDestroyTempInstance(
            string path,
            GameObject prefabInstance)
        {
            var saved = PrefabUtility.SaveAsPrefabAsset(prefabInstance, path);

            if (saved == null)
                throw new InvalidOperationException("Failed to save prefab.");

            UnityEngine.Object.DestroyImmediate(prefabInstance);
            return saved;
        }

        // Helpers
        private static string GetValidModelName(GameObject model)
        {
            if (string.IsNullOrEmpty(model.name))
                throw new InvalidOperationException("Model name is null or empty.");

            return model.name;
        }

        private static string BuildVariantAssetPath(
            string folderPath,
            string modelName)
        {
            return $"{folderPath}/{modelName}.prefab";
        }

        private static void MoveChildrenToParentAndDestroyRootObject(
            Transform sourceRoot,
            Transform targetParent)
        {
            if (sourceRoot == null)
                throw new InvalidOperationException("Source root is null.");

            if (targetParent == null)
                throw new InvalidOperationException("Target parent is null.");

            for (int i = sourceRoot.childCount - 1; i >= 0; i--)
            {
                var child = sourceRoot.GetChild(i);
                child.SetParent(targetParent, true);
            }

            UnityEngine.Object.DestroyImmediate(sourceRoot.gameObject);
        }
    }
}
