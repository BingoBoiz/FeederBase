using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    [FilePath("UserSettings/FeederSceneLoader.asset", FilePathAttribute.Location.ProjectFolder)]
    public class FSceneLoaderStore : ScriptableSingleton<FSceneLoaderStore>
    {
        const int RecentLimit = 8;

        [SerializeField] List<string> favoriteGuids = new();
        [SerializeField] List<string> recentGuids = new();
        [SerializeField] List<string> expandedFolders = new();
        [SerializeField] string currentFolder;
        [SerializeField] bool toolbarShown;
        [SerializeField] bool showHidden;

        public bool ShowHidden
        {
            get => showHidden;
            set
            {
                showHidden = value;
                Save(true);
            }
        }

        public bool ToolbarShown
        {
            get => toolbarShown;
            set
            {
                toolbarShown = value;
                Save(true);
            }
        }

        public string CurrentFolder
        {
            get => currentFolder;
            set
            {
                if (currentFolder == value) return;
                currentFolder = value;
                Save(true);
            }
        }

        public List<string> FavoritePaths() => ResolvePaths(favoriteGuids, "favorite");

        public List<string> RecentPaths() => ResolvePaths(recentGuids, "recent");

        public bool IsFavorite(string path) => favoriteGuids.Contains(AssetDatabase.AssetPathToGUID(path));

        public void ToggleFavorite(string path)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (!favoriteGuids.Remove(guid)) favoriteGuids.Add(guid);
            Save(true);
        }

        public void PushRecent(string path)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return;
            recentGuids.Remove(guid);
            recentGuids.Insert(0, guid);
            if (recentGuids.Count > RecentLimit) recentGuids.RemoveRange(RecentLimit, recentGuids.Count - RecentLimit);
            Save(true);
        }

        public void ClearRecent()
        {
            recentGuids.Clear();
            Save(true);
        }

        public bool IsExpanded(string folder) => expandedFolders.Contains(folder);

        public void SetExpanded(string folder, bool expanded)
        {
            if (expanded == expandedFolders.Contains(folder)) return;
            if (expanded) expandedFolders.Add(folder);
            else expandedFolders.Remove(folder);
            Save(true);
        }

        List<string> ResolvePaths(List<string> guids, string listName)
        {
            var paths = new List<string>(guids.Count);
            var pruned = false;
            for (var i = guids.Count - 1; i >= 0; i--)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!string.IsNullOrEmpty(path) && AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(SceneAsset)) continue;
                Debug.LogWarning($"Scene Loader: dropped missing {listName} scene guid '{guids[i]}'.");
                guids.RemoveAt(i);
                pruned = true;
            }
            if (pruned) Save(true);
            foreach (var guid in guids) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            return paths;
        }
    }
}
