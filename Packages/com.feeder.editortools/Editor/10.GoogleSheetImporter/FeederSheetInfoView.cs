using System;
using System.Collections.Generic;
using System.IO;
using NabaGame.Core.Runtime.Extensions;
using UnityEditor;
using UnityEngine;
using Object = System.Object;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
using Sirenix.Serialization;
#endif

namespace Feeder
{
#if ODIN_INSPECTOR
    [Serializable, ShowOdinSerializedPropertiesInInspector]
    public class FeederSheetInfoView
    {
        private const char SkipColumnPrefix = '/';

        [FoldoutGroup("Sheet Info", true), OnValueChanged("OnNameChanged")]
        public string sheetName;

        [FoldoutGroup("Sheet Info", true), OnValueChanged("OnIdChanged")]
        public string SpreadsheetID;

        [FoldoutGroup("Sheet Info", true),
         Sirenix.OdinInspector.FolderPath(ParentFolder = "Assets", RequireExistingPath = true),
         OnValueChanged("OnScriptFolderChanged")]
        public string ScriptFolder;

        [FoldoutGroup("Sheet Info", true),
         Sirenix.OdinInspector.FolderPath(ParentFolder = "Assets", RequireExistingPath = true),
         OnValueChanged("OnAssetFolderChanged")]
        public string AssetFolder;

        [FoldoutGroup("Sheet Info", true),
         Sirenix.OdinInspector.FolderPath(ParentFolder = "Assets", RequireExistingPath = true),
         OnValueChanged("OnSpriteAssetFolderChanged")]
        public string SpriteAssetFolder;

        [FoldoutGroup("Sheet Info", true),
         Sirenix.OdinInspector.FolderPath(ParentFolder = "Assets", RequireExistingPath = true),
         OnValueChanged("OnSkeletonDataFolderChanged")]
        public string SkeletonDataFolder;

        [FoldoutGroup("Sheet Info", true),
         Sirenix.OdinInspector.FolderPath(ParentFolder = "Assets", RequireExistingPath = true),
         OnValueChanged("OnPrefabFolderChanged")]
        public string PrefabFolder;

        [FoldoutGroup("Sheet Info", true), OnValueChanged("OndefaultSpriteChanged")]
        public Sprite defaultSprite;

        private Dictionary<string, IList<IList<Object>>> sheetData
        {
            get => info.sheetData;
            set
            {
                info.sheetData = value;
                EditorUtility.SetDirty(info);
            }
        }

        private List<string> sheetNames
        {
            get => info.sheetNames;
            set
            {
                info.sheetNames = value;
                EditorUtility.SetDirty(info);
            }
        }

        [FoldoutGroup("Sheet Data", true), ValueDropdown("sheetNames"), OnValueChanged("OnSheetSelected"), OdinSerialize, PropertyOrder(20)]
        public string selectTab
        {
            get => info.selectTab;
            set
            {
                info.selectTab = value;
                EditorUtility.SetDirty(info);
            }
        }

        [FoldoutGroup("Sheet Data", true), PropertyOrder(30)]
        public string[,] cells
        {
            get => info.cells;
            set
            {
                info.cells = value;
                EditorUtility.SetDirty(info);
            }
        }

        [FoldoutGroup("Sheet Data", true), PropertyOrder(35)]
        public List<string> rawFields
        {
            get => info.rawFields;
            set
            {
                info.rawFields = value;
                EditorUtility.SetDirty(info);
            }
        }

        private const int TablePageSize = 50;

        [SerializeField, HideInInspector]
        private int _tablePage;

        [SerializeField, HideInInspector]
        private bool _showFullTable;

        [SerializeField, HideInInspector]
        private MonoScript _generatedScriptFile;

        [SerializeField, HideInInspector]
        private ScriptableObject _generatedScriptableAsset;

        [FoldoutGroup("Sheet Data", true), HorizontalGroup("Sheet Data/Toolbar"), PropertyOrder(5), Button("@(_showFullTable ? \"Page\" : \"Full\")", ButtonSizes.Small), EnableIf("@HasTableDataForToolbar")]
        private void ToolbarToggleFullTableView()
        {
            _showFullTable = !_showFullTable;
        }

        [FoldoutGroup("Sheet Data", true), HorizontalGroup("Sheet Data/Toolbar"), PropertyOrder(5), Button("<", ButtonSizes.Small), EnableIf("@HasTableDataForToolbar && !_showFullTable && _tablePage > 0")]
        private void ToolbarTablePrevPage()
        {
            if (_tablePage > 0) _tablePage--;
        }

