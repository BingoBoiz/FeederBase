using System;
using System.Collections.Generic;
using NabaGame.Core.Runtime.Extensions;

namespace Feeder
{
    public static class FeederEnumScanner
    {
        private const char SkipColumnPrefix = '/';
        private const char ZeroWidthSpace = '​';

        private static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
            "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
            "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
            "virtual", "void", "volatile", "while",
        };

        public static List<FeederEnumColumnScan> ScanEnumColumns(string[,] cells, List<string> rawFields, string sourceTab)
        {
            List<FeederEnumColumnScan> results = new List<FeederEnumColumnScan>();
            if (cells == null)
            {
                return results;
            }

            int columnCount = cells.GetLength(0);
            int rowCount = cells.GetLength(1);
            if (columnCount == 0 || rowCount < 3)
            {
                return results;
            }

            for (int column = 0; column < columnCount; column++)
            {
                string header = GetHeader(cells, rawFields, column, rowCount);
                if (!TryParseEnumHeader(header, out string fieldName, out string enumTypeToken))
                {
                    continue;
                }

                FeederEnumColumnScan scan = new FeederEnumColumnScan
                {
                    ColumnIndex = column,
                    SourceTab = sourceTab,
                    RawHeader = header,
                    FieldName = fieldName,
                    EnumTypeToken = enumTypeToken,
                };

                CollectValues(cells, column, rowCount, sourceTab, scan);
                results.Add(scan);
            }

            return results;
        }

        private static string GetHeader(string[,] cells, List<string> rawFields, int column, int rowCount)
        {
            if (rawFields != null && column < rawFields.Count && !rawFields[column].IsNullOrWhitespace())
            {
                return rawFields[column];
            }

            return rowCount > 1 ? cells[column, 1] : null;
        }

        public static bool TryParseEnumHeader(string rawHeader, out string fieldName, out string enumTypeToken)
        {
            fieldName = null;
            enumTypeToken = null;
            if (rawHeader.IsNullOrWhitespace())
            {
                return false;
            }

            string header = rawHeader.Trim();
            if (header[0] == SkipColumnPrefix)
            {
                return false;
            }

            int underscore = header.IndexOf('_');
            if (underscore < 0)
            {
                return false;
            }

            if (header.Substring(0, underscore).Trim().ToLower() != "s")
            {
                return false;
            }

            string[] parts = header.Substring(underscore + 1).Split(':');
            if (parts.Length < 2)
            {
                return false;
            }

            fieldName = parts[0].Trim();
            enumTypeToken = parts[1].Trim();
            return !fieldName.IsNullOrWhitespace() && !enumTypeToken.IsNullOrWhitespace();
        }

        private static void CollectValues(string[,] cells, int column, int rowCount, string sourceTab,
            FeederEnumColumnScan scan)
        {
            HashSet<string> seenOrdinal = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, string> seenIgnoreCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int row = 2; row < rowCount; row++)
            {
                string raw = cells[column, row];

                if (raw.IsNullOrWhitespace())
                {
                    continue;
                }

                raw = raw.Trim();
                if (!seenOrdinal.Add(raw))
                {
                    continue;
                }

                FeederEnumSheetValue value = new FeederEnumSheetValue
                {
                    RawValue = raw,
                    FirstRow = row,
                    SourceTab = sourceTab,
                };

                if (seenIgnoreCase.TryGetValue(raw, out string existing))
                {
                    value.Status = FeederEnumValueStatus.DuplicateIgnoreCase;
                    value.StatusDetail = $"trùng với '{existing}' nếu bỏ qua hoa/thường (dòng sheet {row + 1})";
                    scan.Warnings.Add($"'{raw}' và '{existing}' chỉ khác hoa/thường — bỏ qua '{raw}'.");
                    scan.Values.Add(value);
                    continue;
                }

                seenIgnoreCase.Add(raw, raw);
                value.Status = Classify(raw, out string memberName, out string detail);
                value.MemberName = memberName;
                value.StatusDetail = detail;
                if (value.Status == FeederEnumValueStatus.Invalid)
                {
                    scan.Warnings.Add($"Dòng sheet {row + 1}: '{raw}' không phải tên C# hợp lệ ({detail}).");
                }

                scan.Values.Add(value);
            }
        }

        public static FeederEnumValueStatus Classify(string raw, out string memberName, out string detail)
        {
            memberName = raw;
            detail = null;

            if (string.IsNullOrEmpty(raw))
            {
                detail = "rỗng";
                return FeederEnumValueStatus.Invalid;
            }

            if (raw.IndexOf(ZeroWidthSpace) >= 0)
            {
                detail = "chứa ký tự ẩn U+200B (zero-width space)";
                return FeederEnumValueStatus.Invalid;
            }

            char first = raw[0];
            if (!(first == '_' || (first >= 'A' && first <= 'Z') || (first >= 'a' && first <= 'z')))
            {
                detail = $"ký tự đầu '{first}' không hợp lệ";
                return FeederEnumValueStatus.Invalid;
            }

            for (int i = 1; i < raw.Length; i++)
            {
                char c = raw[i];
                bool ok = c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (!ok)
                {
                    detail = c == ' ' ? "chứa dấu cách" : $"chứa ký tự '{c}'";
                    return FeederEnumValueStatus.Invalid;
                }
            }

            if (CSharpKeywords.Contains(raw))
            {
                memberName = "@" + raw;
                detail = "trùng từ khoá C#, tự thêm tiền tố '@'";
                return FeederEnumValueStatus.Keyword;
            }

            return FeederEnumValueStatus.Ok;
        }
    }
}
