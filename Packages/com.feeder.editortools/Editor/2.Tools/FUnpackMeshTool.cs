using System;
using System.Collections.Generic;
using System.IO;
using Sirenix.OdinInspector;
using Sirenix.Serialization;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public sealed class FUnpackMeshTool : FTargetPrefabsToolBase
    {
        private const string CommonFolderName = "_Common";
        private const string ModelsFolderName = "Models";
        private const string MaterialsFolderName = "Materials";
        private const string TexturesFolderName = "Textures";
        private const string AvatarFolderName = "Avatar";

        public enum CollectMode
        {
            [LabelText("Skinned Mesh Only")]
            SkinnedMeshOnly,
            [LabelText("Mesh Filter Only")]
            MeshFilterOnly,
            [LabelText("Both")]
            Both
        }

        public enum NamingMode
        {
            [LabelText("Source Mesh Name")]
            BySourceMesh,
            [LabelText("GameObject Name (+ _Mesh)")]
            ByGameObject
        }

        protected override string GetDescription()
        {
            return "Tách mesh, material, texture (và avatar nếu có SkinnedMeshRenderer) ra thư mục riêng. Sau khi unpack có thể xóa FBX gốc mà scene không thay đổi.";
        }

        [Title("Settings")]
        [LabelText("Save Folder")]
        [FolderPath(AbsolutePath = false, RequireExistingPath = true)]
        [ShowInInspector, OdinSerialize]
        private string saveFolderPath;

        [LabelText("Collect Mode")]
        [ShowInInspector, OdinSerialize]
        private CollectMode collectMode = CollectMode.Both;

        [LabelText("Mesh Naming")]
        [ShowInInspector, OdinSerialize]
        private NamingMode meshNamingMode = NamingMode.BySourceMesh;

        [LabelText("Skip TextMeshPro")]
        [ShowInInspector, OdinSerialize]
        private bool skipTextMeshPro = false;

        [LabelText("Skip Disabled GameObjects")]
        [ShowInInspector, OdinSerialize]
        private bool skipDisabledGameObjects = false;

        [OnInspectorGUI]
        private void DrawGuide()
        {
            GUILayout.Space(2);
            FStylesUtils.DrawInfoBox(
                "TargetPrefabs     root của object cần unpack\n" +
                "Collect Mode      Skinned Only / Mesh Filter Only / Both\n" +
                "Mesh Naming       tên file mesh theo mesh gốc hay theo GameObject\n" +
                "mỗi target → Models, Materials, Textures (+ Avatar nếu có Skinned)\n" +
                "asset dùng chung nhiều target → lưu vào _Common"
            );
            GUILayout.Space(4);
        }

        private struct MeshEntry
        {
            public GameObject GameObject;
            public string TargetRootName;
            public SkinnedMeshRenderer SkinnedRenderer;
            public MeshFilter MeshFilter;
            public MeshRenderer MeshRenderer;
            public Mesh SourceMesh;
            public Material[] SourceMaterials;
            public bool IsSkinned => SkinnedRenderer != null;
        }

        [Button(ButtonSizes.Large), GUIColor(0.3f, 0.8f, 1f)]
        public void Unpack()
        {
            ValidateInput();

            var meshToTargetNames = new Dictionary<Mesh, HashSet<string>>();
            var materialToTargetNames = new Dictionary<Material, HashSet<string>>();
            var textureToTargetNames = new Dictionary<Texture, HashSet<string>>();

            List<MeshEntry> entries = CollectEntries(meshToTargetNames, materialToTargetNames, textureToTargetNames);
            if (entries.Count == 0)
            {
                Debug.LogWarning("[FUnpackMeshTool] No renderers found to unpack.");
                return;
            }

            bool includesSkinned = collectMode != CollectMode.MeshFilterOnly;
            EnsureFolderStructure(CollectAllTargetNames(), includesSkinned);

            // ── Phase 1: textures ──────────────────────────────────────────
            // Create → refresh → load by path (CopyAsset path needs refresh before loading).
            var textureCopyPaths = new Dictionary<string, Texture>(); // newPath → source
            var textureMap = new Dictionary<Texture, Texture>();       // source → new

            AssetDatabase.StartAssetEditing();
            try { CreateAllTextureAssets(textureToTargetNames, textureMap, textureCopyPaths); }
            finally { AssetDatabase.StopAssetEditing(); AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); }

            LoadCopiedTexturesAfterRefresh(textureCopyPaths, textureMap);

            // ── Phase 2: materials ─────────────────────────────────────────
            // Create → refresh → load by path.
            var materialPaths = new Dictionary<Material, string>(); // source → newPath

            AssetDatabase.StartAssetEditing();
            try { CreateAllMaterialAssets(materialToTargetNames, textureMap, materialPaths); }
            finally { AssetDatabase.StopAssetEditing(); AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); }

            var materialMap = LoadMaterialsAfterRefresh(materialPaths);

            // ── Phase 3a: mesh assets ──────────────────────────────────────
            // Create → refresh → load by path.
            var meshPaths = new Dictionary<Mesh, string>(); // source → newPath

            AssetDatabase.StartAssetEditing();
            try { CreateAllMeshAssets(entries, meshToTargetNames, meshPaths); }
            finally { AssetDatabase.StopAssetEditing(); AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); }

            var meshMap = LoadMeshesAfterRefresh(meshPaths);

            // ── Phase 3b: assign to renderers ──────────────────────────────
            // Done OUTSIDE any StartAssetEditing so scene modifications are applied
            // immediately and not deferred — this fixes assignment failures on inactive GOs.
            AssignToRenderers(entries, meshMap, materialMap);
            AssetDatabase.SaveAssets();

            // ── Phase 4: avatars ───────────────────────────────────────────
            if (includesSkinned)
                RebuildAvatarPhase();

            UnityEngine.Object firstCreated = FirstNonNull(meshMap.Values)
                ?? FirstNonNull(materialMap.Values)
                ?? FirstNonNull(textureMap.Values);
            FSelectionUtils.Ping(firstCreated);
            Debug.Log($"<color=green>[FUnpackMeshTool] Done. {entries.Count} renderer(s) unpacked.</color>");
        }

        private static UnityEngine.Object FirstNonNull<T>(IEnumerable<T> items) where T : UnityEngine.Object
        {
            foreach (T item in items)
                if (item != null)
                    return item;
            return null;
        }

        // ══════════════════════════════════════════════════════════════════
        // Collect
        // ══════════════════════════════════════════════════════════════════

        private HashSet<string> CollectAllTargetNames()
        {
            var names = new HashSet<string>();
            foreach (GameObject target in TargetPrefabs)
                if (target != null)
                    names.Add(ValidateAssetName(target.name));
            return names;
        }

        private List<MeshEntry> CollectEntries(
            Dictionary<Mesh, HashSet<string>> meshToTargetNames,
            Dictionary<Material, HashSet<string>> materialToTargetNames,
            Dictionary<Texture, HashSet<string>> textureToTargetNames)
        {
            var entries = new List<MeshEntry>();

            for (int i = 0; i < TargetPrefabs.Count; i++)
            {
                GameObject target = TargetPrefabs[i];
                if (target == null)
                {
                    Debug.LogWarning($"[FUnpackMeshTool] Skipping null at TargetPrefabs[{i}].");
                    continue;
                }

                if (PrefabUtility.IsPartOfPrefabAsset(target))
                    throw new InvalidOperationException($"prefab asset is not supported: {target.name}");

                string targetName = ValidateAssetName(target.name);

                if (collectMode == CollectMode.SkinnedMeshOnly || collectMode == CollectMode.Both)
                    CollectSkinnedEntries(target, targetName, entries, meshToTargetNames, materialToTargetNames, textureToTargetNames);

                if (collectMode == CollectMode.MeshFilterOnly || collectMode == CollectMode.Both)
                    CollectMeshFilterEntries(target, targetName, entries, meshToTargetNames, materialToTargetNames, textureToTargetNames);
            }

            return entries;
        }

        private void CollectSkinnedEntries(GameObject target, string targetName, List<MeshEntry> entries,
            Dictionary<Mesh, HashSet<string>> meshToTargetNames,
            Dictionary<Material, HashSet<string>> materialToTargetNames,
            Dictionary<Texture, HashSet<string>> textureToTargetNames)
        {
            foreach (SkinnedMeshRenderer smr in target.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                GameObject child = smr.gameObject;
                if (skipDisabledGameObjects && !child.activeInHierarchy) continue;
                if (skipTextMeshPro && HasTextMeshProComponent(child)) continue;

                Mesh sourceMesh = smr.sharedMesh;
                if (sourceMesh == null) continue;

                Material[] sourceMaterials = smr.sharedMaterials;
                if (sourceMaterials == null || sourceMaterials.Length == 0) continue;

                AddToSet(meshToTargetNames, sourceMesh, targetName);
                foreach (Material mat in sourceMaterials)
                {
                    if (mat == null) continue;
                    AddToSet(materialToTargetNames, mat, targetName);
                    CollectTexturesFromMaterial(mat, textureToTargetNames, targetName);
                }

                entries.Add(new MeshEntry
                {
                    GameObject = child,
                    TargetRootName = targetName,
                    SkinnedRenderer = smr,
                    SourceMesh = sourceMesh,
                    SourceMaterials = sourceMaterials
                });
            }
        }

        private void CollectMeshFilterEntries(GameObject target, string targetName, List<MeshEntry> entries,
            Dictionary<Mesh, HashSet<string>> meshToTargetNames,
            Dictionary<Material, HashSet<string>> materialToTargetNames,
            Dictionary<Texture, HashSet<string>> textureToTargetNames)
        {
            foreach (MeshFilter meshFilter in target.GetComponentsInChildren<MeshFilter>(true))
            {
                GameObject child = meshFilter.gameObject;
                if (skipDisabledGameObjects && !child.activeInHierarchy) continue;
                if (skipTextMeshPro && HasTextMeshProComponent(child)) continue;

                MeshRenderer meshRenderer = child.GetComponent<MeshRenderer>();
                if (meshRenderer == null) continue;

                Mesh sourceMesh = meshFilter.sharedMesh;
                if (sourceMesh == null) continue;

                Material[] sourceMaterials = meshRenderer.sharedMaterials;
                if (sourceMaterials == null || sourceMaterials.Length == 0) continue;

                AddToSet(meshToTargetNames, sourceMesh, targetName);
                foreach (Material mat in sourceMaterials)
                {
                    if (mat == null) continue;
                    AddToSet(materialToTargetNames, mat, targetName);
                    CollectTexturesFromMaterial(mat, textureToTargetNames, targetName);
                }

                entries.Add(new MeshEntry
                {
                    GameObject = child,
                    TargetRootName = targetName,
                    MeshFilter = meshFilter,
                    MeshRenderer = meshRenderer,
                    SourceMesh = sourceMesh,
                    SourceMaterials = sourceMaterials
                });
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Phase 1 – textures
        // ══════════════════════════════════════════════════════════════════

        // Fills textureMap immediately for CreateAsset path; fills textureCopyPaths for CopyAsset
        // path (objects not available until after Refresh).
        private void CreateAllTextureAssets(
            Dictionary<Texture, HashSet<string>> textureToTargetNames,
            Dictionary<Texture, Texture> textureMap,
            Dictionary<string, Texture> textureCopyPaths)
        {
            foreach (Texture sourceTexture in textureToTargetNames.Keys)
            {
                if (!(sourceTexture is Texture2D sourceTex2D))
                {
                    Debug.LogWarning($"[FUnpackMeshTool] Skipping non-Texture2D: {sourceTexture.name}");
                    continue;
                }

                HashSet<string> targetNames = textureToTargetNames[sourceTexture];
                string firstTarget = GetFirstTargetName(targetNames);
                string folder = GetAssetFolder(firstTarget, targetNames, TexturesFolderName);
                string assetName = ValidateAssetName(string.IsNullOrWhiteSpace(sourceTex2D.name) ? "Texture" : sourceTex2D.name);

                string sourcePath = AssetDatabase.GetAssetPath(sourceTex2D);
                bool isMainAsset = !string.IsNullOrEmpty(sourcePath)
                    && AssetDatabase.LoadAssetAtPath<Texture2D>(sourcePath) == sourceTex2D;

                if (isMainAsset)
                {
                    string ext = Path.GetExtension(sourcePath);
                    string destPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{assetName}{ext}");
                    if (AssetDatabase.CopyAsset(sourcePath, destPath))
                    {
                        textureCopyPaths[destPath] = sourceTexture;
                        continue;
                    }
                }

                Texture2D duplicated = CopyTexture2DViaRenderTexture(sourceTex2D);
                if (duplicated == null) continue;
                duplicated.name = assetName;
                string pngPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{assetName}.png");
                AssetDatabase.CreateAsset(duplicated, pngPath);
                textureMap[sourceTexture] = duplicated;
            }
        }

        private static void LoadCopiedTexturesAfterRefresh(
            Dictionary<string, Texture> textureCopyPaths,
            Dictionary<Texture, Texture> textureMap)
        {
            foreach (KeyValuePair<string, Texture> kv in textureCopyPaths)
            {
                Texture2D loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(kv.Key);
                if (loaded == null)
                {
                    Debug.LogWarning($"[FUnpackMeshTool] Failed to load texture after refresh: {kv.Key}");
                    continue;
                }
                textureMap[kv.Value] = loaded;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Phase 2 – materials
        // ══════════════════════════════════════════════════════════════════

        private void CreateAllMaterialAssets(
            Dictionary<Material, HashSet<string>> materialToTargetNames,
            Dictionary<Texture, Texture> textureMap,
            Dictionary<Material, string> materialPaths)
        {
            foreach (Material sourceMaterial in materialToTargetNames.Keys)
            {
                HashSet<string> targetNames = materialToTargetNames[sourceMaterial];
                string firstTarget = GetFirstTargetName(targetNames);
                string folder = GetAssetFolder(firstTarget, targetNames, MaterialsFolderName);
                string assetName = BuildAssetNameForMaterial(sourceMaterial.name, firstTarget);

                Material newMat = new Material(sourceMaterial) { name = assetName };

                string[] texPropNames = sourceMaterial.GetTexturePropertyNames();
                if (texPropNames != null)
                {
                    foreach (string propName in texPropNames)
                    {
                        Texture srcTex = sourceMaterial.GetTexture(propName);
                        if (srcTex == null) continue;
                        if (textureMap.TryGetValue(srcTex, out Texture newTex) && newTex != null)
                            newMat.SetTexture(propName, newTex);
                    }
                }

                string matPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{assetName}.mat");
                AssetDatabase.CreateAsset(newMat, matPath);
                materialPaths[sourceMaterial] = matPath;
            }
        }

        private static Dictionary<Material, Material> LoadMaterialsAfterRefresh(Dictionary<Material, string> materialPaths)
        {
            var map = new Dictionary<Material, Material>();
            foreach (KeyValuePair<Material, string> kv in materialPaths)
            {
                Material loaded = AssetDatabase.LoadAssetAtPath<Material>(kv.Value);
                if (loaded == null)
                    Debug.LogWarning($"[FUnpackMeshTool] Failed to load material after refresh: {kv.Value}");
                else
                    map[kv.Key] = loaded;
            }
            return map;
        }

        // ══════════════════════════════════════════════════════════════════
        // Phase 3a – mesh assets
        // ══════════════════════════════════════════════════════════════════

        private void CreateAllMeshAssets(
            List<MeshEntry> entries,
            Dictionary<Mesh, HashSet<string>> meshToTargetNames,
            Dictionary<Mesh, string> meshPaths)
        {
            var processedSourceMeshes = new HashSet<Mesh>();
            foreach (MeshEntry entry in entries)
            {
                if (!processedSourceMeshes.Add(entry.SourceMesh)) continue;

                HashSet<string> targetNames = meshToTargetNames[entry.SourceMesh];
                string firstTarget = GetFirstTargetName(targetNames);
                string folder = GetAssetFolder(firstTarget, targetNames, ModelsFolderName);
                string assetName = GetMeshAssetName(entry.GameObject, entry.SourceMesh);

                Mesh meshCopy = CopyMesh(entry.SourceMesh);
                meshCopy.name = assetName;

                string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{assetName}.asset");
                AssetDatabase.CreateAsset(meshCopy, assetPath);
                meshPaths[entry.SourceMesh] = assetPath;
            }
        }

        private static Dictionary<Mesh, Mesh> LoadMeshesAfterRefresh(Dictionary<Mesh, string> meshPaths)
        {
            var map = new Dictionary<Mesh, Mesh>();
            foreach (KeyValuePair<Mesh, string> kv in meshPaths)
            {
                Mesh loaded = AssetDatabase.LoadAssetAtPath<Mesh>(kv.Value);
                if (loaded == null)
                    Debug.LogWarning($"[FUnpackMeshTool] Failed to load mesh after refresh: {kv.Value}");
                else
                    map[kv.Key] = loaded;
            }
            return map;
        }

        // ══════════════════════════════════════════════════════════════════
        // Phase 3b – assign (outside any asset-editing batch)
        // ══════════════════════════════════════════════════════════════════

        private void AssignToRenderers(
            List<MeshEntry> entries,
            Dictionary<Mesh, Mesh> meshMap,
            Dictionary<Material, Material> materialMap)
        {
            // Key by the specific renderer component so a GO with both SMR + MeshFilter works.
            var processedRenderers = new HashSet<Component>();

            foreach (MeshEntry entry in entries)
            {
                Component key = entry.IsSkinned ? (Component)entry.SkinnedRenderer : entry.MeshFilter;
                if (!processedRenderers.Add(key)) continue;

                if (!meshMap.TryGetValue(entry.SourceMesh, out Mesh newMesh))
                {
                    Debug.LogWarning($"[FUnpackMeshTool] No new mesh for '{entry.GameObject.name}', skipping assignment.");
                    continue;
                }

                Material[] newMaterials = GetNewMaterialsForEntry(entry.SourceMaterials, materialMap);

                if (entry.IsSkinned)
                {
                    Undo.RecordObject(entry.SkinnedRenderer, "Unpack Mesh");
                    entry.SkinnedRenderer.sharedMesh = newMesh;
                    entry.SkinnedRenderer.sharedMaterials = newMaterials;
                    if (PrefabUtility.IsPartOfPrefabInstance(entry.GameObject))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(entry.SkinnedRenderer);
                    EditorUtility.SetDirty(entry.SkinnedRenderer);
                }
                else
                {
                    Undo.RecordObject(entry.MeshFilter, "Unpack Mesh");
                    Undo.RecordObject(entry.MeshRenderer, "Unpack Mesh");
                    entry.MeshFilter.sharedMesh = newMesh;
                    entry.MeshRenderer.sharedMaterials = newMaterials;
                    if (PrefabUtility.IsPartOfPrefabInstance(entry.GameObject))
                    {
                        PrefabUtility.RecordPrefabInstancePropertyModifications(entry.MeshFilter);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(entry.MeshRenderer);
                    }
                    EditorUtility.SetDirty(entry.MeshFilter);
                    EditorUtility.SetDirty(entry.MeshRenderer);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Phase 4 – avatars
        // ══════════════════════════════════════════════════════════════════

        private void RebuildAvatarPhase()
        {
            // Build avatars outside any batch (AvatarBuilder reads the live scene hierarchy).
            var builtAvatars = new Dictionary<GameObject, (Avatar avatar, string targetName)>();
            foreach (GameObject target in TargetPrefabs)
            {
                if (target == null) continue;
                Avatar built = BuildAvatar(target);
                if (built != null)
                    builtAvatars[target] = (built, ValidateAssetName(target.name));
            }
            if (builtAvatars.Count == 0) return;

            // Save avatar assets in a batch.
            var avatarPaths = new Dictionary<GameObject, string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (KeyValuePair<GameObject, (Avatar avatar, string targetName)> kv in builtAvatars)
                {
                    string avatarName = $"{kv.Value.targetName}_Avatar";
                    string folder = $"{saveFolderPath}/{kv.Value.targetName}/{AvatarFolderName}";
                    string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{avatarName}.asset");
                    kv.Value.avatar.name = avatarName;
                    AssetDatabase.CreateAsset(kv.Value.avatar, assetPath);
                    avatarPaths[kv.Key] = assetPath;
                }
            }
            finally { AssetDatabase.StopAssetEditing(); AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); }

            // Load and assign outside any batch.
            foreach (KeyValuePair<GameObject, string> kv in avatarPaths)
            {
                Avatar loaded = AssetDatabase.LoadAssetAtPath<Avatar>(kv.Value);
                if (loaded == null)
                {
                    Debug.LogWarning($"[FUnpackMeshTool] Failed to load avatar: {kv.Value}");
                    continue;
                }
                AssignAvatar(kv.Key, loaded);
            }
        }

        private static Avatar BuildAvatar(GameObject target)
        {
            Animator animator = target.GetComponent<Animator>() ?? target.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null)
            {
                Debug.LogWarning($"[FUnpackMeshTool] No Animator/Avatar on '{target.name}', skipping avatar.");
                return null;
            }

            Avatar source = animator.avatar;
            Avatar built = source.isHuman
                ? AvatarBuilder.BuildHumanAvatar(animator.gameObject, source.humanDescription)
                : AvatarBuilder.BuildGenericAvatar(animator.gameObject, "");

            if (built == null || !built.isValid)
            {
                Debug.LogWarning($"[FUnpackMeshTool] AvatarBuilder failed for '{target.name}'.");
                return null;
            }

            return built;
        }

        private static void AssignAvatar(GameObject target, Avatar newAvatar)
        {
            Animator animator = target.GetComponent<Animator>() ?? target.GetComponentInChildren<Animator>(true);
            if (animator == null) return;

            Undo.RecordObject(animator, "Unpack Mesh Avatar");
            animator.avatar = newAvatar;
            if (PrefabUtility.IsPartOfPrefabInstance(animator.gameObject))
                PrefabUtility.RecordPrefabInstancePropertyModifications(animator);
            EditorUtility.SetDirty(animator);
        }

        // ══════════════════════════════════════════════════════════════════
        // Mesh copy
        // ══════════════════════════════════════════════════════════════════

        private string GetMeshAssetName(GameObject contextGo, Mesh sourceMesh)
        {
            if (meshNamingMode == NamingMode.ByGameObject)
                return ValidateAssetName($"{contextGo.name}_Mesh");

            string name = string.IsNullOrWhiteSpace(sourceMesh.name) ? "Mesh" : sourceMesh.name;
            return ValidateAssetName(name);
        }

        // Works for both skinned (blend shapes, bindposes, boneWeights) and non-skinned meshes.
        private static Mesh CopyMesh(Mesh source)
        {
            if (source == null)
                throw new InvalidOperationException("source mesh is null.");

            Mesh mesh = new Mesh { indexFormat = source.indexFormat };
            mesh.vertices = source.vertices;
            mesh.normals = source.normals;
            mesh.tangents = source.tangents;
            mesh.colors = source.colors;
            mesh.uv = source.uv;
            mesh.uv2 = source.uv2;
            mesh.uv3 = source.uv3;
            mesh.uv4 = source.uv4;
            mesh.bindposes = source.bindposes;
            mesh.boneWeights = source.boneWeights;
            mesh.subMeshCount = source.subMeshCount;

            for (int i = 0; i < source.subMeshCount; i++)
                mesh.SetTriangles(source.GetTriangles(i), i);

            if (source.blendShapeCount > 0)
            {
                var deltaV = new Vector3[source.vertexCount];
                var deltaN = new Vector3[source.vertexCount];
                var deltaT = new Vector3[source.vertexCount];
                for (int s = 0; s < source.blendShapeCount; s++)
                {
                    string shapeName = source.GetBlendShapeName(s);
                    for (int f = 0; f < source.GetBlendShapeFrameCount(s); f++)
                    {
                        float weight = source.GetBlendShapeFrameWeight(s, f);
                        source.GetBlendShapeFrameVertices(s, f, deltaV, deltaN, deltaT);
                        mesh.AddBlendShapeFrame(shapeName, weight, deltaV, deltaN, deltaT);
                    }
                }
            }

            mesh.RecalculateBounds();
            return mesh;
        }

        // ══════════════════════════════════════════════════════════════════
        // Material helpers
        // ══════════════════════════════════════════════════════════════════

        private static Material[] GetNewMaterialsForEntry(Material[] sourceMaterials, Dictionary<Material, Material> materialMap)
        {
            Material[] result = new Material[sourceMaterials.Length];
            for (int i = 0; i < sourceMaterials.Length; i++)
            {
                Material src = sourceMaterials[i];
                result[i] = src != null && materialMap.TryGetValue(src, out Material newMat) ? newMat : null;
            }
            return result;
        }

        // ══════════════════════════════════════════════════════════════════
        // Texture helpers
        // ══════════════════════════════════════════════════════════════════

        private static Texture2D CopyTexture2DViaRenderTexture(Texture2D source)
        {
            int w = source.width, h = source.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            Texture2D result = new Texture2D(w, h, TextureFormat.RGBA32, false);
            result.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            result.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            return result;
        }

        // ══════════════════════════════════════════════════════════════════
        // Folder structure
        // ══════════════════════════════════════════════════════════════════

        private void EnsureFolderStructure(HashSet<string> allTargetNames, bool includeAvatar)
        {
            EnsureFolder(saveFolderPath, CommonFolderName);
            string commonPath = $"{saveFolderPath}/{CommonFolderName}";
            EnsureFolder(commonPath, ModelsFolderName);
            EnsureFolder(commonPath, MaterialsFolderName);
            EnsureFolder(commonPath, TexturesFolderName);

            foreach (string targetName in allTargetNames)
            {
                EnsureFolder(saveFolderPath, targetName);
                string targetPath = $"{saveFolderPath}/{targetName}";
                EnsureFolder(targetPath, ModelsFolderName);
                EnsureFolder(targetPath, MaterialsFolderName);
                EnsureFolder(targetPath, TexturesFolderName);
                if (includeAvatar)
                    EnsureFolder(targetPath, AvatarFolderName);
            }
        }

        private static void EnsureFolder(string parentPath, string folderName)
        {
            string childPath = $"{parentPath}/{folderName}";
            if (AssetDatabase.IsValidFolder(childPath)) return;
            string guid = AssetDatabase.CreateFolder(parentPath, folderName);
            if (string.IsNullOrEmpty(guid))
                throw new InvalidOperationException($"failed to create folder: {childPath}");
        }

        // ══════════════════════════════════════════════════════════════════
        // Generic helpers
        // ══════════════════════════════════════════════════════════════════

        private static void CollectTexturesFromMaterial(Material material,
            Dictionary<Texture, HashSet<string>> textureToTargetNames, string targetName)
        {
            string[] propNames = material.GetTexturePropertyNames();
            if (propNames == null) return;
            foreach (string propName in propNames)
            {
                Texture tex = material.GetTexture(propName);
                if (tex == null) continue;
                AddToSet(textureToTargetNames, tex, targetName);
            }
        }

        private static void AddToSet<T>(Dictionary<T, HashSet<string>> map, T key, string targetName)
            where T : UnityEngine.Object
        {
            if (key == null) return;
            if (!map.TryGetValue(key, out HashSet<string> set))
            {
                set = new HashSet<string>();
                map.Add(key, set);
            }
            set.Add(targetName);
        }

        private string GetAssetFolder(string assetTargetName, HashSet<string> targetNames, string subFolder)
        {
            if (targetNames.Count > 1)
                return $"{saveFolderPath}/{CommonFolderName}/{subFolder}";
            return $"{saveFolderPath}/{assetTargetName}/{subFolder}";
        }

        private static string GetFirstTargetName(HashSet<string> targetNames)
        {
            foreach (string n in targetNames) return n;
            throw new InvalidOperationException("target names set is empty.");
        }

        private static bool HasTextMeshProComponent(GameObject go)
        {
            if (go == null) return false;
            return go.GetComponent<TextMeshProUGUI>() != null || go.GetComponent<TextMeshPro>() != null;
        }

        private void ValidateInput()
        {
            if (TargetPrefabs == null || TargetPrefabs.Count == 0)
                throw new InvalidOperationException("target objects is empty.");
            if (string.IsNullOrWhiteSpace(saveFolderPath) || !AssetDatabase.IsValidFolder(saveFolderPath))
                throw new InvalidOperationException("save folder path is invalid.");
        }

        private static string BuildAssetNameForMaterial(string sourceName, string contextName)
        {
            string baseName = ValidateAssetName(string.IsNullOrWhiteSpace(sourceName) ? "Material" : sourceName);
            return $"{ValidateAssetName(contextName)}_{baseName}";
        }

        private static string ValidateAssetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Unknown";
            foreach (char ch in Path.GetInvalidFileNameChars())
                name = name.Replace(ch, '_');
            name = name.Trim();
            return string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
        }
    }
}
