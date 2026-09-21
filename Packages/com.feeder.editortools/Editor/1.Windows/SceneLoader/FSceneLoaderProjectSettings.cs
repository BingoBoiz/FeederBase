using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    [FilePath("ProjectSettings/FeederSceneLoader.asset", FilePathAttribute.Location.ProjectFolder)]
    public class FSceneLoaderProjectSettings : ScriptableSingleton<FSceneLoaderProjectSettings>
    {
        [Serializable]
        public class FolderRule
        {
            public string path;
            public string label;
            public bool customColor;
            public Color color = Color.white;
            public bool hidden;

            public bool IsEmpty => string.IsNullOrEmpty(label) && !customColor && !hidden;
        }

        public static readonly (string name, Color color)[] Palette =
        {
            ("Blue", new Color32(111, 168, 220, 255)), ("Orange", new Color32(242, 165, 60, 255)),
            ("Red", new Color32(227, 100, 92, 255)), ("Purple", new Color32(173, 134, 242, 255)),
            ("Green", new Color32(92, 192, 138, 255)), ("Yellow", new Color32(232, 197, 71, 255)),
            ("Teal", new Color32(79, 193, 201, 255)), ("Pink", new Color32(229, 123, 181, 255)),
        };

        [SerializeField] List<string> scanRoots = new() { "Assets" };
        [SerializeField] List<string> folderOrder = new();
        [SerializeField] List<FolderRule> folderRules = new();

        public IReadOnlyList<string> FolderOrder => folderOrder;

        void OnEnable() => hideFlags &= ~HideFlags.NotEditable;

        public List<string> ValidScanRoots()
        {
            var roots = new List<string>();
            foreach (var root in scanRoots)
            {
                var trimmed = root?.Trim().TrimEnd('/');
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (AssetDatabase.IsValidFolder(trimmed)) roots.Add(trimmed);
                else Debug.LogWarning($"Scene Loader: scan root '{trimmed}' is not a folder (Project Settings > Feeder > Scene Loader).");
            }
            return roots;
        }

        public FolderRule RuleFor(string path) => folderRules.Find(rule => rule.path == path);

        public void EditRule(string path, Action<FolderRule> edit)
        {
            var rule = RuleFor(path);
            if (rule == null) folderRules.Add(rule = new FolderRule { path = path });
            edit(rule);
            if (rule.IsEmpty) folderRules.Remove(rule);
            Commit();
        }

        public void RemoveRule(string path)
        {
            folderRules.RemoveAll(rule => rule.path == path);
            Commit();
        }

        public void SetFolderOrder(IEnumerable<string> keys)
        {
            folderOrder = keys.ToList();
            Commit();
        }

        public void Commit() => Save(true);
    }
}
