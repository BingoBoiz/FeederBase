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

        public static FeederEnumUpdatePlan BuildPlan(IList<FeederEnumColumnScan> scans, string scriptFolderAssetPath,
            string sheetTypeName, string enumScriptAssetPath = null, string sheetNamespace = null)
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

            Dictionary<string, FeederEnumFileChange> filesByPath =
                new Dictionary<string, FeederEnumFileChange>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < tokenOrder.Count; i++)
            {
                string token = tokenOrder[i];
                BuildChangeForToken(plan, filesByPath, token, byToken[token], scriptFolderAssetPath,
                    enumScriptAssetPath, sheetNamespace);
            }

            return plan;
        }

        private static void BuildChangeForToken(FeederEnumUpdatePlan plan,
            Dictionary<string, FeederEnumFileChange> filesByPath,
            string token, List<FeederEnumColumnScan> columns, string scriptFolderAssetPath,
            string enumScriptAssetPath, string sheetNamespace)
        {
            MergeValues(columns, out List<FeederEnumSheetValue> writable,
                out List<FeederEnumSheetValue> rejected, out List<string> warnings, out List<string> tabs);

            FeederEnumResolveStatus status = FeederEnumUtils.TryResolveEnumType(token, sheetNamespace,
                out Type existing, out List<Type> candidates);

            if (status == FeederEnumResolveStatus.Ambiguous)
            {
                List<string> names = new List<string>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    names.Add(candidates[i].FullName);
                }

                plan.Issues.Add(
                    $"'{token}' trùng tên ở nhiều nơi ({string.Join(", ", names)}). " +
                    "Ghi tên đầy đủ trong header sheet (ví dụ s_Field:Yolo.testEnum.TreeType), " +
                    "hoặc đặt Namespace của sheet cho đúng.");
                return;
            }

            FeederEnumChange change = new FeederEnumChange
            {
                EnumToken = token,
                Include = true,
                RejectedValues = rejected,
                Warnings = warnings,
                SourceTabs = tabs,
            };

            if (status == FeederEnumResolveStatus.NotFound)
            {
                List<Type> elsewhere = FeederEnumUtils.FindEnumsOutsideScope(token, sheetNamespace);
                if (elsewhere.Count > 0)
                {
                    List<string> names = new List<string>();
                    for (int i = 0; i < elsewhere.Count; i++)
                    {
                        names.Add(elsewhere[i].FullName?.Replace('+', '.'));
                    }

                    plan.Issues.Add(
                        $"'{token}' không có ở {FeederEnumUtils.DescribeScope(sheetNamespace)} nhưng có ở: " +
                        $"{string.Join(", ", names)}. Apply sẽ TẠO MỚI một enum '{token}' thứ hai và KHÔNG " +
                        $"đụng tới cái đang có. Muốn dùng lại cái có sẵn: đặt Namespace của sheet = " +
                        $"'{elsewhere[0].Namespace ?? string.Empty}', hoặc ghi tên đầy đủ trong header " +
                        $"(ví dụ s_Field:{names[0]}).");
                }

                BuildNewEnumChange(plan, filesByPath, change, token, writable, scriptFolderAssetPath,
                    enumScriptAssetPath, sheetNamespace);
                return;
            }

            BuildExistingEnumChange(plan, filesByPath, change, existing, writable, enumScriptAssetPath);
        }

        private static void BuildNewEnumChange(FeederEnumUpdatePlan plan,
            Dictionary<string, FeederEnumFileChange> filesByPath, FeederEnumChange change, string token,
            List<FeederEnumSheetValue> writable, string scriptFolderAssetPath, string enumScriptAssetPath,
            string sheetNamespace)
        {
            if (token.IndexOf('.') >= 0)
            {
                plan.Issues.Add($"'{token}' không tồn tại và có dấu '.' — tool chỉ tạo mới enum tên đơn giản.");
                return;
            }

            bool useEnumScript = !enumScriptAssetPath.IsNullOrWhitespace();
            if (!useEnumScript && scriptFolderAssetPath.IsNullOrWhitespace())
            {
                plan.Issues.Add(
                    $"Không tạo được enum '{token}': cả Enum Script lẫn Script Folder đều đang trống.");
                return;
            }

            string ns = sheetNamespace.IsNullOrWhitespace() ? string.Empty : sheetNamespace.Trim();

            change.EnumName = token;
            change.EnumFullName = ns.Length == 0 ? token : $"{ns}.{token}";
            change.IsNew = true;
            change.InsertNoneZero = true;
            change.UnderlyingTypeKeyword =
                writable.Count + 1 <= ByteMemberThreshold ? "byte" : "int";

            string assetPath = useEnumScript
                ? enumScriptAssetPath
                : $"{scriptFolderAssetPath.TrimEnd('/')}/{token}.cs";

            string existingText = null;
            bool hadBom = false;
            if (useEnumScript)
            {
                existingText = FeederEnumSourceEditor.TryReadText(assetPath, out hadBom);
            }
            else if (File.Exists(Path.GetFullPath(assetPath)))
            {
                change.BlockedReason = $"File {assetPath} đã tồn tại nhưng type chưa load (có thể đang lỗi compile).";
            }

            AssignNewEnumMembers(change, writable);

            if (useEnumScript && existingText != null)
            {
                string masked = FeederEnumSourceEditor.MaskCommentsAndStrings(existingText);
                if (!change.IsBlocked && FeederEnumSourceEditor.ContainsEnumDeclaration(masked, token))
                {
                    change.BlockedReason =
                        $"'{token}' đã được khai báo trong {assetPath} nhưng type chưa load " +
                        "(có thể file đang lỗi compile) — không nối thêm khai báo trùng tên.";
                }

                bool foundBlock = FeederEnumSourceEditor.TryFindNamespaceBlock(existingText, masked, ns,
                    out int nsOffset, out string nsIndent, out bool nsEmpty, out string nsError);

                if (nsError != null && !change.IsBlocked)
                {
                    change.BlockedReason = $"{assetPath}: {nsError}";
                }

                if (foundBlock)
                {
                    change.InsertOffset = nsOffset;
                    change.BlockIndent = nsIndent;
                    change.BodyIsEmpty = nsEmpty;
                }
                else
                {
                    change.InsertOffset = FeederEnumSourceEditor.ComputeAppendOffset(existingText);

                    if (ns.Length > 0)
                    {
                        change.WrapNamespace = ns;
                        change.Warnings.Add(
                            $"{assetPath} chưa có khối 'namespace {ns}' — enum mới được thêm vào một khối " +
                            $"'namespace {ns}' mới ở cuối file.");
                    }
                    else if (FeederEnumSourceEditor.DeclaresNamespace(masked))
                    {
                        change.Warnings.Add(
                            $"{assetPath} có namespace nhưng sheet đang để trống Namespace — enum mới nằm ở " +
                            "global scope (cuối file). Muốn nó nằm cùng namespace với code còn lại thì đặt " +
                            "trường Namespace của sheet.");
                    }
                }

                if (!filesByPath.TryGetValue(assetPath, out FeederEnumFileChange target))
                {
                    target = new FeederEnumFileChange
                    {
                        AssetPath = assetPath,
                        IsNewFile = false,
                        OriginalText = existingText,
                        OriginalHadBom = hadBom,
                        Newline = FeederEnumSourceEditor.DetectNewline(existingText),
                    };
                    filesByPath.Add(assetPath, target);
                    plan.Files.Add(target);
                }

                change.DeclareInExistingFile = true;
                target.Enums.Add(change);
                return;
            }

            if (!filesByPath.TryGetValue(assetPath, out FeederEnumFileChange file))
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

        public static void AssignNewEnumMembers(FeederEnumChange change, List<FeederEnumSheetValue> writable)
        {
            change.NewMembers.Clear();
            List<FeederEnumSheetValue> ordered = new List<FeederEnumSheetValue>(writable);

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
            for (int i = 0; i < ordered.Count; i++)
            {
                change.NewMembers.Add(new FeederEnumNewMember
                {
                    RawSheetValue = ordered[i].RawValue,
                    MemberName = ordered[i].MemberName ?? ordered[i].RawValue,
                    Value = i,
                    FirstSheetRow = ordered[i].FirstRow,
                    SourceTab = ordered[i].SourceTab,
                });
            }

            if (!change.IsBlocked && ordered.Count - 1 > ceiling)
            {
                change.BlockedReason =
                    $"{ordered.Count} giá trị vượt trần của '{change.UnderlyingTypeKeyword}' ({ceiling}). " +
                    "Đổi underlying type ở dropdown bên cạnh.";
            }
        }

        private static void BuildExistingEnumChange(FeederEnumUpdatePlan plan,
            Dictionary<string, FeederEnumFileChange> filesByPath, FeederEnumChange change, Type existing,
            List<FeederEnumSheetValue> writable, string enumScriptAssetPath)
        {
            change.EnumName = existing.Name;
            change.EnumFullName = existing.FullName;
            change.ExistingType = existing;
            change.IsNew = false;

            HashSet<string> existingNames = new HashSet<string>(Enum.GetNames(existing), StringComparer.Ordinal);

            List<FeederEnumSheetValue> missing = new List<FeederEnumSheetValue>();
            for (int i = 0; i < writable.Count; i++)
            {
                if (!existingNames.Contains(writable[i].RawValue))
                {
                    missing.Add(writable[i]);
                }
            }

            foreach (string name in existingNames)
            {
                bool inSheet = false;
                for (int i = 0; i < writable.Count; i++)
                {
                    if (string.Equals(writable[i].RawValue, name, StringComparison.Ordinal))
                    {
                        inSheet = true;
                        break;
                    }
                }

                if (!inSheet && !string.Equals(name, NoneMemberName, StringComparison.Ordinal))
                {
                    change.Orphans.Add(name);
                }
            }

            if (missing.Count == 0)
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

            if (!enumScriptAssetPath.IsNullOrWhitespace() &&
                !string.Equals(location.AssetPath, enumScriptAssetPath, StringComparison.OrdinalIgnoreCase))
            {
                change.Warnings.Add(
                    $"enum {existing.Name} đang nằm ở {location.AssetPath}, không phải Enum Script đã chỉ định " +
                    $"({enumScriptAssetPath}) — member mới vẫn được thêm tại chỗ.");
            }

            FeederEnumSourceEditor.GetNumbering(existing, out decimal next, out decimal ceiling, out string keyword);
            change.UnderlyingTypeKeyword = keyword;

            if (!change.IsBlocked && next + missing.Count - 1 > ceiling)
            {
                decimal room = ceiling - next + 1;
                change.BlockedReason =
                    $"enum {existing.Name} : {keyword} chỉ còn {room} chỗ nhưng cần {missing.Count}. " +
                    "Đổi underlying type sang int/ushort trước.";
            }

            FeederEnumSourceEditor.ComputeInsertion(location.Text, location.Masked,
                location.OpenBraceIndex, location.CloseBraceIndex,
                out int insertOffset, out bool needsComma, out bool bodyEmpty, out string indent);

            change.InsertOffset = insertOffset;
            change.NeedsLeadingComma = needsComma;
            change.BodyIsEmpty = bodyEmpty;
            change.Indent = indent;

            for (int i = 0; i < missing.Count; i++)
            {
                change.NewMembers.Add(new FeederEnumNewMember
                {
                    RawSheetValue = missing[i].RawValue,
                    MemberName = missing[i].MemberName ?? missing[i].RawValue,
                    Value = next + i,
                    FirstSheetRow = missing[i].FirstRow,
                    SourceTab = missing[i].SourceTab,
                });
            }

            if (!filesByPath.TryGetValue(location.AssetPath, out FeederEnumFileChange file))
            {
                file = new FeederEnumFileChange
                {
                    AssetPath = location.AssetPath,
                    IsNewFile = false,
                    OriginalText = location.Text,
                    OriginalHadBom = location.HadBom,
                    Newline = location.Newline,
                };
                filesByPath.Add(location.AssetPath, file);
                plan.Files.Add(file);
            }

            file.Enums.Add(change);
        }

        private static void MergeValues(List<FeederEnumColumnScan> columns,
            out List<FeederEnumSheetValue> writable, out List<FeederEnumSheetValue> rejected,
            out List<string> warnings, out List<string> tabs)
        {
            writable = new List<FeederEnumSheetValue>();
            rejected = new List<FeederEnumSheetValue>();
            warnings = new List<string>();
            tabs = new List<string>();

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int c = 0; c < columns.Count; c++)
            {
                FeederEnumColumnScan column = columns[c];
                if (!column.SourceTab.IsNullOrWhitespace() && !tabs.Contains(column.SourceTab))
                {
                    tabs.Add(column.SourceTab);
                }

                warnings.AddRange(column.Warnings);
                for (int i = 0; i < column.Values.Count; i++)
                {
                    FeederEnumSheetValue value = column.Values[i];
                    if (!seen.Add(value.RawValue))
                    {
                        continue;
                    }

                    if (value.IsWritable)
                    {
                        writable.Add(value);
                    }
                    else
                    {
                        rejected.Add(value);
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
                    AppendRejectedWarnings(item, change);
                    if (change.Orphans.Count > 0)
                    {
                        item.Warnings.Add(
                            $"{change.Orphans.Count} member có trong enum nhưng không có trong sheet " +
                            $"(chỉ báo, không xoá): {string.Join(", ", change.Orphans.ToArray())}");
                    }

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
            return $"+{change.NewMembers.Count} giá trị · {change.UnderlyingTypeKeyword}{tabs}";
        }

        private static void AppendRejectedWarnings(FeederChangeItem item, FeederEnumChange change)
        {
            if (change.RejectedValues.Count == 0)
            {
                return;
            }

            List<string> names = new List<string>();
            for (int i = 0; i < change.RejectedValues.Count && i < 8; i++)
            {
                names.Add($"'{change.RejectedValues[i].RawValue}'");
            }

            string suffix = change.RejectedValues.Count > names.Count
                ? $" (+{change.RejectedValues.Count - names.Count} nữa)"
                : string.Empty;
            item.Warnings.Add(
                $"{change.RejectedValues.Count} giá trị bị loại vì không phải tên C# hợp lệ: " +
                $"{string.Join(", ", names.ToArray())}{suffix}. Những dòng này sẽ lỗi Enum.Parse khi Generate Assets.");
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
                        memberCount += change.NewMembers.Count;
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