        [FoldoutGroup("Sheet Data", true), HorizontalGroup("Sheet Data/Toolbar"), PropertyOrder(5), ShowInInspector, ReadOnly, HideLabel, DisplayAsString]
        private string ToolbarTablePaginationCaption
        {
            get
            {
                if (info?.cells == null || info.cells.GetLength(1) == 0)
                    return "—";
                if (_showFullTable)
                    return $"{info.cells.GetLength(1)} rows";
                int totalPages = Mathf.Max(1, Mathf.CeilToInt((float)info.cells.GetLength(1) / TablePageSize));
                return $"{_tablePage + 1} / {totalPages}";
            }
        }

        [FoldoutGroup("Sheet Data", true), HorizontalGroup("Sheet Data/Toolbar"), PropertyOrder(5), Button(">", ButtonSizes.Small), EnableIf("@HasTableDataForToolbar && !_showFullTable && ToolbarTableHasNextPage")]
        private void ToolbarTableNextPage()
        {
            int total = info.cells.GetLength(1);
            if ((_tablePage + 1) * TablePageSize < total) _tablePage++;
        }

        private bool HasTableDataForToolbar => info?.cells != null && info.cells.GetLength(1) > 0;

        private bool ToolbarTableHasNextPage
        {
            get
            {
                int total = info?.cells?.GetLength(1) ?? 0;
                return (_tablePage + 1) * TablePageSize < total;
            }
        }

        [FoldoutGroup("Sheet Data", true), HorizontalGroup("Sheet Data/GeneratedOutputs"), PropertyOrder(37), ShowInInspector, ReadOnly, HideLabel]
        private MonoScript GeneratedScriptFilePreview => _generatedScriptFile;

        [FoldoutGroup("Sheet Data", true), HorizontalGroup("Sheet Data/GeneratedOutputs"), PropertyOrder(38), ShowInInspector, ReadOnly, HideLabel]
        private ScriptableObject GeneratedScriptableAssetPreview => _generatedScriptableAsset;

        [FoldoutGroup("Sheet Data", true), TableMatrix, ShowInInspector, HideLabel, PropertyOrder(40)]
        private string[,] TablePreview
        {
            get
            {
                string[,] full = info?.cells;
                if (full == null) return null;
                if (_showFullTable)
                    return full;
                int colCount = full.GetLength(0);
                int totalRows = full.GetLength(1);
                int startRow = _tablePage * TablePageSize;
                if (startRow >= totalRows)
                {
                    _tablePage = 0;
                    startRow = 0;
                }

                int endRow = Mathf.Min(startRow + TablePageSize, totalRows);
                int pageRows = endRow - startRow;
                string[,] page = new string[colCount, pageRows];
                for (int c = 0; c < colCount; c++)
                    for (int r = 0; r < pageRows; r++)
                        page[c, r] = full[c, startRow + r];
                return page;
            }
            set
            {
                string[,] full = info?.cells;
                if (value == null || full == null) return;
                int colCount = full.GetLength(0);
                int totalRows = full.GetLength(1);
                if (_showFullTable)
                {
                    int valueCols = value.GetLength(0);
                    int valueRows = value.GetLength(1);
                    for (int c = 0; c < colCount && c < valueCols; c++)
                        for (int r = 0; r < totalRows && r < valueRows; r++)
                            full[c, r] = value[c, r];
                    EditorUtility.SetDirty(info);
                    return;
                }

                int startRow = _tablePage * TablePageSize;
                int pageRows = Mathf.Min(value.GetLength(1), totalRows - startRow);
                for (int c = 0; c < colCount && c < value.GetLength(0); c++)
                    for (int r = 0; r < pageRows; r++)
                        full[c, startRow + r] = value[c, r];
                EditorUtility.SetDirty(info);
            }
        }

        private FeederGoogleSheetController googleSheetController;
        private string infoBoxMessage;
        private FeederSheetInfo info;

        public FeederSheetInfoView(FeederSheetInfo sheetInfo)
        {
            info = sheetInfo;
            sheetName = sheetInfo.sheetName;
            SpreadsheetID = sheetInfo.SpreadsheetID;
            ScriptFolder = sheetInfo.ScriptFolder;
            AssetFolder = sheetInfo.AssetFolder;
            SpriteAssetFolder = sheetInfo.SpriteAssetFolder;
            SkeletonDataFolder = sheetInfo.SkeletonDataFolder;
            PrefabFolder = sheetInfo.PrefabFolder;
            defaultSprite = sheetInfo.defaultSprite;
            _tablePage = 0;
            _showFullTable = false;
            RefreshGeneratedOutputReferences();
        }

