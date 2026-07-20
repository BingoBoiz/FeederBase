using System;
using UnityEditor;
using UnityEngine;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace Feeder
{
#if ODIN_INSPECTOR
    [Serializable]
    public class FeederSetupGuideView
    {
        private const float FontSizeScale = 0.6f;

        private static readonly string[][] PrefixRows = new[]
        {
            new[] { "Prefix",     "Kiểu C#",           "Phạm vi / Ghi chú"                                          },
            new[] { "n",          "int",                "-2,147,483,648  →  2,147,483,647"                           },
            new[] { "f",          "float",              "±3.4 × 10³⁸  (~7 chữ số thập phân)"                        },
            new[] { "b",          "bool",               "true / false"                                               },
            new[] { "s",          "string",             "Chuỗi ký tự bất kỳ"                                         },
            new[] { "d",          "double",             "±1.7 × 10³⁰⁸  (~15 chữ số)"                                },
            new[] { "l",          "long",               "-9,223,372,036,854,775,808  →  9,223,372,036,854,775,807"  },
            new[] { "by",         "byte",               "0  →  255"                                                  },
            new[] { "sh",         "short",              "-32,768  →  32,767"                                         },
            new[] { "us",         "ushort",             "0  →  65,535"                                               },
            new[] { "ui",         "uint",               "0  →  4,294,967,295"                                        },
            new[] { "ul",         "ulong",              "0  →  18,446,744,073,709,551,615"                           },
            new[] { "sp",         "Sprite",             "Tên file sprite trong SpriteAssetFolder"                    },
            new[] { "pref",       "GameObject",         "Tên prefab trong PrefabFolder"                              },
            new[] { "spine",      "SkeletonAnimation",  "Tên skeleton trong SkeletonDataFolder"                      },
            new[] { "skeDat",     "SkeletonDataAsset",  "Tên asset trong SkeletonDataFolder"                         },
            new[] { "p<k,v>",     "PairKV",             "Cặp key-value, ví dụ: p<n,f>_StatPair"                     },
            new[] { "li.X",       "List<X>",            "Danh sách phần tử kiểu X, ví dụ: li.n_IDs"                 },
            new[] { "s:EnumType", "EnumType",           "Tên enum value, ví dụ: s_Type:SkillConfigType"             },
        };

        [OnInspectorGUI]
        private void Draw()
        {
            int baseFontSize = GUI.skin.label.fontSize > 0 ? GUI.skin.label.fontSize : 12;
            int fontSize = Mathf.Max(1, Mathf.RoundToInt(baseFontSize * 2 * FontSizeScale));

            GUIStyle title = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = fontSize,
                wordWrap = true,
                margin = new RectOffset(4, 4, 10, 4),
            };
            GUIStyle body = new GUIStyle(EditorStyles.label)
            {
                fontSize = fontSize,
                wordWrap = true,
                richText = true,
                margin = new RectOffset(4, 4, 2, 6),
            };
            GUIStyle headerCell = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = fontSize,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 6, 4, 4),
            };
            GUIStyle cell = new GUIStyle(EditorStyles.label)
            {
                fontSize = fontSize,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 6, 3, 3),
                wordWrap = false,
            };

            GUILayout.Label("1. Cấp quyền truy cập sheet", title);
            GUILayout.Label(
                "Tool chỉ đọc được sheet khi Google Sheets được chia sẻ công khai qua link.\n\n" +
                "Trong Google Sheets: bấm <b>Chia sẻ</b> → <b>Quyền truy cập chung</b> → chọn " +
                "<b>Bất kỳ ai có đường liên kết</b> → quyền <b>Người xem</b> (Viewer) là đủ.\n\n" +
                "Nếu sheet để riêng tư hoặc chỉ mời email cụ thể, tool sẽ không tải được dữ liệu.",
                body);

            GUILayout.Space(8);

            GUILayout.Label("2. Lấy Spreadsheet ID", title);
            GUILayout.Label(
                "Mở Google Sheets trên trình duyệt. URL có dạng:\n" +
                "<color=#4FC3F7>https://docs.google.com/spreadsheets/d/<b>SPREADSHEET_ID</b>/edit?...</color>\n\n" +
                "Copy phần nằm giữa  <b>/d/</b>  và  <b>/edit</b>  — đó là Spreadsheet ID.\n" +
                "Dán vào trường <b>SpreadsheetID</b> trong sheet info tương ứng.",
                body);

            GUILayout.Space(8);

            GUILayout.Label("3. Định dạng tên cột (header)", title);
            GUILayout.Label(
                "Mỗi cột trong sheet phải đặt tên theo dạng:  <b>prefix_FieldName</b>\n\n" +
                "Ví dụ:\n" +
                "  <b>n_ID</b>  →  public int ID;\n" +
                "  <b>f_Damage</b>  →  public float Damage;\n" +
                "  <b>s_Name</b>  →  public string Name;\n\n" +
                "Enum:  <b>s_Type:SkillConfigType</b>  →  public SkillConfigType Type;\n" +
                "List:   <b>li.n_Values</b>  →  public List<int> Values;",
                body);

            GUILayout.Space(8);

            GUILayout.Label("4. Bảng prefix", title);

            Color headerBg = new Color(0.25f, 0.25f, 0.25f, 1f);
            Color rowEven  = new Color(0.22f, 0.22f, 0.22f, 1f);
            Color rowOdd   = new Color(0.18f, 0.18f, 0.18f, 1f);

            float colW0 = 130f;
            float colW1 = 180f;

            for (int r = 0; r < PrefixRows.Length; r++)
            {
                bool isHeader = r == 0;
                Color bg = isHeader ? headerBg : (r % 2 == 0 ? rowEven : rowOdd);

                Rect rowRect = EditorGUILayout.BeginHorizontal();
                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(rowRect, bg);
                }

                GUIStyle s = isHeader ? headerCell : cell;
                GUILayout.Label(PrefixRows[r][0], s, GUILayout.Width(colW0));
                GUILayout.Label(PrefixRows[r][1], s, GUILayout.Width(colW1));
                GUILayout.Label(PrefixRows[r][2], s, GUILayout.ExpandWidth(true));

                EditorGUILayout.EndHorizontal();

                if (isHeader)
                {
                    Rect sep = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
                    EditorGUI.DrawRect(sep, new Color(0.5f, 0.5f, 0.5f, 1f));
                }
            }

            GUILayout.Space(12);
        }
    }
#endif
}
