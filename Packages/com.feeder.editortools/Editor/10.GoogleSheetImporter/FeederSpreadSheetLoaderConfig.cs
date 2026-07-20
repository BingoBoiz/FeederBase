using System.Collections.Generic;
using System.IO;
using NabaGame.Core.Runtime.Extensions;
using UnityEditor;
using UnityEngine;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
#endif

namespace Feeder
{
    public delegate void FeederUpdateSheetListAction();

#if ODIN_INSPECTOR
    public class FeederSpreadSheetLoaderConfig : ScriptableObject
    {
        public const string DefaultAssetFolder = "_Feeder/Editor/GooglesheetImporter";
        public const string DefaultSheetFolder = "_Feeder/Editor/GooglesheetImporter/SheetInfos";
        public const string DefaultCredentialRelativePath = "Assets/_Feeder/Editor/GooglesheetImporter/bb-googlesheet-data-collector.json";

        [FoldoutGroup("Sheet List", true, 0), PropertyOrder(1), InlineEditor, OnCollectionChanged("Before", "After")]
        public List<FeederSheetInfo> sheetList;

        [HideInInspector] public int sheetIndex;
        public FeederUpdateSheetListAction sheetUpdateCallback;

        [FoldoutGroup("Settings", true, 1)]
        public string credentialFilePath = DefaultCredentialRelativePath;

        public static FeederSpreadSheetLoaderConfig Instance
        {
            get => GetInstance();
        }

        private static FeederSpreadSheetLoaderConfig GetInstance()
        {
            FeederSpreadSheetLoaderConfig value =
                AssetDatabase.LoadAssetAtPath<FeederSpreadSheetLoaderConfig>(
                    $"Assets/{DefaultAssetFolder}/FeederSpreadSheetLoaderConfig.asset");
            if (value == null)
            {
                value = CreateInstance<FeederSpreadSheetLoaderConfig>();
                if (!Directory.Exists($"{Application.dataPath}/{DefaultAssetFolder}"))
                {
                    Directory.CreateDirectory($"{Application.dataPath}/{DefaultAssetFolder}");
                }

                AssetDatabase.CreateAsset(value, $"Assets/{DefaultAssetFolder}/FeederSpreadSheetLoaderConfig.asset");
            }

            if (value.sheetList == null)
            {
                value.sheetList = new List<FeederSheetInfo>();
            }

            if (value.credentialFilePath.IsNullOrWhitespace())
            {
                value.credentialFilePath = DefaultCredentialRelativePath;
            }

            return value;
        }

        public void Before(CollectionChangeInfo info, object value)
        {
        }

        public void After(CollectionChangeInfo info, object value)
        {
            sheetUpdateCallback?.Invoke();
        }

        [FoldoutGroup("Sheet List", true, 0), Button(ButtonSizes.Large), PropertyOrder(0), GUIColor(0, 1, 0)]
        public void AddNewSheet()
        {
            FeederSheetInfo newSheet = CreateInstance<FeederSheetInfo>();
            newSheet.LoadName();

            if (!Directory.Exists($"{Application.dataPath}/{DefaultSheetFolder}"))
            {
                Directory.CreateDirectory($"{Application.dataPath}/{DefaultSheetFolder}");
            }

            AssetDatabase.CreateAsset(newSheet, $"Assets/{DefaultSheetFolder}/{newSheet.sheetName}.asset");
            sheetList.Add(newSheet);
        }

        [BoxGroup("Delete Sheet", order: 2), ShowInInspector, ValueDropdown("sheetList")]
        private FeederSheetInfo wantToDeleteSheet;

        [BoxGroup("Delete Sheet", order: 2), Button(ButtonSizes.Large), EnableIf("@wantToDeleteSheet != null"),
         GUIColor(1, 0.6f, 0.4f)]
        private void DeleteSheet()
        {
            if (EditorUtility.DisplayDialog("Delete Confirmation",
                    $"Do you want to delete sheet {wantToDeleteSheet.name}?", "Yes", "No"))
            {
                sheetList.Remove(wantToDeleteSheet);
                AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(wantToDeleteSheet.GetInstanceID()));
            }
        }
    }
#endif
}