        public FeederSheetInfoView(FeederSheetInfoView sheetInfo)
        {
            info = sheetInfo.info;
            sheetName = sheetInfo.sheetName;
            SpreadsheetID = sheetInfo.SpreadsheetID;
            ScriptFolder = sheetInfo.ScriptFolder;
            AssetFolder = sheetInfo.AssetFolder;
            SpriteAssetFolder = sheetInfo.SpriteAssetFolder;
            SkeletonDataFolder = sheetInfo.SkeletonDataFolder;
            PrefabFolder = sheetInfo.PrefabFolder;
            defaultSprite = sheetInfo.defaultSprite;
            sheetData = sheetInfo.sheetData;
            sheetNames = sheetInfo.sheetNames;
            selectTab = sheetInfo.selectTab;
            _tablePage = sheetInfo._tablePage;
            _showFullTable = sheetInfo._showFullTable;
            RefreshGeneratedOutputReferences();
        }

        private void OnNameChanged()
        {
            info.sheetName = sheetName;
            info.name = sheetName;
            EditorUtility.SetDirty(info);
        }

        private void OnIdChanged()
        {
            info.SpreadsheetID = SpreadsheetID;
            EditorUtility.SetDirty(info);
        }

        private void OnScriptFolderChanged()
        {
            info.ScriptFolder = ScriptFolder;
            RefreshGeneratedOutputReferences();
            EditorUtility.SetDirty(info);
        }

        private void OnAssetFolderChanged()
        {
            info.AssetFolder = AssetFolder;
            RefreshGeneratedOutputReferences();
            EditorUtility.SetDirty(info);
        }

        private void OnSpriteAssetFolderChanged()
        {
            info.SpriteAssetFolder = SpriteAssetFolder;
            EditorUtility.SetDirty(info);
        }

        private void OnSkeletonDataFolderChanged()
        {
            info.SkeletonDataFolder = SkeletonDataFolder;
            EditorUtility.SetDirty(info);
        }

        private void OndefaultSpriteChanged()
        {
            info.defaultSprite = defaultSprite;
            EditorUtility.SetDirty(info);
        }

        private void OnPrefabFolderChanged()
        {
            info.PrefabFolder = PrefabFolder;
            EditorUtility.SetDirty(info);
        }

        [ButtonGroup("Sheet Info/Script", 1), Button(ButtonSizes.Large)]
        public void GenerateScript()
        {
            if (ScriptFolder.IsNullOrWhitespace())
            {
                infoBoxMessage = "Script Folder path is Null";
                return;
            }

            if (selectTab.IsNullOrWhitespace())
            {
                infoBoxMessage = "No sheet is selected";
                return;
            }

            string folderPath = $"{Application.dataPath}/{ScriptFolder}";
            FeederScriptGenerator.GenerateClass(cells[0, 0], rawFields, folderPath);
        }

        [ButtonGroup("Sheet Info/Script", 1), Button(ButtonSizes.Large)]
        public void GenerateAssets()
        {
            if (AssetFolder.IsNullOrWhitespace())
            {
                infoBoxMessage = "Asset Folder path is Null";
                return;
            }

            FeederDataAssetGenerator.GenerateClass(selectTab, cells, rawFields, AssetFolder, SpriteAssetFolder, PrefabFolder);
        }

        [FoldoutGroup("Sheet Info", true, 0), Button(ButtonSizes.Large), GUIColor(0.91f, 0.98f, 0.50f), EnableIf("@SpreadsheetID != string.Empty"),
         InfoBox("$infoBoxMessage", InfoMessageType.Warning, "@!string.IsNullOrEmpty(infoBoxMessage)")]
        public void LoadSheet()
        {
            string configuredCredentialPath = FeederSpreadSheetLoaderConfig.Instance.credentialFilePath;
            string credentialFilePath = Path.GetFullPath(configuredCredentialPath);
            if (credentialFilePath.IsNullOrWhitespace())
            {
                EditorUtility.DisplayDialog("Load Sheet Data", "credential file path is invalid !! check the data config", "close");
                return;
            }

            try
            {
                if (googleSheetController == null)
                {
                    googleSheetController = new FeederGoogleSheetController(SpreadsheetID, credentialFilePath);
                }
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Load Sheet Data", $"{e.Message}", "close");
                return;
            }

            sheetNames = googleSheetController.GetAllSheetName();
            sheetData = googleSheetController.GetAllSheetValueRange(sheetNames);
            if (!selectTab.IsNullOrWhitespace() && sheetNames.Contains(selectTab))
            {
                OnSheetSelected();
            }
            else
            {
                selectTab = String.Empty;
                cells = null;
                RefreshGeneratedOutputReferences();
            }

            AssetDatabase.SaveAssets();
        }

