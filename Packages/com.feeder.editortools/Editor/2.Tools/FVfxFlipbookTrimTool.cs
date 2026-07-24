using System;
using System.Collections.Generic;
using System.IO;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Cắt tile thừa trong flipbook/atlas của VFX particle để giảm size build.
    /// Mô hình an toàn: mỗi VFX một atlas trimmed riêng (clone texture + material), KHÔNG sửa asset gốc.
    /// </summary>
    public sealed class FVfxFlipbookTrimTool : FTargetPrefabsToolBase
    {
        private const int CurveSampleCount = 256;
        private const float PreviewMaxWidth = 320f;

        // Base Map property names: URP = _BaseMap, Built-in = _MainTex
        private static readonly string[] BaseMapPropertyNames = { "_BaseMap", "_MainTex" };
        private static readonly string[] OverridePlatformNames = { "Standalone", "Android", "iPhone", "WebGL" };

        protected override string GetDescription()
        {
            return "Quét TargetPrefabs (prefab VFX) tìm atlas flipbook mà Texture Sheet Animation chỉ dùng một phần, " +
                   "repack lại atlas chỉ chứa tile cần thiết rồi tự bù numTiles + frameOverTime để VFX chạy y hệt. " +
                   "Texture/material gốc giữ nguyên, tool sinh bản copy riêng cho VFX này.";
        }

        private const string PrefsOwner = nameof(FVfxFlipbookTrimTool);

        [Title("Settings")]
        [LabelText("Check Tile Content (alpha/luma)")]
        [ShowInInspector]
        private bool checkTileContent
        {
            get => FToolPrefs.GetBool(PrefsOwner, nameof(checkTileContent), true);
            set => FToolPrefs.SetBool(PrefsOwner, nameof(checkTileContent), value);
        }

        [LabelText("Alpha Threshold"), PropertyRange(0f, 0.2f)]
        [ShowInInspector]
        private float alphaThreshold
        {
            get => FToolPrefs.GetFloat(PrefsOwner, nameof(alphaThreshold), 0.004f);
            set => FToolPrefs.SetFloat(PrefsOwner, nameof(alphaThreshold), value);
        }

        [LabelText("Luma Threshold"), PropertyRange(0f, 0.2f)]
        [ShowInInspector]
        private float lumaThreshold
        {
            get => FToolPrefs.GetFloat(PrefsOwner, nameof(lumaThreshold), 0.004f);
            set => FToolPrefs.SetFloat(PrefsOwner, nameof(lumaThreshold), value);
        }

        [LabelText("Scan Project For Other Consumers")]
        [ShowInInspector]
        private bool scanProjectConsumers
        {
            get => FToolPrefs.GetBool(PrefsOwner, nameof(scanProjectConsumers), true);
            set => FToolPrefs.SetBool(PrefsOwner, nameof(scanProjectConsumers), value);
        }

        [LabelText("Output Subfolder")]
        [ShowInInspector]
        private string outputSubfolder
        {
            get => FToolPrefs.GetString(PrefsOwner, nameof(outputSubfolder), "_Trim");
            set => FToolPrefs.SetString(PrefsOwner, nameof(outputSubfolder), value);
        }

        [LabelText("Asset Suffix")]
        [ShowInInspector]
        private string assetSuffix
        {
            get => FToolPrefs.GetString(PrefsOwner, nameof(assetSuffix), "_trim");
            set => FToolPrefs.SetString(PrefsOwner, nameof(assetSuffix), value);
        }

        private List<TrimPlan> _plans;

        [OnInspectorGUI]
        private void DrawGuide()
        {
            GUILayout.Space(2);
            FStylesUtils.DrawInfoBox(
                "TargetPrefabs    prefab VFX (asset hoặc instance trong scene)\n" +
                "Analyze          chỉ đọc: tính tile nào được TSA dùng, vẽ lưới xanh/đỏ\n" +
                "Click ô đỏ       giữ lại tile đó thủ công (ô reachable bị khóa)\n" +
                "Apply            sinh texture + material _trim, trỏ prefab sang bản mới"
            );
            GUILayout.Space(4);
        }

        [Button(ButtonSizes.Large), GUIColor(0.3f, 0.8f, 1f)]
        public void Analyze()
        {
            _plans = null;
            if (TargetPrefabs == null || TargetPrefabs.Count == 0)
            {
                Debug.LogWarning("[FVfxFlipbookTrimTool] Add at least one TargetPrefab.");
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar("VFX Flipbook Trim", "Collecting particle systems...", 0.1f);
                _plans = CollectPlans();

                if (checkTileContent)
                {
                    for (int i = 0; i < _plans.Count; i++)
                    {
                        EditorUtility.DisplayProgressBar("VFX Flipbook Trim",
                            $"Checking tile content: {_plans[i].Source.name}", 0.3f + 0.4f * i / Mathf.Max(1, _plans.Count));
                        ComputeTileContent(_plans[i]);
                    }
                }

                if (scanProjectConsumers)
                    ScanExternalConsumers(_plans);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            int trimmable = 0;
            foreach (TrimPlan plan in _plans)
            {
                if (plan.CanTrim && plan.KeepCount < plan.TotalFrames)
                    trimmable++;
            }
            Debug.Log($"<color=green>[FVfxFlipbookTrimTool] Found {_plans.Count} atlas texture(s), {trimmable} trimmable.</color>");
        }

        // ---------------------------------------------------------------- Collect

        private List<TrimPlan> CollectPlans()
        {
            Dictionary<Texture2D, TrimPlan> planByTexture = new Dictionary<Texture2D, TrimPlan>();

            for (int i = 0; i < TargetPrefabs.Count; i++)
            {
                GameObject root = TargetPrefabs[i];
                if (root == null)
                {
                    Debug.LogWarning($"[FVfxFlipbookTrimTool] Skipping null at TargetPrefabs[{i}].");
                    continue;
                }

                ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
                foreach (ParticleSystem ps in systems)
                {
                    ParticleSystemRenderer psr = ps.GetComponent<ParticleSystemRenderer>();
                    if (psr == null)
                        continue;

                    Material mat = psr.sharedMaterial;
                    Texture2D tex = GetBaseMapTexture(mat, out string propName);
                    if (tex != null)
                    {
                        TrimPlan plan = GetOrCreatePlan(planByTexture, tex);
                        RegisterSystem(plan, ps, mat, propName, root);
                    }

                    // Trail sample nguyên texture (không qua TSA grid) → texture của trail material không cắt được
                    if (ps.trails.enabled)
                    {
                        Material trailMat = psr.trailMaterial != null ? psr.trailMaterial : mat;
                        Texture2D trailTex = GetBaseMapTexture(trailMat, out _);
                        if (trailTex != null)
                        {
                            TrimPlan trailPlan = GetOrCreatePlan(planByTexture, trailTex);
                            Block(trailPlan, $"{GetPath(ps.transform)}: trails bật, trail material dùng nguyên texture");
                            AddUnique(trailPlan.Roots, root);
                        }
                    }
                }
            }

            // Pass 2 (sau khi đã gom đủ plan từ mọi root):
            // renderer thường (mesh, sprite...) dùng material trỏ vào atlas → atlas đó không cắt được
            foreach (GameObject root in TargetPrefabs)
            {
                if (root == null)
                    continue;
                Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer r in renderers)
                {
                    if (r is ParticleSystemRenderer)
                        continue;
                    Material[] mats = r.sharedMaterials;
                    if (mats == null)
                        continue;
                    foreach (Material m in mats)
                    {
                        Texture2D tex = GetBaseMapTexture(m, out _);
                        if (tex != null && planByTexture.TryGetValue(tex, out TrimPlan plan))
                            Block(plan, $"{GetPath(r.transform)}: renderer thường (không phải particle) dùng texture này");
                    }
                }
            }

            List<TrimPlan> plans = new List<TrimPlan>(planByTexture.Values);

            foreach (TrimPlan plan in plans)
                FinalizePlan(plan);

            plans.Sort((a, b) => b.EstimatedSavedBytes.CompareTo(a.EstimatedSavedBytes));
            return plans;
        }

        private static TrimPlan GetOrCreatePlan(Dictionary<Texture2D, TrimPlan> map, Texture2D tex)
        {
            if (!map.TryGetValue(tex, out TrimPlan plan))
            {
                plan = new TrimPlan
                {
                    Source = tex,
                    SourcePath = AssetDatabase.GetAssetPath(tex),
                };
                map.Add(tex, plan);
            }
            return plan;
        }

        private void RegisterSystem(TrimPlan plan, ParticleSystem ps, Material mat, string propName, GameObject root)
        {
            AddUnique(plan.Roots, root);
            plan.MaterialProps[mat] = propName;

            string sysPath = GetPath(ps.transform);
            var tsa = ps.textureSheetAnimation;

            if (!tsa.enabled)
            {
                Block(plan, $"{sysPath}: TSA tắt → particle dùng nguyên texture");
                return;
            }
            if (tsa.mode != ParticleSystemAnimationMode.Grid)
            {
                Block(plan, $"{sysPath}: TSA mode Sprites chưa hỗ trợ");
                return;
            }
            if (tsa.animation != ParticleSystemAnimationType.WholeSheet)
            {
                Block(plan, $"{sysPath}: animationType SingleRow chưa hỗ trợ");
                return;
            }
            if (tsa.timeMode != ParticleSystemAnimationTimeMode.Lifetime)
            {
                Block(plan, $"{sysPath}: timeMode {tsa.timeMode} chưa hỗ trợ (chỉ Lifetime)");
                return;
            }

            GetValueRange(tsa.startFrame, out float startMin, out float startMax);
            if (Mathf.Abs(startMin) > 1e-4f || Mathf.Abs(startMax) > 1e-4f)
            {
                Block(plan, $"{sysPath}: startFrame != 0 chưa hỗ trợ");
                return;
            }

            if (plan.TilesX == 0)
            {
                plan.TilesX = tsa.numTilesX;
                plan.TilesY = tsa.numTilesY;
            }
            else if (plan.TilesX != tsa.numTilesX || plan.TilesY != tsa.numTilesY)
            {
                Block(plan, $"{sysPath}: lưới {tsa.numTilesX}x{tsa.numTilesY} khác hệ trước ({plan.TilesX}x{plan.TilesY})");
                return;
            }

            // Tiling/offset khác mặc định → UV không còn map 1:1 vào tile
            Vector2 scale = mat.GetTextureScale(propName);
            Vector2 offset = mat.GetTextureOffset(propName);
            if (scale != Vector2.one || offset != Vector2.zero)
            {
                Block(plan, $"{sysPath}: material '{mat.name}' có tiling/offset khác mặc định");
                return;
            }

            int total = plan.TilesX * plan.TilesY;
            GetValueRange(tsa.frameOverTime, out float vMin, out float vMax);
            if (vMin < -1e-3f || vMax > 1.001f)
            {
                Block(plan, $"{sysPath}: frameOverTime ngoài [0,1] ({vMin:0.###}..{vMax:0.###})");
                return;
            }

            int minFrame = ClampFrame(vMin, total);
            int maxFrame = ClampFrame(vMax, total);
            plan.Systems.Add(new SystemUsage
            {
                Path = sysPath,
                MinFrame = minFrame,
                MaxFrame = maxFrame,
                IsConstantPick = tsa.frameOverTime.mode == ParticleSystemCurveMode.Constant,
            });
        }

        private void FinalizePlan(TrimPlan plan)
        {
            if (plan.TilesX <= 0 || plan.TilesY <= 0)
            {
                Block(plan, "Không có particle system hợp lệ dùng texture này qua TSA Grid.");
                plan.TilesX = Mathf.Max(plan.TilesX, 1);
                plan.TilesY = Mathf.Max(plan.TilesY, 1);
            }

            if (plan.TilesX * plan.TilesY <= 1)
                Block(plan, "Lưới 1x1 → không có gì để cắt.");

            if (plan.Source.width % plan.TilesX != 0 || plan.Source.height % plan.TilesY != 0)
                Block(plan, $"Kích thước texture {plan.Source.width}x{plan.Source.height} không chia hết cho lưới {plan.TilesX}x{plan.TilesY}.");

            plan.TotalFrames = plan.TilesX * plan.TilesY;
            plan.TileW = plan.Source.width / plan.TilesX;
            plan.TileH = plan.Source.height / plan.TilesY;
            plan.Reachable = new bool[plan.TotalFrames];

            foreach (SystemUsage sys in plan.Systems)
            {
                // Curve có thể không đơn điệu → fill cả dải [min..max] để giữ tính liên tục khi remap
                for (int f = sys.MinFrame; f <= sys.MaxFrame && f < plan.TotalFrames; f++)
                    plan.Reachable[f] = true;
            }

            plan.Keep = (bool[])plan.Reachable.Clone();

            if (!string.IsNullOrEmpty(plan.SourcePath))
            {
                FileInfo fi = new FileInfo(plan.SourcePath);
                if (fi.Exists)
                    plan.SourceFileBytes = fi.Length;
            }
        }

        // ---------------------------------------------------------------- Content check

        private void ComputeTileContent(TrimPlan plan)
        {
            if (!plan.CanTrim)
                return;

            Color32[] pixels = ReadTexturePixels(plan.Source, plan.SourcePath);
            if (pixels == null)
                return;

            plan.HasContent = new bool[plan.TotalFrames];
            byte alphaT = (byte)Mathf.RoundToInt(alphaThreshold * 255f);
            byte lumaT = (byte)Mathf.RoundToInt(lumaThreshold * 255f);

            int w = plan.Source.width;
            for (int frame = 0; frame < plan.TotalFrames; frame++)
            {
                GetTilePixelOrigin(frame, plan.TilesX, plan.TilesY, plan.TileW, plan.TileH, out int x0, out int y0);
                bool has = false;
                for (int row = 0; row < plan.TileH && !has; row++)
                {
                    int start = (y0 + row) * w + x0;
                    for (int col = 0; col < plan.TileW; col++)
                    {
                        Color32 c = pixels[start + col];
                        byte luma = Math.Max(c.r, Math.Max(c.g, c.b));
                        if (c.a > alphaT || luma > lumaT)
                        {
                            has = true;
                            break;
                        }
                    }
                }
                plan.HasContent[frame] = has;
            }
        }

        // ---------------------------------------------------------------- External consumers (informational)

        private void ScanExternalConsumers(List<TrimPlan> plans)
        {
            if (plans == null || plans.Count == 0)
                return;

            Dictionary<string, TrimPlan> planByTexPath = new Dictionary<string, TrimPlan>();
            foreach (TrimPlan plan in plans)
            {
                if (!string.IsNullOrEmpty(plan.SourcePath))
                    planByTexPath[plan.SourcePath] = plan;
            }

            HashSet<string> targetPaths = new HashSet<string>();
            foreach (TrimPlan plan in plans)
            {
                foreach (GameObject root in plan.Roots)
                {
                    string p = AssetDatabase.GetAssetPath(root);
                    if (!string.IsNullOrEmpty(p))
                        targetPaths.Add(p);
                }
            }

            // Material nào trong project trỏ vào texture của plan
            Dictionary<string, TrimPlan> planByMatPath = new Dictionary<string, TrimPlan>();
            string[] matGuids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
            for (int i = 0; i < matGuids.Length; i++)
            {
                string matPath = AssetDatabase.GUIDToAssetPath(matGuids[i]);
                if (i % 50 == 0)
                    EditorUtility.DisplayProgressBar("VFX Flipbook Trim", $"Scanning materials ({i}/{matGuids.Length})", 0.7f + 0.15f * i / matGuids.Length);

                string[] deps = AssetDatabase.GetDependencies(matPath, false);
                foreach (string dep in deps)
                {
                    if (!planByTexPath.TryGetValue(dep, out TrimPlan plan))
                        continue;
                    Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                    if (mat != null && plan.MaterialProps.ContainsKey(mat))
                        planByMatPath[matPath] = plan;
                    else
                        AddUnique(plan.ExternalConsumers, $"{matPath} (material khác cũng dùng texture — bản gốc giữ nguyên)");
                }
            }

            // Prefab/scene nào ngoài TargetPrefabs trỏ vào các material đó
            List<string> containerPaths = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
                containerPaths.Add(AssetDatabase.GUIDToAssetPath(guid));
            foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
                containerPaths.Add(AssetDatabase.GUIDToAssetPath(guid));

            for (int i = 0; i < containerPaths.Count; i++)
            {
                string path = containerPaths[i];
                if (targetPaths.Contains(path))
                    continue;
                if (i % 50 == 0)
                    EditorUtility.DisplayProgressBar("VFX Flipbook Trim", $"Scanning prefabs/scenes ({i}/{containerPaths.Count})", 0.85f + 0.15f * i / containerPaths.Count);

                string[] deps = AssetDatabase.GetDependencies(path, false);
                foreach (string dep in deps)
                {
                    if (planByMatPath.TryGetValue(dep, out TrimPlan plan))
                        AddUnique(plan.ExternalConsumers, $"{path} (dùng chung material — bản gốc giữ nguyên)");
                }
            }
        }

        // ---------------------------------------------------------------- Draw plans

        [PropertyOrder(100)]
        [OnInspectorGUI]
        private void DrawPlans()
        {
            if (_plans == null || _plans.Count == 0)
            {
                EditorGUILayout.HelpBox("Add TargetPrefabs (prefab VFX), then click Analyze.", MessageType.Info);
                return;
            }

            bool anyTrimmable = false;

            for (int i = 0; i < _plans.Count; i++)
            {
                TrimPlan plan = _plans[i];
                EditorGUILayout.Space(10f);

                int keep = plan.KeepCount;
                string status = plan.Applied ? "ĐÃ APPLY"
                    : !plan.CanTrim ? "BỎ QUA"
                    : keep >= plan.TotalFrames ? "DÙNG HẾT"
                    : $"tiết kiệm ~{EditorUtility.FormatBytes(plan.EstimatedSavedBytes)}";

                EditorGUILayout.LabelField(
                    $"{plan.Source.name}   {plan.TilesX}x{plan.TilesY}   giữ {keep}/{plan.TotalFrames}   [{status}]",
                    EditorStyles.boldLabel);

                EditorGUILayout.ObjectField(plan.Source, typeof(Texture2D), false);

                foreach (string warning in plan.Warnings)
                    EditorGUILayout.HelpBox(warning, MessageType.Warning);

                if (plan.ExternalConsumers.Count > 0)
                {
                    EditorGUILayout.HelpBox(
                        $"Texture/material này còn {plan.ExternalConsumers.Count} consumer khác trong project (không bị ảnh hưởng vì tool chỉ tạo bản copy):\n- " +
                        string.Join("\n- ", plan.ExternalConsumers),
                        MessageType.Info);
                }

                if (plan.CanTrim)
                {
                    DrawTileGrid(plan);

                    bool canApply = !plan.Applied && keep < plan.TotalFrames;
                    if (canApply)
                        anyTrimmable = true;

                    EditorGUI.BeginDisabledGroup(!canApply);
                    if (GUILayout.Button($"Apply '{plan.Source.name}'", GUILayout.Height(24f)))
                    {
                        ApplyPlan(plan);
                        GUIUtility.ExitGUI();
                    }
                    EditorGUI.EndDisabledGroup();
                }
            }

            EditorGUILayout.Space(12f);
            EditorGUI.BeginDisabledGroup(!anyTrimmable);
            GUI.backgroundColor = new Color(0.5f, 1f, 0.6f);
            if (GUILayout.Button("Apply All Trimmable", GUILayout.Height(30f)))
            {
                ApplyAllTrimmable();
                GUIUtility.ExitGUI();
            }
            GUI.backgroundColor = Color.white;
            EditorGUI.EndDisabledGroup();
        }

        private void DrawTileGrid(TrimPlan plan)
        {
            float width = Mathf.Min(EditorGUIUtility.currentViewWidth - 60f, PreviewMaxWidth);
            float height = width * plan.Source.height / plan.Source.width;
            Rect rect = GUILayoutUtility.GetRect(width, height, GUILayout.ExpandWidth(false));

            EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));
            EditorGUI.DrawPreviewTexture(rect, plan.Source, null, ScaleMode.StretchToFill);

            float cellW = rect.width / plan.TilesX;
            float cellH = rect.height / plan.TilesY;

            for (int frame = 0; frame < plan.TotalFrames; frame++)
            {
                int col = frame % plan.TilesX;
                int rowTop = frame / plan.TilesX;
                Rect cell = new Rect(rect.x + col * cellW, rect.y + rowTop * cellH, cellW, cellH);

                Color overlay;
                if (plan.Keep[frame])
                {
                    bool empty = plan.HasContent != null && !plan.HasContent[frame];
                    overlay = empty ? new Color(1f, 0.8f, 0.1f, 0.30f) : new Color(0.2f, 1f, 0.3f, 0.10f);
                }
                else
                {
                    overlay = new Color(1f, 0.15f, 0.15f, 0.45f);
                }
                EditorGUI.DrawRect(cell, overlay);
            }

            // Click ô không-reachable để giữ/bỏ thủ công; ô reachable bị khóa (cắt sẽ phá animation)
            Event e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                int col = Mathf.Clamp((int)((e.mousePosition.x - rect.x) / cellW), 0, plan.TilesX - 1);
                int rowTop = Mathf.Clamp((int)((e.mousePosition.y - rect.y) / cellH), 0, plan.TilesY - 1);
                int frame = rowTop * plan.TilesX + col;
                if (!plan.Reachable[frame])
                {
                    plan.Keep[frame] = !plan.Keep[frame];
                    e.Use();
                    GUI.changed = true;
                }
            }

            EditorGUILayout.LabelField(
                "xanh = giữ   đỏ = cắt   vàng = giữ nhưng tile rỗng (kiểm tra lại VFX)",
                EditorStyles.miniLabel);
        }

        // ---------------------------------------------------------------- Apply

        private void ApplyAllTrimmable()
        {
            if (_plans == null)
                return;
            foreach (TrimPlan plan in _plans)
            {
                if (plan.CanTrim && !plan.Applied && plan.KeepCount < plan.TotalFrames)
                    ApplyPlan(plan);
            }
        }

        private void ApplyPlan(TrimPlan plan)
        {
            try
            {
                EditorUtility.DisplayProgressBar("VFX Flipbook Trim", $"Repacking {plan.Source.name}...", 0.2f);

                // 1. Rank map: frame gốc -> chỉ số tile mới (-1 nếu bị cắt)
                int[] rankMap = new int[plan.TotalFrames];
                int keptCount = 0;
                for (int f = 0; f < plan.TotalFrames; f++)
                    rankMap[f] = plan.Keep[f] ? keptCount++ : -1;

                if (keptCount == 0 || keptCount >= plan.TotalFrames)
                {
                    Debug.LogWarning($"[FVfxFlipbookTrimTool] '{plan.Source.name}': nothing to trim.");
                    return;
                }

                int newX = Mathf.Min(keptCount, plan.TilesX);
                int newY = Mathf.CeilToInt(keptCount / (float)newX);

                // 2. Repack texture
                string outDir = ResolveOutputFolder(plan);
                string newTexPath = WriteTrimmedTexture(plan, rankMap, newX, newY, outDir);
                if (newTexPath == null)
                    return;
                Texture2D newTex = AssetDatabase.LoadAssetAtPath<Texture2D>(newTexPath);

                // 3. Clone materials, trỏ sang texture mới
                EditorUtility.DisplayProgressBar("VFX Flipbook Trim", "Cloning materials...", 0.6f);
                Dictionary<Material, Material> matMap = new Dictionary<Material, Material>();
                foreach (KeyValuePair<Material, string> kvp in plan.MaterialProps)
                {
                    Material src = kvp.Key;
                    if (src == null)
                        continue;
                    string matPath = AssetDatabase.GenerateUniqueAssetPath($"{outDir}/{src.name}{assetSuffix}.mat");
                    Material clone = new Material(src);
                    clone.SetTexture(kvp.Value, newTex);
                    AssetDatabase.CreateAsset(clone, matPath);
                    matMap[src] = clone;
                }

                // 4. Reassign + remap TSA trong từng target
                EditorUtility.DisplayProgressBar("VFX Flipbook Trim", "Rewiring prefabs...", 0.8f);
                int changed = 0;
                foreach (GameObject root in plan.Roots)
                {
                    string assetPath = AssetDatabase.GetAssetPath(root);
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        GameObject contents = PrefabUtility.LoadPrefabContents(assetPath);
                        try
                        {
                            changed += ApplyToHierarchy(contents, plan, matMap, rankMap, newX, newY, recordUndo: false);
                            PrefabUtility.SaveAsPrefabAsset(contents, assetPath);
                        }
                        finally
                        {
                            PrefabUtility.UnloadPrefabContents(contents);
                        }
                    }
                    else
                    {
                        Undo.RegisterFullObjectHierarchyUndo(root, "VFX Flipbook Trim");
                        changed += ApplyToHierarchy(root, plan, matMap, rankMap, newX, newY, recordUndo: true);
                        EditorUtility.SetDirty(root);
                    }
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                long newBytes = new FileInfo(newTexPath).Length;
                plan.Applied = true;
                FSelectionUtils.Ping(newTex);
                Debug.Log(
                    $"<color=green>[FVfxFlipbookTrimTool] '{plan.Source.name}': {plan.TotalFrames} → {keptCount} tiles " +
                    $"({plan.TilesX}x{plan.TilesY} → {newX}x{newY}), {changed} particle system(s) rewired.\n" +
                    $"File: {EditorUtility.FormatBytes(plan.SourceFileBytes)} → {EditorUtility.FormatBytes(newBytes)} " +
                    $"(gốc giữ nguyên tại {plan.SourcePath}).</color>");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private string ResolveOutputFolder(TrimPlan plan)
        {
            string baseDir = null;
            foreach (GameObject root in plan.Roots)
            {
                string p = AssetDatabase.GetAssetPath(root);
                if (!string.IsNullOrEmpty(p))
                {
                    baseDir = Path.GetDirectoryName(p)?.Replace('\\', '/');
                    break;
                }
            }
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Path.GetDirectoryName(plan.SourcePath)?.Replace('\\', '/');

            string outDir = $"{baseDir}/{outputSubfolder}";
            if (!AssetDatabase.IsValidFolder(outDir))
                AssetDatabase.CreateFolder(baseDir, outputSubfolder);
            return outDir;
        }

        private string WriteTrimmedTexture(TrimPlan plan, int[] rankMap, int newX, int newY, string outDir)
        {
            Color32[] src = ReadTexturePixels(plan.Source, plan.SourcePath);
            if (src == null)
            {
                Debug.LogError($"[FVfxFlipbookTrimTool] Cannot read pixels of '{plan.Source.name}'.");
                return null;
            }

            int srcW = plan.Source.width;
            int newW = newX * plan.TileW;
            int newH = newY * plan.TileH;
            Color32[] dst = new Color32[newW * newH];

            for (int frame = 0; frame < plan.TotalFrames; frame++)
            {
                int rank = rankMap[frame];
                if (rank < 0)
                    continue;
                GetTilePixelOrigin(frame, plan.TilesX, plan.TilesY, plan.TileW, plan.TileH, out int sx, out int sy);
                GetTilePixelOrigin(rank, newX, newY, plan.TileW, plan.TileH, out int dx, out int dy);
                for (int row = 0; row < plan.TileH; row++)
                    Array.Copy(src, (sy + row) * srcW + sx, dst, (dy + row) * newW + dx, plan.TileW);
            }

            Texture2D outTex = new Texture2D(newW, newH, TextureFormat.RGBA32, false);
            byte[] png;
            try
            {
                outTex.SetPixels32(dst);
                outTex.Apply();
                png = outTex.EncodeToPNG();
            }
            finally
            {
                DestroyImmediate(outTex);
            }

            string newTexPath = AssetDatabase.GenerateUniqueAssetPath($"{outDir}/{plan.Source.name}{assetSuffix}.png");
            File.WriteAllBytes(newTexPath, png);
            AssetDatabase.ImportAsset(newTexPath);
            CopyImporterSettings(plan.SourcePath, newTexPath);
            return newTexPath;
        }

        private static int ApplyToHierarchy(GameObject root, TrimPlan plan, Dictionary<Material, Material> matMap,
            int[] rankMap, int newX, int newY, bool recordUndo)
        {
            int changed = 0;
            int newTotal = newX * newY;

            ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
            foreach (ParticleSystem ps in systems)
            {
                ParticleSystemRenderer psr = ps.GetComponent<ParticleSystemRenderer>();
                if (psr == null || psr.sharedMaterial == null)
                    continue;
                if (!matMap.TryGetValue(psr.sharedMaterial, out Material newMat))
                    continue;

                if (recordUndo)
                {
                    Undo.RecordObject(ps, "VFX Flipbook Trim");
                    Undo.RecordObject(psr, "VFX Flipbook Trim");
                }

                var tsa = ps.textureSheetAnimation;
                GetValueRange(tsa.frameOverTime, out float vMin, out _);
                int minFrame = ClampFrame(vMin, plan.TotalFrames);
                int frameOffset = rankMap[minFrame] - minFrame; // âm hoặc 0; nguyên → biến đổi affine chính xác

                tsa.frameOverTime = TransformFrameCurve(tsa.frameOverTime, plan.TotalFrames, newTotal, frameOffset);
                tsa.numTilesX = newX;
                tsa.numTilesY = newY;

                psr.sharedMaterial = newMat;

                if (recordUndo)
                {
                    EditorUtility.SetDirty(ps);
                    EditorUtility.SetDirty(psr);
                }
                changed++;
            }
            return changed;
        }

        // ---------------------------------------------------------------- Frame curve math

        /// <summary>
        /// Biến đổi frameOverTime sang lưới mới: tileMới = tileCũ + frameOffset.
        /// Affine trên giá trị normalized: v' = (v*oldTotal + frameOffset) / newTotal,
        /// chính xác tuyệt đối vì frameOffset nguyên → floor(v'*newTotal) = floor(v*oldTotal) + frameOffset.
        /// </summary>
        private static ParticleSystem.MinMaxCurve TransformFrameCurve(ParticleSystem.MinMaxCurve src, int oldTotal, int newTotal, int frameOffset)
        {
            float ratio = (float)oldTotal / newTotal;
            float offsetNorm = (float)frameOffset / newTotal;

            switch (src.mode)
            {
                case ParticleSystemCurveMode.Constant:
                {
                    // Dùng tâm tile để tránh sai số float ở biên
                    int oldFrame = ClampFrame(src.constant, oldTotal);
                    return new ParticleSystem.MinMaxCurve((oldFrame + frameOffset + 0.5f) / newTotal);
                }
                case ParticleSystemCurveMode.TwoConstants:
                    return new ParticleSystem.MinMaxCurve(
                        src.constantMin * ratio + offsetNorm,
                        src.constantMax * ratio + offsetNorm);
                case ParticleSystemCurveMode.Curve:
                    return new ParticleSystem.MinMaxCurve(1f,
                        TransformCurve(src.curve, src.curveMultiplier * ratio, offsetNorm));
                case ParticleSystemCurveMode.TwoCurves:
                    return new ParticleSystem.MinMaxCurve(1f,
                        TransformCurve(src.curveMin, src.curveMultiplier * ratio, offsetNorm),
                        TransformCurve(src.curveMax, src.curveMultiplier * ratio, offsetNorm));
                default:
                    return src;
            }
        }

        private static AnimationCurve TransformCurve(AnimationCurve src, float valueScale, float valueOffset)
        {
            if (src == null)
                return null;
            Keyframe[] keys = src.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                Keyframe k = keys[i];
                k.value = k.value * valueScale + valueOffset;
                k.inTangent *= valueScale;
                k.outTangent *= valueScale;
                keys[i] = k;
            }
            AnimationCurve curve = new AnimationCurve(keys)
            {
                preWrapMode = src.preWrapMode,
                postWrapMode = src.postWrapMode,
            };
            return curve;
        }

        private static void GetValueRange(ParticleSystem.MinMaxCurve mmc, out float min, out float max)
        {
            switch (mmc.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    min = max = mmc.constant;
                    break;
                case ParticleSystemCurveMode.TwoConstants:
                    min = Mathf.Min(mmc.constantMin, mmc.constantMax);
                    max = Mathf.Max(mmc.constantMin, mmc.constantMax);
                    break;
                case ParticleSystemCurveMode.Curve:
                    SampleCurveRange(mmc.curve, mmc.curveMultiplier, out min, out max);
                    break;
                case ParticleSystemCurveMode.TwoCurves:
                    SampleCurveRange(mmc.curveMin, mmc.curveMultiplier, out float minA, out float maxA);
                    SampleCurveRange(mmc.curveMax, mmc.curveMultiplier, out float minB, out float maxB);
                    min = Mathf.Min(minA, minB);
                    max = Mathf.Max(maxA, maxB);
                    break;
                default:
                    min = 0f;
                    max = 1f;
                    break;
            }
        }

        private static void SampleCurveRange(AnimationCurve curve, float multiplier, out float min, out float max)
        {
            min = float.MaxValue;
            max = float.MinValue;
            if (curve == null || curve.length == 0)
            {
                min = max = 0f;
                return;
            }
            for (int i = 0; i <= CurveSampleCount; i++)
            {
                float v = curve.Evaluate(i / (float)CurveSampleCount) * multiplier;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        private static int ClampFrame(float normalizedValue, int totalFrames)
        {
            return Mathf.Clamp(Mathf.FloorToInt(normalizedValue * totalFrames), 0, totalFrames - 1);
        }

        // ---------------------------------------------------------------- Pixels / importer

        /// <summary>Đọc pixel qua RT blit (đọc được cả texture non-readable/compressed). Origin: góc dưới-trái.</summary>
        private static Color32[] ReadTexturePixels(Texture2D texture, string assetPath)
        {
            if (texture == null || texture.width <= 0 || texture.height <= 0)
                return null;

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            bool srgb = importer == null || importer.sRGBTexture;
            var readWrite = srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;

            int w = texture.width;
            int h = texture.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, readWrite);
            try
            {
                RenderTexture prev = RenderTexture.active;
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                try
                {
                    Texture2D temp = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    try
                    {
                        temp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                        temp.Apply();
                        return temp.GetPixels32();
                    }
                    finally
                    {
                        DestroyImmediate(temp);
                    }
                }
                finally
                {
                    RenderTexture.active = prev;
                }
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>Tile theo thứ tự frame Unity (trái→phải, trên→xuống) sang origin pixel (góc dưới-trái).</summary>
        private static void GetTilePixelOrigin(int frame, int tilesX, int tilesY, int tileW, int tileH, out int x, out int y)
        {
            int col = frame % tilesX;
            int rowTop = frame / tilesX;
            x = col * tileW;
            y = (tilesY - 1 - rowTop) * tileH;
        }

        private static void CopyImporterSettings(string srcPath, string dstPath)
        {
            var src = AssetImporter.GetAtPath(srcPath) as TextureImporter;
            var dst = AssetImporter.GetAtPath(dstPath) as TextureImporter;
            if (src == null || dst == null)
                return;

            dst.textureType = src.textureType;
            dst.sRGBTexture = src.sRGBTexture;
            dst.alphaSource = src.alphaSource;
            dst.alphaIsTransparency = src.alphaIsTransparency;
            dst.mipmapEnabled = src.mipmapEnabled;
            dst.streamingMipmaps = src.streamingMipmaps;
            dst.wrapMode = src.wrapMode;
            dst.filterMode = src.filterMode;
            dst.anisoLevel = src.anisoLevel;
            dst.npotScale = src.npotScale;
            dst.maxTextureSize = src.maxTextureSize;
            dst.textureCompression = src.textureCompression;
            dst.crunchedCompression = src.crunchedCompression;
            dst.compressionQuality = src.compressionQuality;

            foreach (string platform in OverridePlatformNames)
            {
                TextureImporterPlatformSettings ps = src.GetPlatformTextureSettings(platform);
                if (ps != null && ps.overridden)
                    dst.SetPlatformTextureSettings(ps);
            }

            dst.SaveAndReimport();
        }

        // ---------------------------------------------------------------- Helpers

        private static Texture2D GetBaseMapTexture(Material material, out string usedPropertyName)
        {
            usedPropertyName = null;
            if (material == null)
                return null;
            foreach (string propName in BaseMapPropertyNames)
            {
                if (!material.HasProperty(propName))
                    continue;
                Texture tex = material.GetTexture(propName);
                if (tex is Texture2D tex2d)
                {
                    usedPropertyName = propName;
                    return tex2d;
                }
            }
            return null;
        }

        private static void Block(TrimPlan plan, string reason)
        {
            plan.CanTrim = false;
            if (!plan.Warnings.Contains(reason))
                plan.Warnings.Add(reason);
        }

        private static void AddUnique<T>(List<T> list, T item)
        {
            if (!list.Contains(item))
                list.Add(item);
        }

        private static string GetPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        // ---------------------------------------------------------------- Data

        private sealed class SystemUsage
        {
            public string Path;
            public int MinFrame;
            public int MaxFrame;
            public bool IsConstantPick;
        }

        private sealed class TrimPlan
        {
            public Texture2D Source;
            public string SourcePath;
            public long SourceFileBytes;

            public int TilesX;
            public int TilesY;
            public int TotalFrames;
            public int TileW;
            public int TileH;

            public bool[] Reachable;     // tile có thể được TSA hiển thị (khóa, không cho cắt tay)
            public bool[] HasContent;    // null nếu không check
            public bool[] Keep;          // tile giữ lại (Reachable + override tay)

            public readonly List<SystemUsage> Systems = new List<SystemUsage>();
            public readonly Dictionary<Material, string> MaterialProps = new Dictionary<Material, string>();
            public readonly List<GameObject> Roots = new List<GameObject>();
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> ExternalConsumers = new List<string>();

            public bool CanTrim = true;
            public bool Applied;

            public int KeepCount
            {
                get
                {
                    if (Keep == null)
                        return 0;
                    int n = 0;
                    foreach (bool k in Keep)
                        if (k) n++;
                    return n;
                }
            }

            public long EstimatedSavedBytes
            {
                get
                {
                    if (!CanTrim || TotalFrames == 0 || Keep == null)
                        return 0;
                    return (long)(SourceFileBytes * (1f - KeepCount / (float)TotalFrames));
                }
            }
        }
    }
}
