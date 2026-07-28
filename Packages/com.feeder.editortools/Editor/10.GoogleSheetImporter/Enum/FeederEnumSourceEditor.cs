using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Feeder
{
    public sealed class FeederEnumLocation
    {
        public string AssetPath;
        public string Text;

        // Bản đã che comment/string, CÙNG ĐỘ DÀI với Text nên offset dùng chung được.
        public string Masked;
        public bool HadBom;
        public string Newline;
        public int OpenBraceIndex;
        public int CloseBraceIndex;
    }

    /// <summary>
    /// Đọc/sửa nguồn .cs của enum: che comment-string, tìm khai báo, khớp ngoặc, chèn member mới.
    /// Mọi thao tác đều thuần chuỗi — không hàm nào ở đây ghi ra đĩa.
    /// </summary>
    public static class FeederEnumSourceEditor
    {
        /// <summary>
        /// Trả về chuỗi CÙNG ĐỘ DÀI với source: ký tự trong comment/string bị thay bằng ' ',
        /// riêng '\r' '\n' giữ nguyên. Nhờ vậy offset và số dòng khớp 1:1 với bản gốc,
        /// nên "// enum Foo {" hay "\"enum Foo {\"" không thể tạo match giả và không phá phép đếm ngoặc.
        /// </summary>
        public static string MaskCommentsAndStrings(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return source ?? string.Empty;
            }

            char[] result = source.ToCharArray();
            int n = source.Length;
            int i = 0;
            while (i < n)
            {
                char c = source[i];
                if (c == '/' && i + 1 < n && source[i + 1] == '/')
                {
                    result[i] = ' ';
                    result[i + 1] = ' ';
                    i += 2;
                    while (i < n && source[i] != '\n' && source[i] != '\r')
                    {
                        result[i] = ' ';
                        i++;
                    }
                }
                else if (c == '/' && i + 1 < n && source[i + 1] == '*')
                {
                    result[i] = ' ';
                    result[i + 1] = ' ';
                    i += 2;
                    while (i < n)
                    {
                        if (source[i] == '*' && i + 1 < n && source[i + 1] == '/')
                        {
                            result[i] = ' ';
                            result[i + 1] = ' ';
                            i += 2;
                            break;
                        }

                        MaskUnlessNewline(source, result, i);
                        i++;
                    }
                }
                else if (c == '$' && i + 2 < n && source[i + 1] == '@' && source[i + 2] == '"')
                {
                    // $@"..." — che dấu '$' rồi để vòng sau xử lý như verbatim string.
                    result[i] = ' ';
                    i++;
                }
                else if (c == '@' && i + 1 < n && source[i + 1] == '"')
                {
                    result[i] = ' ';
                    result[i + 1] = ' ';
                    i += 2;
                    while (i < n)
                    {
                        if (source[i] == '"')
                        {
                            if (i + 1 < n && source[i + 1] == '"')
                            {
                                result[i] = ' ';
                                result[i + 1] = ' ';
                                i += 2;
                                continue;
                            }

                            result[i] = ' ';
                            i++;
                            break;
                        }

                        MaskUnlessNewline(source, result, i);
                        i++;
                    }
                }
                else if (c == '"' || c == '\'')
                {
                    char quote = c;
                    result[i] = ' ';
                    i++;
                    while (i < n)
                    {
                        if (source[i] == '\\' && i + 1 < n)
                        {
                            result[i] = ' ';
                            MaskUnlessNewline(source, result, i + 1);
                            i += 2;
                            continue;
                        }

                        if (source[i] == quote)
                        {
                            result[i] = ' ';
                            i++;
                            break;
                        }

                        // Chuỗi không đóng trước khi xuống dòng: dừng lại, đừng che cả phần còn lại của file.
                        if (source[i] == '\n' || source[i] == '\r')
                        {
                            break;
                        }

                        result[i] = ' ';
                        i++;
                    }
                }
                else
                {
                    i++;
                }
            }

            return new string(result);
        }

        private static void MaskUnlessNewline(string source, char[] result, int index)
        {
            if (source[index] != '\n' && source[index] != '\r')
            {
                result[index] = ' ';
            }
        }

        // ---------- tìm khai báo enum ----------

        public static bool TryLocate(Type enumType, out FeederEnumLocation location, out string error)
        {
            location = null;
            error = null;
            if (enumType == null)
            {
                error = "enum type null";
                return false;
            }

            List<string> candidatePaths = GetCandidateSourceFiles(enumType);
            if (candidatePaths.Count == 0)
            {
                error = $"Không tìm được source file nào cho assembly '{enumType.Assembly.GetName().Name}' " +
                        "(có thể enum nằm trong DLL biên dịch sẵn).";
                return false;
            }

            string shortName = StripGenericArity(enumType.Name);
            Regex pattern = new Regex(
                $@"(?<![A-Za-z0-9_])enum\s+{Regex.Escape(shortName)}\s*(:\s*[A-Za-z0-9_\.]+\s*)?\{{");
            string expectedScope = GetExpectedScope(enumType);

            List<FeederEnumLocation> matches = new List<FeederEnumLocation>();
            List<string> outsideAssets = new List<string>();

            for (int i = 0; i < candidatePaths.Count; i++)
            {
                string assetPath = candidatePaths[i];
                string text = TryReadText(assetPath, out bool hadBom);
                if (text == null || text.IndexOf("enum", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                string masked = MaskCommentsAndStrings(text);
                foreach (Match match in pattern.Matches(masked))
                {
                    if (GetEnclosingScope(masked, match.Index) != expectedScope)
                    {
                        continue;
                    }

                    if (!IsUnderAssets(assetPath))
                    {
                        outsideAssets.Add(assetPath);
                        continue;
                    }

                    int openBrace = masked.IndexOf('{', match.Index);
                    if (openBrace < 0 || !TryMatchBrace(masked, openBrace, out int closeBrace))
                    {
                        error = $"Không tìm được dấu '}}' đóng khối enum {shortName} trong {assetPath}.";
                        return false;
                    }

                    matches.Add(new FeederEnumLocation
                    {
                        AssetPath = assetPath,
                        Text = text,
                        Masked = masked,
                        HadBom = hadBom,
                        Newline = DetectNewline(text),
                        OpenBraceIndex = openBrace,
                        CloseBraceIndex = closeBrace,
                    });
                }
            }

            if (matches.Count == 1)
            {
                location = matches[0];
                return true;
            }

            if (matches.Count > 1)
            {
                List<string> paths = new List<string>();
                for (int i = 0; i < matches.Count; i++)
                {
                    paths.Add(matches[i].AssetPath);
                }

                error = $"enum {shortName} khai báo ở nhiều nơi: {string.Join(", ", paths)}.";
                return false;
            }

            if (outsideAssets.Count > 0)
            {
                error = $"enum {shortName} nằm ngoài Assets/ ({outsideAssets[0]}) — tool không sửa file trong package.";
                return false;
            }

            error = $"Không tìm thấy khai báo 'enum {shortName}' trong source của assembly " +
                    $"'{enumType.Assembly.GetName().Name}'.";
            return false;
        }

        private static List<string> GetCandidateSourceFiles(Type enumType)
        {
            List<string> paths = new List<string>();
            string assemblyName = enumType.Assembly.GetName().Name;
            try
            {
                UnityEditor.Compilation.Assembly[] compiled =
                    CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                for (int i = 0; i < compiled.Length; i++)
                {
                    if (compiled[i].name != assemblyName || compiled[i].sourceFiles == null)
                    {
                        continue;
                    }

                    for (int j = 0; j < compiled[i].sourceFiles.Length; j++)
                    {
                        paths.Add(compiled[i].sourceFiles[j].Replace('\\', '/'));
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Feeder] Không đọc được CompilationPipeline: {e.Message}");
            }

            if (paths.Count > 0)
            {
                return paths;
            }

            // Dự phòng: quét toàn bộ script trong project (FindAssets trả path Assets/ tương đối, bỏ .meta và Library/).
            string[] guids = AssetDatabase.FindAssets("t:MonoScript");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }

        private static string GetExpectedScope(Type enumType)
        {
            string full = enumType.FullName;
            if (string.IsNullOrEmpty(full))
            {
                return string.Empty;
            }

            full = full.Replace('+', '.');
            int lastDot = full.LastIndexOf('.');
            if (lastDot < 0)
            {
                return string.Empty;
            }

            string scope = full.Substring(0, lastDot);
            string[] parts = scope.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = StripGenericArity(parts[i]);
            }

            return string.Join(".", parts);
        }

        private static string StripGenericArity(string name)
        {
            int tick = name.IndexOf('`');
            return tick >= 0 ? name.Substring(0, tick) : name;
        }

        private static readonly Regex BlockOwnerPattern =
            new Regex(@"(?<![A-Za-z0-9_])(namespace|class|struct|interface|record)\s+([A-Za-z_][A-Za-z0-9_\.]*)");

        // Đi ngược từ vị trí khai báo, mọi '{' ở độ sâu 0 là một khối bao ngoài.
        // Ghép tên các khối đó lại được scope thật, dùng để phân biệt hai enum trùng tên.
        private static string GetEnclosingScope(string masked, int declarationIndex)
        {
            List<string> scopes = new List<string>();
            int depth = 0;
            for (int i = declarationIndex - 1; i >= 0; i--)
            {
                char c = masked[i];
                if (c == '}')
                {
                    depth++;
                }
                else if (c == '{')
                {
                    if (depth == 0)
                    {
                        string owner = ReadBlockOwnerName(masked, i);
                        if (owner != null)
                        {
                            scopes.Insert(0, owner);
                        }
                    }
                    else
                    {
                        depth--;
                    }
                }
            }

            return string.Join(".", scopes);
        }

        private static string ReadBlockOwnerName(string masked, int braceIndex)
        {
            int start = braceIndex - 1;
            while (start >= 0)
            {
                char c = masked[start];
                if (c == ';' || c == '{' || c == '}')
                {
                    break;
                }

                start--;
            }

            string header = masked.Substring(start + 1, braceIndex - start - 1);
            MatchCollection found = BlockOwnerPattern.Matches(header);
            if (found.Count == 0)
            {
                return null;
            }

            return StripGenericArity(found[found.Count - 1].Groups[2].Value);
        }

        public static bool TryMatchBrace(string masked, int openIndex, out int closeIndex)
        {
            closeIndex = -1;
            int depth = 0;
            for (int i = openIndex; i < masked.Length; i++)
            {
                if (masked[i] == '{')
                {
                    depth++;
                }
                else if (masked[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeIndex = i;
                        return true;
                    }
                }
            }

            return false;
        }

        // ---------- tính điểm chèn ----------

        /// <summary>
        /// Tìm vị trí chèn ngay SAU ký tự CODE có nghĩa cuối cùng của thân enum.
        /// Chèn một phát duy nhất bằng String.Insert nên phần còn lại của file giữ nguyên từng byte —
        /// đó chính là thứ làm cho diff chỉ hiện đúng những dòng thêm vào.
        /// </summary>
        /// <param name="masked">
        /// Bản che comment/string cùng độ dài với <paramref name="original"/>. BẮT BUỘC quét trên bản này:
        /// nếu quét trên bản gốc và khối kết thúc bằng comment, dấu phẩy sẽ bị chèn vào giữa comment
        /// (bị comment nuốt mất) và enum không còn compile được.
        /// </param>
        public static void ComputeInsertion(string original, string masked, int openBraceIndex, int closeBraceIndex,
            out int insertOffset, out bool needsLeadingComma, out bool bodyIsEmpty, out string indent)
        {
            if (string.IsNullOrEmpty(masked) || masked.Length != original.Length)
            {
                masked = MaskCommentsAndStrings(original);
            }

            int lastContent = closeBraceIndex - 1;
            while (lastContent > openBraceIndex && char.IsWhiteSpace(masked[lastContent]))
            {
                lastContent--;
            }

            bodyIsEmpty = lastContent <= openBraceIndex;
            needsLeadingComma = !bodyIsEmpty && masked[lastContent] != ',';
            insertOffset = bodyIsEmpty ? openBraceIndex + 1 : lastContent + 1;
            indent = bodyIsEmpty
                ? GetLineIndent(original, openBraceIndex) + OneIndentLevel(GetLineIndent(original, openBraceIndex))
                : GetLineIndent(original, lastContent);
        }

        private static string GetLineIndent(string text, int index)
        {
            if (index >= text.Length)
            {
                index = text.Length - 1;
            }

            int lineStart = index <= 0 ? 0 : text.LastIndexOf('\n', index) + 1;
            int p = lineStart;
            while (p < text.Length && (text[p] == ' ' || text[p] == '\t'))
            {
                p++;
            }

            return text.Substring(lineStart, p - lineStart);
        }

        private static string OneIndentLevel(string baseIndent)
        {
            return baseIndent.IndexOf('\t') >= 0 ? "\t" : "    ";
        }

        public static string DetectNewline(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Environment.NewLine;
            }

            return text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        }

        // ---------- đánh số ----------

        /// <summary>
        /// Lấy giá trị kế tiếp từ reflection thay vì parse text. Dùng decimal làm bộ cộng vì
        /// Convert.ToInt64 tràn với enum nền ulong, còn Convert.ToUInt64 ném lỗi với sbyte âm.
        /// </summary>
        public static void GetNumbering(Type enumType, out decimal next, out decimal ceiling, out string keyword)
        {
            Type underlying = Enum.GetUnderlyingType(enumType);
            keyword = UnderlyingKeyword(underlying);
            ceiling = UnderlyingCeiling(underlying);

            Array values = Enum.GetValues(enumType);
            if (values.Length == 0)
            {
                next = 0m;
                return;
            }

            decimal max = decimal.MinValue;
            foreach (object value in values)
            {
                decimal current = underlying == typeof(ulong)
                    ? Convert.ToUInt64(value)
                    : (decimal)Convert.ToInt64(value);
                if (current > max)
                {
                    max = current;
                }
            }

            next = max + 1m;
        }

        public static string UnderlyingKeyword(Type underlying)
        {
            if (underlying == typeof(byte)) return "byte";
            if (underlying == typeof(sbyte)) return "sbyte";
            if (underlying == typeof(short)) return "short";
            if (underlying == typeof(ushort)) return "ushort";
            if (underlying == typeof(uint)) return "uint";
            if (underlying == typeof(long)) return "long";
            if (underlying == typeof(ulong)) return "ulong";
            return "int";
        }

        public static decimal UnderlyingCeiling(Type underlying)
        {
            if (underlying == typeof(byte)) return byte.MaxValue;
            if (underlying == typeof(sbyte)) return sbyte.MaxValue;
            if (underlying == typeof(short)) return short.MaxValue;
            if (underlying == typeof(ushort)) return ushort.MaxValue;
            if (underlying == typeof(uint)) return uint.MaxValue;
            if (underlying == typeof(long)) return long.MaxValue;
            if (underlying == typeof(ulong)) return ulong.MaxValue;
            return int.MaxValue;
        }

        public static decimal CeilingForKeyword(string keyword)
        {
            switch (keyword)
            {
                case "byte": return byte.MaxValue;
                case "sbyte": return sbyte.MaxValue;
                case "short": return short.MaxValue;
                case "ushort": return ushort.MaxValue;
                case "uint": return uint.MaxValue;
                case "long": return long.MaxValue;
                case "ulong": return ulong.MaxValue;
                default: return int.MaxValue;
            }
        }

        // ---------- sinh text ----------

        public static string BuildInsertion(FeederEnumChange change, string newline)
        {
            StringBuilder builder = new StringBuilder();
            if (change.NeedsLeadingComma)
            {
                builder.Append(',');
            }

            for (int i = 0; i < change.NewMembers.Count; i++)
            {
                builder.Append(newline).Append(change.Indent).Append(MemberLine(change.NewMembers[i]));
            }

            return builder.ToString();
        }

        /// <summary>
        /// File enum mới: KHÔNG namespace. Đây là ràng buộc cứng — FeederScriptGenerator sinh class
        /// không namespace và ghi field "public {token} {Field};", enum trong namespace sẽ làm file đó không compile.
        /// </summary>
        public static string BuildNewFileText(FeederEnumChange change, string newline)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("public enum ").Append(change.EnumName)
                .Append(" : ").Append(change.UnderlyingTypeKeyword).Append(newline);
            builder.Append('{').Append(newline);
            for (int i = 0; i < change.NewMembers.Count; i++)
            {
                builder.Append("    ").Append(MemberLine(change.NewMembers[i])).Append(newline);
            }

            builder.Append('}').Append(newline);
            return builder.ToString();
        }

        private static string MemberLine(FeederEnumNewMember member)
        {
            return $"{member.MemberName} = {member.Value.ToString(CultureInfo.InvariantCulture)},";
        }

        // ---------- IO đọc ----------

        public static bool IsUnderAssets(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) &&
                   assetPath.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
        }

        public static string TryReadText(string assetPath, out bool hadBom)
        {
            hadBom = false;
            try
            {
                string fullPath = Path.GetFullPath(assetPath);
                if (!File.Exists(fullPath))
                {
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(fullPath);
                hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                return hadBom
                    ? new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3)
                    : new UTF8Encoding(false).GetString(bytes);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Feeder] Không đọc được '{assetPath}': {e.Message}");
                return null;
            }
        }
    }
}
