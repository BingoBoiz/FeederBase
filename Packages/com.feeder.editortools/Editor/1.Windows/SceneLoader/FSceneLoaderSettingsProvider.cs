using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Feeder
{
    static class FSceneLoaderSettingsProvider
    {
        public const string SettingsPath = "Project/Feeder/Scene Loader";

        [SettingsProvider]
        static SettingsProvider Create() => new(SettingsPath, SettingsScope.Project)
        {
            label = "Scene Loader",
            keywords = new[] { "Scene", "Loader", "Folder", "Color", "Scan", "Order" },
            activateHandler = (_, root) =>
            {
                var settings = FSceneLoaderProjectSettings.instance;
                var serialized = new SerializedObject(settings);
                var body = new ScrollView();
                body.AddToClassList("fsl-settings-page");
                var title = new Label("Scene Loader");
                title.AddToClassList("fsl-settings-title");
                body.Add(title);
                body.Add(new HelpBox(
                    "Scan Roots: folders searched for scenes. 'Assets' shows its subfolders as top-level groups, any other root is a group of its own.\n" +
                    "Top-Level Order: drag to reorder groups (unlisted groups follow alphabetically). You can also drag groups in the Scene Loader sidebar.\n" +
                    "Folder Rules: display label, color and visibility per folder. Right-click a folder in the Scene Loader to edit it.\n" +
                    "Saved in ProjectSettings/FeederSceneLoader.asset - commit it to share with the team.",
                    HelpBoxMessageType.Info));
                body.Add(new PropertyField(serialized.FindProperty("scanRoots"), "Scan Roots"));
                body.Add(new PropertyField(serialized.FindProperty("folderOrder"), "Top-Level Order"));
                body.Add(new PropertyField(serialized.FindProperty("folderRules"), "Folder Rules"));
                body.Bind(serialized);
                body.TrackSerializedObjectValue(serialized, _ =>
                {
                    settings.Commit();
                    FSceneLoaderWindow.RescanAll();
                });
                var sheet = FSceneLoaderWindow.LoadStyleSheet();
                if (sheet != null) root.styleSheets.Add(sheet);
                root.Add(body);
            },
        };
    }
}
