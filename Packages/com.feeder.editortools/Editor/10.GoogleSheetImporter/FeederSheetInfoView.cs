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
         ValueDropdown(nameof(GetNamespaceOptions), AppendNextDrawer = true),
         PropertyTooltip("Namespace của {Class}Data.cs và của enum do Update Enum tạo. Để trống = global scope.\n" +
                         "Token enum trần trong header CHỈ khớp enum cùng namespace này."),
         OnValueChanged("OnNamespaceChanged")]
        public string Namespace;

        [FoldoutGroup("Sheet Info", true), OnValueChanged("OnEnumScriptChanged")]
        public MonoScript EnumScript;

        private static IEnumerable<ValueDropdownItem<string>> GetNamespaceOptions()
        {
            yield return new ValueDropdownItem<string>("<không có namespace>", string.Empty);

            List<string> all = FeederEnumUtils.ProjectNamespaces;
            for (int i = 0; i < all.Count; i++)
            {
                yield return new ValueDropdownItem<string>(all[i], all[i]);
            }
        }

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

        [FoldoutGroup("Sheet Data", true), HorizontalGroup("Sheet Data/GeneratedOutputs"), PropertyOrder(39),
         Button("Show Data", ButtonSizes.Medium), GUIColor(0.60f, 0.90f, 0.98f), EnableIf("@HasShowDataTarget"),
         PropertyTooltip("Mở cửa sổ lớn hiển thị toàn bộ dữ liệu của tab đang chọn (chỉ để xem). " +
                         "Cần Generate Assets trước vì cửa sổ đọc từ asset Raw{Tab}.asset.")]
        private void ShowDataWindow()
        {
            RefreshGeneratedOutputReferences();
            FeederSheetDataWindow.Open(_generatedScriptableAsset, selectTab);
        }

        private bool HasShowDataTarget => _generatedScriptableAsset != null;

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
        private FeederSheetInfo info;

        private string StatusStateKey => "Feeder.GoogleSheetImporter.Status." + sheetName;

        private string infoBoxMessage
        {
            get => SessionState.GetString(StatusStateKey, string.Empty);
            set => SessionState.SetString(StatusStateKey, value ?? string.Empty);
        }

        public FeederSheetInfoView(FeederSheetInfo sheetInfo)
        {
            info = sheetInfo;
            sheetName = sheetInfo.sheetName;
            SpreadsheetID = sheetInfo.SpreadsheetID;
            ScriptFolder = sheetInfo.ScriptFolder;
            Namespace = sheetInfo.Namespace;
            EnumScript = sheetInfo.EnumScript;
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
            Namespace = sheetInfo.Namespace;
            EnumScript = sheetInfo.EnumScript;
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

        private static readonly System.Text.RegularExpressions.Regex NamespaceIdentifier =
            new System.Text.RegularExpressions.Regex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$");

        private void OnNamespaceChanged()
        {
            Namespace = (Namespace ?? string.Empty).Trim().Trim('.');
            if (!Namespace.IsNullOrWhitespace() && !NamespaceIdentifier.IsMatch(Namespace))
            {
                infoBoxMessage = $"'{Namespace}' không phải namespace hợp lệ — file sinh ra sẽ không compile.";
            }

            info.Namespace = Namespace;
            EditorUtility.SetDirty(info);
        }

        private void OnEnumScriptChanged()
        {
            info.EnumScript = EnumScript;
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

        [ButtonGroup("Sheet Info/Enum", 2), Button("Update Enum", ButtonSizes.Large),
         GUIColor(0.52f, 0.88f, 0.62f), EnableIf("@CanUpdateEnum")]
        public void UpdateEnum()
        {
            if (ScriptFolder.IsNullOrWhitespace() && EnumScript == null)
            {
                infoBoxMessage = "Cần điền Script Folder hoặc gán Enum Script.";
                return;
            }

            if (selectTab.IsNullOrWhitespace())
            {
                infoBoxMessage = "No sheet is selected";
                return;
            }

            List<FeederEnumColumnScan> scans = FeederEnumScanner.ScanEnumColumns(cells, rawFields, selectTab);
            OpenEnumPreview(scans);
        }

        [ButtonGroup("Sheet Info/Enum", 2), Button("Update All Enums", ButtonSizes.Large),
         GUIColor(0.72f, 0.87f, 0.76f), EnableIf("@CanUpdateAllEnums")]
        public void UpdateAllEnums()
        {
            if (ScriptFolder.IsNullOrWhitespace() && EnumScript == null)
            {
                infoBoxMessage = "Cần điền Script Folder hoặc gán Enum Script.";
                return;
            }

            if (sheetData == null || sheetData.Count == 0)
            {
                infoBoxMessage = "Chưa tải dữ liệu. Bấm 'Load Sheet' trước.";
                return;
            }

            List<FeederEnumColumnScan> scans = new List<FeederEnumColumnScan>();
            foreach (KeyValuePair<string, IList<IList<Object>>> pair in sheetData)
            {
                if (!BuildCells(pair.Key, pair.Value, out string[,] tabCells, out List<string> tabRawFields))
                {
                    continue;
                }

                scans.AddRange(FeederEnumScanner.ScanEnumColumns(tabCells, tabRawFields, pair.Key));
            }

            OpenEnumPreview(scans);
        }

        private void OpenEnumPreview(List<FeederEnumColumnScan> scans)
        {
            string sheetTypeName = cells != null && cells.GetLength(0) > 0 && cells.GetLength(1) > 0
                ? cells[0, 0]
                : string.Empty;
            string enumScriptPath = EnumScript != null ? AssetDatabase.GetAssetPath(EnumScript) : null;
            FeederEnumUpdatePlan plan = FeederEnumUpdater.BuildPlan(scans, BuildAssetFolderPath(ScriptFolder),
                sheetTypeName, enumScriptPath, Namespace);

            if (!plan.HasApplicableChange)
            {
                infoBoxMessage = plan.Issues.Count > 0
                    ? string.Join(" | ", plan.Issues.ToArray())
                    : "Không có enum nào cần cập nhật.";
                Debug.Log($"<color=cyan>[Update Enum] {infoBoxMessage}</color>");
                return;
            }

            infoBoxMessage = string.Empty;
            FeederChangeSet changeSet = FeederEnumUpdater.ToChangeSet(plan);
            FeederDiffPreviewWindow.Open("Update Enum — Preview", changeSet, result =>
            {
                if (!result.Accepted || result.AcceptedFiles.Count == 0)
                {
                    return;
                }

                infoBoxMessage = FeederEnumUpdater.Apply(result.AcceptedFiles);
            });
        }

        [ButtonGroup("Sheet Info/Generate", 3), Button("Generate Script", ButtonSizes.Large),
         GUIColor(0.98f, 0.80f, 0.45f)]
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

            string className = cells != null && cells.GetLength(0) > 0 && cells.GetLength(1) > 0 ? cells[0, 0] : null;
            string newText = FeederScriptGenerator.BuildScriptText(className, rawFields, Namespace,
                out List<string> warnings);
            if (newText == null)
            {
                infoBoxMessage = string.Join(" | ", warnings.ToArray());
                return;
            }

            string assetPath = $"{BuildAssetFolderPath(ScriptFolder)}/{className}Data.cs";
            string originalText = FeederEnumSourceEditor.TryReadText(assetPath, out bool hadBom);

            FeederFileChange file = new FeederFileChange
            {
                AssetPath = assetPath,
                OriginalText = originalText,
                OriginalHadBom = hadBom,
                NewText = newText,
            };
            FeederChangeItem item = new FeederChangeItem
            {
                Title = $"class {className} / {className}Data",
                Subtitle = $"{rawFields.Count} cột",
            };
            item.Warnings.AddRange(warnings);
            file.Items.Add(item);

            FeederChangeSet changeSet = new FeederChangeSet();
            changeSet.Files.Add(file);

            FeederDiffPreviewWindow.Open("Generate Script — Preview", changeSet, result =>
            {
                if (!result.Accepted || result.AcceptedFiles.Count == 0)
                {
                    return;
                }

                if (!FeederChangeWriter.CanWriteNow(out string reason))
                {
                    EditorUtility.DisplayDialog("Generate Script", reason, "close");
                    return;
                }

                FeederWriteReport report = FeederChangeWriter.Write(result.AcceptedFiles);
                infoBoxMessage = report.AnyWritten
                    ? $"Đã ghi {className}Data.cs. Đợi compile xong rồi bấm 'Generate Assets'."
                    : $"Không ghi được: {string.Join(" | ", report.Problems.ToArray())}";
                Debug.Log($"<color=cyan>[Generate Script] {infoBoxMessage}</color>");
                RefreshGeneratedOutputReferences();
            });
        }

        [ButtonGroup("Sheet Info/Generate", 3), Button("Generate Assets", ButtonSizes.Large),
         GUIColor(0.98f, 0.80f, 0.45f)]
        public void GenerateAssets()
        {
            if (AssetFolder.IsNullOrWhitespace())
            {
                infoBoxMessage = "Asset Folder path is Null";
                return;
            }

            FeederDataAssetGenerator.GenerateClass(selectTab, cells, rawFields, AssetFolder, SpriteAssetFolder,
                PrefabFolder, Namespace);
            RefreshGeneratedOutputReferences();
        }

        private bool HasEnumTarget => !ScriptFolder.IsNullOrWhitespace() || EnumScript != null;

        private bool CanUpdateEnum => HasEnumTarget && !selectTab.IsNullOrWhitespace()
                                                    && cells != null && cells.GetLength(0) > 0
                                                    && cells.GetLength(1) > 2;

        private bool CanUpdateAllEnums => HasEnumTarget && sheetData != null && sheetData.Count > 0;

        [ButtonGroup("Sheet Info/Generate", 3), Button("Generate All Assets", ButtonSizes.Large),
         GUIColor(0.95f, 0.63f, 0.38f)]
        public void GenerateAllAssets()
        {
            if (AssetFolder.IsNullOrWhitespace())
            {
                infoBoxMessage = "Asset Folder path is Null";
                return;
            }

            FeederGoogleSheetController controller;
            try
            {
                controller = new FeederGoogleSheetController(SpreadsheetID);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Generate All Assets", $"{e.Message}", "close");
                return;
            }

            List<string> names = controller.GetAllSheetName();
            Dictionary<string, IList<IList<Object>>> allData = controller.GetAllSheetValueRange(names);
            info.strikethroughRows = controller.GetStrikethroughRowsPerSheet(names);
            EditorUtility.SetDirty(info);

            string assetFolderPath = BuildAssetFolderPath(AssetFolder);
            int updated = 0;
            int skipped = 0;
            try
            {
                foreach (string name in names)
                {
                    string assetPath = $"{assetFolderPath}/Raw{name.Replace(" ", string.Empty)}.asset";
                    if (AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath) == null)
                    {
                        skipped++;
                        continue;
                    }

                    if (!allData.TryGetValue(name, out IList<IList<Object>> data) ||
                        !BuildCells(name, data, out string[,] tabCells, out List<string> tabRawFields) ||
                        tabCells == null || tabCells.GetLength(0) == 0 || tabCells[0, 0].IsNullOrWhitespace())
                    {
                        skipped++;
                        continue;
                    }

                    FeederDataAssetGenerator.GenerateClass(name, tabCells, tabRawFields, AssetFolder,
                        SpriteAssetFolder, PrefabFolder, Namespace);
                    updated++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            infoBoxMessage = $"Generate All Assets: cập nhật {updated}, bỏ qua {skipped} tab (chưa có asset).";
            Debug.Log($"[Feeder] {infoBoxMessage}");
        }

        [FoldoutGroup("Sheet Info", true, 0), PropertyOrder(4), OnInspectorGUI]
        private void DrawStatusMessage()
        {
            if (string.IsNullOrEmpty(infoBoxMessage))
            {
                return;
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.HelpBox(infoBoxMessage, MessageType.Info);
        }

        [ButtonGroup("Sheet Info/Load", 1), Button("Load Sheet", ButtonSizes.Large),
         GUIColor(0.48f, 0.74f, 0.96f), EnableIf("@!string.IsNullOrEmpty(SpreadsheetID)")]
        public void LoadSheet()
        {
            try
            {
                googleSheetController = new FeederGoogleSheetController(SpreadsheetID);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Load Sheet Data", $"{e.Message}", "close");
                return;
            }

            sheetNames = googleSheetController.GetAllSheetName();
            sheetData = googleSheetController.GetAllSheetValueRange(sheetNames);
            try
            {
                info.strikethroughRows = googleSheetController.GetStrikethroughRowsPerSheet(sheetNames);
                EditorUtility.SetDirty(info);
            }
            catch (Exception ex)
            {
                info.strikethroughRows = null;
                Debug.LogWarning($"Không đọc được strikethrough, sẽ không bỏ qua hàng nào: {ex.Message}");
            }

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

        [ButtonGroup("Sheet Info/Load", 1), Button("Load This Tab", ButtonSizes.Large),
         GUIColor(0.68f, 0.84f, 0.96f), EnableIf("@!string.IsNullOrEmpty(SpreadsheetID)")]
        public void LoadThisSheet()
        {
            try
            {
                googleSheetController = FeederGoogleSheetController.CreateForSingleSheet(SpreadsheetID);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Load Sheet Data", $"{e.Message}", "close");
                return;
            }

            sheetNames = googleSheetController.GetAllSheetName();

            if (selectTab.IsNullOrWhitespace() || !sheetNames.Contains(selectTab))
            {
                infoBoxMessage = "Chọn 'selectTab' trong mục Sheet Data rồi bấm lại 'Load This Sheet'.";
                return;
            }

            infoBoxMessage = string.Empty;
            List<string> only = new List<string> { selectTab };
            googleSheetController.EnsureSheet(selectTab);
            sheetData = googleSheetController.GetAllSheetValueRange(only);
            try
            {
                info.strikethroughRows = googleSheetController.GetStrikethroughRowsPerSheet(only);
                EditorUtility.SetDirty(info);
            }
            catch (Exception ex)
            {
                info.strikethroughRows = null;
                Debug.LogWarning($"Không đọc được strikethrough: {ex.Message}");
            }

            OnSheetSelected();
            AssetDatabase.SaveAssets();
        }

        private void OnSheetSelected()
        {
            _tablePage = 0;
            _showFullTable = false;

            if (!selectTab.IsNullOrWhitespace() && googleSheetController != null &&
                (sheetData == null || !sheetData.ContainsKey(selectTab)))
            {
                EnsureTabLoaded(selectTab);
            }

            if (sheetData != null && sheetData.Count > 0 &&
                sheetData.TryGetValue(selectTab, out IList<IList<Object>> data))
            {
                infoBoxMessage = string.Empty;
                info.selectTab = selectTab;
                if (BuildCells(selectTab, data, out string[,] newCells, out List<string> newRawFields))
                {
                    rawFields = newRawFields;
                    cells = newCells;
                }

                RefreshGeneratedOutputReferences();
                EditorUtility.SetDirty(info);
            }
            else
            {
                infoBoxMessage = $"Tab '{selectTab}' chưa được tải. Bấm 'Load This Sheet' hoặc 'Load Sheet'.";
            }
        }

        private void EnsureTabLoaded(string tab)
        {
            List<string> only = new List<string> { tab };
            googleSheetController.EnsureSheet(tab);

            Dictionary<string, IList<IList<Object>>> mergedData = sheetData != null
                ? new Dictionary<string, IList<IList<Object>>>(sheetData)
                : new Dictionary<string, IList<IList<Object>>>();
            foreach (KeyValuePair<string, IList<IList<Object>>> kv in googleSheetController.GetAllSheetValueRange(only))
            {
                mergedData[kv.Key] = kv.Value;
            }

            sheetData = mergedData;

            Dictionary<string, List<int>> mergedStruck = info.strikethroughRows != null
                ? new Dictionary<string, List<int>>(info.strikethroughRows)
                : new Dictionary<string, List<int>>();
            foreach (KeyValuePair<string, List<int>> kv in googleSheetController.GetStrikethroughRowsPerSheet(only))
            {
                mergedStruck[kv.Key] = kv.Value;
            }

            info.strikethroughRows = mergedStruck;
            EditorUtility.SetDirty(info);
        }

        private bool BuildCells(string tab, IList<IList<Object>> data, out string[,] resultCells, out List<string> resultRawFields)
        {
            resultCells = null;
            resultRawFields = new List<string>();
            if (data.IsNullOrEmpty() || data.Count < 2)
            {
                return false;
            }

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
            resultRawFields = new List<string>(validColumnCount);

            HashSet<int> struckRowSet = GetStruckRowSet(tab);
            List<int> validRowIndices = new List<int>(rowCount);
            for (int i = 0; i < rowCount; i++)
            {
                if (i >= 2 && struckRowSet.Contains(i))
                {
                    continue;
                }

                validRowIndices.Add(i);
            }

            string[,] newCells = new string[validColumnCount, validRowIndices.Count];
            for (int outRow = 0; outRow < validRowIndices.Count; outRow++)
            {
                int sourceRow = validRowIndices[outRow];
                if (data[sourceRow].IsNullOrEmpty())
                {
                    continue;
                }

                for (int validColumnIndex = 0; validColumnIndex < validColumnCount; validColumnIndex++)
                {
                    int sourceColumnIndex = validColumnIndices[validColumnIndex];
                    if (sourceColumnIndex < data[sourceRow].Count)
                    {
                        string value = data[sourceRow][sourceColumnIndex].ToString();
                        if (sourceRow == 1)
                        {
                            resultRawFields.Add(value);
                        }

                        newCells[validColumnIndex, outRow] = value;
                    }
                }
            }

            if (!sheetTypeName.IsNullOrWhitespace() && validColumnCount > 0)
            {
                newCells[0, 0] = sheetTypeName;
            }

            resultCells = newCells;
            return true;
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

        private HashSet<int> GetStruckRowSet(string tab)
        {
            if (info?.strikethroughRows != null &&
                info.strikethroughRows.TryGetValue(tab, out List<int> rows) && rows != null)
            {
                return new HashSet<int>(rows);
            }

            return new HashSet<int>();
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
