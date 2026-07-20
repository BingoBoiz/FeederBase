using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using Sirenix.Utilities;
using Sirenix.Utilities.Editor;
#endif

namespace Feeder
{
#if ODIN_INSPECTOR
    public class FeederGooglesheetImporterWindow : OdinMenuEditorWindow
    {
        private static FeederSpreadSheetLoaderConfig config;

        [MenuItem("Tools/Feeder/Googlesheet Importer")]
        private static void OpenWindow()
        {
            FeederGooglesheetImporterWindow window = GetWindow<FeederGooglesheetImporterWindow>();
            window.position = GUIHelper.GetEditorWindowRect().AlignCenter(1000, 600);
        }

        protected override OdinMenuTree BuildMenuTree()
        {
            OdinMenuTree tree = new OdinMenuTree(true);
            OdinMenuStyle customMenuStyle = new OdinMenuStyle
            {
                BorderPadding = 0f,
                AlignTriangleLeft = true,
                TriangleSize = 16f,
                TrianglePadding = 0f,
                Offset = 20f,
                Height = 23,
                IconPadding = 0f,
                BorderAlpha = 0.323f
            };
            tree.DefaultMenuStyle = customMenuStyle;
            tree.Config.DrawSearchToolbar = true;
            config = FeederSpreadSheetLoaderConfig.Instance;
            tree.AddObjectAtPath("Setup Guide", new FeederSetupGuideView());
            tree.AddObjectAtPath("Data Config", new FeederDataConfigView(this, config));
            if (!config.sheetList.IsNullOrEmpty())
            {
                for (int i = 0; i < config.sheetList.Count; i++)
                {
                    tree.AddMenuItemAtPath("Sheets",
                        new FeederSheetInfoMenuItem(tree, new FeederSheetInfoView(config.sheetList[i])));
                }
            }

            return tree;
        }
    }

    public class FeederSheetInfoMenuItem : OdinMenuItem
    {
        public FeederSheetInfoView instance;

        public FeederSheetInfoMenuItem(OdinMenuTree tree, FeederSheetInfoView instance) : base(tree, instance.sheetName, instance)
        {
            this.instance = instance;
        }

        protected override void OnDrawMenuItem(Rect rect, Rect labelRect)
        {
            labelRect.x -= 16;
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Space)
            {
                var selection = this.MenuTree.Selection
                    .Select(x => x.Value)
                    .OfType<FeederSheetInfo>();

                if (selection.Any())
                {
                    Event.current.Use();
                }
            }
        }

        public override string SmartName
        {
            get { return instance.sheetName; }
        }
    }

    [Serializable]
    public class FeederDataConfigView
    {
        private OdinMenuEditorWindow window;

        [InlineEditor(InlineEditorModes.FullEditor)]
        public FeederSpreadSheetLoaderConfig config;

        public FeederDataConfigView(OdinMenuEditorWindow viewWindow, FeederSpreadSheetLoaderConfig viewConfig)
        {
            window = viewWindow;
            config = viewConfig;
            config.sheetUpdateCallback = OnUpdateSheetList;
        }

        private void OnUpdateSheetList()
        {
            window.ForceMenuTreeRebuild();
        }
    }

#else
    public class FeederGooglesheetImporterWindow
    {
        [MenuItem("Tools/Feeder/Googlesheet Importer")]
        private static void OpenWarningPanel()
        {
            EditorUtility.DisplayDialog("Install Odin", "Please Install Odin Package to use Google sheet Importer", "OK");
        }
    }
#endif
}
