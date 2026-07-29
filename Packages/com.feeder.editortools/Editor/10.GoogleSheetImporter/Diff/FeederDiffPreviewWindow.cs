using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public sealed class FeederDiffPreviewWindow : EditorWindow
    {
        private const int MaxVisualRows = 20000;
        private const float ToolbarHeight = 20f;
        private const float BottomBarHeight = 40f;
        private const float CheckboxWidth = 18f;
        private const float FoldoutWidth = 14f;

        private enum RowKind
        {
            FileHeader,
            ItemHeader,
            HunkHeader,
            DiffLine,
            Message,
        }

        private struct VisualRow
        {
            public RowKind Kind;
            public int File;
            public int Item;
            public int Hunk;
            public int Line;
            public string Message;
        }

        private static FeederDiffPreviewWindow current;

        [NonSerialized] private FeederChangeSet changeSet;
        [NonSerialized] private Action<FeederDiffApplyResult> onComplete;
        [NonSerialized] private bool live;
        [NonSerialized] private bool completed;
        [NonSerialized] private bool pendingClose;

        [NonSerialized] private bool[] fileSelected;
        [NonSerialized] private bool[] fileExpanded;
        [NonSerialized] private bool[][] itemSelected;
        [NonSerialized] private string[] pendingText;
        [NonSerialized] private FeederDiffFileResult[] diffs;

        [NonSerialized] private readonly List<VisualRow> rows = new List<VisualRow>();
        [NonSerialized] private float[] rowTop;
        [NonSerialized] private float totalHeight;
        [NonSerialized] private int maxLineChars;
        [NonSerialized] private bool truncated;

        [NonSerialized] private Vector2 scroll;
        [NonSerialized] private string search = string.Empty;
        [NonSerialized] private int hoverRow = -1;

        public static void Open(string title, FeederChangeSet set, Action<FeederDiffApplyResult> onCompleteCallback)
        {
            if (set == null || set.Files.Count == 0)
            {
                EditorUtility.DisplayDialog(title, "Không có thay đổi nào để xem trước.", "close");
                onCompleteCallback?.Invoke(new FeederDiffApplyResult { Accepted = false });
                return;
            }

            if (current != null)
            {
                current.Close();
                current = null;
            }

            FeederDiffPreviewWindow window = CreateInstance<FeederDiffPreviewWindow>();
            window.titleContent = new GUIContent(title);
            window.minSize = new Vector2(820f, 480f);
            window.changeSet = set;
            window.onComplete = onCompleteCallback;
            window.live = true;
            window.RebuildAll();
            window.position = Centered(1280f, 800f, 0.9f);
            window.wantsMouseMove = true;
            window.ShowUtility();
            current = window;
        }

        private static Rect Centered(float preferredWidth, float preferredHeight, float ratio)
        {
            Rect main = EditorGUIUtility.GetMainWindowPosition();
            float width = Mathf.Min(preferredWidth, main.width * ratio);
            float height = Mathf.Min(preferredHeight, main.height * ratio);
            return new Rect(main.x + (main.width - width) * 0.5f, main.y + (main.height - height) * 0.5f, width, height);
        }

        private void OnEnable()
        {
            EditorApplication.delayCall += () =>
            {
                if (this != null && !live)
                {
                    Close();
                }
            };
        }

        private void OnDestroy()
        {
            if (current == this)
            {
                current = null;
            }

            if (!completed && onComplete != null)
            {
                Action<FeederDiffApplyResult> callback = onComplete;
                onComplete = null;
                callback(new FeederDiffApplyResult { Accepted = false });
            }
        }

        private void RebuildAll()
        {
            int count = changeSet.Files.Count;
            fileSelected = new bool[count];
            fileExpanded = new bool[count];
            itemSelected = new bool[count][];
            pendingText = new string[count];
            diffs = new FeederDiffFileResult[count];

            for (int i = 0; i < count; i++)
            {
                FeederFileChange file = changeSet.Files[i];
                itemSelected[i] = new bool[file.Items.Count];
                for (int j = 0; j < file.Items.Count; j++)
                {
                    itemSelected[i][j] = !file.Items[j].IsBlocked;
                }

                pendingText[i] = file.NewText;
                diffs[i] = FeederTextDiff.Compute(file.OriginalText, file.NewText ?? string.Empty);
                fileSelected[i] = diffs[i].HasChanges;
                fileExpanded[i] = true;
            }

            RebuildRows();
        }

        private void RecomputeFile(int fileIndex)
        {
            FeederFileChange file = changeSet.Files[fileIndex];
            if (file.Rebuild != null)
            {
                List<FeederChangeItem> picked = new List<FeederChangeItem>();
                for (int j = 0; j < file.Items.Count; j++)
                {
                    if (itemSelected[fileIndex][j])
                    {
                        picked.Add(file.Items[j]);
                    }
                }

                pendingText[fileIndex] = file.Rebuild(picked);
            }
            else
            {
                pendingText[fileIndex] = file.NewText;
            }

            diffs[fileIndex] = FeederTextDiff.Compute(file.OriginalText, pendingText[fileIndex] ?? string.Empty);
            if (!diffs[fileIndex].HasChanges)
            {
                fileSelected[fileIndex] = false;
            }

            RebuildRows();
        }

        private bool PassesFilter(int fileIndex)
        {
            if (string.IsNullOrEmpty(search))
            {
                return true;
            }

            FeederFileChange file = changeSet.Files[fileIndex];
            if (file.AssetPath != null &&
                file.AssetPath.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            for (int j = 0; j < file.Items.Count; j++)
            {
                string title = file.Items[j].Title;
                if (title != null && title.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void RebuildRows()
        {
            rows.Clear();
            maxLineChars = 0;
            truncated = false;

            hoverRow = -1;

            for (int f = 0; f < changeSet.Files.Count; f++)
            {
                if (!PassesFilter(f))
                {
                    continue;
                }

                rows.Add(new VisualRow { Kind = RowKind.FileHeader, File = f });
                if (!fileExpanded[f])
                {
                    continue;
                }

                FeederFileChange file = changeSet.Files[f];
                for (int i = 0; i < file.Items.Count; i++)
                {
                    rows.Add(new VisualRow { Kind = RowKind.ItemHeader, File = f, Item = i });
                }

                FeederDiffFileResult diff = diffs[f];
                if (diff.MaxLineChars > maxLineChars)
                {
                    maxLineChars = diff.MaxLineChars;
                }

                if (diff.Hunks.Count == 0)
                {
                    rows.Add(new VisualRow
                    {
                        Kind = RowKind.Message,
                        File = f,
                        Message = "Không có thay đổi nào được chọn cho file này.",
                    });
                    continue;
                }

                if (diff.BailedOut)
                {
                    rows.Add(new VisualRow
                    {
                        Kind = RowKind.Message,
                        File = f,
                        Message = "Khác biệt quá lớn — hiển thị dạng thay toàn bộ nội dung.",
                    });
                }

                for (int h = 0; h < diff.Hunks.Count && !truncated; h++)
                {
                    rows.Add(new VisualRow { Kind = RowKind.HunkHeader, File = f, Hunk = h });
                    List<FeederDiffLine> lines = diff.Hunks[h].Lines;
                    for (int l = 0; l < lines.Count; l++)
                    {
                        if (rows.Count >= MaxVisualRows)
                        {
                            truncated = true;
                            rows.Add(new VisualRow
                            {
                                Kind = RowKind.Message,
                                File = f,
                                Message = "Diff quá lớn, chỉ hiển thị phần đầu. Dùng 'Copy diff' để xem đầy đủ.",
                            });
                            break;
                        }

                        rows.Add(new VisualRow { Kind = RowKind.DiffLine, File = f, Hunk = h, Line = l });
                    }
                }
            }

            RebuildRowOffsets();
        }

        private void RebuildRowOffsets()
        {
            rowTop = new float[rows.Count + 1];
            float y = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                rowTop[i] = y;
                y += HeightOf(rows[i].Kind);
            }

            rowTop[rows.Count] = y;
            totalHeight = y;
        }

        private static float HeightOf(RowKind kind)
        {
            switch (kind)
            {
                case RowKind.FileHeader:
                    return FeederDiffStyles.FileHeaderHeight;
                case RowKind.ItemHeader:
                    return FeederDiffStyles.ItemHeaderHeight;
                case RowKind.HunkHeader:
                    return FeederDiffStyles.HunkHeaderHeight;
                case RowKind.Message:
                    return FeederDiffStyles.ItemHeaderHeight;
                default:
                    return FeederDiffStyles.LineHeight;
            }
        }

        private int IndexAtY(float y)
        {
            if (rows.Count == 0)
            {
                return 0;
            }

            int index = Array.BinarySearch(rowTop, 0, rows.Count, y);
            if (index < 0)
            {
                index = ~index - 1;
            }

            return Mathf.Clamp(index, 0, rows.Count - 1);
        }

        private void OnGUI()
        {
            if (changeSet == null || onComplete == null)
            {
                DrawExpiredSession();
                return;
            }

            FeederDiffStyles.Refresh();
            HandleKeyboard();
            DrawToolbar();
            DrawIssues();

            Rect viewport = GUILayoutUtility.GetRect(10f, 100000f, 10f, 100000f,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            DrawBody(viewport);
            DrawBottomBar();

            if (pendingClose)
            {
                pendingClose = false;
                Close();
            }
        }

        private void DrawExpiredSession()
        {
            EditorGUILayout.HelpBox(
                "Phiên xem trước đã mất do Unity biên dịch lại script. Đóng cửa sổ và chạy lại.",
                MessageType.Warning);
            if (GUILayout.Button("Close"))
            {
                pendingClose = true;
            }

            if (pendingClose)
            {
                pendingClose = false;
                Close();
            }
        }

        private void HandleKeyboard()
        {
            Event evt = Event.current;
            if (evt.type != EventType.KeyDown)
            {
                return;
            }

            if (evt.keyCode == KeyCode.Escape)
            {
                evt.Use();
                Cancel();
            }
            else if ((evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) && evt.control)
            {
                evt.Use();

                if (SelectedCount() > 0)
                {
                    Apply();
                }
                else
                {
                    ShowNotification(new GUIContent("Chưa chọn file nào"));
                }
            }
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar, GUILayout.Height(ToolbarHeight));

            if (GUILayout.Button("Select All", EditorStyles.toolbarButton, GUILayout.Width(72f)))
            {
                SetAllSelected(true);
            }

            if (GUILayout.Button("None", EditorStyles.toolbarButton, GUILayout.Width(50f)))
            {
                SetAllSelected(false);
            }

            if (GUILayout.Button("Expand All", EditorStyles.toolbarButton, GUILayout.Width(78f)))
            {
                SetAllExpanded(true);
            }

            if (GUILayout.Button("Collapse All", EditorStyles.toolbarButton, GUILayout.Width(84f)))
            {
                SetAllExpanded(false);
            }

            GUILayout.Space(8f);
            string next = GUILayout.TextField(search, EditorStyles.toolbarSearchField, GUILayout.Width(200f));
            if (next != search)
            {
                search = next;
                RebuildRows();
            }

            if (GUILayout.Button("Copy diff", EditorStyles.toolbarButton, GUILayout.Width(70f)))
            {
                CopyDiff();
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label(BuildSummary(), EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawIssues()
        {
            if (changeSet.Issues.Count == 0)
            {
                return;
            }

            EditorGUILayout.HelpBox(string.Join("\n", changeSet.Issues.ToArray()), MessageType.Warning);
        }

        private void DrawBody(Rect viewport)
        {
            float codeWidth = maxLineChars * FeederDiffStyles.CharWidth + 32f;
            float contentWidth = Mathf.Max(viewport.width - 16f, FeederDiffStyles.CodeOffsetX + codeWidth);

            UpdateHover(viewport);

            float[] tops = rowTop;
            scroll = GUI.BeginScrollView(viewport, scroll, new Rect(0f, 0f, contentWidth, totalHeight));
            if (rows.Count > 0)
            {
                int first = Mathf.Max(0, IndexAtY(scroll.y) - 1);
                int last = Mathf.Min(rows.Count, IndexAtY(scroll.y + viewport.height) + 2);
                for (int i = first; i < last && rowTop == tops; i++)
                {
                    Rect rect = new Rect(0f, tops[i], contentWidth, tops[i + 1] - tops[i]);
                    DrawRow(rect, i, contentWidth);
                }
            }

            GUI.EndScrollView();

            if (rowTop != tops)
            {
                Repaint();
            }
        }

        private void UpdateHover(Rect viewport)
        {
            Event evt = Event.current;
            if (evt.type == EventType.MouseMove || evt.type == EventType.MouseDrag)
            {
                int previous = hoverRow;
                hoverRow = viewport.Contains(evt.mousePosition)
                    ? IndexAtY(evt.mousePosition.y - viewport.y + scroll.y)
                    : -1;
                if (previous != hoverRow)
                {
                    Repaint();
                }
            }
        }

        private void DrawRow(Rect rect, int index, float contentWidth)
        {
            VisualRow row = rows[index];
            switch (row.Kind)
            {
                case RowKind.FileHeader:
                    DrawFileHeader(rect, row.File, contentWidth);
                    break;
                case RowKind.ItemHeader:
                    DrawItemHeader(rect, row.File, row.Item, contentWidth);
                    break;
                case RowKind.HunkHeader:
                    DrawHunkHeader(rect, row.File, row.Hunk, contentWidth);
                    break;
                case RowKind.Message:
                    DrawMessage(rect, row.Message, contentWidth);
                    break;
                default:
                    DrawDiffLine(rect, row, index, contentWidth);
                    break;
            }
        }

        private void DrawFileHeader(Rect rect, int fileIndex, float contentWidth)
        {
            FeederFileChange file = changeSet.Files[fileIndex];
            FeederDiffFileResult diff = diffs[fileIndex];

            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, FeederDiffStyles.FileHeaderBg);
                EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, contentWidth, 1f), FeederDiffStyles.Separator);
            }

            float x = scroll.x + 4f;

            Rect toggleRect = new Rect(x, rect.y + 5f, CheckboxWidth, 16f);
            using (new EditorGUI.DisabledScope(!diff.HasChanges))
            {
                bool selected = EditorGUI.Toggle(toggleRect, fileSelected[fileIndex]);
                if (selected != fileSelected[fileIndex])
                {
                    fileSelected[fileIndex] = selected;
                }
            }

            x += CheckboxWidth + 2f;
            Rect foldoutRect = new Rect(x, rect.y + 5f, FoldoutWidth, 16f);
            bool expanded = EditorGUI.Foldout(foldoutRect, fileExpanded[fileIndex], GUIContent.none);
            if (expanded != fileExpanded[fileIndex])
            {
                fileExpanded[fileIndex] = expanded;
                RebuildRows();
            }

            x += FoldoutWidth + 4f;
            string label = file.AssetPath + (file.IsNewFile ? "   [NEW]" : string.Empty);
            Rect labelRect = new Rect(x, rect.y, Mathf.Max(60f, position.width - (x - scroll.x) - 140f), rect.height);
            GUI.Label(labelRect, label, FeederDiffStyles.FileHeader);

            DrawBadges(new Rect(scroll.x + position.width - 150f, rect.y, 130f, rect.height), diff);
        }

        private void DrawBadges(Rect rect, FeederDiffFileResult diff)
        {
            Color previous = GUI.contentColor;
            Rect addedRect = new Rect(rect.x, rect.y, rect.width * 0.5f, rect.height);
            GUI.contentColor = FeederDiffStyles.AddedBadge;
            GUI.Label(addedRect, $"+{diff.AddedCount}", FeederDiffStyles.Badge);

            Rect removedRect = new Rect(rect.x + rect.width * 0.5f, rect.y, rect.width * 0.5f, rect.height);
            GUI.contentColor = FeederDiffStyles.RemovedBadge;
            GUI.Label(removedRect, $"-{diff.RemovedCount}", FeederDiffStyles.Badge);
            GUI.contentColor = previous;
        }

        private void DrawItemHeader(Rect rect, int fileIndex, int itemIndex, float contentWidth)
        {
            FeederChangeItem item = changeSet.Files[fileIndex].Items[itemIndex];
            bool canToggle = changeSet.Files[fileIndex].Rebuild != null && !item.IsBlocked;

            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, FeederDiffStyles.ItemHeaderBg);
            }

            float x = scroll.x + 22f;
            if (canToggle)
            {
                Rect toggleRect = new Rect(x, rect.y + 2f, CheckboxWidth, 16f);
                bool selected = EditorGUI.Toggle(toggleRect, itemSelected[fileIndex][itemIndex]);
                if (selected != itemSelected[fileIndex][itemIndex])
                {
                    itemSelected[fileIndex][itemIndex] = selected;
                    RecomputeFile(fileIndex);
                    return;
                }
            }

            x += CheckboxWidth + 4f;
            Color previous = GUI.contentColor;
            if (item.IsBlocked)
            {
                GUI.contentColor = FeederDiffStyles.BlockedText;
            }

            string text = item.Title;
            if (!string.IsNullOrEmpty(item.Subtitle))
            {
                text += "   —   " + item.Subtitle;
            }

            if (item.Warnings.Count > 0)
            {
                text += $"   ⚠ {item.Warnings.Count}";
            }

            Rect labelRect = new Rect(x, rect.y, Mathf.Max(60f, position.width - (x - scroll.x) - 20f), rect.height);
            GUI.Label(labelRect, text, FeederDiffStyles.ItemHeader);
            GUI.contentColor = previous;

            if (item.Warnings.Count > 0 && Event.current.type == EventType.Repaint)
            {
                GUI.Label(labelRect, new GUIContent(string.Empty, string.Join("\n", item.Warnings.ToArray())));
            }
        }

        private void DrawHunkHeader(Rect rect, int fileIndex, int hunkIndex, float contentWidth)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            EditorGUI.DrawRect(rect, FeederDiffStyles.HunkHeaderBg);
            Rect textRect = new Rect(scroll.x + 4f, rect.y, position.width - 24f, rect.height);
            GUI.Label(textRect, diffs[fileIndex].Hunks[hunkIndex].Header, FeederDiffStyles.Hunk);
        }

        private void DrawMessage(Rect rect, string message, float contentWidth)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            Rect textRect = new Rect(scroll.x + 24f, rect.y, position.width - 40f, rect.height);
            GUI.Label(textRect, message, FeederDiffStyles.ItemHeader);
        }

        private void DrawDiffLine(Rect rect, VisualRow row, int index, float contentWidth)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            FeederDiffLine line = diffs[row.File].Hunks[row.Hunk].Lines[row.Line];

            // dòng bị hạ kiểu (enum không tồn tại → string) tô vàng để nổi hơn cả xanh added;
            // dòng removed giữ nguyên đỏ vì đó là dấu vết cũ đang được gỡ đi
            bool warn = line.Op != FeederDiffOp.Removed && IsWarningLine(row.File, line);

            Color background = warn
                ? FeederDiffStyles.WarnRow
                : line.Op == FeederDiffOp.Added
                    ? FeederDiffStyles.AddedRow
                    : line.Op == FeederDiffOp.Removed
                        ? FeederDiffStyles.RemovedRow
                        : Color.clear;

            if (background.a > 0f)
            {
                EditorGUI.DrawRect(rect, background);
            }
            else if ((index & 1) == 1)
            {
                EditorGUI.DrawRect(rect, FeederDiffStyles.AltRowFill);
            }

            if (index == hoverRow)
            {
                EditorGUI.DrawRect(rect, FeederDiffStyles.HoverFill);
            }

            float codeX = FeederDiffStyles.CodeOffsetX;

            if (FeederDiffStyles.HasMonoFont && line.HlEnd > line.HlStart && !warn)
            {
                float charWidth = FeederDiffStyles.CharWidth;
                Rect wordRect = new Rect(
                    codeX + line.HlStart * charWidth, rect.y,
                    (line.HlEnd - line.HlStart) * charWidth, rect.height);
                EditorGUI.DrawRect(wordRect,
                    line.Op == FeederDiffOp.Added ? FeederDiffStyles.AddedWord : FeederDiffStyles.RemovedWord);
            }

            Rect codeRect = new Rect(codeX, rect.y, Mathf.Max(10f, contentWidth - codeX), rect.height);
            GUI.Label(codeRect, line.Text ?? string.Empty, FeederDiffStyles.Code);

            float gutterX = scroll.x;
            EditorGUI.DrawRect(new Rect(gutterX, rect.y, FeederDiffStyles.CodeOffsetX, rect.height),
                FeederDiffStyles.GutterBg);

            GUI.Label(new Rect(gutterX, rect.y, FeederDiffStyles.GutterWidth, rect.height),
                line.OldLine > 0 ? line.OldLine.ToString() : string.Empty, FeederDiffStyles.Gutter);
            GUI.Label(
                new Rect(gutterX + FeederDiffStyles.GutterWidth, rect.y, FeederDiffStyles.GutterWidth, rect.height),
                line.NewLine > 0 ? line.NewLine.ToString() : string.Empty, FeederDiffStyles.Gutter);
            GUI.Label(
                new Rect(gutterX + FeederDiffStyles.GutterWidth * 2f, rect.y, FeederDiffStyles.SignWidth, rect.height),
                FeederTextDiff.SignOf(line.Op).ToString(), FeederDiffStyles.Sign);
        }

        private bool IsWarningLine(int fileIndex, FeederDiffLine line)
        {
            string marker = changeSet.Files[fileIndex].WarningLineMarker;
            if (string.IsNullOrEmpty(marker))
            {
                return false;
            }

            string text = line.RawText ?? line.Text;
            return text != null && text.IndexOf(marker, StringComparison.Ordinal) >= 0;
        }

        private void DrawBottomBar()
        {
            EditorGUILayout.BeginHorizontal(GUILayout.Height(BottomBarHeight));
            GUILayout.Space(6f);
            EditorGUILayout.LabelField(
                "Thay đổi file .cs không undo được bằng Ctrl+Z — nên commit trước khi apply.",
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Cancel", GUILayout.Width(90f), GUILayout.Height(26f)))
            {
                Cancel();
            }

            int selected = SelectedCount();
            using (new EditorGUI.DisabledScope(selected == 0))
            {
                Color previous = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.55f, 0.90f, 0.60f);
                if (GUILayout.Button($"Apply ({selected})", GUILayout.Width(140f), GUILayout.Height(26f)))
                {
                    Apply();
                }

                GUI.backgroundColor = previous;
            }

            GUILayout.Space(6f);
            EditorGUILayout.EndHorizontal();
        }

        private void SetAllSelected(bool value)
        {
            for (int i = 0; i < fileSelected.Length; i++)
            {
                fileSelected[i] = value && diffs[i].HasChanges;
            }
        }

        private void SetAllExpanded(bool value)
        {
            for (int i = 0; i < fileExpanded.Length; i++)
            {
                fileExpanded[i] = value;
            }

            RebuildRows();
        }

        private int SelectedCount()
        {
            int count = 0;
            for (int i = 0; i < fileSelected.Length; i++)
            {
                if (fileSelected[i] && diffs[i].HasChanges)
                {
                    count++;
                }
            }

            return count;
        }

        private string BuildSummary()
        {
            int added = 0;
            int removed = 0;
            for (int i = 0; i < diffs.Length; i++)
            {
                added += diffs[i].AddedCount;
                removed += diffs[i].RemovedCount;
            }

            return $"{changeSet.Files.Count} file · +{added} −{removed}";
        }

        private void CopyDiff()
        {
            StringBuilder builder = new StringBuilder();
            int count = 0;
            for (int i = 0; i < changeSet.Files.Count; i++)
            {
                if (!fileSelected[i] || !diffs[i].HasChanges)
                {
                    continue;
                }

                builder.Append(FeederTextDiff.ToUnifiedText(
                    changeSet.Files[i].AssetPath, changeSet.Files[i].IsNewFile, diffs[i]));
                count++;
            }

            if (count == 0)
            {
                ShowNotification(new GUIContent("Chưa chọn file nào"));
                return;
            }

            EditorGUIUtility.systemCopyBuffer = builder.ToString();
            ShowNotification(new GUIContent($"Đã copy diff của {count} file"));
        }

        private void Cancel()
        {
            Complete(new FeederDiffApplyResult { Accepted = false });
        }

        private void Apply()
        {
            FeederDiffApplyResult result = new FeederDiffApplyResult { Accepted = true };
            for (int i = 0; i < changeSet.Files.Count; i++)
            {
                if (!fileSelected[i] || !diffs[i].HasChanges)
                {
                    continue;
                }

                FeederFileChange file = changeSet.Files[i];
                file.NewText = pendingText[i];
                result.AcceptedFiles.Add(file);
            }

            Complete(result);
        }

        private void Complete(FeederDiffApplyResult result)
        {
            completed = true;
            Action<FeederDiffApplyResult> callback = onComplete;
            onComplete = null;
            pendingClose = true;

            if (callback != null)
            {
                EditorApplication.delayCall += () => callback(result);
            }
        }
    }
}
