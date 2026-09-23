using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.ShortcutManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Feeder
{
    public class FSceneLoaderWindow : EditorWindow
    {
        public const string ShortcutId = "Feeder/Scene Loader/Toggle Window";
        const string SourceFavorites = "@favorites";
        const string SourceRecent = "@recent";
        const string SourceBuild = "@build";
        const string FallbackProfile = "Feeder";
        const float DragThreshold = 5f;

        static readonly Color NeutralTint = new Color32(140, 140, 140, 255);
        static readonly Comparer<string> Natural = Comparer<string>.Create(EditorUtility.NaturalCompare);
        static readonly Comparer<string> FolderOrder = Comparer<string>.Create((a, b) =>
            a.StartsWith("_") != b.StartsWith("_") ? a.StartsWith("_") ? -1 : 1 : EditorUtility.NaturalCompare(a, b));
        static readonly HashSet<string> MissingIcons = new();

        static bool recordingShortcut;

        readonly List<string> scenePaths = new();
        readonly Dictionary<string, int> buildIndices = new();
        readonly Dictionary<string, FolderNode> folders = new();
        readonly Dictionary<string, FolderNode> sceneFolders = new();
        readonly List<FolderNode> topLevel = new();
        readonly List<(FolderNode node, VisualElement row)> topRows = new();

        VisualElement crumbs;
        ScrollView sidebar;
        VisualElement reorderLine;
        VisualElement tiles;
        VisualElement dock;
        Button shortcutChip;
        Button shortcutReset;
        Label countLabel;
        Texture sceneIcon;
        Texture folderIcon;
        Texture starIcon;
        string query = string.Empty;
        string selectedPath;
        bool refreshQueued;
        FolderNode reorderNode;
        Vector3 reorderOrigin;
        bool reordering;
        bool suppressClick;

        sealed class FolderNode
        {
            public string Key;
            public string ContentPath;
            public string AutoLabel;
            public string Label;
            public FolderNode Parent;
            public Color Tint;
            public bool OwnHidden;
            public bool Hidden;
            public int Count;
            public readonly List<FolderNode> Children = new();
            public readonly List<string> Scenes = new();
        }

        sealed class RawFolder
        {
            public readonly string Key;
            public readonly string Name;
            public readonly SortedDictionary<string, RawFolder> Dirs = new(FolderOrder);
            public readonly List<string> Files = new();

            public RawFolder(string key, string name)
            {
                Key = key;
                Name = name;
            }

            public RawFolder Child(string name)
            {
                if (!Dirs.TryGetValue(name, out var child)) Dirs.Add(name, child = new RawFolder(Key + "/" + name, name));
                return child;
            }
        }

        #region API

        [MenuItem("Tools/Feeder/Scene Loader", priority = 1)]
        public static void Open()
        {
            var projectBrowser = Type.GetType("UnityEditor.ProjectBrowser,UnityEditor");
            if (projectBrowser != null) GetWindow<FSceneLoaderWindow>("Scene Loader", true, projectBrowser);
            else GetWindow<FSceneLoaderWindow>("Scene Loader");
        }

        [Shortcut(ShortcutId, KeyCode.S, ShortcutModifiers.Alt)]
        public static void Toggle()
        {
            if (recordingShortcut) return;
            if (focusedWindow is FSceneLoaderWindow focused) focused.Close();
            else Open();
        }

        public static string ShortcutLabel()
        {
            var binding = ShortcutManager.instance.GetShortcutBinding(ShortcutId);
            return binding.keyCombinationSequence.Any() ? binding.ToString() : "Unbound";
        }

        public static void RescanAll()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<FSceneLoaderWindow>()) window.Rescan();
        }

        public static StyleSheet LoadStyleSheet()
        {
            var scriptPath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(FSceneLoaderProjectSettings.instance));
            var sheetPath = Path.GetDirectoryName(scriptPath)?.Replace('\\', '/') + "/FSceneLoaderWindow.uss";
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(sheetPath);
            if (sheet == null) Debug.LogError($"Scene Loader: stylesheet not found at '{sheetPath}'.");
            return sheet;
        }

        public static Texture Icon(string name)
        {
            // IconContent logs an error for a name the running unity version does not ship; one warning replaces it
            var logEnabled = Debug.unityLogger.logEnabled;
            Debug.unityLogger.logEnabled = false;
            Texture icon;
            try
            {
                icon = EditorGUIUtility.IconContent(name).image;
            }
            finally
            {
                Debug.unityLogger.logEnabled = logEnabled;
            }
            if (icon == null && MissingIcons.Add(name)) Debug.LogWarning($"Scene Loader: built-in icon '{name}' does not exist in this Unity version.");
            return icon;
        }

        #endregion

        #region Logic

        void OnEnable()
        {
            titleContent = new GUIContent("Scene Loader", Icon("SceneAsset Icon"));
            minSize = new Vector2(320, 220);
            Subscribe(true);
        }

        void OnDisable()
        {
            Subscribe(false);
            recordingShortcut = false;
        }

        void OnLostFocus()
        {
            if (!recordingShortcut) return;
            recordingShortcut = false;
            Refresh();
        }

        void Subscribe(bool subscribe)
        {
            EditorApplication.projectChanged -= Rescan;
            EditorBuildSettings.sceneListChanged -= Rescan;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorSceneManager.sceneOpened -= OnSceneEvent;
            EditorSceneManager.sceneClosed -= OnSceneEvent;
            EditorSceneManager.newSceneCreated -= OnSceneEvent;
            EditorSceneManager.activeSceneChangedInEditMode -= OnSceneEvent;
            EditorSceneManager.sceneDirtied -= OnSceneEvent;
            EditorSceneManager.sceneSaved -= OnSceneEvent;
            SceneManager.sceneLoaded -= OnSceneEvent;
            SceneManager.sceneUnloaded -= OnSceneEvent;
            SceneManager.activeSceneChanged -= OnSceneEvent;
            ShortcutManager.instance.shortcutBindingChanged -= OnBindingChanged;
            if (!subscribe) return;
            EditorApplication.projectChanged += Rescan;
            EditorBuildSettings.sceneListChanged += Rescan;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorSceneManager.sceneOpened += OnSceneEvent;
            EditorSceneManager.sceneClosed += OnSceneEvent;
            EditorSceneManager.newSceneCreated += OnSceneEvent;
            EditorSceneManager.activeSceneChangedInEditMode += OnSceneEvent;
            EditorSceneManager.sceneDirtied += OnSceneEvent;
            EditorSceneManager.sceneSaved += OnSceneEvent;
            SceneManager.sceneLoaded += OnSceneEvent;
            SceneManager.sceneUnloaded += OnSceneEvent;
            SceneManager.activeSceneChanged += OnSceneEvent;
            ShortcutManager.instance.shortcutBindingChanged += OnBindingChanged;
        }

        void OnSceneEvent(Scene scene) => RequestRefresh();
        void OnSceneEvent(Scene previous, Scene next) => RequestRefresh();
        void OnSceneEvent(Scene scene, OpenSceneMode mode) => RequestRefresh();
        void OnSceneEvent(Scene scene, LoadSceneMode mode) => RequestRefresh();
        void OnSceneEvent(Scene scene, NewSceneSetup setup, NewSceneMode mode) => RequestRefresh();
        void OnPlayModeChanged(PlayModeStateChange change) => RequestRefresh();
        void OnBindingChanged(ShortcutBindingChangedEventArgs args) => RequestRefresh();

        void RequestRefresh()
        {
            if (refreshQueued || tiles == null) return;
            refreshQueued = true;
            rootVisualElement.schedule.Execute(() =>
            {
                refreshQueued = false;
                Refresh();
            });
        }

        void Rescan()
        {
            scenePaths.Clear();
            buildIndices.Clear();
            var roots = FSceneLoaderProjectSettings.instance.ValidScanRoots();
            if (roots.Count > 0)
                foreach (var guid in AssetDatabase.FindAssets("t:SceneAsset", roots.ToArray()))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!scenePaths.Contains(path)) scenePaths.Add(path);
                }
            foreach (var entry in EditorBuildSettings.scenes)
            {
                if (!entry.enabled) continue;
                if (AssetDatabase.GetMainAssetTypeAtPath(entry.path) != typeof(SceneAsset))
                    Debug.LogWarning($"Scene Loader: Build Settings lists a missing scene '{entry.path}'.");
                else buildIndices[entry.path] = SceneUtility.GetBuildIndexByScenePath(entry.path);
            }
            BuildTree(roots);
            Refresh();
        }

        void BuildTree(List<string> roots)
        {
            folders.Clear();
            sceneFolders.Clear();
            topLevel.Clear();
            var rawRoots = roots.Select(root => new RawFolder(root, Path.GetFileName(root))).ToList();
            foreach (var path in scenePaths)
            {
                var owner = rawRoots.Where(root => path.StartsWith(root.Key + "/")).OrderByDescending(root => root.Key.Length).FirstOrDefault();
                if (owner == null) continue;
                var segments = path.Substring(owner.Key.Length + 1).Split('/');
                var folder = owner;
                for (var i = 0; i < segments.Length - 1; i++) folder = folder.Child(segments[i]);
                folder.Files.Add(path);
            }

            var groups = new List<RawFolder>();
            foreach (var root in rawRoots)
            {
                if (root.Key != "Assets")
                {
                    if (root.Files.Count > 0 || root.Dirs.Count > 0) groups.Add(root);
                    continue;
                }
                groups.AddRange(root.Dirs.Values);
                if (root.Files.Count == 0) continue;
                var loose = new RawFolder("Assets", "Assets");
                loose.Files.AddRange(root.Files);
                groups.Add(loose);
            }

            var palette = FSceneLoaderProjectSettings.Palette;
            var nodes = groups.Select((group, index) => Compact(group, null, true, palette[index % palette.Length].color)).ToList();
            var order = FSceneLoaderProjectSettings.instance.FolderOrder.ToList();
            topLevel.AddRange(nodes.OrderBy(node =>
            {
                var index = order.IndexOf(node.Key);
                return index < 0 ? int.MaxValue : index;
            }));
        }

        FolderNode Compact(RawFolder raw, FolderNode parent, bool top, Color autoTint)
        {
            var tail = raw;
            while (tail.Files.Count == 0 && tail.Dirs.Count == 1) tail = tail.Dirs.Values.First();
            var rule = FSceneLoaderProjectSettings.instance.RuleFor(raw.Key);
            var autoLabel = top ? Regex.Replace(raw.Name.TrimStart('_'), "(?<=[a-z])(?=[A-Z])", " ") : raw.Name;
            var node = new FolderNode
            {
                Key = raw.Key,
                ContentPath = tail.Key,
                AutoLabel = autoLabel,
                Label = string.IsNullOrEmpty(rule?.label) ? autoLabel : rule.label,
                Parent = parent,
                Tint = rule != null && rule.customColor ? rule.color : parent?.Tint ?? autoTint,
                OwnHidden = rule != null && rule.hidden,
            };
            node.Hidden = node.OwnHidden || (parent?.Hidden ?? false);
            foreach (var path in tail.Files.OrderBy(Path.GetFileNameWithoutExtension, Natural))
            {
                node.Scenes.Add(path);
                sceneFolders[path] = node;
            }
            foreach (var dir in tail.Dirs.Values) node.Children.Add(Compact(dir, node, false, autoTint));
            node.Count = node.Scenes.Count + node.Children.Sum(child => child.Count);
            folders[node.Key] = node;
            return node;
        }

        bool IsVisible(FolderNode node) => !node.Hidden || FSceneLoaderStore.instance.ShowHidden;

        bool IsVisibleScene(string path) => !sceneFolders.TryGetValue(path, out var node) || IsVisible(node);

        Color TintOfScene(string path) => sceneFolders.TryGetValue(path, out var node) ? node.Tint : NeutralTint;

        List<string> RankByQuery()
        {
            var ranked = new List<(string path, int score)>();
            foreach (var path in scenePaths)
            {
                if (!IsVisibleScene(path)) continue;
                var score = FuzzyScore(Path.GetFileNameWithoutExtension(path), query);
                if (score > int.MinValue) ranked.Add((path, score));
            }
            return ranked.OrderByDescending(entry => entry.score).ThenBy(entry => entry.path, Natural).Select(entry => entry.path).ToList();
        }

        static int FuzzyScore(string text, string pattern)
        {
            var score = 0;
            var cursor = 0;
            var previous = -2;
            foreach (var ch in pattern)
            {
                if (char.IsWhiteSpace(ch)) continue;
                var found = text.IndexOf(ch.ToString(), cursor, StringComparison.OrdinalIgnoreCase);
                if (found < 0) return int.MinValue;
                score += found == previous + 1 ? 6 : 1;
                if (found == 0 || text[found - 1] == '_' || text[found - 1] == ' ' || char.IsUpper(text[found])) score += 4;
                previous = found;
                cursor = found + 1;
            }
            return score * 10 - text.Length;
        }

        void Navigate(string source)
        {
            var store = FSceneLoaderStore.instance;
            store.CurrentFolder = source;
            if (folders.TryGetValue(source, out var node))
                for (var parent = node.Parent; parent != null; parent = parent.Parent) store.SetExpanded(parent.Key, true);
            query = string.Empty;
            rootVisualElement.Q<ToolbarSearchField>()?.SetValueWithoutNotify(string.Empty);
            Refresh();
        }

        bool CheckEditable()
        {
            if (FSceneOps.CanEdit) return true;
            ShowNotification(new GUIContent("Exit Play Mode first"));
            return false;
        }

        void OpenScene(string path)
        {
            if (CheckEditable()) FSceneOps.Open(path);
        }

        void AddScene(string path)
        {
            if (CheckEditable() && !FSceneOps.AddAdditive(path))
                ShowNotification(new GUIContent($"{Path.GetFileNameWithoutExtension(path)} is already in the Hierarchy"));
        }

        void ToggleFavorite(string path)
        {
            FSceneLoaderStore.instance.ToggleFavorite(path);
            Refresh();
        }

        void Select(string path)
        {
            selectedPath = path;
            foreach (var tile in tiles.Children()) tile.EnableInClassList("is-selected", Equals(tile.userData, path));
        }

        void EditFolder(FolderNode node, Action<FSceneLoaderProjectSettings.FolderRule> edit)
        {
            FSceneLoaderProjectSettings.instance.EditRule(node.Key, edit);
            RescanAll();
        }

        Rect ScreenRectOf(VisualElement element)
        {
            var bounds = element.worldBound;
            if (element.panel == null || float.IsNaN(bounds.x) || float.IsNaN(bounds.y))
                return new Rect(position.x + 40, position.y + 40, 0, 0);
            return new Rect(position.x + bounds.x, position.y + bounds.y - rootVisualElement.worldBound.y, bounds.width, bounds.height);
        }

        int ReorderTarget(float pointerY)
        {
            for (var i = 0; i < topRows.Count; i++)
                if (pointerY < topRows[i].row.worldBound.center.y) return i;
            return topRows.Count;
        }

        void ApplyReorder(FolderNode node, int target)
        {
            var visible = topRows.Select(entry => entry.node).ToList();
            var from = visible.IndexOf(node);
            if (target == from || target == from + 1)
            {
                Refresh();
                return;
            }
            var anchor = target < visible.Count ? visible[target] : null;
            var order = topLevel.Where(other => other != node).ToList();
            order.Insert(anchor != null ? order.IndexOf(anchor) : order.Count, node);
            FSceneLoaderProjectSettings.instance.SetFolderOrder(order.Select(other => other.Key));
            RescanAll();
        }

        static void StartSceneDrag(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
            if (asset == null) return;
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = new Object[] { asset };
            DragAndDrop.paths = new[] { path };
            DragAndDrop.StartDrag(asset.name);
        }

        static List<string> DraggedScenePaths() =>
            DragAndDrop.paths.Where(path => path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)).ToList();

        void StartRecording()
        {
            recordingShortcut = true;
            rootVisualElement.focusable = true;
            rootVisualElement.Focus();
            Refresh();
        }

        void OnRecordKey(KeyDownEvent evt)
        {
            if (!recordingShortcut) return;
            evt.StopImmediatePropagation();
            if (evt.keyCode == KeyCode.None || IsModifierKey(evt.keyCode)) return;
            recordingShortcut = false;
            if (evt.keyCode != KeyCode.Escape) Rebind(new KeyCombination(evt.keyCode, ModifiersOf(evt)));
            Refresh();
        }

        static bool IsModifierKey(KeyCode key) => key >= KeyCode.RightShift && key <= KeyCode.AltGr;

        static ShortcutModifiers ModifiersOf(KeyDownEvent evt)
        {
            var modifiers = ShortcutModifiers.None;
            if (evt.actionKey) modifiers |= ShortcutModifiers.Action;
            if (evt.altKey) modifiers |= ShortcutModifiers.Alt;
            if (evt.shiftKey) modifiers |= ShortcutModifiers.Shift;
            return modifiers;
        }

        static void Rebind(KeyCombination combination)
        {
            var manager = ShortcutManager.instance;
            var binding = new ShortcutBinding(combination);
            var clashes = manager.GetAvailableShortcutIds()
                .Where(id => id != ShortcutId && manager.GetShortcutBinding(id).Equals(binding)).ToList();
            if (clashes.Count > 0 && !EditorUtility.DisplayDialog("Shortcut already in use",
                    $"{binding} is also bound to:\n{string.Join("\n", clashes.Take(8))}\n\nBind it to Scene Loader anyway?", "Bind", "Cancel"))
                return;
            if (manager.IsProfileReadOnly(manager.activeProfileId))
            {
                if (!EditorUtility.DisplayDialog("Read-only shortcut profile",
                        $"The active shortcut profile '{manager.activeProfileId}' cannot be edited.\nSwitch to profile '{FallbackProfile}' (created if missing)?", "Switch", "Cancel"))
                    return;
                if (!manager.GetAvailableProfileIds().Contains(FallbackProfile)) manager.CreateProfile(FallbackProfile);
                manager.activeProfileId = FallbackProfile;
            }
            manager.RebindShortcut(ShortcutId, binding);
        }

        void ResetShortcut()
        {
            ShortcutManager.instance.ClearShortcutOverride(ShortcutId);
            Refresh();
        }

        void ShowSettingsMenu()
        {
            var store = FSceneLoaderStore.instance;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Refresh"), false, Rescan);
            menu.AddItem(new GUIContent("Show Hidden Folders"), store.ShowHidden, () =>
            {
                store.ShowHidden = !store.ShowHidden;
                Refresh();
            });
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Clear Recent"), false, () =>
            {
                store.ClearRecent();
                Refresh();
            });
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Project Settings..."), false, () => SettingsService.OpenProjectSettings(FSceneLoaderSettingsProvider.SettingsPath));
            menu.AddItem(new GUIContent("Shortcuts Manager..."), false, () => EditorApplication.ExecuteMenuItem("Edit/Shortcuts..."));
            menu.ShowAsContext();
        }

        #endregion

        #region UI

        void CreateGUI()
        {
            sceneIcon = Icon("SceneAsset Icon");
            folderIcon = Icon("Folder Icon");
            starIcon = Icon("Favorite Icon");

            var root = rootVisualElement;
            var sheet = LoadStyleSheet();
            if (sheet != null) root.styleSheets.Add(sheet);
            root.AddToClassList("fsl-root");
            root.RegisterCallback<KeyDownEvent>(OnRecordKey, TrickleDown.TrickleDown);

            var banner = new Label("Play Mode - exit Play Mode to open or add scenes.");
            banner.AddToClassList("fsl-banner");
            root.Add(banner);

            var bar = new Toolbar();
            bar.AddToClassList("fsl-bar");
            crumbs = new VisualElement();
            crumbs.AddToClassList("fsl-crumbs");
            bar.Add(crumbs);
            var search = new ToolbarSearchField { tooltip = "Search all scenes" };
#if UNITY_6000_0_OR_NEWER
            search.placeholderText = "Search all scenes";
#endif
            search.AddToClassList("fsl-search");
            search.RegisterValueChangedCallback(OnSearchChanged);
            bar.Add(search);
            var settings = new ToolbarButton(ShowSettingsMenu) { tooltip = "Settings" };
            settings.AddToClassList("fsl-settings");
            settings.Add(new Image { image = Icon("_Popup") });
            bar.Add(settings);
            root.Add(bar);

            var split = new TwoPaneSplitView(0, 150, TwoPaneSplitViewOrientation.Horizontal) { viewDataKey = "fsl-split" };
            split.AddToClassList("fsl-split");
            sidebar = new ScrollView(ScrollViewMode.Vertical);
            sidebar.AddToClassList("fsl-side");
            sidebar.RegisterCallback<PointerDownEvent>(_ => suppressClick = false, TrickleDown.TrickleDown);
            split.Add(sidebar);
            var tileScroll = new ScrollView(ScrollViewMode.Vertical);
            tileScroll.AddToClassList("fsl-tiles");
            tiles = tileScroll.contentContainer;
            tiles.AddToClassList("fsl-tiles__content");
            split.Add(tileScroll);
            root.Add(split);

            dock = new VisualElement();
            dock.AddToClassList("fsl-dock");
            dock.RegisterCallback<DragUpdatedEvent>(OnDockDragUpdated);
            dock.RegisterCallback<DragPerformEvent>(OnDockDragPerform);
            dock.RegisterCallback<DragLeaveEvent>(OnDockDragLeave);
            dock.RegisterCallback<DragExitedEvent>(OnDockDragExited);
            root.Add(dock);

            var foot = new VisualElement();
            foot.AddToClassList("fsl-foot");
            shortcutChip = new Button(StartRecording) { tooltip = "Click, then press the new shortcut (Esc cancels)" };
            shortcutChip.AddToClassList("fsl-kb");
            foot.Add(shortcutChip);
            shortcutReset = new Button(ResetShortcut) { tooltip = "Reset shortcut to default (Alt+S)" };
            shortcutReset.AddToClassList("fsl-icon-btn");
            shortcutReset.Add(new Image { image = Icon("Refresh") });
            foot.Add(shortcutReset);
            var hint = new Label("toggle window");
            hint.AddToClassList("fsl-dim");
            foot.Add(hint);
            var spacer = new VisualElement();
            spacer.AddToClassList("fsl-spacer");
            foot.Add(spacer);
            countLabel = new Label();
            countLabel.AddToClassList("fsl-dim");
            foot.Add(countLabel);
            root.Add(foot);

            Rescan();
        }

        void OnSearchChanged(ChangeEvent<string> evt)
        {
            query = evt.newValue.Trim();
            Refresh();
        }

        void Refresh()
        {
            if (tiles == null) return;
            var store = FSceneLoaderStore.instance;
            var source = store.CurrentFolder;
            var validFolder = source != null && folders.TryGetValue(source, out var current) && IsVisible(current);
            if (source != SourceFavorites && source != SourceRecent && source != SourceBuild && !validFolder)
                store.CurrentFolder = topLevel.FirstOrDefault(IsVisible)?.Key ?? SourceFavorites;
            rootVisualElement.EnableInClassList("fsl-root--playing", !FSceneOps.CanEdit);
            BuildSidebar(store);
            BuildContent(store);
            BuildDock();
            shortcutChip.text = recordingShortcut ? "Press keys... (Esc)" : ShortcutLabel();
            shortcutChip.EnableInClassList("is-recording", recordingShortcut);
            shortcutReset.style.display = ShortcutManager.instance.IsShortcutOverridden(ShortcutId) ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void BuildSidebar(FSceneLoaderStore store)
        {
            sidebar.Clear();
            topRows.Clear();
            sidebar.Add(SideRow(store, SourceFavorites, "Favorites", starIcon, store.FavoritePaths().Count, 0, null));
            sidebar.Add(SideRow(store, SourceRecent, "Recent", Icon("UnityEditor.HistoryWindow"), store.RecentPaths().Count, 0, null));
            sidebar.Add(SideRow(store, SourceBuild, "In Build", Icon("BuildSettings.Editor.Small"), buildIndices.Count, 0, null));
            var separator = new VisualElement();
            separator.AddToClassList("fsl-separator");
            sidebar.Add(separator);
            foreach (var node in topLevel.Where(IsVisible)) topRows.Add((node, AddFolderRows(store, node, 0)));
            if (topLevel.Count == 0)
            {
                var empty = new Label("No scenes under the scan roots");
                empty.AddToClassList("fsl-empty");
                sidebar.Add(empty);
            }
            reorderLine = new VisualElement();
            reorderLine.AddToClassList("fsl-reorder-line");
            sidebar.Add(reorderLine);
        }

        VisualElement AddFolderRows(FSceneLoaderStore store, FolderNode node, int depth)
        {
            var row = SideRow(store, node.Key, node.Label, folderIcon, node.Count, depth, node);
            sidebar.Add(row);
            if (store.IsExpanded(node.Key))
                foreach (var child in node.Children.Where(IsVisible)) AddFolderRows(store, child, depth + 1);
            return row;
        }

        VisualElement SideRow(FSceneLoaderStore store, string source, string label, Texture icon, int count, int depth, FolderNode node)
        {
            var row = new VisualElement { tooltip = node?.ContentPath };
            row.AddToClassList("fsl-side-row");
            row.EnableInClassList("is-selected", query.Length == 0 && store.CurrentFolder == source);
            row.EnableInClassList("is-hidden", node != null && node.Hidden);
            row.style.paddingLeft = 2 + depth * 12;
            var hasChildren = node != null && node.Children.Any(IsVisible);
            var expanded = hasChildren && store.IsExpanded(source);
            var arrow = new Label(hasChildren ? expanded ? "▾" : "▸" : string.Empty);
            arrow.AddToClassList("fsl-arrow");
            if (hasChildren)
                arrow.RegisterCallback<ClickEvent>(evt =>
                {
                    evt.StopPropagation();
                    store.SetExpanded(source, !expanded);
                    Refresh();
                });
            row.Add(arrow);
            var image = new Image { image = icon };
            image.AddToClassList("fsl-side-icon");
            if (node != null) image.tintColor = node.Tint;
            row.Add(image);
            var name = new Label(label);
            name.AddToClassList("fsl-side-name");
            row.Add(name);
            var total = new Label(count.ToString());
            total.AddToClassList("fsl-count");
            row.Add(total);
            row.RegisterCallback<ClickEvent>(_ =>
            {
                if (suppressClick) suppressClick = false;
                else Navigate(source);
            });
            if (node == null) return row;
            row.AddManipulator(new ContextualMenuManipulator(evt => FillFolderMenu(evt, node, row)));
            if (depth == 0) RegisterReorder(row, node);
            return row;
        }

        void RegisterReorder(VisualElement row, FolderNode node)
        {
            row.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                reorderNode = node;
                reorderOrigin = evt.position;
            });
            row.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (reorderNode != node) return;
                if (!reordering && (evt.pressedButtons & 1) == 0)
                {
                    reorderNode = null;
                    return;
                }
                if (!reordering)
                {
                    if (Mathf.Abs(evt.position.y - reorderOrigin.y) < DragThreshold) return;
                    reordering = true;
                    row.CapturePointer(evt.pointerId);
                    row.AddToClassList("is-dragging");
                }
                var target = ReorderTarget(evt.position.y);
                var content = sidebar.contentContainer;
                reorderLine.style.top = target < topRows.Count
                    ? topRows[target].row.layout.y
                    : content.ElementAt(content.childCount - 2).layout.yMax;
                reorderLine.style.display = DisplayStyle.Flex;
            });
            row.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (reorderNode != node) return;
                reorderNode = null;
                if (!reordering) return;
                reordering = false;
                suppressClick = true;
                row.ReleasePointer(evt.pointerId);
                ApplyReorder(node, ReorderTarget(evt.position.y));
            });
        }

        void FillFolderMenu(ContextualMenuPopulateEvent evt, FolderNode node, VisualElement anchor)
        {
            var rule = FSceneLoaderProjectSettings.instance.RuleFor(node.Key);
            var custom = rule != null && rule.customColor;
            evt.menu.AppendAction("Edit Folder...", _ => FSceneLoaderFolderPopup.Open(node.Key, node.AutoLabel, ScreenRectOf(anchor)));
            evt.menu.AppendAction("Color/Auto", _ => EditFolder(node, edit => edit.customColor = false),
                custom ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Checked);
            foreach (var (name, color) in FSceneLoaderProjectSettings.Palette)
                evt.menu.AppendAction("Color/" + name, _ => EditFolder(node, edit =>
                {
                    edit.customColor = true;
                    edit.color = color;
                }), custom && rule.color == color ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            evt.menu.AppendAction(node.OwnHidden ? "Unhide" : "Hide", _ => EditFolder(node, edit => edit.hidden = !node.OwnHidden));
            evt.menu.AppendSeparator();
            evt.menu.AppendAction("Show in Project", _ => EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Object>(node.ContentPath)));
        }

        void BuildContent(FSceneLoaderStore store)
        {
            tiles.Clear();
            crumbs.Clear();
            var source = store.CurrentFolder;
            List<string> scenes;
            IEnumerable<FolderNode> subfolders = Array.Empty<FolderNode>();
            if (query.Length > 0)
            {
                scenes = RankByQuery();
                AddCrumb($"Search \"{query}\"", null);
            }
            else if (source == SourceFavorites)
            {
                scenes = store.FavoritePaths();
                AddCrumb("Favorites", null);
            }
            else if (source == SourceRecent)
            {
                scenes = store.RecentPaths();
                AddCrumb("Recent", null);
            }
            else if (source == SourceBuild)
            {
                scenes = buildIndices.OrderBy(entry => entry.Value).Select(entry => entry.Key).ToList();
                AddCrumb("In Build", null);
            }
            else
            {
                var node = folders[source];
                scenes = node.Scenes;
                subfolders = node.Children.Where(IsVisible);
                var chain = new List<FolderNode>();
                for (var folder = node; folder != null; folder = folder.Parent) chain.Insert(0, folder);
                foreach (var folder in chain) AddCrumb(folder.Label, folder.Key);
            }
            foreach (var folder in subfolders) tiles.Add(FolderTile(folder));
            foreach (var path in scenes) tiles.Add(SceneTile(store, path));
            if (tiles.childCount == 0)
            {
                var empty = new Label(query.Length > 0 ? "No scene matches"
                    : source == SourceFavorites ? "No favorites yet - hover a scene and click the star"
                    : source == SourceRecent ? "No recent scenes" : "Empty");
                empty.AddToClassList("fsl-empty");
                tiles.Add(empty);
            }
            countLabel.text = scenes.Count == 1 ? "1 scene" : $"{scenes.Count} scenes";
        }

        void AddCrumb(string text, string source)
        {
            if (crumbs.childCount > 0)
            {
                var separator = new Label("›");
                separator.AddToClassList("fsl-crumb-sep");
                crumbs.Add(separator);
            }
            var crumb = new Label(text);
            crumb.AddToClassList("fsl-crumb");
            if (source != null) crumb.RegisterCallback<ClickEvent>(_ => Navigate(source));
            crumbs.Add(crumb);
        }

        VisualElement FolderTile(FolderNode node)
        {
            var tile = new VisualElement { tooltip = node.ContentPath + "\nDouble-click to open" };
            tile.AddToClassList("fsl-tile");
            tile.AddToClassList("fsl-tile--folder");
            tile.EnableInClassList("is-hidden", node.Hidden);
            var band = new VisualElement();
            band.AddToClassList("fsl-tile__band");
            band.style.backgroundColor = node.Tint;
            tile.Add(band);
            var count = new Label(node.Count.ToString());
            count.AddToClassList("fsl-tile__count");
            tile.Add(count);
            var icon = new Image { image = folderIcon, tintColor = node.Tint };
            icon.AddToClassList("fsl-tile__icon");
            tile.Add(icon);
            var name = new Label(node.Label);
            name.AddToClassList("fsl-tile__name");
            tile.Add(name);
            tile.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.button == 0 && evt.clickCount == 2) Navigate(node.Key);
            });
            tile.AddManipulator(new ContextualMenuManipulator(evt => FillFolderMenu(evt, node, tile)));
            return tile;
        }

        VisualElement SceneTile(FSceneLoaderStore store, string path)
        {
            var tile = new VisualElement { tooltip = path, userData = path };
            tile.AddToClassList("fsl-tile");
            tile.EnableInClassList("is-selected", selectedPath == path);
            var band = new VisualElement();
            band.AddToClassList("fsl-tile__band");
            band.style.backgroundColor = TintOfScene(path);
            tile.Add(band);
            if (buildIndices.TryGetValue(path, out var index))
            {
                var badge = new Label("#" + index);
                badge.AddToClassList("fsl-tile__index");
                tile.Add(badge);
            }
            var icon = new Image { image = sceneIcon };
            icon.AddToClassList("fsl-tile__icon");
            tile.Add(icon);
            var name = new Label(Path.GetFileNameWithoutExtension(path));
            name.AddToClassList("fsl-tile__name");
            tile.Add(name);
            var favorite = store.IsFavorite(path);
            if (favorite)
            {
                var star = new Image { image = starIcon };
                star.AddToClassList("fsl-tile__star");
                tile.Add(star);
            }
            var scene = SceneManager.GetSceneByPath(path);
            var dot = new VisualElement();
            dot.AddToClassList("fsl-dot");
            dot.EnableInClassList("is-loaded", scene.IsValid() && scene.isLoaded);
            dot.EnableInClassList("is-active", scene.IsValid() && scene == SceneManager.GetActiveScene());
            tile.Add(dot);

            var actions = new VisualElement();
            actions.AddToClassList("fsl-tile__actions");
            actions.Add(TileButton("Toolbar Plus", "Add additive (Alt+Click)", () => AddScene(path)));
            actions.Add(TileButton("SceneLoadIn", "Open single (Double-click)", () => OpenScene(path)));
            var favoriteButton = TileButton("Favorite Icon", favorite ? "Remove from Favorites" : "Add to Favorites", () => ToggleFavorite(path));
            favoriteButton.EnableInClassList("is-on", favorite);
            actions.Add(favoriteButton);
            tile.Add(actions);

            tile.RegisterCallback<ClickEvent>(evt => OnSceneTileClick(evt, path));
            tile.AddManipulator(new ContextualMenuManipulator(evt => FillSceneMenu(evt, path)));
            var pressed = false;
            var origin = Vector3.zero;
            tile.RegisterCallback<PointerDownEvent>(evt =>
            {
                pressed = evt.button == 0;
                origin = evt.position;
            });
            tile.RegisterCallback<PointerUpEvent>(_ => pressed = false);
            tile.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if ((evt.pressedButtons & 1) == 0) pressed = false;
                if (!pressed || (evt.position - origin).sqrMagnitude < DragThreshold * DragThreshold) return;
                pressed = false;
                StartSceneDrag(path);
            });
            return tile;
        }

        static Button TileButton(string iconName, string tooltip, Action action)
        {
            var button = new Button(action) { tooltip = tooltip };
            button.AddToClassList("fsl-icon-btn");
            button.Add(new Image { image = Icon(iconName) });
            return button;
        }

        void OnSceneTileClick(ClickEvent evt, string path)
        {
            if (evt.button != 0 || evt.target is Button || (evt.target as VisualElement)?.parent is Button) return;
            if (evt.altKey) AddScene(path);
            else if (evt.clickCount == 2) OpenScene(path);
            else Select(path);
        }

        void FillSceneMenu(ContextualMenuPopulateEvent evt, string path)
        {
            var editable = FSceneOps.CanEdit ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
            var scene = SceneManager.GetSceneByPath(path);
            evt.menu.AppendAction("Open", _ => OpenScene(path), editable);
            evt.menu.AppendAction("Add Additive", _ => AddScene(path), editable);
            if (scene.IsValid())
            {
                evt.menu.AppendAction("Set Active Scene", _ => FSceneOps.SetActive(scene), editable);
                evt.menu.AppendAction("Remove from Hierarchy", _ => FSceneOps.Remove(scene),
                    SceneManager.sceneCount > 1 ? editable : DropdownMenuAction.Status.Disabled);
            }
            evt.menu.AppendSeparator();
            evt.menu.AppendAction("Favorite", _ => ToggleFavorite(path),
                FSceneLoaderStore.instance.IsFavorite(path) ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            evt.menu.AppendAction("Ping in Project", _ => EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<SceneAsset>(path)));
            evt.menu.AppendAction("Show in Explorer", _ => EditorUtility.RevealInFinder(path));
        }

        void BuildDock()
        {
            dock.Clear();
            var title = new Label("LOADED");
            title.AddToClassList("fsl-dock__title");
            dock.Add(title);
            var scenes = FSceneOps.HierarchyScenes();
            var active = SceneManager.GetActiveScene();
            foreach (var scene in scenes) dock.Add(SceneChip(scene, scene == active, scenes.Count > 1));
            var spacer = new VisualElement();
            spacer.AddToClassList("fsl-spacer");
            dock.Add(spacer);
            var hint = new Label("Drop scenes here");
            hint.AddToClassList("fsl-dock__hint");
            dock.Add(hint);
        }

        VisualElement SceneChip(Scene scene, bool active, bool removable)
        {
            var chip = new VisualElement { tooltip = active ? scene.path : scene.path + "\nClick to make it the Active scene" };
            chip.AddToClassList("fsl-chip");
            chip.EnableInClassList("is-active", active);
            chip.EnableInClassList("is-unloaded", !scene.isLoaded);
            var dot = new VisualElement();
            dot.AddToClassList("fsl-dot");
            dot.EnableInClassList(active ? "is-active" : "is-loaded", scene.isLoaded);
            chip.Add(dot);
            var sceneName = string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name;
            var name = new Label(scene.isDirty ? sceneName + "*" : sceneName);
            name.AddToClassList("fsl-chip__name");
            chip.Add(name);
            if (removable)
            {
                var close = new Button(() => FSceneOps.Remove(scene)) { text = "×", tooltip = "Remove from Hierarchy" };
                close.AddToClassList("fsl-chip__close");
                chip.Add(close);
            }
            chip.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.button == 0 && !active && !(evt.target is Button) && CheckEditable()) FSceneOps.SetActive(scene);
            });
            chip.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                var editable = FSceneOps.CanEdit ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
                evt.menu.AppendAction("Set Active Scene", _ => FSceneOps.SetActive(scene), active ? DropdownMenuAction.Status.Disabled : editable);
                evt.menu.AppendAction("Remove from Hierarchy", _ => FSceneOps.Remove(scene), removable ? editable : DropdownMenuAction.Status.Disabled);
                evt.menu.AppendAction("Ping in Project", _ => EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path)));
            }));
            return chip;
        }

        void OnDockDragUpdated(DragUpdatedEvent evt)
        {
            var accept = FSceneOps.CanEdit && DraggedScenePaths().Count > 0;
            DragAndDrop.visualMode = accept ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            dock.EnableInClassList("is-drop-target", accept);
        }

        void OnDockDragPerform(DragPerformEvent evt)
        {
            DragAndDrop.AcceptDrag();
            dock.RemoveFromClassList("is-drop-target");
            foreach (var path in DraggedScenePaths()) FSceneOps.AddAdditive(path);
        }

        void OnDockDragLeave(DragLeaveEvent evt) => dock.RemoveFromClassList("is-drop-target");

        void OnDockDragExited(DragExitedEvent evt) => dock.RemoveFromClassList("is-drop-target");

        #endregion
    }
}
