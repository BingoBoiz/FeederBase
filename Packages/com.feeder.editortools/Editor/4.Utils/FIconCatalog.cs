using Sirenix.OdinInspector;
using Sirenix.Utilities.Editor;
using UnityEngine;

namespace Feeder
{
    internal static class FIconCatalog
    {
        public const SdfIconType MenuWindowIcon = SdfIconType.LayoutTextSidebar;
        public const SdfIconType SceneLoaderWindowIcon = SdfIconType.Map;
        public const SdfIconType ScriptTemplateWindowIcon = SdfIconType.FileEarmarkText;

        public const SdfIconType AssetCollectorToolIcon = SdfIconType.FolderSymlink;
        public const SdfIconType AssetOrganizerToolIcon = SdfIconType.FolderCheck;
        public const SdfIconType SortOrderToolIcon = SdfIconType.SortAlphaDown;
        public const SdfIconType RenameToolIcon = SdfIconType.PencilSquare;
        public const SdfIconType AssetGroupToolIcon = SdfIconType.Collection;
        public const SdfIconType ComponentModifyToolIcon = SdfIconType.Sliders;
        public const SdfIconType PrefabModifyToolIcon = SdfIconType.Sliders;
        public const SdfIconType PrefabVariantCreatorToolIcon = SdfIconType.Files;
        public const SdfIconType NameOffsetToolIcon = SdfIconType.TextIndentRight;
        public const SdfIconType ModelScaleToColliderToolIcon = SdfIconType.ArrowsExpand;
        public const SdfIconType ComponentReplacerToolIcon = SdfIconType.ArrowRepeat;
        public const SdfIconType ScriptableObjectsFillerToolIcon = SdfIconType.FileEarmarkPlus;
        public const SdfIconType CharacterMeshUpdaterToolIcon = SdfIconType.BoxSeam;
        public const SdfIconType MissingScriptHandlerToolIcon = SdfIconType.FileEarmarkX;
        public const SdfIconType DataClonerToolIcon = SdfIconType.Clipboard;
        public const SdfIconType CompareStringToolIcon = SdfIconType.Search;
        public const SdfIconType MeshBoxColliderFitterToolIcon = SdfIconType.BoundingBox;

        public const SdfIconType LegacyPrefabCleanupIcon = SdfIconType.Trash;
        public const SdfIconType LegacyPrefabDisableIcon = SdfIconType.EyeSlash;
        public const SdfIconType LegacyPrefabHierarchyDeleterIcon = SdfIconType.Trash2;
        public const SdfIconType LegacyPrefabMergeVariantIcon = SdfIconType.ArrowsCollapse;
        public const SdfIconType LegacyPrefabReferenceSyncIcon = SdfIconType.ArrowRepeat;
        public const SdfIconType LegacyPrefabScriptReplacerIcon = SdfIconType.FileEarmarkCode;
        public const SdfIconType LegacyPrefabSpawnerIcon = SdfIconType.BoxArrowInDown;
        public const SdfIconType DefaultToolIcon = SdfIconType.Tools;
        public const SdfIconType LegacyPrefabVariantCreatorIcon = SdfIconType.FileEarmarkPlus;
        public const SdfIconType LegacyRenameToolIcon = SdfIconType.PencilSquare;
        public const SdfIconType LegacyScriptableObjectsFillerIcon = SdfIconType.ClipboardPlus;


        public static GUIContent CreateWindowTitle(string title, EditorIcon icon)
        {
            return new GUIContent(title, icon.Active);
        }

        public static EditorIcon WindowMenuTitleIcon => EditorIcons.List;
        public static EditorIcon SceneLoaderTitleIcon => EditorIcons.Folder;
        public static EditorIcon ScriptTemplateTitleIcon => EditorIcons.File;
        public static EditorIcon CharacterMeshUpdateTitleIcon => EditorIcons.Pen;
        public static EditorIcon MissingScriptTitleIcon => EditorIcons.AlertCircle;
        public static EditorIcon PrefabCleanupTitleIcon => EditorIcons.Cut;
        public static EditorIcon PrefabDisableTitleIcon => EditorIcons.LockLocked;
        public static EditorIcon PrefabHierarchyDeleterTitleIcon => EditorIcons.Cut;
        public static EditorIcon PrefabMergeVariantTitleIcon => EditorIcons.Link;
        public static EditorIcon PrefabReferenceSyncTitleIcon => EditorIcons.Refresh;
        public static EditorIcon PrefabScriptReplacerTitleIcon => EditorIcons.File;
        public static EditorIcon PrefabSpawnerTitleIcon => EditorIcons.Plus;
        public static EditorIcon PrefabToolTitleIcon => EditorIcons.SettingsCog;
        public static EditorIcon PrefabVariantCreatorTitleIcon => EditorIcons.File;
        public static EditorIcon RenameToolTitleIcon => EditorIcons.Pen;
        public static EditorIcon ScriptableObjectDataClonerTitleIcon => EditorIcons.File;
        public static EditorIcon ScriptableObjectsFillerTitleIcon => EditorIcons.File;
    }
}
