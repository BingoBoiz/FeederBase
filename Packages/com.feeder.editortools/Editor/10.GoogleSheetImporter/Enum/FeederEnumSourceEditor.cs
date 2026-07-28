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

        public string Masked;
        public bool HadBom;
        public string Newline;
        public int OpenBraceIndex;
        public int CloseBraceIndex;
    }

    public static class FeederEnumSourceEditor
    {
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

        public static string BuildInsertion(IList<FeederEnumChange> group, string newline)
        {
            if (group.Count == 1 || !group[0].DeclareInExistingFile)
            {
                StringBuilder single = new StringBuilder();
                for (int i = 0; i < group.Count; i++)
                {
                    single.Append(BuildInsertion(group[i], newline));
                }

                return single.ToString();
            }

            FeederEnumChange first = group[0];
            StringBuilder builder = new StringBuilder();
            if (first.InsertOffset > 0)
            {
                builder.Append(newline);
                if (!first.BodyIsEmpty)
                {
                    builder.Append(newline);
                }
            }

            bool wrap = !string.IsNullOrEmpty(first.WrapNamespace);
            if (wrap)
            {
                builder.Append("namespace ").Append(first.WrapNamespace).Append(newline)
                    .Append('{').Append(newline);
            }

            for (int i = 0; i < group.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(newline).Append(newline);
                }

                AppendEnumBlock(builder, group[i], newline, wrap ? "    " : group[i].BlockIndent);
            }

            if (wrap)
            {
                builder.Append(newline).Append('}');
            }

            return builder.ToString();
        }

        public static string BuildInsertion(FeederEnumChange change, string newline)
        {
            StringBuilder builder = new StringBuilder();

            if (change.DeclareInExistingFile)
            {
                if (change.InsertOffset > 0)
                {
                    builder.Append(newline);
                    if (!change.BodyIsEmpty)
                    {
                        builder.Append(newline);
                    }
                }

                if (!string.IsNullOrEmpty(change.WrapNamespace))
                {
                    builder.Append("namespace ").Append(change.WrapNamespace).Append(newline)
                        .Append('{').Append(newline);
                    AppendEnumBlock(builder, change, newline, "    ");
                    builder.Append(newline).Append('}');
                }
                else
                {
                    AppendEnumBlock(builder, change, newline, change.BlockIndent);
                }

                return builder.ToString();
            }

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

        public static string BuildNewFileText(IList<FeederEnumChange> changes, string newline, string sheetNamespace)
        {
            bool wrap = !string.IsNullOrEmpty(sheetNamespace) && sheetNamespace.Trim().Length > 0;
            string blockIndent = wrap ? "    " : string.Empty;

            StringBuilder builder = new StringBuilder();
            if (wrap)
            {
                builder.Append("namespace ").Append(sheetNamespace.Trim()).Append(newline)
                    .Append('{').Append(newline);
            }

            for (int i = 0; i < changes.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(newline);
                }

                AppendEnumBlock(builder, changes[i], newline, blockIndent);
                builder.Append(newline);
            }

            if (wrap)
            {
                builder.Append('}').Append(newline);
            }

            return builder.ToString();
        }

        private static void AppendEnumBlock(StringBuilder builder, FeederEnumChange change, string newline,
            string blockIndent)
        {
            builder.Append(blockIndent).Append("public enum ").Append(change.EnumName)
                .Append(" : ").Append(change.UnderlyingTypeKeyword).Append(newline);
            builder.Append(blockIndent).Append('{').Append(newline);
            for (int i = 0; i < change.NewMembers.Count; i++)
            {
                builder.Append(blockIndent).Append("    ").Append(MemberLine(change.NewMembers[i])).Append(newline);
            }

            builder.Append(blockIndent).Append('}');
        }

        public static bool TryFindNamespaceBlock(string text, string masked, string ns,
            out int insertOffset, out string blockIndent, out bool blockIsEmpty, out string error)
        {
            insertOffset = 0;
            blockIndent = string.Empty;
            blockIsEmpty = false;
            error = null;

            if (string.IsNullOrEmpty(masked))
            {
                return false;
            }

            Match chosen = null;
            foreach (Match match in NamespaceBlockPattern.Matches(masked))
            {
                if (match.Groups[2].Value == ";")
                {
                    error = "file dùng namespace file-scoped (\"namespace X;\") — tool không chèn vào loại này. " +
                            "Đổi sang dạng có { } hoặc trỏ Enum Script sang file khác.";
                    return false;
                }

                string declared = match.Groups[1].Value.Trim('.');
                string outer = GetEnclosingScope(masked, match.Index);
                string full = string.IsNullOrEmpty(outer) ? declared : outer + "." + declared;
                if (string.Equals(full, ns, StringComparison.Ordinal))
                {
                    chosen = match;
                }
            }

            if (chosen == null)
            {
                return false;
            }

            int openBrace = masked.IndexOf('{', chosen.Index);
            if (openBrace < 0 || !TryMatchBrace(masked, openBrace, out int closeBrace))
            {
                error = $"không tìm được '}}' đóng khối 'namespace {ns}'.";
                return false;
            }

            int p = closeBrace - 1;
            while (p > openBrace && char.IsWhiteSpace(masked[p]))
            {
                p--;
            }

            blockIsEmpty = p <= openBrace;
            insertOffset = blockIsEmpty ? openBrace + 1 : p + 1;

            string outerIndent = GetLineIndent(text, openBrace);
            blockIndent = outerIndent + OneIndentLevel(outerIndent);
            return true;
        }

        private static readonly Regex NamespaceBlockPattern =
            new Regex(@"(?<![A-Za-z0-9_])namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*([{;])");

        public static int ComputeAppendOffset(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int offset = text.Length;
            while (offset > 0 && char.IsWhiteSpace(text[offset - 1]))
            {
                offset--;
            }

            return offset;
        }

        private static readonly Regex NamespacePattern =
            new Regex(@"(?<![A-Za-z0-9_])namespace\s+[A-Za-z_]");

        public static bool DeclaresNamespace(string masked)
        {
            return !string.IsNullOrEmpty(masked) && NamespacePattern.IsMatch(masked);
        }

        public static bool ContainsEnumDeclaration(string masked, string enumName)
        {
            if (string.IsNullOrEmpty(masked) || string.IsNullOrEmpty(enumName))
            {
                return false;
            }

            return Regex.IsMatch(masked,
                $@"(?<![A-Za-z0-9_])enum\s+{Regex.Escape(enumName)}\s*(:\s*[A-Za-z0-9_\.]+\s*)?\{{");
        }

        private static string MemberLine(FeederEnumNewMember member)
        {
            return $"{member.MemberName} = {member.Value.ToString(CultureInfo.InvariantCulture)},";
        }

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
