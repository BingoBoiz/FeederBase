# Feeder Editor Tools

**Unity:** 2022.3+

**Requirements:** This package requires [Odin Inspector](https://assetstore.unity.com/packages/tools/utilities/odin-inspector-and-serializer-89041). 

---

## Installation

1. Open **Window > Package Manager**.
2. Click **+** and choose **Add package from git URL**.
3. Paste:

```
https://github.com/BingoBoiz/FeederBase.git?path=/Packages/com.feeder.editortools
```

4. Click **Add**.

---

## Contents

- **Windows:** Align mesh toolbar, scene loader, script template, style menu.
- **Tools:** See [Tools](#tools) below.
- **Utils:** Hierarchy/path resolution, prefab helpers, naming/sequence utilities.
- **MazeGenerator:** Procedural maze algorithms (e.g. Aldous-Broder, cellular automaton).
- **Google Sheet Importer:** Read Google Sheets through a service account, preview tabs, and generate C# data classes and ScriptableObject assets.

Tools are exposed via Feeder menu and/or toolbar where applicable.

---

## Tools

| Tool | Description |
|------|-------------|
| **FDeduplicateMeshTool** | Compare mesh assets side-by-side; find similar meshes in scene and align/replace. |
| **FDeduplicateTextureTool** | Scan target roots for MeshRenderer materials; group textures by resolution and pixel data; resolve duplicates to a single texture. |
| **FDeduplicateMaterialTool** | Scan target roots for materials; group duplicates by same base map (_BaseMap / _MainTex); resolve to one material per group. |
| **FPrefabVariantCreatorTool** | Pick a base prefab and a “locate model” transform; batch-create prefab variants from a list of models into a save folder. |
| **FComponentReplacerTool** | Replace one component type with another on prefabs/scene objects and copy over compatible field data. |
| **FComponentModifyTool** | Add component by hierarchy path, modify property values in bulk, or remove a component type from all targets. |
| **FMissingComponentHandlerTool** | Handle GameObjects that have missing script references (inspect and fix or strip). |
| **FScriptableObjectsFillerTool** | Fill a ScriptableObject dictionary (e.g. `Dictionary<Enum, Sprite>`) by matching enum names to asset names in a folder. |
| **FDataClonerTool** | Clone or duplicate data assets (structure depends on tool implementation). |
| **FCharacterMeshUpdateTool** | Update character meshes (placeholder for project-specific workflow). |
| **FModelScaleToColliderTool** | Scale models to match a base prefab’s collider size; choose base prefab, target path, and run on a list of objects. |
| **FRepackModelsTool** | Export each target mesh as FBX next to the mesh asset, replace scene references with the FBX mesh, then remove the original mesh asset. |
| **FUnpackEviromentTool** | Unpack environment: extract meshes, materials, and textures from MeshRenderers into per-target folders; shared assets go to a common folder. |
| **FRenameTool** | Rename assets by pattern (`{number}`, `{variant}`, enum placeholders) or by find-and-replace over TargetAssets. |
| **FSortOrderTool** | Map TargetAssets to an enum by name; reorder assets to match enum order (e.g. for ordered lists or atlases). |
| **FNameOffsetTool** | Adjust name/label position (e.g. UI or 3D text) by a Y offset on a chosen holder path across targets. |
| **FPrefabModifyTool** | Batch-modify prefabs (structure depends on tool implementation). |
| **FPrefabReferenceSyncTool** | Sync or fix prefab references (used in prefab workflows). |
| **FeederGooglesheetImporterWindow** | Feeder-branded copy of the Google Sheet importer. Open via `Tools > Feeder > Googlesheet Importer`. |

## Google Sheet Importer

The importer keeps the original import and generation behavior while using Feeder naming throughout.

1. Enable Google Sheets API in Google Cloud, create a service account, and download its JSON key.
2. Share the spreadsheet with the JSON key's `client_email`.
3. Open **Tools > Feeder > Googlesheet Importer**, configure the credential path under **Data Config**, and add a sheet entry.
4. Paste the Spreadsheet ID, load the sheet, select a tab, then generate the script and data asset.

The first row contains the generated type name and the second row contains typed fields such as `n_ID`, `f_Damage`, `s_Name`, or `pref_Enemy:EnemyController`. Prefix a field header with `/` to exclude that column.

Credential JSON files are project-local secrets. No credential, Spreadsheet ID, config asset, or cached sheet data is included in this package.

## Scene Loader

Browse, open and add the project's scenes from one window.

**Open it**

- Click **Scenes** next to Play/Pause/Step in the main toolbar. The arrow next to it lists Favorites, Recent, Add Additive and Open Scene Loader.
- Press `Alt+S`. Rebind it from the shortcut chip at the bottom of the window, or in **Edit > Shortcuts** (`Feeder/Scene Loader/Toggle Window`).
- Use **Tools > Feeder > Scene Loader**.

On Unity 6.3 and newer the button is a main toolbar element. If you hid it, right-click the main toolbar and enable **Feeder/Scene Loader**.

**Use it**

- Double-click a scene to open it. Alt+Click or the `+` button adds it additively. The star adds it to Favorites.
- Drag a scene into the Hierarchy or onto the LOADED strip to add it. Click a LOADED chip to make it the active scene; its close button removes it.
- Opening and adding scenes is disabled in Play Mode.

**Configure it**

- **Project Settings > Feeder > Scene Loader**: Scan Roots (folders searched for scenes, default `Assets`), Top-Level Order, and Folder Rules (label, color, hidden per folder).
- Right-click a folder in the window for Edit Folder..., Color, Hide/Unhide and Show in Project. Drag top-level folders in the sidebar to reorder them. Hidden folders come back with the gear menu > Show Hidden Folders.

**Saved files**

| File | Content | Commit |
|------|---------|--------|
| `ProjectSettings/FeederSceneLoader.asset` | Scan roots, folder order, folder rules | Yes, shared with the team |
| `UserSettings/FeederSceneLoader.asset` | Favorites, recent scenes, expanded folders | No, per machine |

---

## License

See repository root for license terms.
