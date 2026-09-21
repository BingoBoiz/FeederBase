using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Feeder
{
    public class FSceneLoaderFolderPopup : EditorWindow
    {
        [SerializeField] string folderPath;
        [SerializeField] string autoLabel;
        TextField labelField;
        ColorField colorField;
        Toggle hiddenToggle;
        VisualElement swatches;
        Button autoButton;

        public static void Open(string folderPath, string autoLabel, Rect screenAnchor)
        {
            foreach (var open in Resources.FindObjectsOfTypeAll<FSceneLoaderFolderPopup>()) open.Close();
            var popup = CreateInstance<FSceneLoaderFolderPopup>();
            popup.folderPath = folderPath;
            popup.autoLabel = autoLabel;
            popup.titleContent = new GUIContent("Edit Folder");
            popup.ShowUtility();
            popup.minSize = popup.maxSize = new Vector2(320, 128);
            popup.position = new Rect(screenAnchor.x, screenAnchor.yMax + 4, 320, 128);
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            var sheet = FSceneLoaderWindow.LoadStyleSheet();
            if (sheet != null) root.styleSheets.Add(sheet);
            root.AddToClassList("fsl-popup");
            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            var path = new Label(folderPath);
            path.AddToClassList("fsl-popup__path");
            root.Add(path);

            labelField = new TextField("Label") { isDelayed = true, tooltip = "Empty = " + autoLabel };
#if UNITY_6000_0_OR_NEWER
            labelField.textEdition.placeholder = autoLabel;
#endif
            labelField.RegisterValueChangedCallback(evt => Edit(rule => rule.label = evt.newValue.Trim()));
            root.Add(labelField);

            var colorRow = new VisualElement();
            colorRow.AddToClassList("fsl-popup__row");
            var colorLabel = new Label("Color");
            colorLabel.AddToClassList("fsl-popup__label");
            colorRow.Add(colorLabel);
            autoButton = new Button(() => Edit(rule => rule.customColor = false)) { text = "Auto" };
            autoButton.AddToClassList("fsl-popup__auto");
            colorRow.Add(autoButton);
            swatches = new VisualElement();
            swatches.AddToClassList("fsl-popup__swatches");
            foreach (var (name, color) in FSceneLoaderProjectSettings.Palette)
            {
                var swatch = new VisualElement { tooltip = name, userData = color };
                swatch.AddToClassList("fsl-swatch");
                swatch.style.backgroundColor = color;
                swatch.RegisterCallback<ClickEvent>(_ => Edit(rule =>
                {
                    rule.customColor = true;
                    rule.color = color;
                }));
                swatches.Add(swatch);
            }
            colorRow.Add(swatches);
            colorField = new ColorField { showAlpha = false, tooltip = "Custom color" };
            colorField.AddToClassList("fsl-popup__custom");
            colorField.RegisterValueChangedCallback(evt => Edit(rule =>
            {
                rule.customColor = true;
                rule.color = evt.newValue;
            }));
            colorRow.Add(colorField);
            root.Add(colorRow);

            hiddenToggle = new Toggle("Hidden");
            hiddenToggle.RegisterValueChangedCallback(evt => Edit(rule => rule.hidden = evt.newValue));
            root.Add(hiddenToggle);

            var buttons = new VisualElement();
            buttons.AddToClassList("fsl-popup__buttons");
            buttons.Add(new Button(ResetRule) { text = "Reset" });
            var spacer = new VisualElement();
            spacer.AddToClassList("fsl-spacer");
            buttons.Add(spacer);
            buttons.Add(new Button(Close) { text = "Done" });
            root.Add(buttons);

            Render();
        }

        void Edit(System.Action<FSceneLoaderProjectSettings.FolderRule> edit)
        {
            FSceneLoaderProjectSettings.instance.EditRule(folderPath, edit);
            FSceneLoaderWindow.RescanAll();
            Render();
        }

        void ResetRule()
        {
            FSceneLoaderProjectSettings.instance.RemoveRule(folderPath);
            FSceneLoaderWindow.RescanAll();
            Render();
        }

        void Render()
        {
            var rule = FSceneLoaderProjectSettings.instance.RuleFor(folderPath);
            var custom = rule != null && rule.customColor;
            labelField.SetValueWithoutNotify(rule?.label ?? string.Empty);
            colorField.SetValueWithoutNotify(custom ? rule.color : Color.white);
            hiddenToggle.SetValueWithoutNotify(rule != null && rule.hidden);
            autoButton.EnableInClassList("is-selected", !custom);
            foreach (var swatch in swatches.Children())
                swatch.EnableInClassList("is-selected", custom && (Color)swatch.userData == rule.color);
        }

        void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Escape) return;
            evt.StopImmediatePropagation();
            Close();
        }
    }
}