        private void OnSheetSelected()
        {
            _tablePage = 0;
            _showFullTable = false;
            if (sheetData != null && sheetData.Count > 0 &&
                sheetData.TryGetValue(selectTab, out IList<IList<Object>> data))
            {
                infoBoxMessage = string.Empty;
                info.selectTab = selectTab;
                if (!data.IsNullOrEmpty() && data.Count >= 2)
                {
                    int rowCount = data.Count;
                    int sourceColumnCount = data[1].Count;
                    string sheetTypeName = GetSheetTypeNameFromHeaderRow(data);

                    List<int> validColumnIndices = new List<int>(sourceColumnCount);
                    for (int columnIndex = 0; columnIndex < sourceColumnCount; columnIndex++)
                    {
                        if (ShouldSkipColumnByFieldRowPrefix(data, columnIndex))
                        {
                            continue;
                        }

                        validColumnIndices.Add(columnIndex);
                    }

                    int validColumnCount = validColumnIndices.Count;
                    rawFields = new List<string>(validColumnCount);
                    string[,] newCells = new string[validColumnCount, rowCount];
                    for (int i = 0; i < rowCount; i++)
                    {
                        if (!data[i].IsNullOrEmpty())
                        {
                            for (int validColumnIndex = 0; validColumnIndex < validColumnCount; validColumnIndex++)
                            {
                                int sourceColumnIndex = validColumnIndices[validColumnIndex];
                                if (sourceColumnIndex < data[i].Count)
                                {
                                    string value = data[i][sourceColumnIndex].ToString();
                                    if (i == 1)
                                    {
                                        rawFields.Add(value);
                                    }

                                    newCells[validColumnIndex, i] = value;
                                }
                            }
                        }
                    }

                    if (!sheetTypeName.IsNullOrWhitespace() && validColumnCount > 0)
                    {
                        newCells[0, 0] = sheetTypeName;
                    }

                    cells = newCells;
                }

                RefreshGeneratedOutputReferences();
                EditorUtility.SetDirty(info);
            }
            else
            {
                Debug.LogError($"data {sheetName} is null or does not contain sheet {selectTab}");
            }
        }

        private void RefreshGeneratedOutputReferences()
        {
            _generatedScriptFile = LoadGeneratedScriptFile();
            _generatedScriptableAsset = LoadGeneratedScriptableAsset();
        }

        private MonoScript LoadGeneratedScriptFile()
        {
            if (cells == null || cells.GetLength(0) == 0 || cells.GetLength(1) == 0)
            {
                return null;
            }

            string className = cells[0, 0];
            if (className.IsNullOrWhitespace())
            {
                return null;
            }

            string scriptFolderPath = BuildAssetFolderPath(ScriptFolder);
            if (scriptFolderPath.IsNullOrWhitespace())
            {
                return null;
            }

            string scriptAssetPath = $"{scriptFolderPath}/{className}Data.cs";
            return AssetDatabase.LoadAssetAtPath<MonoScript>(scriptAssetPath);
        }

        private ScriptableObject LoadGeneratedScriptableAsset()
        {
            if (selectTab.IsNullOrWhitespace())
            {
                return null;
            }

            string assetFolderPath = BuildAssetFolderPath(AssetFolder);
            if (assetFolderPath.IsNullOrWhitespace())
            {
                return null;
            }

            string assetName = $"Raw{selectTab.Replace(" ", string.Empty)}.asset";
            string assetPath = $"{assetFolderPath}/{assetName}";
            return AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
        }

        private string BuildAssetFolderPath(string configuredFolder)
        {
            if (configuredFolder.IsNullOrWhitespace())
            {
                return string.Empty;
            }

            string normalizedFolder = configuredFolder.Replace("\\", "/").Trim('/');
            if (normalizedFolder.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedFolder;
            }

            return $"Assets/{normalizedFolder}";
        }

        private bool ShouldSkipColumnByFieldRowPrefix(IList<IList<Object>> data, int columnIndex)
        {
            IList<Object> fieldRow = data[1];
            if (fieldRow.IsNullOrEmpty())
            {
                return false;
            }

            if (columnIndex >= fieldRow.Count)
            {
                return false;
            }

            string fieldCellValue = fieldRow[columnIndex].ToString();
            if (fieldCellValue.IsNullOrWhitespace())
            {
                return false;
            }

            return fieldCellValue[0] == SkipColumnPrefix;
        }

        private string GetSheetTypeNameFromHeaderRow(IList<IList<Object>> data)
        {
            IList<Object> headerRow = data[0];
            if (headerRow.IsNullOrEmpty())
            {
                return string.Empty;
            }

            for (int i = 0; i < headerRow.Count; i++)
            {
                string cellValue = headerRow[i].ToString();
                if (!cellValue.IsNullOrWhitespace())
                {
                    return cellValue;
                }
            }

            return string.Empty;
        }
    }
#endif
}
