using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Feeder
{
    public class FSceneLoaderWindow : OdinEditorWindow
    {
        [MenuItem("Tools/Feeder/Scenes Loader Window", priority = 1)]
        private static void OpenWindow()
        {
            var window = GetWindow<FSceneLoaderWindow>();
            window.titleContent = FIconCatalog.CreateWindowTitle("Scenes Loader Window", FIconCatalog.SceneLoaderTitleIcon);
            window.Show();
        }

        [Title("Play Mode Warning")]
        [InfoBox("This tool is disabled in Play Mode. Please exit Play Mode to use this tool.", InfoMessageType.Warning)]
        [ShowIf("@EditorApplication.isPlaying")]
        [PropertySpace(SpaceBefore = 10)]
        public bool playModeWarning = true;

        protected override void OnEnable()
        {
            base.OnEnable();
            RefreshSceneList();
        }

        [Button("Refresh Scene List", ButtonSizes.Medium)]
        [PropertySpace(SpaceBefore = 10)]
        [ShowIf("@!EditorApplication.isPlaying")]
        public void RefreshSceneList()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("Scene Loader: Cannot refresh scenes in Play Mode.");
                return;
            }

            sceneDataList.Clear();
            EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;

            for (int i = 0; i < buildScenes.Length; i++)
            {
                EditorBuildSettingsScene buildScene = buildScenes[i];
                if (buildScene == null || string.IsNullOrEmpty(buildScene.path))
                    continue;

                string sceneName = System.IO.Path.GetFileNameWithoutExtension(buildScene.path);
                sceneDataList.Add(new SceneData
                {
                    sceneName = sceneName,
                    scenePath = buildScene.path
                });
            }

            // sort by name for easier navigation
            sceneDataList = sceneDataList.OrderBy(s => s.sceneName).ToList();
        }

        [TableList(ShowIndexLabels = false, IsReadOnly = true, NumberOfItemsPerPage = 20, AlwaysExpanded = true)]
        [PropertySpace(SpaceBefore = 10)]
        [HideLabel]
        public List<SceneData> sceneDataList = new List<SceneData>();

        [System.Serializable]
        public class SceneData
        {
            [HideInInspector] public string sceneName;

            [Button("@sceneName", ButtonSizes.Medium)]
            [TableColumnWidth(300)]
            public void SceneList()
            {
                if (EditorApplication.isPlaying)
                {
                    EditorUtility.DisplayDialog("Cannot Add Scene", "Cannot add scenes while in Play Mode. Please exit Play Mode first.", "OK");
                    return;
                }

                if (string.IsNullOrEmpty(scenePath))
                {
                    EditorUtility.DisplayDialog("Invalid Scene", "Scene path is empty.", "OK");
                    return;
                }

                // check if scene file exists
                if (!System.IO.File.Exists(scenePath))
                {
                    EditorUtility.DisplayDialog("Scene Not Found", $"Scene file not found at path:\n{scenePath}", "OK");
                    return;
                }

                // save current scenes if modified
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    Debug.Log("Scene Loader: User cancelled adding scene.");
                    return;
                }

                // open scene additively
                try
                {
                    Scene newScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                    if (newScene.IsValid())
                    {
                        Debug.Log($"<color=green>Scene Loader: Successfully added scene '{sceneName}' to the hierarchy.</color>");
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Failed to Add Scene", $"Failed to open scene '{sceneName}'.", "OK");
                    }
                }
                catch (System.Exception ex)
                {
                    EditorUtility.DisplayDialog("Error", $"Failed to add scene '{sceneName}':\n{ex.Message}", "OK");
                    Debug.LogError($"Scene Loader: Error adding scene '{sceneName}': {ex}");
                }
            }

            [HideInInspector]
            public string scenePath;
        }
    }
}
