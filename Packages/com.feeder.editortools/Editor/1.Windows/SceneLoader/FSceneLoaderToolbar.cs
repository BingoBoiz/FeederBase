#if UNITY_6000_3_OR_NEWER
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEditor.Toolbars;
using UnityEngine;

namespace Feeder
{
    [InitializeOnLoad]
    static class FSceneLoaderToolbar
    {
        const string ElementPath = "Feeder/Scene Loader";

        static FSceneLoaderToolbar()
        {
            ShortcutManager.instance.shortcutBindingChanged -= OnBindingChanged;
            ShortcutManager.instance.shortcutBindingChanged += OnBindingChanged;
            if (!FSceneLoaderStore.instance.ToolbarShown) EditorApplication.delayCall += ShowOnce;
        }

        static void ShowOnce()
        {
            // unity hides new toolbar elements until the user enables them; show ours once, then respect the user's choice
            var showAll = typeof(MainToolbar).GetMethod("ShowAll", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (showAll != null) showAll.Invoke(null, new object[] { ElementPath });
            else Debug.LogWarning($"Scene Loader: right-click the main toolbar and enable '{ElementPath}' to show the Scenes button.");
            FSceneLoaderStore.instance.ToolbarShown = true;
        }

        [MainToolbarElement(ElementPath, defaultDockPosition = MainToolbarDockPosition.Middle)]
        static IEnumerable<MainToolbarElement> Create()
        {
            var content = new MainToolbarContent(FSceneLoaderToolbarMenu.ButtonText, FSceneLoaderToolbarMenu.ButtonIcon(), FSceneLoaderToolbarMenu.ButtonTooltip());
            yield return new MainToolbarButton(content, FSceneLoaderWindow.Toggle);
            yield return new MainToolbarDropdown(new MainToolbarContent(string.Empty, FSceneLoaderToolbarMenu.DropdownTooltip), FSceneLoaderToolbarMenu.Show);
        }

        static void OnBindingChanged(ShortcutBindingChangedEventArgs args)
        {
            if (args.shortcutId == FSceneLoaderWindow.ShortcutId) MainToolbar.Refresh(ElementPath);
        }
    }
}
#endif
