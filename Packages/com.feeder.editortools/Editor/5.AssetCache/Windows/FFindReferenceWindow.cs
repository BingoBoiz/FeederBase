using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using Sirenix.Utilities;
using Sirenix.Utilities.Editor;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Standalone asset cache window for the Feeder suite. Fully independent of the
    /// FindReference2 (FR2) asset while borrowing its search layout ideas.
    /// </summary>
    public class FFindReferenceWindow : OdinMenuEditorWindow
    {
        [MenuItem("Tools/Feeder/Feeder Asset Cache", priority = 1)]
        private static void OpenWindow()
        {
            var window = GetWindow<FFindReferenceWindow>();
            window.titleContent = FIconCatalog.CreateWindowTitle("Feeder Asset Cache", FIconCatalog.WindowMenuTitleIcon);
            window.position = GUIHelper.GetEditorWindowRect().AlignCenter(1100, 680);
            window.Show();
        }

        protected override OdinMenuTree BuildMenuTree()
        {
            var settings = FMenuTreeSettings.instance;
            var tree = new OdinMenuTree(false);
            settings.ApplyTo(tree);

            tree.Add("Scan Database", CreateInstance<FAssetCacheScanDatabaseMode>(), SdfIconType.FolderCheck);
            tree.Add("Build Estimate", CreateInstance<FAssetCacheBuildEstimateMode>(), SdfIconType.Files);
            tree.Add("Search", CreateInstance<FAssetCacheSearchMode>(), SdfIconType.Search);
            tree.Add("FBX Unpack", CreateInstance<FFbxUnpackMode>(), SdfIconType.BoxArrowInDown);

            return tree;
        }

        protected override void OnBeginDrawEditors()
        {
            var selectedMode = MenuTree?.Selection?.SelectedValue as FRefModeBase;
            FRefHeaderGUI.DrawGuide(selectedMode != null ? selectedMode.Description : null);

            if (FRefDatabase.instance.IsBuilding)
                Repaint();
        }

        private void OnSelectionChange()
        {
            var selectedMode = MenuTree?.Selection?.SelectedValue as FRefModeBase;
            selectedMode?.OnHostSelectionChanged();
            Repaint();
        }
    }
}
