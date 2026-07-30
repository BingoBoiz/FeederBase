using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Vẽ cấu hình khớp tên thành hai dòng thẳng cột nhau:
    /// <c>Match Threshold [icon] [slider]</c> và <c>Match Strategy [Fuzzy|Unique Asset|Fill All]</c>.
    /// Bấm icon để đổi giữa ngưỡng theo % và ngưỡng theo số ký tự lệch. Tắt Fuzzy thì dòng ngưỡng
    /// biến mất vì không còn tác dụng. Giá trị lưu qua <see cref="FToolPrefs"/> theo từng tool.
    /// Gọi từ một method <c>[OnInspectorGUI]</c> trong tool.
    /// </summary>
    public static class FMatchThresholdGUI
    {
        private const float ModeButtonWidth = 18f;
        private const float ModeButtonGap = 3f;
        private const string ModeTooltip = "Đổi kiểu ngưỡng: theo phần trăm (%) hoặc theo số ký tự lệch";

        private const string StrategyLabel = "Match Strategy";

        private const string FuzzyTooltip =
            "Fuzzy Match — khớp gần đúng theo ngưỡng %/Δ. Tắt = chỉ gán khi tên asset giống hệt tên " +
            "key từng ký tự (phân biệt hoa/thường và thứ tự token), và dòng ngưỡng sẽ ẩn đi.";

        private const string UniqueTooltip =
            "Unique Asset Per Key — mỗi asset chỉ được gán cho một key. " +
            "Tắt = nhiều key có thể dùng chung một asset.";

        private const string FillAllTooltip =
            "Fill All Keys — key không đạt ngưỡng vẫn nhận asset tốt nhất còn lại (giả định số asset " +
            "≥ số key). Lúc này ngưỡng thành mốc tin cậy: dòng bị gán ép có dấu * ở cột Score.";

        // GUIContent tạo sẵn: Draw chạy mỗi frame nên đừng alloc lại.
        private static readonly GUIContent FuzzyContent = new GUIContent("Fuzzy", FuzzyTooltip);
        private static readonly GUIContent UniqueContent = new GUIContent("Unique Asset", UniqueTooltip);
        private static readonly GUIContent FillAllContent = new GUIContent("Fill All", FillAllTooltip);

        private static GUIContent _modeIcon;
        private static GUIStyle _modeButtonStyle;

        // "_Popup" là icon options built-in của Unity. Cache lại nên nếu tên icon không tồn tại
        // thì cũng chỉ log warning một lần, và vẫn còn glyph dự phòng để nút không bị vô hình.
        private static GUIContent ModeIcon
        {
            get
            {
                if (_modeIcon != null) return _modeIcon;
                Texture icon = EditorGUIUtility.IconContent("_Popup")?.image;
                _modeIcon = icon != null
                    ? new GUIContent(icon, ModeTooltip)
                    : new GUIContent("▾", ModeTooltip);
                return _modeIcon;
            }
        }

        private static GUIStyle ModeButtonStyle => _modeButtonStyle ??= new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(0, 0, 0, 0),
        };

        public static void Draw(string owner, float defaultPercent)
        {
            GUILayout.Space(6f);
            FMatchThreshold threshold = FMatchThreshold.Load(owner, defaultPercent);
            float labelWidth = EditorGUIUtility.labelWidth;

            // Tắt Fuzzy thì ngưỡng không còn tác dụng nên ẩn hẳn dòng này. An toàn về layout vì
            // threshold chỉ đọc một lần ở đầu method, còn nút bật/tắt Fuzzy nằm ở dòng DƯỚI — nên
            // trong cùng một pass số control luôn khớp, thay đổi chỉ có hiệu lực từ frame sau.
            if (threshold.Fuzzy)
                DrawThresholdRow(owner, threshold, labelWidth);

            DrawStrategyRow(owner, threshold, labelWidth);
        }

        private static void DrawThresholdRow(string owner, FMatchThreshold threshold, float labelWidth)
        {
            Rect row = EditorGUILayout.GetControlRect();
            var buttonRect = new Rect(row.x + labelWidth - ModeButtonWidth - ModeButtonGap, row.y, ModeButtonWidth, row.height);
            var labelRect = new Rect(row.x, row.y, Mathf.Max(0f, buttonRect.x - row.x), row.height);
            var fieldRect = new Rect(row.x + labelWidth, row.y, row.width - labelWidth, row.height);

            EditorGUI.LabelField(labelRect, threshold.ByCharDiff ? "Max Char Diff" : "Match Threshold (%)");

            EditorGUIUtility.AddCursorRect(buttonRect, MouseCursor.Link);
            if (GUI.Button(buttonRect, ModeIcon, ModeButtonStyle))
                ShowModeMenu(buttonRect, owner, threshold.ByCharDiff);

            EditorGUI.BeginChangeCheck();
            if (threshold.ByCharDiff)
            {
                int maxCharDiff = EditorGUI.IntSlider(fieldRect, threshold.MaxCharDiff, 0, FMatchThreshold.CharDiffMax);
                if (EditorGUI.EndChangeCheck())
                    FToolPrefs.SetInt(owner, FMatchThreshold.CharDiffKey, maxCharDiff);
            }
            else
            {
                float minSimilarity = EditorGUI.Slider(fieldRect, threshold.MinSimilarity, 0f, 1f);
                if (EditorGUI.EndChangeCheck())
                    FToolPrefs.SetFloat(owner, FMatchThreshold.PercentKey, minSimilarity);
            }
        }

        // Ba công tắc gộp thành một dải nút liền nhau, thẳng cột với slider ngưỡng ở trên.
        private static void DrawStrategyRow(string owner, FMatchThreshold threshold, float labelWidth)
        {
            Rect row = EditorGUILayout.GetControlRect();
            EditorGUI.LabelField(new Rect(row.x, row.y, labelWidth, row.height), StrategyLabel);

            // Chia bề rộng theo độ dài chữ, không chia đều ba phần — nếu chia đều thì nhãn dài
            // ("Unique Asset") bị cắt trước trong khi nhãn ngắn ("Fuzzy") còn thừa chỗ.
            float fuzzyWidth = EditorStyles.miniButtonLeft.CalcSize(FuzzyContent).x;
            float uniqueWidth = EditorStyles.miniButtonMid.CalcSize(UniqueContent).x;
            float fillAllWidth = EditorStyles.miniButtonRight.CalcSize(FillAllContent).x;
            float total = fuzzyWidth + uniqueWidth + fillAllWidth;

            float available = Mathf.Max(0f, row.width - labelWidth);
            float scale = total > 0f ? available / total : 0f;
            fuzzyWidth *= scale;
            uniqueWidth *= scale;

            float x = row.x + labelWidth;
            DrawSegment(new Rect(x, row.y, fuzzyWidth, row.height), owner,
                FMatchThreshold.FuzzyKey, FuzzyContent, threshold.Fuzzy, EditorStyles.miniButtonLeft);
            DrawSegment(new Rect(x + fuzzyWidth, row.y, uniqueWidth, row.height), owner,
                FMatchThreshold.UniqueKey, UniqueContent, threshold.UniqueAssetPerKey, EditorStyles.miniButtonMid);
            // Nút cuối lấy phần dư để không hở khe do làm tròn.
            DrawSegment(new Rect(x + fuzzyWidth + uniqueWidth, row.y, available - fuzzyWidth - uniqueWidth, row.height), owner,
                FMatchThreshold.FillAllKey, FillAllContent, threshold.FillAllKeys, EditorStyles.miniButtonRight);
        }

        private static void DrawSegment(Rect rect, string owner, string key, GUIContent content,
                                        bool value, GUIStyle style)
        {
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);

            EditorGUI.BeginChangeCheck();
            bool next = GUI.Toggle(rect, value, content, style);
            if (!EditorGUI.EndChangeCheck()) return;

            FToolPrefs.SetBool(owner, key, next);
            // Bật/tắt Fuzzy làm dòng ngưỡng ẩn/hiện, nên phải repaint để layout mới hiện ra ngay.
            EditorWindow.focusedWindow?.Repaint();
        }

        private static void ShowModeMenu(Rect anchor, string owner, bool byCharDiff)
        {
            // Đổi chế độ là đổi cả label lẫn kiểu field, nên phải repaint window đang vẽ —
            // callback của GenericMenu chạy sau khi menu đóng nên bắt window ngay lúc bấm.
            EditorWindow window = EditorWindow.focusedWindow;

            void SetMode(bool value)
            {
                FToolPrefs.SetBool(owner, FMatchThreshold.ModeKey, value);
                window?.Repaint();
            }

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Theo phần trăm (%)"), !byCharDiff, () => SetMode(false));
            menu.AddItem(new GUIContent("Theo số ký tự lệch"), byCharDiff, () => SetMode(true));
            menu.DropDown(anchor);
        }
    }
}
