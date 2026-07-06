#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Feeder.MB;
using Feeder.MB.Core;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Feeder
{
    /// <summary>
    /// Drives the copied MeshBaker pipeline: creates transient TextureBaker/MeshBaker components,
    /// bakes the texture atlas + combined material, then bakes the combined mesh into a prefab.
    /// Source objects are never modified.
    /// </summary>
    public static class FMeshBakeService
    {
        private const string ProgressTitle = "Feeder Mesh Modifier";

        public static FMeshBakeResult Bake(FMeshBakeSession session, FMeshBakeSettings settings, out string error)
        {
            error = Validate(session, settings);
            if (error != null)
                return null;

            string folder = settings.outputFolder.Replace('\\', '/').TrimEnd('/');
            var tempInstances = new List<GameObject>();
            GameObject bakerGo = null;
            FMeshBakeResult result = null;

            try
            {
                EnsureFolder(folder);

                bool anySceneInput;
                List<GameObject> objsToMesh = CollectSourceObjects(session.Targets, tempInstances, out anySceneInput);
                if (objsToMesh.Count == 0)
                {
                    error = "Không tìm thấy MeshRenderer nào có mesh hợp lệ trong các mục tiêu.";
                    return null;
                }

                bakerGo = new GameObject("~FeederMeshModifier_Baker");
                MB3_TextureBaker tb = bakerGo.AddComponent<MB3_TextureBaker>();
                var mbGo = new GameObject("meshBaker");
                mbGo.transform.SetParent(bakerGo.transform);
                MB3_MeshBaker mb = mbGo.AddComponent<MB3_MeshBaker>();
                if (tb == null || mb == null)
                {
                    // AddComponent returns null when Unity refuses the component (e.g. it lives in an editor-only assembly).
                    error = "Không thể tạo component baker tạm. Kiểm tra Console — có thể asmdef Feeder.MB chưa được import đúng.";
                    return null;
                }

                ConfigureTextureBaker(tb, settings, objsToMesh);
                CreateCombinedMaterialAssets(tb, settings, objsToMesh);

                // Bake the texture atlas(es); MB3_EditorMethods handles non-readable textures and saves atlas PNGs.
                MB_AtlasesAndRects[] atlases = tb.CreateAtlases(
                    (msg, progress) => EditorUtility.DisplayProgressBar(ProgressTitle, msg, progress),
                    true, new MB3_EditorMethods());
                if (atlases == null || atlases.Length == 0 || tb.textureBakeResults == null)
                {
                    error = "Bake texture atlas thất bại. Xem Console để biết chi tiết.";
                    return null;
                }
                EditorUtility.SetDirty(tb.textureBakeResults);

                // The mesh baker writes the combined mesh next to this prefab and rebuilds it.
                GameObject resultPrefab = CreatePrefabShell(settings);
                mb.resultPrefab = resultPrefab;
                mb.resultPrefabLeaveInstanceInSceneAfterBake = settings.placeResultInScene && anySceneInput;
                mb.textureBakeResults = tb.textureBakeResults;
                mb.clearBuffersAfterBake = true;
                // The betaNativeArrayAPI default asserts ("Mesh Channels Cache has not collected mesh data")
                // when it must generate tangents mid-collection; the simple API is the mature path.
                mb.meshCombiner.meshAPI = MB_MeshCombineAPIType.simpleMeshAPI;
                // Only bake a tangent channel when at least one source mesh actually has tangents;
                // otherwise MeshBaker tries to generate them and fails on meshes with collapsed UVs.
                mb.meshCombiner.doTan = objsToMesh.Any(o =>
                {
                    var mf = o.GetComponent<MeshFilter>();
                    return mf != null && mf.sharedMesh != null &&
                           mf.sharedMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent);
                });
                mb.meshCombiner.renderType = MB_RenderType.meshRenderer;
                mb.meshCombiner.outputOption = MB2_OutputOptions.bakeIntoPrefab;
                mb.meshCombiner.lightmapOption = settings.generateLightmapUV2
                    ? MB2_LightmapOptions.generate_new_UV2_layout
                    : MB2_LightmapOptions.ignore_UV2;
                mb.meshCombiner.pivotLocationType = settings.pivotLocation;

                bool createdDummyResults;
                if (!MB3_MeshBakerEditorFunctions.BakeIntoCombined(mb, out createdDummyResults))
                {
                    error = "Gộp mesh thất bại. Xem Console để biết chi tiết.";
                    return null;
                }

                result = new FMeshBakeResult
                {
                    PrefabPath = settings.PrefabPath,
                    MeshPath = settings.MeshPath,
                    MaterialPath = settings.MaterialPath,
                    BakeResultsPath = settings.BakeResultsPath
                };
                CollectAtlasPaths(tb.resultMaterial, result.AtlasPaths);
                return result;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                error = "Bake gặp lỗi ngoại lệ: " + e.Message + "\n\nXem Console để biết chi tiết.";
                return null;
            }
            finally
            {
                foreach (GameObject temp in tempInstances)
                    if (temp != null)
                        Object.DestroyImmediate(temp);
                if (bakerGo != null)
                    Object.DestroyImmediate(bakerGo);

                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                if (result != null)
                    ReportSuccess(result);
            }
        }

        private static string Validate(FMeshBakeSession session, FMeshBakeSettings settings)
        {
            if (session.Targets.Count == 0)
                return "Hãy thêm ít nhất một mục tiêu (object trong scene hoặc prefab).";
            if (string.IsNullOrWhiteSpace(settings.baseName))
                return "Base Name không được để trống.";
            if (settings.baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "Base Name chứa ký tự không hợp lệ cho tên file.";

            string folder = settings.outputFolder.Replace('\\', '/').TrimEnd('/');
            if (!folder.StartsWith("Assets/") && folder != "Assets")
                return "Output Folder phải nằm trong thư mục Assets của project.";
            return null;
        }

        private static List<GameObject> CollectSourceObjects(List<Object> targets,
            List<GameObject> tempInstances, out bool anySceneInput)
        {
            anySceneInput = false;
            var objsToMesh = new List<GameObject>();
            foreach (Object target in targets)
            {
                var go = target as GameObject;
                if (go == null)
                    continue;

                GameObject root = go;
                if (EditorUtility.IsPersistent(go))
                {
                    // Prefab asset: bake from a transient scene instance, removed in finally.
                    root = (GameObject)PrefabUtility.InstantiatePrefab(go);
                    root.hideFlags = HideFlags.HideInHierarchy;
                    tempInstances.Add(root);
                }
                else
                {
                    anySceneInput = true;
                }

                foreach (MeshRenderer mr in root.GetComponentsInChildren<MeshRenderer>(false))
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null)
                        continue;
                    if (!objsToMesh.Contains(mr.gameObject))
                        objsToMesh.Add(mr.gameObject);
                }
            }

            return objsToMesh;
        }

        private static void ConfigureTextureBaker(MB3_TextureBaker tb, FMeshBakeSettings settings,
            List<GameObject> objsToMesh)
        {
            tb.maxAtlasSize = settings.maxAtlasSize;
            tb.atlasPadding = settings.atlasPadding;
            tb.packingAlgorithm = settings.packingAlgorithm;
            tb.resizePowerOfTwoTextures = settings.resizePowerOfTwoTextures;
            tb.fixOutOfBoundsUVs = settings.considerMeshUVs;
            tb.maxTilingBakeSize = settings.maxTilingBakeSize;
            tb.considerNonTextureProperties = settings.considerNonTextureProperties;

            foreach (string propName in settings.customShaderPropNames)
                if (!string.IsNullOrWhiteSpace(propName))
                    tb.customShaderProperties.Add(new ShaderTextureProperty(propName.Trim(), false));

            foreach (string propName in settings.texturePropNamesToIgnore)
                if (!string.IsNullOrWhiteSpace(propName))
                    tb.texturePropNamesToIgnore.Add(propName.Trim());

            tb.GetObjectsToCombine().AddRange(objsToMesh);
        }

        /// <summary>
        /// Port of MeshBaker's MB3_TextureBakerEditorInternal.CreateCombinedMaterialAssets (single-material path):
        /// clones the first source material so the combined material keeps its shader and non-texture settings.
        /// </summary>
        private static void CreateCombinedMaterialAssets(MB3_TextureBaker tb, FMeshBakeSettings settings,
            List<GameObject> objsToMesh)
        {
            Material newMat = null;
            Renderer firstRenderer = objsToMesh[0].GetComponent<Renderer>();
            if (firstRenderer != null && firstRenderer.sharedMaterial != null)
            {
                newMat = new Material(firstRenderer.sharedMaterial);
                MB3_TextureBaker.ConfigureNewMaterialToMatchOld(newMat, firstRenderer.sharedMaterial);
            }

            if (newMat == null)
            {
                Shader fallback = Shader.Find("Standard");
                if (fallback == null)
                    fallback = Shader.Find("Diffuse");
                newMat = new Material(fallback);
            }

            AssetDatabase.CreateAsset(newMat, settings.MaterialPath);
            tb.resultMaterial = AssetDatabase.LoadAssetAtPath<Material>(settings.MaterialPath);

            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<MB2_TextureBakeResults>(), settings.BakeResultsPath);
            tb.textureBakeResults = AssetDatabase.LoadAssetAtPath<MB2_TextureBakeResults>(settings.BakeResultsPath);
            AssetDatabase.Refresh();
        }

        private static GameObject CreatePrefabShell(FMeshBakeSettings settings)
        {
            var shell = new GameObject(settings.baseName);
            try
            {
                return PrefabUtility.SaveAsPrefabAsset(shell, settings.PrefabPath);
            }
            finally
            {
                Object.DestroyImmediate(shell);
            }
        }

        private static void CollectAtlasPaths(Material combinedMaterial, List<string> atlasPaths)
        {
            if (combinedMaterial == null || combinedMaterial.shader == null)
                return;

            int propertyCount = ShaderUtil.GetPropertyCount(combinedMaterial.shader);
            for (int i = 0; i < propertyCount; i++)
            {
                if (ShaderUtil.GetPropertyType(combinedMaterial.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                    continue;

                Texture tex = combinedMaterial.GetTexture(ShaderUtil.GetPropertyName(combinedMaterial.shader, i));
                if (tex == null)
                    continue;

                string path = AssetDatabase.GetAssetPath(tex);
                if (!string.IsNullOrEmpty(path) && !atlasPaths.Contains(path))
                    atlasPaths.Add(path);
            }
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent))
                return;

            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        private static void ReportSuccess(FMeshBakeResult result)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabPath);
            if (prefab != null)
                EditorGUIUtility.PingObject(prefab);

            var lines = new List<string>
            {
                $"<color=#4CE04C><b>[Feeder Mesh Modifier]</b></color> Bake completed. Created assets:",
                "  " + result.PrefabPath,
                "  " + result.MeshPath,
                "  " + result.MaterialPath,
                "  " + result.BakeResultsPath
            };
            foreach (string atlasPath in result.AtlasPaths)
                lines.Add("  " + atlasPath);

            Debug.Log(string.Join("\n", lines), prefab);
        }
    }
}
#endif
