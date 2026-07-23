using System.Collections.Generic;
using System.Linq;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public sealed class FAssetCollectorTool : FTargetAssetsToolBase
    {
        private const string AllTypesLabel = "All (Tất cả)";
        private const string SpriteFilter = "t:Sprite";

        private static readonly (string Label, string Filter)[] AssetTypeMap =
        {
            ("Animation Clip",      "t:AnimationClip"),
            ("Audio Clip",          "t:AudioClip"),
            ("Font",                "t:Font"),
            ("GUI Skin",            "t:GUISkin"),
            ("Material",            "t:Material"),
            ("Mesh",                "t:Mesh"),
            ("Model",               "t:Model"),
            ("Prefab",              "t:Prefab"),
            ("Scene",               "t:Scene"),
            ("Script",              "t:MonoScript"),
            ("Shader",              "t:Shader"),
            ("Sprite",              SpriteFilter),
            ("Texture",             "t:Texture"),
            ("Video Clip",          "t:VideoClip"),
            ("Visual Effect Asset", "t:VisualEffectAsset"),
        };

        protected override string GetDescription()
            => "Quét thư mục, chọn loại asset từ dropdown (chỉ hiển thị loại có trong folder), rồi bấm Collect để nạp toàn bộ vào TargetAssets cho các tool khác sử dụng.";

        [PropertyOrder(-800)]
        [FolderPath(AbsolutePath = false)]
        [LabelText("Search Folder")]
        [ShowInInspector]
        [OnValueChanged(nameof(OnFolderChanged))]
        [InlineButton(nameof(ScanButton), "Scan")]
        private string SearchFolder
        {
            get => FDataPersistenceService.GetOrCreateDataContainer().AssetCollectorFolder;
            set
            {
                var c = FDataPersistenceService.GetOrCreateDataContainer();
                c.AssetCollectorFolder = value;
                FDataPersistenceService.SaveData(c);
            }
        }

        [PropertyOrder(-790)]
        [ValueDropdown(nameof(GetAvailableTypesDropdown))]
        [LabelText("Asset Type")]
        [ShowInInspector]
        private string _selectedType;

        [System.NonSerialized] private readonly List<string> _availableTypes = new List<string>();
        [System.NonSerialized] private bool _initialized;

        [OnInspectorGUI]
        private void EnsureScanned()
        {
            if (_initialized) return;
            _initialized = true;
            RunScan();
        }

        private void OnFolderChanged() => RunScan();

        private void ScanButton() => RunScan();

        private void RunScan()
        {
            var folder = FDataPersistenceService.GetOrCreateDataContainer().AssetCollectorFolder;
            _availableTypes.Clear();
            _selectedType = null;

            if (string.IsNullOrEmpty(folder)) return;

            foreach (var (label, filter) in AssetTypeMap)
            {
                if (AssetDatabase.FindAssets(filter, new[] { folder }).Length > 0)
                    _availableTypes.Add(label);
            }

            // "All" chỉ có nghĩa khi folder chứa ít nhất 1 loại asset.
            if (_availableTypes.Count > 0)
                _availableTypes.Insert(0, AllTypesLabel);
        }

        [PropertyOrder(0)]
        [Button("Collect Assets", ButtonSizes.Large)]
        [GUIColor(0.3f, 0.8f, 0.3f)]
        private void CollectAssets()
        {
            if (string.IsNullOrEmpty(_selectedType))
            {
                Debug.LogWarning("[Asset Collector] Chưa chọn loại asset.");
                return;
            }

            var folder = FDataPersistenceService.GetOrCreateDataContainer().AssetCollectorFolder;

            // "All" gộp mọi loại asset trong map; các loại khác chỉ nạp loại được chọn.
            // Distinct dedupe ref vì một số filter có thể trả cùng object.
            IEnumerable<Object> loaded;
            if (_selectedType == AllTypesLabel)
            {
                loaded = AssetTypeMap.SelectMany(t => LoadAssetsForFilter(t.Filter, folder));
            }
            else
            {
                var filter = GetFilter(_selectedType);
                if (filter == null) return;
                loaded = LoadAssetsForFilter(filter, folder);
            }

            var assets = loaded
                .Where(a => a != null)
                .Distinct()
                .ToList();

            var data = FDataPersistenceService.GetOrCreateDataContainer();
            data.TargetAssets.Clear();
            data.TargetAssets.AddRange(assets);
            data.SyncAllFromAssets();
            FDataPersistenceService.SaveData(data);

            Debug.Log($"<color=cyan>[Asset Collector] Đã nạp {assets.Count} '{_selectedType}' → TargetAssets ({data.TargetPrefabs.Count} prefabs → TargetPrefabs)</color>");
        }

        // Sprite là sub-asset của texture nên phải LoadAllAssetsAtPath rồi lọc Sprite,
        // các loại khác nạp main asset như thường.
        private static IEnumerable<Object> LoadAssetsForFilter(string filter, string folder)
        {
            var paths = AssetDatabase.FindAssets(filter, new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath);

            if (filter == SpriteFilter)
                return paths.SelectMany(p => AssetDatabase.LoadAllAssetsAtPath(p).OfType<Sprite>()).Cast<Object>();

            return paths.Select(AssetDatabase.LoadAssetAtPath<Object>);
        }

        private IEnumerable<string> GetAvailableTypesDropdown() => _availableTypes;

        private static string GetFilter(string label)
        {
            foreach (var (l, f) in AssetTypeMap)
                if (l == label) return f;
            return null;
        }
    }
}
