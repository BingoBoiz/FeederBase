using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using Sirenix.Utilities.Editor;
using UnityEditor;
using UnityEngine;
using Sirenix.Utilities;

namespace Feeder
{
    public class FStyleMenuWindow : OdinEditorWindow
    {
        [MenuItem("Tools/Feeder/Feeder Menu Style Window", priority = 10)]
        private static void OpenWindow()
        {
            var window = GetWindow<FStyleMenuWindow>();
            window.titleContent = FIconCatalog.CreateWindowTitle("Feeder Menu Style Window", FIconCatalog.WindowMenuTitleIcon);
            window.position = GUIHelper.GetEditorWindowRect().AlignCenter(800, 600);
            window.Show();
        }

        [ShowInInspector]
        [InlineEditor(InlineEditorModes.FullEditor)]
        [HideLabel]
        private FMenuTreeSettings settings;

        protected override void OnEnable()
        {
            base.OnEnable();
            settings = FMenuTreeSettings.instance;
        }

        [Button(ButtonSizes.Large)]
        private void ApplyToMenuWindow()
        {
            if (settings == null) throw new System.NullReferenceException(nameof(settings));
            var windows = Resources.FindObjectsOfTypeAll<FMenuEditorWindow>();
            foreach (var window in windows)
            {
                window.ForceMenuTreeRebuild();
                window.Repaint();
            }
            settings.SaveSettings();
        }
    }

    [UnityEditor.FilePath("ProjectSettings/FeederMenuTreeSettings.asset", UnityEditor.FilePathAttribute.Location.ProjectFolder)]
    internal sealed class FMenuTreeSettings : ScriptableSingleton<FMenuTreeSettings>
    {
        [Title("Tree Config")]
        public bool DrawSearchToolbar = true;

        [Title("Tree Config")]
        public bool SupportsMultiSelect = false;

        [Title("Menu Style")]
        public float BorderPadding = 0f;

        [Title("Menu Style")]
        public bool AlignTriangleLeft = true;

        [Title("Menu Style")]
        public float TriangleSize = 16f;

        [Title("Menu Style")]
        public float TrianglePadding = 0f;

        [Title("Menu Style")]
        public float Offset = 20f;

        [Title("Menu Style")]
        public int Height = 23;

        [Title("Menu Style")]
        public float IconPadding = 0f;

        [Title("Menu Style")]
        public float BorderAlpha = 0.323f;

        public void ApplyTo(OdinMenuTree tree)
        {
            tree = tree ?? throw new System.ArgumentNullException(nameof(tree));
            tree.DefaultMenuStyle = BuildMenuStyle();
            tree.Config.DrawSearchToolbar = DrawSearchToolbar;
        }

        private OdinMenuStyle BuildMenuStyle()
        {
            return new OdinMenuStyle
            {
                BorderPadding = BorderPadding,
                AlignTriangleLeft = AlignTriangleLeft,
                TriangleSize = TriangleSize,
                TrianglePadding = TrianglePadding,
                Offset = Offset,
                Height = Height,
                IconPadding = IconPadding,
                BorderAlpha = BorderAlpha
            };
        }

        public void SaveSettings()
        {
            Save(true);
        }
    }
}

