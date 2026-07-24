using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public abstract class FBaseTool : SerializedScriptableObject
    {
        private const string DefaultDescription = "Tool này chưa code, ở đây cho đẹp thôi.";

        [OnInspectorGUI, PropertyOrder(-1000)]
        private void DrawAutoDescription()
        {
            DrawDescription(GetDescription() ?? DefaultDescription);
        }

        protected virtual string GetDescription() => null;

        protected void DrawDescription(string description)
        {
            GUILayout.Space(4);
            FStylesUtils.DrawDescription(description);
            GUILayout.Space(6);
        }
    }

    public abstract class FTargetPrefabsToolBase : FBaseTool
    {
        [PropertyOrder(-900)]
        [ListDrawerSettings(ShowFoldout = true, DraggableItems = true, ShowIndexLabels = true, NumberOfItemsPerPage = 10)]
        [OnValueChanged(nameof(HandleTargetPrefabsChanged))]
        [ShowInInspector]
        public List<GameObject> TargetPrefabs
        {
            get => GetTargetPrefabsData().TargetPrefabs;
            set
            {
                var data = GetTargetPrefabsData();
                data.TargetPrefabs.Clear();
                if (value != null)
                    data.TargetPrefabs.AddRange(value);
                FDataPersistenceService.SaveData(data);
            }
        }

        [PropertyOrder(-899)]
        [ButtonGroup("TargetPrefabSelectionRow"), Button("Add Selection", ButtonSizes.Small)]
        private void AddSelectionToTargetPrefabs() => ApplyPrefabSelection(replace: false);

        [PropertyOrder(-899)]
        [ButtonGroup("TargetPrefabSelectionRow"), Button("Replace With Selection", ButtonSizes.Small)]
        private void ReplaceTargetPrefabsWithSelection() => ApplyPrefabSelection(replace: true);

        [PropertyOrder(-899)]
        [ButtonGroup("TargetPrefabSelectionRow"), Button("Clear", ButtonSizes.Small)]
        private void ClearTargetPrefabs()
        {
            GetTargetPrefabsData().TargetPrefabs.Clear();
            HandleTargetPrefabsChanged();
        }

        private void ApplyPrefabSelection(bool replace)
        {
            var picked = FSelectionUtils.CollectGameObjects();
            if (picked.Count == 0)
            {
                Debug.LogWarning("[Feeder] Chưa chọn GameObject nào trong Hierarchy/Project.");
                return;
            }
            var list = GetTargetPrefabsData().TargetPrefabs;
            if (replace) list.Clear();
            foreach (var go in picked)
                if (!list.Contains(go))
                    list.Add(go);
            HandleTargetPrefabsChanged();
        }

        /// <summary>Persisted data asset so refs survive tool close and Unity restart.</summary>
        protected FDataContainer GetTargetPrefabsData() => FDataPersistenceService.GetOrCreateDataContainer();

        protected virtual void OnTargetPrefabsChanged()
        {
        }

        private void HandleTargetPrefabsChanged()
        {
            FDataPersistenceService.SaveData(GetTargetPrefabsData());
            OnTargetPrefabsChanged();
        }
    }

    /// <summary>Base for tools that operate on any Unity assets (sprites, scenes, audio, prefabs, etc.).</summary>
    public abstract class FTargetAssetsToolBase : FBaseTool
    {
        private bool _pendingTargetAssetsChange;
        private bool _delayCallScheduled;

        [PropertyOrder(-900)]
        [ListDrawerSettings(ShowFoldout = true, DraggableItems = true, ShowIndexLabels = true, NumberOfItemsPerPage = 10)]
        [OnValueChanged(nameof(HandleTargetAssetsChanged))]
        [ShowInInspector]
        public List<Object> TargetAssets
        {
            get => GetDataContainer().TargetAssets;
            set
            {
                var c = GetDataContainer();
                c.TargetAssets.Clear();
                if (value != null)
                    c.TargetAssets.AddRange(value);
                FDataPersistenceService.SaveData(c);
            }
        }

        [PropertyOrder(-899)]
        [ButtonGroup("TargetAssetSelectionRow"), Button("Add Selection", ButtonSizes.Small)]
        private void AddSelectionToTargetAssets() => ApplyAssetSelection(replace: false);

        [PropertyOrder(-899)]
        [ButtonGroup("TargetAssetSelectionRow"), Button("Replace With Selection", ButtonSizes.Small)]
        private void ReplaceTargetAssetsWithSelection() => ApplyAssetSelection(replace: true);

        [PropertyOrder(-899)]
        [ButtonGroup("TargetAssetSelectionRow"), Button("Clear", ButtonSizes.Small)]
        private void ClearTargetAssets()
        {
            GetDataContainer().TargetAssets.Clear();
            HandleTargetAssetsChanged();
        }

        private void ApplyAssetSelection(bool replace)
        {
            var picked = FSelectionUtils.CollectAssetsAndSceneObjects();
            if (picked.Count == 0)
            {
                Debug.LogWarning("[Feeder] Chưa chọn gì trong Hierarchy/Project.");
                return;
            }
            var list = GetDataContainer().TargetAssets;
            if (replace) list.Clear();
            foreach (var obj in picked)
                if (!list.Contains(obj))
                    list.Add(obj);
            HandleTargetAssetsChanged();
        }

        protected FDataContainer GetDataContainer() => FDataPersistenceService.GetOrCreateDataContainer();

        protected virtual void OnTargetAssetsChanged()
        {
        }

        private void HandleTargetAssetsChanged()
        {
            FDataPersistenceService.SaveData(GetDataContainer());
            _pendingTargetAssetsChange = true;
            if (_delayCallScheduled) return;
            _delayCallScheduled = true;
            EditorApplication.delayCall += InvokeDelayedOnTargetAssetsChanged;
        }

        private void InvokeDelayedOnTargetAssetsChanged()
        {
            _delayCallScheduled = false;
            if (!_pendingTargetAssetsChange) return;
            _pendingTargetAssetsChange = false;
            var c = GetDataContainer();
            c.SyncAllFromAssets();
            FDataPersistenceService.SaveData(c);
            OnTargetAssetsChanged();
        }
    }

    /// <summary>Base for tools that target a single ScriptableObject (e.g. Scriptable Objects Filler).</summary>
    public abstract class FTargetScriptableObjectToolBase : FBaseTool
    {
        [PropertyOrder(-900)]
        [InlineButton(nameof(UseSelectedScriptableObject), "Use Selection")]
        [ShowInInspector, OnValueChanged(nameof(HandleTargetSOChanged))]
        public ScriptableObject TargetSO
        {
            get => GetDataContainer().TargetSO;
            set
            {
                GetDataContainer().TargetSO = value;
                FDataPersistenceService.SaveData(GetDataContainer());
            }
        }

        private void UseSelectedScriptableObject()
        {
            var so = FSelectionUtils.FirstScriptableObject();
            if (so == null)
            {
                Debug.LogWarning("[Feeder] Chưa chọn ScriptableObject nào trong Project.");
                return;
            }
            GetDataContainer().TargetSO = so;
            HandleTargetSOChanged();
        }

        protected FDataContainer GetDataContainer() => FDataPersistenceService.GetOrCreateDataContainer();

        protected virtual void OnTargetSOChanged()
        {
        }

        private void HandleTargetSOChanged()
        {
            FDataPersistenceService.SaveData(GetDataContainer());
            OnTargetSOChanged();
        }
    }

    /// <summary>Base for tools that operate on a list of MeshRenderers (e.g. Deduplicate Mesh).</summary>
    public abstract class FTargetMeshRenderersToolBase : FBaseTool
    {
        [PropertyOrder(-900)]
        [ListDrawerSettings(ShowFoldout = true, DraggableItems = true, ShowIndexLabels = true, NumberOfItemsPerPage = 10)]
        [OnValueChanged(nameof(HandleTargetsMeshChanged))]
        [ShowInInspector]
        public List<MeshRenderer> TargetsMesh
        {
            get => GetDataContainer().TargetsMesh;
            set
            {
                var c = GetDataContainer();
                c.TargetsMesh.Clear();
                if (value != null)
                    c.TargetsMesh.AddRange(value);
                FDataPersistenceService.SaveData(c);
            }
        }

        [PropertyOrder(-899)]
        [ButtonGroup("TargetsMeshSelectionRow"), Button("Add Selection", ButtonSizes.Small)]
        private void AddSelectionToTargetsMesh() => ApplyMeshRendererSelection(replace: false);

        [PropertyOrder(-899)]
        [ButtonGroup("TargetsMeshSelectionRow"), Button("Replace With Selection", ButtonSizes.Small)]
        private void ReplaceTargetsMeshWithSelection() => ApplyMeshRendererSelection(replace: true);

        [PropertyOrder(-899)]
        [ButtonGroup("TargetsMeshSelectionRow"), Button("Clear", ButtonSizes.Small)]
        private void ClearTargetsMesh()
        {
            GetDataContainer().TargetsMesh.Clear();
            HandleTargetsMeshChanged();
        }

        private void ApplyMeshRendererSelection(bool replace)
        {
            var picked = FSelectionUtils.CollectMeshRenderers();
            if (picked.Count == 0)
            {
                Debug.LogWarning("[Feeder] Selection không chứa MeshRenderer nào.");
                return;
            }
            var list = GetDataContainer().TargetsMesh;
            if (replace) list.Clear();
            foreach (var mr in picked)
                if (!list.Contains(mr))
                    list.Add(mr);
            HandleTargetsMeshChanged();
        }

        protected FDataContainer GetDataContainer() => FDataPersistenceService.GetOrCreateDataContainer();

        protected virtual void OnTargetsMeshChanged()
        {
        }

        private void HandleTargetsMeshChanged()
        {
            FDataPersistenceService.SaveData(GetDataContainer());
            OnTargetsMeshChanged();
        }
    }

    /// <summary>Base for tools that operate on a list of Mesh assets (e.g. Deduplicate Mesh).</summary>
    public abstract class FTargetMeshesToolBase : FBaseTool
    {
        [PropertyOrder(-900)]
        [ListDrawerSettings(ShowFoldout = true, DraggableItems = true, ShowIndexLabels = true, NumberOfItemsPerPage = 10)]
        [OnValueChanged(nameof(HandleTargetMeshesChanged))]
        [ShowInInspector]
        public List<Mesh> TargetMeshes
        {
            get => GetDataContainer().TargetMeshes;
            set
            {
                var c = GetDataContainer();
                c.TargetMeshes.Clear();
                if (value != null)
                    c.TargetMeshes.AddRange(value);
                FDataPersistenceService.SaveData(c);
            }
        }

        [PropertyOrder(-899)]
        [ButtonGroup("TargetMeshesSelectionRow"), Button("Add Selection", ButtonSizes.Small)]
        private void AddSelectionToTargetMeshes() => ApplyMeshSelection(replace: false);

        [PropertyOrder(-899)]
        [ButtonGroup("TargetMeshesSelectionRow"), Button("Replace With Selection", ButtonSizes.Small)]
        private void ReplaceTargetMeshesWithSelection() => ApplyMeshSelection(replace: true);

        [PropertyOrder(-899)]
        [ButtonGroup("TargetMeshesSelectionRow"), Button("Clear", ButtonSizes.Small)]
        private void ClearTargetMeshes()
        {
            GetDataContainer().TargetMeshes.Clear();
            HandleTargetMeshesChanged();
        }

        private void ApplyMeshSelection(bool replace)
        {
            var picked = FSelectionUtils.CollectMeshes();
            if (picked.Count == 0)
            {
                Debug.LogWarning("[Feeder] Selection không chứa Mesh nào (chọn Mesh asset hoặc GameObject có MeshFilter/SkinnedMeshRenderer).");
                return;
            }
            var list = GetDataContainer().TargetMeshes;
            if (replace) list.Clear();
            foreach (var mesh in picked)
                if (!list.Contains(mesh))
                    list.Add(mesh);
            HandleTargetMeshesChanged();
        }

        protected FDataContainer GetDataContainer() => FDataPersistenceService.GetOrCreateDataContainer();

        protected virtual void OnTargetMeshesChanged()
        {
        }

        private void HandleTargetMeshesChanged()
        {
            FDataPersistenceService.SaveData(GetDataContainer());
            OnTargetMeshesChanged();
        }
    }
}
