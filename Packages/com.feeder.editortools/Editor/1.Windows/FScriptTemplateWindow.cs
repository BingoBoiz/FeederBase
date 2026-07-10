using UnityEditor;
using UnityEngine;
using System.IO;
using System.Text.RegularExpressions;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using FilePath = Sirenix.OdinInspector.FilePathAttribute;

namespace Feeder {
    public class FScriptTemplateWindow : OdinEditorWindow
    {
        private const string TemplatePathKey = "CustomScriptTemplatePath";
        private const string SessionResetKey = "CustomScriptTemplateSessionReset";

        [MenuItem("Tools/Feeder/Script Template Window", priority = 2)]
        private static void OpenWindow()
        {
            var window = GetWindow<FScriptTemplateWindow>();
            window.titleContent = FIconCatalog.CreateWindowTitle("Script Template Window", FIconCatalog.ScriptTemplateTitleIcon);
            window.Show();
        }

        [Title("Template Settings")]
        [InfoBox("Drag and drop a template file here. If no template is set, the default Unity C# script will be created.")]
        [PropertySpace(SpaceAfter = 10)]

        [FilePath(AbsolutePath = false, Extensions = "txt,cs")]
        [LabelText("Template Path")]
        [Tooltip("Path to the script template file. Leave empty to use default Unity C# script.")]
        [OnValueChanged(nameof(OnTemplatePathChanged))]
        public string templatePath = string.Empty;

        [Title("Create Script")]
        [InfoBox("Use the menu item 'Assets/Create/C# My Base Script' to create a new script using the template.")]
        [PropertySpace(SpaceBefore = 10)]

        [Button("Test Create Script", ButtonSizes.Medium)]
        private void TestCreateScript()
        {
            CreateMyBaseScript();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            
            // reset templatePath to null when Unity starts (on domain reload)
            // check if Unity just started by checking timeSinceStartup
            double currentTime = EditorApplication.timeSinceStartup;
            string lastSessionTime = EditorPrefs.GetString(SessionResetKey, "0");
            
            bool shouldReset = false;
            if (currentTime < 1.0)
            {
                // Unity just started (timeSinceStartup is very small)
                shouldReset = true;
            }
            else if (double.TryParse(lastSessionTime, out double lastTime))
            {
                // if stored time is greater than current time, domain reload happened
                if (lastTime > currentTime)
                {
                    shouldReset = true;
                }
            }
            else
            {
                // invalid stored time, reset
                shouldReset = true;
            }
            
            if (shouldReset)
            {
                EditorPrefs.DeleteKey(TemplatePathKey);
                EditorPrefs.SetString(SessionResetKey, currentTime.ToString("F2"));
            }
            
            // load templatePath from EditorPrefs
            string savedPath = EditorPrefs.GetString(TemplatePathKey, string.Empty);
            if (!string.IsNullOrEmpty(savedPath) && File.Exists(savedPath))
            {
                templatePath = savedPath;
            }
            else
            {
                templatePath = string.Empty;
            }
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            // save templatePath to EditorPrefs
            if (!string.IsNullOrEmpty(templatePath))
            {
                EditorPrefs.SetString(TemplatePathKey, templatePath);
            }
            else
            {
                EditorPrefs.DeleteKey(TemplatePathKey);
            }
        }

        private void OnTemplatePathChanged()
        {
            // validate template path
            if (!string.IsNullOrEmpty(templatePath))
            {
                if (!File.Exists(templatePath))
                {
                    Debug.LogWarning($"Template file not found: {templatePath}");
                    templatePath = string.Empty;
                    EditorPrefs.DeleteKey(TemplatePathKey);
                }
                else
                {
                    // save template path immediately when changed
                    EditorPrefs.SetString(TemplatePathKey, templatePath);
                    Debug.Log($"Template path saved: {templatePath}");
                }
            }
            else
            {
                EditorPrefs.DeleteKey(TemplatePathKey);
            }
        }

