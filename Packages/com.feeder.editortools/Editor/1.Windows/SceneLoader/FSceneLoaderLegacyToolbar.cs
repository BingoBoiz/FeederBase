#if !UNITY_6000_3_OR_NEWER
using System.Reflection;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

namespace Feeder
{
    // no public main toolbar api before 6000.3; from 6000.3 unity warns about elements injected this way, so FSceneLoaderToolbar takes over
    [InitializeOnLoad]
    static class FSceneLoaderLegacyToolbar
    {
        const string StripName = "FeederSceneLoader";
        const string ToolbarTypeName = "UnityEditor.Toolbar";
        const string InstanceFieldName = "get";
        const string RootFieldName = "m_Root";
        const string ZoneName = "ToolbarZonePlayMode";
        const int MaxAttempts = 100;

        static EditorToolbarButton scenesButton;
        static int attempts;
        static bool warned;

        static FSceneLoaderLegacyToolbar()
        {
            ShortcutManager.instance.shortcutBindingChanged -= OnBindingChanged;
            ShortcutManager.instance.shortcutBindingChanged += OnBindingChanged;
            ScheduleAttach();
        }

        static void ScheduleAttach()
        {
            attempts = 0;
            EditorApplication.delayCall -= TryAttach;
            EditorApplication.delayCall += TryAttach;
        }

        static void TryAttach()
        {
            var toolbarType = typeof(EditorWindow).Assembly.GetType(ToolbarTypeName);
            var instanceField = toolbarType?.GetField(InstanceFieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var rootField = toolbarType?.GetField(RootFieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (toolbarType == null || instanceField == null || rootField == null)
            {
                Warn(toolbarType == null ? ToolbarTypeName : $"{ToolbarTypeName}.{(instanceField == null ? InstanceFieldName : RootFieldName)}");
                return;
            }

            var toolbar = instanceField.GetValue(null);
            var root = toolbar != null ? rootField.GetValue(toolbar) as VisualElement : null;
            var zone = root?.Q(ZoneName);
            if (zone?.panel == null)
            {
                if (++attempts < MaxAttempts) EditorApplication.delayCall += TryAttach;
                else Warn(toolbar == null ? $"the {ToolbarTypeName} instance" : root == null ? $"{ToolbarTypeName}.{RootFieldName}" : $"the '{ZoneName}' zone");
                return;
            }

            if (zone.Q(StripName) == null) zone.Add(CreateStrip());
        }

        static VisualElement CreateStrip()
        {
            var strip = new VisualElement { name = StripName };
            strip.style.flexDirection = FlexDirection.Row;
            scenesButton = new EditorToolbarButton(FSceneLoaderToolbarMenu.ButtonText, FSceneLoaderToolbarMenu.ButtonIcon(), FSceneLoaderWindow.Toggle)
            {
                tooltip = FSceneLoaderToolbarMenu.ButtonTooltip(),
            };
            var dropdown = new EditorToolbarDropdown { tooltip = FSceneLoaderToolbarMenu.DropdownTooltip };
            dropdown.clicked += () => FSceneLoaderToolbarMenu.Show(dropdown.worldBound);
            strip.Add(scenesButton);
            strip.Add(dropdown);
            EditorToolbarUtility.SetupChildrenAsButtonStrip(strip);
            strip.RegisterCallback<DetachFromPanelEvent>(OnStripDetached);
            return strip;
        }

        static void OnStripDetached(DetachFromPanelEvent evt) => ScheduleAttach();

        static void OnBindingChanged(ShortcutBindingChangedEventArgs args)
        {
            if (args.shortcutId == FSceneLoaderWindow.ShortcutId && scenesButton != null) scenesButton.tooltip = FSceneLoaderToolbarMenu.ButtonTooltip();
        }

        static void Warn(string missing)
        {
            if (warned) return;
            warned = true;
            Debug.LogWarning($"Scene Loader: no Scenes toolbar button, {missing} was not found in this Unity version. Open it from Tools > Feeder > Scene Loader or {FSceneLoaderWindow.ShortcutLabel()}.");
        }
    }
}
#endif
