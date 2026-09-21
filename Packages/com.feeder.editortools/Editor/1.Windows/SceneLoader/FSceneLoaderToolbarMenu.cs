using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    static class FSceneLoaderToolbarMenu
    {
        public const string ButtonText = "Scenes";
        public const string DropdownTooltip = "Favorites & Recent scenes";

        public static string ButtonTooltip() => $"Scene Loader ({FSceneLoaderWindow.ShortcutLabel()})";

        public static Texture2D ButtonIcon() => FSceneLoaderWindow.Icon("SceneAsset Icon") as Texture2D;

        public static void Show(Rect anchor) => Build().DropDown(anchor);

        public static GenericMenu Build()
        {
            var menu = new GenericMenu();
            var favorites = FSceneLoaderStore.instance.FavoritePaths();
            var recent = FSceneLoaderStore.instance.RecentPaths();
            AddSection(menu, "Favorites", favorites);
            AddSection(menu, "Recent", recent);
            var additive = new List<string>(favorites);
            foreach (var path in recent)
                if (!additive.Contains(path)) additive.Add(path);
            foreach (var path in additive)
                AddItem(menu, "Add Additive/" + Path.GetFileNameWithoutExtension(path), () => FSceneOps.AddAdditive(path));
            if (additive.Count > 0) menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent($"Open Scene Loader ({FSceneLoaderWindow.ShortcutLabel()})"), false, FSceneLoaderWindow.Toggle);
            return menu;
        }

        static void AddSection(GenericMenu menu, string title, List<string> paths)
        {
            if (paths.Count == 0) return;
            menu.AddDisabledItem(new GUIContent(title));
            foreach (var path in paths)
                AddItem(menu, "  " + Path.GetFileNameWithoutExtension(path), () => FSceneOps.Open(path));
            menu.AddSeparator(string.Empty);
        }

        static void AddItem(GenericMenu menu, string label, GenericMenu.MenuFunction action)
        {
            if (FSceneOps.CanEdit) menu.AddItem(new GUIContent(label), false, action);
            else menu.AddDisabledItem(new GUIContent(label));
        }
    }
}