        [MenuItem("Assets/Create/My Script", false, 80)]
        public static void CreateMyBaseScript()
        {
            string folder = GetCurrentFolder();
            string newScriptPath = EditorUtility.SaveFilePanel("Create Base Script", folder, "NewScript.cs", "cs");

            if (string.IsNullOrEmpty(newScriptPath))
                return;

            // convert absolute path to relative path
            string relativePath = GetRelativePath(newScriptPath);
            if (string.IsNullOrEmpty(relativePath))
            {
                Debug.LogError("Script must be created inside the Assets folder.");
                return;
            }

            string scriptName = Path.GetFileNameWithoutExtension(relativePath);
            string scriptContent;

            // get template path from EditorPrefs
            string templatePath = EditorPrefs.GetString(TemplatePathKey, string.Empty);

            if (string.IsNullOrEmpty(templatePath) || !File.Exists(templatePath))
            {
                Debug.LogWarning($"Template path is empty or file not found. Using default Unity C# script template. TemplatePath: '{templatePath}'");
                // use default Unity C# script template
                scriptContent = GetDefaultScriptContent(scriptName);
            }
            else
            {
                // use custom template
                try
                {
                    Debug.Log($"Using template from: {templatePath}");
                    string template = File.ReadAllText(templatePath);
                    Debug.Log($"Template content length: {template.Length} characters");
                    scriptContent = ProcessTemplate(template, scriptName);
                    Debug.Log($"Processed template successfully. New class name: {scriptName}");
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"Failed to read template file: {ex.Message}. Using default template instead.");
                    scriptContent = GetDefaultScriptContent(scriptName);
                }
            }

            File.WriteAllText(newScriptPath, scriptContent);
            AssetDatabase.Refresh();

            // select the newly created script
            Object createdScript = AssetDatabase.LoadAssetAtPath<Object>(relativePath);
            if (createdScript != null)
            {
                Selection.activeObject = createdScript;
                EditorUtility.FocusProjectWindow();
            }
        }

        private static string GetCurrentFolder()
        {
            Object obj = Selection.activeObject;
            if (obj == null)
                return "Assets";

            string path = AssetDatabase.GetAssetPath(obj);
            if (File.Exists(path))
                return Path.GetDirectoryName(path);

            return path;
        }

        private static string GetRelativePath(string absolutePath)
        {
            string assetsPath = Application.dataPath.Replace('/', Path.DirectorySeparatorChar);
            absolutePath = absolutePath.Replace('/', Path.DirectorySeparatorChar);

            if (absolutePath.StartsWith(assetsPath))
            {
                string relativePath = "Assets" + absolutePath.Substring(assetsPath.Length);
                return relativePath.Replace(Path.DirectorySeparatorChar, '/');
            }

            return null;
        }

        private static string ProcessTemplate(string template, string newClassName)
        {
            // replace #SCRIPTNAME# placeholder if exists
            string result = template.Replace("#SCRIPTNAME#", newClassName);

            // find and replace class name in template
            // pattern matches various class declarations: public/private/protected/internal/static (optional) + abstract/partial/sealed (optional) + class + ClassName
            // supports patterns like: "public class _BaseTemplateUI", "class Test", "public abstract class Base", etc.
            // class name pattern: [a-zA-Z_][a-zA-Z0-9_]* to support names starting with underscore
            string classPattern = @"(?:(?:public|private|protected|internal|static)\s+)?(?:abstract\s+)?(?:partial\s+)?(?:sealed\s+)?(?:static\s+)?class\s+([a-zA-Z_][a-zA-Z0-9_]*)";

            Match match = Regex.Match(result, classPattern);
            if (match.Success)
            {
                string oldClassName = match.Groups[1].Value;
                Debug.Log($"Found class name in template: '{oldClassName}', will replace with: '{newClassName}'");
                
                // only replace if old class name is different from new class name
                if (oldClassName != newClassName)
                {
                    // replace all occurrences of the old class name with new class name
                    // use negative lookbehind and lookahead to ensure we match whole identifier
                    // this handles class names starting with underscore correctly
                    string escapedOldName = Regex.Escape(oldClassName);
                    // match oldClassName only when it's a complete identifier (not part of another identifier)
                    // (?<![a-zA-Z0-9_]) ensures no word character before, (?![a-zA-Z0-9_]) ensures no word character after
                    result = Regex.Replace(result, @"(?<![a-zA-Z0-9_])" + escapedOldName + @"(?![a-zA-Z0-9_])", newClassName);
                    Debug.Log($"Replaced all occurrences of '{oldClassName}' with '{newClassName}'");
                }
                else
                {
                    Debug.Log($"Class name is already '{newClassName}', no replacement needed.");
                }
            }
            else
            {
                Debug.LogWarning($"Could not find class declaration in template. Template may not be valid. Pattern used: {classPattern}");
            }

            return result;
        }

        private static string GetDefaultScriptContent(string scriptName)
        {
            return $@"using UnityEngine;

public class {scriptName} : MonoBehaviour
{{
    void Start()
    {{
        
    }}

    void Update()
    {{
        
    }}
}}";
        }
    }
}
