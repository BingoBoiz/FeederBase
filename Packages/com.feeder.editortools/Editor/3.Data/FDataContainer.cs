using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>Persistent data for all Feeder tools. Stored in User Settings so path stays stable.</summary>
    [FilePath("UserSettings/Feeder/FDataContainer.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class FDataContainer : ScriptableSingleton<FDataContainer>
    {
        [SerializeField] private List<GameObject> targetPrefabs = new List<GameObject>();
        [SerializeField] private List<Object> targetAssets = new List<Object>();
        [SerializeField] private ScriptableObject targetSO;
        [SerializeField] private List<MeshRenderer> targetsMesh = new List<MeshRenderer>();
        [SerializeField] private int targetsMeshCompareIndex;
        [SerializeField] private List<Mesh> targetMeshes = new List<Mesh>();
        [SerializeField] private int targetMeshesCompareIndex;
        [SerializeField] private int targetMeshesLeftPreviewIndex;
        [SerializeField] private int targetMeshesRightPreviewIndex;
        [SerializeField] private string assetCollectorFolder = "Assets/";
        [SerializeField] private string assetOrganizerFolder = "Assets/";
        [SerializeField] private ScriptableObject dataClonerSourceSO;
        [SerializeField] private ScriptableObject dataClonerDestinationSO;
        [SerializeField] private List<GameObject> characterMeshSources = new List<GameObject>();
        [SerializeField] private Transform characterMeshNewArmature;
        [SerializeField] private Transform characterMeshNewParent;
        [SerializeField] private List<Object> fbxUnpackSources = new List<Object>();
        [SerializeField] private Object dataFillerTarget;
        [SerializeField] private Component dataFillerComponent;

        public List<GameObject> TargetPrefabs => GetOrInit(ref targetPrefabs);
        public List<Object> TargetAssets => GetOrInit(ref targetAssets);
        public ScriptableObject TargetSO { get => targetSO; set => targetSO = value; }
        public List<MeshRenderer> TargetsMesh => GetOrInit(ref targetsMesh);
        public List<Mesh> TargetMeshes => GetOrInit(ref targetMeshes);
        public string AssetCollectorFolder { get => assetCollectorFolder ?? "Assets/"; set => assetCollectorFolder = value; }
        public string AssetOrganizerFolder { get => assetOrganizerFolder ?? "Assets/"; set => assetOrganizerFolder = value; }
        public ScriptableObject DataClonerSourceSO { get => dataClonerSourceSO; set => dataClonerSourceSO = value; }
        public ScriptableObject DataClonerDestinationSO { get => dataClonerDestinationSO; set => dataClonerDestinationSO = value; }
        public List<GameObject> CharacterMeshSources => GetOrInit(ref characterMeshSources);
        public Transform CharacterMeshNewArmature { get => characterMeshNewArmature; set => characterMeshNewArmature = value; }
        public Transform CharacterMeshNewParent { get => characterMeshNewParent; set => characterMeshNewParent = value; }
        public List<Object> FbxUnpackSources => GetOrInit(ref fbxUnpackSources);
        public Object DataFillerTarget { get => dataFillerTarget; set => dataFillerTarget = value; }
        public Component DataFillerComponent { get => dataFillerComponent; set => dataFillerComponent = value; }

        /// <summary>Index in TargetsMesh used as "compare target" in Deduplicate Mesh Tool.</summary>
        public int TargetsMeshCompareIndex { get => targetsMeshCompareIndex; set => targetsMeshCompareIndex = Mathf.Clamp(value, 0, Mathf.Max(0, TargetsMesh.Count - 1)); }

        /// <summary>Index in TargetMeshes for compare target in Deduplicate Mesh Tool.</summary>
        public int TargetMeshesCompareIndex { get => targetMeshesCompareIndex; set => targetMeshesCompareIndex = Mathf.Clamp(value, 0, Mathf.Max(0, TargetMeshes.Count - 1)); }

        public int TargetMeshesLeftPreviewIndex { get => targetMeshesLeftPreviewIndex; set => targetMeshesLeftPreviewIndex = Mathf.Clamp(value, 0, Mathf.Max(0, TargetMeshes.Count - 1)); }
        public int TargetMeshesRightPreviewIndex { get => targetMeshesRightPreviewIndex; set => targetMeshesRightPreviewIndex = Mathf.Clamp(value, 0, Mathf.Max(0, TargetMeshes.Count - 1)); }

        private static List<T> GetOrInit<T>(ref List<T> list) where T : Object
        {
            if (list == null)
                list = new List<T>();
            return list;
        }

        private void OnValidate()
        {
            if (targetPrefabs == null) targetPrefabs = new List<GameObject>();
            if (targetAssets == null) targetAssets = new List<Object>();
            if (targetsMesh == null) targetsMesh = new List<MeshRenderer>();
            if (targetMeshes == null) targetMeshes = new List<Mesh>();
            if (characterMeshSources == null) characterMeshSources = new List<GameObject>();
            if (fbxUnpackSources == null) fbxUnpackSources = new List<Object>();
            if (assetCollectorFolder == null) assetCollectorFolder = "Assets/";
            if (assetOrganizerFolder == null) assetOrganizerFolder = "Assets/";
        }

        /// <summary>
        /// Syncs all typed asset lists from TargetAssets, preserving position.
        /// Non-matching entries become null so every list has the same length as TargetAssets.
        /// </summary>
        public void SyncAllFromAssets()
        {
            SyncTypedList(ref targetPrefabs);
            SyncTypedList(ref targetMeshes);
        }

        private void SyncTypedList<T>(ref List<T> list) where T : Object
        {
            list ??= new List<T>();
            list.Clear();
            if (targetAssets == null) return;
            foreach (var asset in targetAssets)
                list.Add(asset as T);
        }

        public void SaveData()
        {
            Save(true);
        }
    }
}
