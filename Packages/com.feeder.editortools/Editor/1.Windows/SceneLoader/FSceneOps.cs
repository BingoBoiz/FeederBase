using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Feeder
{
    [InitializeOnLoad]
    public static class FSceneOps
    {
        static FSceneOps()
        {
            EditorSceneManager.sceneOpened -= RecordOpened;
            EditorSceneManager.sceneOpened += RecordOpened;
        }

        public static bool CanEdit => !EditorApplication.isPlayingOrWillChangePlaymode;

        public static void Open(string path)
        {
            if (!CanEdit) return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        }

        public static bool AddAdditive(string path)
        {
            if (!CanEdit) return false;
            var scene = SceneManager.GetSceneByPath(path);
            if (scene.IsValid() && scene.isLoaded) return false;
            EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            return true;
        }

        public static void Remove(Scene scene)
        {
            if (!CanEdit || SceneManager.sceneCount <= 1) return;
            if (!EditorSceneManager.SaveModifiedScenesIfUserWantsTo(new[] { scene })) return;
            EditorSceneManager.CloseScene(scene, true);
        }

        public static void SetActive(Scene scene)
        {
            if (!CanEdit) return;
            if (!scene.isLoaded) scene = EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
        }

        public static List<Scene> HierarchyScenes()
        {
            var scenes = new List<Scene>(SceneManager.sceneCount);
            for (var i = 0; i < SceneManager.sceneCount; i++) scenes.Add(SceneManager.GetSceneAt(i));
            return scenes;
        }

        static void RecordOpened(Scene scene, OpenSceneMode mode) => FSceneLoaderStore.instance.PushRecent(scene.path);
    }
}
