using System;
using System.Collections.Generic;
using System.IO;
using NabaGame.Core.Runtime.Extensions;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public static class FeederEnumUpdater
    {
        private const int ByteMemberThreshold = 200;

        private const string NoneMemberName = "None";

        private sealed class EnumScriptSource
        {
            public string AssetPath;
            public string Text;
            public string Masked;
            public bool HadBom;
        }

        public static FeederEnumUpdatePlan BuildPlan(IList<FeederEnumColumnScan> scans, string scriptFolderAssetPath,
            string sheetTypeName, IList<string> enumScriptAssetPaths)
        {
            FeederEnumUpdatePlan plan = new FeederEnumUpdatePlan { SheetTypeName = sheetTypeName };
            if (scans == null || scans.Count == 0)
            {
                return plan;
            }

            List<string> tokenOrder = new List<string>();
            Dictionary<string, List<FeederEnumColumnScan>> byToken =
                new Dictionary<string, List<FeederEnumColumnScan>>(StringComparer.Ordinal);
            for (int i = 0; i < scans.Count; i++)
            {
                string token = scans[i].EnumTypeToken;
                if (!byToken.TryGetValue(token, out List<FeederEnumColumnScan> list))
                {
                    list = new List<FeederEnumColumnScan>();
                    byToken.Add(token, list);
                    tokenOrder.Add(token);
                }

                list.Add(scans[i]);
            }

            List<EnumScriptSource> scripts = LoadEnumScripts(plan, enumScriptAssetPaths);
            Dictionary<string, FeederEnumFileChange> filesByPath =
                new Dictionary<string, FeederEnumFileChange>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < tokenOrder.Count; i++)
            {
                string token = tokenOrder[i];
                BuildChangeForToken(plan, filesByPath, token, byToken[token], scriptFolderAssetPath, scripts);
            }

            return plan;
        }

        private static List<EnumScriptSource> LoadEnumScripts(FeederEnumUpdatePlan plan, IList<string> assetPaths)
        {
            List<EnumScriptSource> scripts = new List<EnumScriptSource>();
            if (assetPaths == null)
            {
                return scripts;
            }

            for (int i = 0; i < assetPaths.Count; i++)
            {
                if (assetPaths[i].IsNullOrWhitespace())
                {
                    continue;
                }

                string path = assetPaths[i].Replace('\\', '/');
                if (scripts.Exists(x => string.Equals(x.AssetPath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string text = FeederEnumSourceEditor.TryReadText(path, out bool hadBom);
                if (text == null)
                {
                    plan.Issues.Add($"Không đọc được Enum Script {path}.");
                    continue;
                }

                scripts.Add(new EnumScriptSource
                {
                    AssetPath = path,
                    Text = text,
                    Masked = FeederEnumSourceEditor.MaskCommentsAndStrings(text),
                    HadBom = hadBom,
                });
            }

            return scripts;
        }

        // enums are read from anywhere, but written only into the sheet's Enum Scripts or files directly in its Script Folder
        private static bool IsWritable(string assetPath, string scriptFolderAssetPath, List<EnumScriptSource> scripts)
        {
            string path = assetPath.Replace('\\', '/');
            if (scripts.Exists(x => string.Equals(x.AssetPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            string folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
            return !scriptFolderAssetPath.IsNullOrWhitespace() &&
                   string.Equals(folder, scriptFolderAssetPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        private static void BuildChangeForToken(FeederEnumUpdatePlan plan,
            Dictionary<string, FeederEnumFileChange> filesByPath,
            string token, List<FeederEnumColumnScan> columns, string scriptFolderAssetPath,
            List<EnumScriptSource> scripts)
        {
            MergeValues(columns, out List<FeederEnumSheetValue> values, out List<string> tabs);

            FeederEnumResolveStatus status = FeederEnumUtils.TryResolveEnumType(token,
                out Type existing, out List<Type> candidates);

            if (status == FeederEnumResolveStatus.Ambiguous)
            {
                plan.Issues.Add(
                    $"'{token}' được khai báo trùng tên đầy đủ ở nhiều assembly " +
                    $"({FeederEnumUtils.DescribeTypes(candidates)}) — tool không biết ghi vào cái nào.");
                return;
            }

            FeederEnumChange change = new FeederEnumChange
            {
                EnumToken = token,
                Include = true,
                SourceTabs = tabs,
            };

            if (status == FeederEnumResolveStatus.NotFound)
            {
                BuildNewEnumChange(plan, filesByPath, change, token.Trim(), values, scriptFolderAssetPath, scripts);
                return;
            }

            BuildExistingEnumChange(plan, filesByPath, change, existing, values, scriptFolderAssetPath, scripts);
        }

        private static void BuildNewEnumChange(FeederEnumUpdatePlan plan,
            Dictionary<string, FeederEnumFileChange> filesByPath, FeederEnumChange change, string token,
            List<FeederEnumSheetValue> values, string scriptFolderAssetPath, List<EnumScriptSource> scripts)
        {
            if (!FeederEnumUtils.QualifiedIdentifier.IsMatch(token))
            {
                plan.Issues.Add($"'{token}' không phải tên enum hợp lệ (TenEnum hoặc Namespace.TenEnum).");
                return;
            }

            FeederEnumUtils.SplitFullName(token, out string ns, out string shortName);
            List<Type> sameShortName = FeederEnumUtils.FindEnumsWithSameShortName(token);
            if (ns.Length == 0 && sameShortName.Count > 0)
            {
                plan.Issues.Add(
                    $"Chưa có enum global '{token}', nhưng đã có {FeederEnumUtils.DescribeTypes(sameShortName)}. " +
                    "Header không có dấu '.' nghĩa là enum global — muốn dùng enum trên thì ghi đủ tên, ví dụ " +
                    $"s_Field:{FeederEnumUtils.ToFullName(sameShortName[0])}. Tool không tự tạo enum global trùng tên ngắn.");
                return;
            }

            if (ns.Length > 0 && FeederEnumUtils.IsProjectTypeName(ns))
            {
                plan.Issues.Add($"Không tạo được enum '{token}': '{ns}' là một type, tool không tạo enum lồng trong class.");
                return;
            }

            change.EnumName = shortName;
            change.EnumFullName = token;
            change.IsNew = true;
            change.InsertNoneZero = true;
            change.UnderlyingTypeKeyword =
                CountWritable(values) + 1 <= ByteMemberThreshold ? "byte" : "int";

            if (sameShortName.Count > 0)
            {
                change.Warnings.Add(
                    $"Đã có enum cùng tên ngắn ({FeederEnumUtils.DescribeTypes(sameShortName)}): trong code thuộc " +
                    $"namespace {ns}, '{shortName}' sẽ trỏ tới enum mới này.");
            }

            if (scripts.Count == 0)
            {
                BuildNewEnumFile(plan, filesByPath, change, ns, values, scriptFolderAssetPath);
                return;
            }

            for (int i = 0; i < scripts.Count; i++)
            {
                if (FeederEnumSourceEditor.ContainsEnumDeclaration(scripts[i].Masked, shortName, ns))
                {
                    plan.Issues.Add(
                        $"'{token}' đã được khai báo trong {scripts[i].AssetPath} nhưng type chưa load " +
                        "(file đang lỗi compile hoặc chưa compile xong) — không tạo khai báo thứ hai.");
                    return;
                }
            }

            List<string> skipped = new List<string>();
            for (int i = 0; i < scripts.Count; i++)
            {
                EnumScriptSource script = scripts[i];
                if (!TryFindNewEnumSlot(script, ns, out int offset, out string indent, out bool bodyEmpty,
                        out string reason))
                {
                    if (reason != null)
                    {
                        skipped.Add($"{script.AssetPath}: {reason}");
                    }

                    continue;
                }

                AssignNewEnumMembers(change, values);
                change.InsertOffset = offset;
                change.BlockIndent = indent;
                change.BodyIsEmpty = bodyEmpty;
                change.DeclareInExistingFile = true;
                GetOrAddFile(plan, filesByPath, script.AssetPath, script.Text, script.HadBom).Enums.Add(change);
                return;
            }

            string scope = ns.Length == 0
                ? "global (file không khai báo namespace nào)"
                : $"có khối 'namespace {ns} {{ }}'";
            plan.Issues.Add(
                $"Không tạo được enum '{token}': không có Enum Script nào {scope}. Thêm một file như vậy " +
                "vào Enum Scripts của sheet." +
                (skipped.Count > 0 ? $" Đã bỏ qua: {string.Join(" | ", skipped.ToArray())}" : string.Empty));
        }

        private static bool TryFindNewEnumSlot(EnumScriptSource script, string ns, out int offset,
            out string indent, out bool bodyEmpty, out string reason)
        {
            offset = 0;
            indent = string.Empty;
            bodyEmpty = false;
            reason = null;

            if (ns.Length == 0)
            {
                if (FeederEnumSourceEditor.DeclaresNamespace(script.Masked))
                {
                    return false;
                }

                offset = FeederEnumSourceEditor.ComputeAppendOffset(script.Text);
            }
            else if (!FeederEnumSourceEditor.TryFindNamespaceBlock(script.Text, script.Masked, ns,
                         out offset, out indent, out bodyEmpty, out reason))
            {
                return false;
            }

            if (FeederEnumSourceEditor.IsInsideConditionalBlock(script.Masked, offset))
            {
                reason = "chỗ chèn nằm trong khối #if";
                return false;
            }

            return true;
        }

        private static void BuildNewEnumFile(FeederEnumUpdatePlan plan,
            Dictionary<string, FeederEnumFileChange> filesByPath, FeederEnumChange change, string ns,
            List<FeederEnumSheetValue> values, string scriptFolderAssetPath)
        {
            if (scriptFolderAssetPath.IsNullOrWhitespace())
            {
                plan.Issues.Add(
                    $"Không tạo được enum '{change.EnumFullName}': Enum Scripts lẫn Script Folder đều đang trống.");
                return;
            }

            string assetPath = $"{scriptFolderAssetPath.TrimEnd('/')}/{change.EnumName}.cs";
            if (filesByPath.TryGetValue(assetPath, out FeederEnumFileChange file) && file.Namespace != ns)
            {
                plan.Issues.Add(
                    $"Không tạo được enum '{change.EnumFullName}': {assetPath} đã dành cho một enum cùng tên " +
                    "ở namespace khác.");
                return;
            }

            if (File.Exists(Path.GetFullPath(assetPath)))
            {
                change.BlockedReason = $"File {assetPath} đã tồn tại nhưng type chưa load (có thể đang lỗi compile).";
            }

            AssignNewEnumMembers(change, values);

            if (file == null)
            {
                file = new FeederEnumFileChange
                {
                    AssetPath = assetPath,
                    IsNewFile = true,
                    OriginalText = null,
                    OriginalHadBom = false,
                    Newline = Environment.NewLine,
                    Namespace = ns,
                };
                filesByPath.Add(assetPath, file);
                plan.Files.Add(file);
            }

            file.Enums.Add(change);
        }

        private static FeederEnumFileChange GetOrAddFile(FeederEnumUpdatePlan plan,
            Dictionary<string, FeederEnumFileChange> filesByPath, string assetPath, string text, bool hadBom)
        {
            if (filesByPath.TryGetValue(assetPath, out FeederEnumFileChange file))
            {
                return file;
            }

            file = new FeederEnumFileChange
            {
                AssetPath = assetPath,
                IsNewFile = false,
                OriginalText = text,
                OriginalHadBom = hadBom,
                Newline = FeederEnumSourceEditor.DetectNewline(text),
            };
            filesByPath.Add(assetPath, file);
            plan.Files.Add(file);
            return file;
        }

        public static void AssignNewEnumMembers(FeederEnumChange change, List<FeederEnumSheetValue> values)
        {
            change.NewMembers.Clear();
            List<FeederEnumSheetValue> ordered = new List<FeederEnumSheetValue>(values);

            int existingNone = ordered.FindIndex(v => string.Equals(v.RawValue, NoneMemberName, StringComparison.Ordinal));
            if (change.InsertNoneZero)
            {
                if (existingNone >= 0)
                {
                    FeederEnumSheetValue none = ordered[existingNone];
                    ordered.RemoveAt(existingNone);
                    ordered.Insert(0, none);
                }
                else
                {
                    ordered.Insert(0, new FeederEnumSheetValue
                    {
                        RawValue = NoneMemberName,
                        MemberName = NoneMemberName,
                        FirstRow = -1,
                        Status = FeederEnumValueStatus.Ok,
                    });
                }
            }

            decimal ceiling = FeederEnumSourceEditor.CeilingForKeyword(change.UnderlyingTypeKeyword);
            int assigned = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (!ordered[i].IsWritable)
                {
                    change.NewMembers.Add(RejectedMember(ordered[i]));
                    continue;
                }

                change.NewMembers.Add(new FeederEnumNewMember
                {
                    RawSheetValue = ordered[i].RawValue,
                    MemberName = ordered[i].MemberName ?? ordered[i].RawValue,
                    Value = assigned,
                    FirstSheetRow = ordered[i].FirstRow,
                    SourceTab = ordered[i].SourceTab,
                });
                assigned++;
            }

            if (!change.IsBlocked && assigned - 1 > ceiling)
            {
                change.BlockedReason =
                    $"{assigned} giá trị vượt trần của '{change.UnderlyingTypeKeyword}' ({ceiling}). " +
                    "Đổi underlying type ở dropdown bên cạnh.";
            }
        }

        // giá trị lỗi vẫn được ghi ra file nhưng dưới dạng comment: không compile, không chiếm số
        private static FeederEnumNewMember RejectedMember(FeederEnumSheetValue value)
        {
            return new FeederEnumNewMember
            {
                RawSheetValue = value.RawValue,
                MemberName = value.RawValue,
                FirstSheetRow = value.FirstRow,
                SourceTab = value.SourceTab,
                RejectReason = value.StatusDetail.IsNullOrWhitespace()
                    ? "không phải tên C# hợp lệ"
                    : value.StatusDetail,
            };
        }

        private static int CountWritable(List<FeederEnumSheetValue> values)
        {
            int count = 0;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i].IsWritable)
                {
                    count++;
                }
            }

            return count;
        }

        private static void BuildExistingEnumChange(FeederEnumUpdatePlan plan,
            Dictionary<string, FeederEnumFileChange> filesByPath, FeederEnumChange change, Type existing,
            List<FeederEnumSheetValue> values, string scriptFolderAssetPath, List<EnumScriptSource> scripts)
        {
            change.EnumName = existing.Name;
            change.EnumFullName = existing.FullName;
            change.ExistingType = existing;
            change.IsNew = false;

            HashSet<string> existingNames = new HashSet<string>(Enum.GetNames(existing), StringComparer.Ordinal);

            int missingCount = 0;
            int rejectedCount = 0;
            for (int i = 0; i < values.Count; i++)
            {
                if (!values[i].IsWritable)
                {
                    rejectedCount++;
                }
                else if (!existingNames.Contains(values[i].RawValue))
                {
                    missingCount++;
                }
            }

            if (missingCount == 0 && rejectedCount == 0)
            {
                return;
            }

            if (existing.IsDefined(typeof(FlagsAttribute), false))
            {
                change.BlockedReason = "enum có [Flags] — tool không tự đánh số bit, hãy thêm tay.";
            }

            if (!FeederEnumSourceEditor.TryLocate(existing, out FeederEnumLocation location, out string locateError))
            {
                plan.Issues.Add($"{existing.Name}: {locateError}");
                return;
            }

            if (!IsWritable(location.AssetPath, scriptFolderAssetPath, scripts))
            {
                List<string> missing = new List<string>();
                for (int i = 0; i < values.Count; i++)
                {
                    if (values[i].IsWritable && !existingNames.Contains(values[i].RawValue))
                    {
                        missing.Add(values[i].RawValue);
                    }
                }

                if (missing.Count > 0)
                {
                    plan.Issues.Add(
                        $"enum {FeederEnumUtils.ToFullName(existing)} ({location.AssetPath}) chưa có {missing.Count} " +
                        $"giá trị từ sheet: {string.Join(", ", missing.ToArray())}. File này không nằm trong Enum " +
                        "Scripts của sheet nên tool không ghi — thêm file vào Enum Scripts nếu sheet này được phép " +
                        "sửa enum đó.");
                }

                return;
            }

            FeederEnumSourceEditor.GetNumbering(existing, out decimal next, out decimal ceiling, out string keyword);
            change.UnderlyingTypeKeyword = keyword;

            if (!change.IsBlocked && next + missingCount - 1 > ceiling)
            {
                decimal room = ceiling - next + 1;
                change.BlockedReason =
                    $"enum {existing.Name} : {keyword} chỉ còn {room} chỗ nhưng cần {missingCount}. " +
                    "Đổi underlying type sang int/ushort trước.";
            }

            FeederEnumSourceEditor.ComputeInsertion(location.Text, location.Masked,
                location.OpenBraceIndex, location.CloseBraceIndex,
                out int insertOffset, out bool needsComma, out bool bodyEmpty, out string indent);

            change.InsertOffset = insertOffset;
            change.NeedsLeadingComma = needsComma;
            change.BodyIsEmpty = bodyEmpty;
            change.Indent = indent;

            int assigned = 0;
            for (int i = 0; i < values.Count; i++)
            {
                FeederEnumSheetValue value = values[i];
                if (!value.IsWritable)
                {
                    if (!FeederEnumSourceEditor.BodyHasRejectedComment(location.Text, location.OpenBraceIndex,
                            location.CloseBraceIndex, value.RawValue))
                    {
                        change.NewMembers.Add(RejectedMember(value));
                    }

                    continue;
                }

                if (existingNames.Contains(value.RawValue))
                {
                    continue;
                }

                change.NewMembers.Add(new FeederEnumNewMember
                {
                    RawSheetValue = value.RawValue,
                    MemberName = value.MemberName ?? value.RawValue,
                    Value = next + assigned,
                    FirstSheetRow = value.FirstRow,
                    SourceTab = value.SourceTab,
                });
                assigned++;
            }

            // comment lỗi đã ghi từ lần trước vẫn nằm trong file: không còn gì để thêm
            if (change.NewMembers.Count == 0)
            {
                return;
            }

            GetOrAddFile(plan, filesByPath, location.AssetPath, location.Text, location.HadBom).Enums.Add(change);
        }

        // giữ nguyên thứ tự quét (tab rồi tới dòng), giá trị lỗi nằm chung danh sách để comment ra đúng chỗ
        private static void MergeValues(List<FeederEnumColumnScan> columns,
            out List<FeederEnumSheetValue> values, out List<string> tabs)
        {
            values = new List<FeederEnumSheetValue>();
            tabs = new List<string>();

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int c = 0; c < columns.Count; c++)
            {
                FeederEnumColumnScan column = columns[c];
                if (!column.SourceTab.IsNullOrWhitespace() && !tabs.Contains(column.SourceTab))
                {
                    tabs.Add(column.SourceTab);
                }

                for (int i = 0; i < column.Values.Count; i++)
                {
                    FeederEnumSheetValue value = column.Values[i];
                    if (seen.Add(value.RawValue))
                    {
                        values.Add(value);
                    }
                }
            }
        }

        public static string BuildFileText(FeederEnumFileChange file, IEnumerable<FeederEnumChange> included)
        {
            HashSet<FeederEnumChange> set = new HashSet<FeederEnumChange>();
            if (included != null)
            {
                foreach (FeederEnumChange change in included)
                {
                    set.Add(change);
                }
            }

            if (file.IsNewFile)
            {
                List<FeederEnumChange> fresh = new List<FeederEnumChange>();
                for (int i = 0; i < file.Enums.Count; i++)
                {
                    if (set.Contains(file.Enums[i]) && file.Enums[i].HasWork)
                    {
                        fresh.Add(file.Enums[i]);
                    }
                }

                return fresh.Count == 0
                    ? string.Empty
                    : FeederEnumSourceEditor.BuildNewFileText(fresh, file.Newline, file.Namespace);
            }

            Dictionary<int, List<FeederEnumChange>> groups = new Dictionary<int, List<FeederEnumChange>>();
            List<int> offsets = new List<int>();
            for (int i = 0; i < file.Enums.Count; i++)
            {
                FeederEnumChange change = file.Enums[i];
                if (!set.Contains(change) || !change.HasWork)
                {
                    continue;
                }

                if (!groups.TryGetValue(change.InsertOffset, out List<FeederEnumChange> group))
                {
                    group = new List<FeederEnumChange>();
                    groups.Add(change.InsertOffset, group);
                    offsets.Add(change.InsertOffset);
                }

                group.Add(change);
            }

            offsets.Sort((a, b) => b.CompareTo(a));

            string text = file.OriginalText ?? string.Empty;
            for (int i = 0; i < offsets.Count; i++)
            {
                text = text.Insert(offsets[i],
                    FeederEnumSourceEditor.BuildInsertion(groups[offsets[i]], file.Newline));
            }

            return text;
        }

        public static FeederChangeSet ToChangeSet(FeederEnumUpdatePlan plan)
        {
            FeederChangeSet set = new FeederChangeSet();
            set.Issues.AddRange(plan.Issues);

            for (int i = 0; i < plan.Files.Count; i++)
            {
                FeederEnumFileChange source = plan.Files[i];
                List<FeederEnumChange> all = source.Enums;

                FeederFileChange file = new FeederFileChange
                {
                    AssetPath = source.AssetPath,
                    OriginalText = source.IsNewFile ? null : source.OriginalText,
                    OriginalHadBom = source.OriginalHadBom,
                    Newline = source.Newline,
                    NewText = BuildFileText(source, all),
                    ErrorLineMarker = FeederEnumSourceEditor.InvalidMemberMarker,
                };

                for (int j = 0; j < all.Count; j++)
                {
                    FeederEnumChange change = all[j];
                    FeederChangeItem item = new FeederChangeItem
                    {
                        Title = change.IsNew ? $"enum {change.EnumName} (mới)" : $"enum {change.EnumName}",
                        Subtitle = BuildItemSubtitle(change),
                        BlockedReason = change.BlockedReason,
                        Payload = change,
                    };
                    item.Warnings.AddRange(change.Warnings);
                    file.Items.Add(item);
                }

                FeederEnumFileChange captured = source;
                file.Rebuild = items =>
                {
                    List<FeederEnumChange> picked = new List<FeederEnumChange>();
                    if (items != null)
                    {
                        for (int k = 0; k < items.Count; k++)
                        {
                            if (items[k].Payload is FeederEnumChange enumChange)
                            {
                                picked.Add(enumChange);
                            }
                        }
                    }

                    return BuildFileText(captured, picked);
                };

                set.Files.Add(file);
            }

            return set;
        }

        private static string BuildItemSubtitle(FeederEnumChange change)
        {
            if (change.IsBlocked)
            {
                return change.BlockedReason;
            }

            string tabs = change.SourceTabs.Count > 0
                ? $" · từ tab {string.Join(", ", change.SourceTabs.ToArray())}"
                : string.Empty;

            int rejected = CountRejected(change);
            string invalid = rejected > 0 ? $" · {rejected} không hợp lệ" : string.Empty;
            return $"+{change.NewMembers.Count - rejected} giá trị{invalid} · " +
                   $"{change.UnderlyingTypeKeyword}{tabs}";
        }

        private static int CountRejected(FeederEnumChange change)
        {
            int count = 0;
            for (int i = 0; i < change.NewMembers.Count; i++)
            {
                if (change.NewMembers[i].IsRejected)
                {
                    count++;
                }
            }

            return count;
        }

        // ---------- ghi ----------

        public static string Apply(IList<FeederFileChange> files)
        {
            if (!FeederChangeWriter.CanWriteNow(out string reason))
            {
                EditorUtility.DisplayDialog("Update Enum", reason, "close");
                return reason;
            }

            FeederWriteReport report = FeederChangeWriter.Write(files);

            FeederEnumUtils.InvalidateCache();

            int memberCount = 0;
            for (int i = 0; i < files.Count; i++)
            {
                for (int j = 0; j < files[i].Items.Count; j++)
                {
                    if (files[i].Items[j].Payload is FeederEnumChange change)
                    {
                        memberCount += change.NewMembers.Count - CountRejected(change);
                    }
                }
            }

            string summary = report.AnyWritten
                ? $"Đã ghi {report.WrittenCount} file enum ({memberCount} giá trị). " +
                  "Bước tiếp theo: (1) đợi Unity compile xong → (2) bấm Generate Script → (3) bấm Generate Assets."
                : "Không ghi được file nào.";

            if (report.Problems.Count > 0)
            {
                summary += $" Bỏ qua {report.SkippedCount}: {string.Join(" | ", report.Problems.ToArray())}";
            }

            Debug.Log($"<color=cyan>[Update Enum] {summary}</color>");
            return summary;
        }
    }
}
