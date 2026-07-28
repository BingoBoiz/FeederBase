using System;
using System.Collections.Generic;

namespace Feeder
{
    public enum FeederEnumValueStatus
    {
        Ok,
        Keyword,
        Invalid,
        DuplicateIgnoreCase,
    }

    public struct FeederEnumSheetValue
    {
        public string RawValue;

        public string MemberName;

        public int FirstRow;
        public string SourceTab;
        public FeederEnumValueStatus Status;
        public string StatusDetail;

        public bool IsWritable => Status == FeederEnumValueStatus.Ok || Status == FeederEnumValueStatus.Keyword;
    }

    public sealed class FeederEnumColumnScan
    {
        public int ColumnIndex;
        public string SourceTab;
        public string RawHeader;
        public string FieldName;
        public string EnumTypeToken;
        public List<FeederEnumSheetValue> Values = new List<FeederEnumSheetValue>();
        public List<string> Warnings = new List<string>();
    }

    public struct FeederEnumNewMember
    {
        public string RawSheetValue;
        public string MemberName;
        public decimal Value;
        public int FirstSheetRow;
        public string SourceTab;
    }

    public sealed class FeederEnumChange
    {
        public bool Include = true;

        public string EnumToken;
        public string EnumName;
        public string EnumFullName;
        public bool IsNew;

        public bool DeclareInExistingFile;

        public string BlockIndent = string.Empty;

        public string WrapNamespace;

        [NonSerialized] public Type ExistingType;

        public string UnderlyingTypeKeyword = "byte";
        public bool InsertNoneZero = true;

        public int InsertOffset;
        public bool NeedsLeadingComma;

        public bool BodyIsEmpty;
        public string Indent = "    ";

        public List<FeederEnumNewMember> NewMembers = new List<FeederEnumNewMember>();
        public List<FeederEnumSheetValue> RejectedValues = new List<FeederEnumSheetValue>();
        public List<string> Orphans = new List<string>();
        public List<string> Warnings = new List<string>();

        public string BlockedReason;

        public List<string> SourceTabs = new List<string>();

        public bool IsBlocked => !string.IsNullOrEmpty(BlockedReason);
        public bool HasWork => !IsBlocked && NewMembers.Count > 0;
    }

    public sealed class FeederEnumFileChange
    {
        public string AssetPath;
        public bool IsNewFile;
        public string OriginalText;
        public bool OriginalHadBom;
        public string Newline = Environment.NewLine;

        public string Namespace;

        public List<FeederEnumChange> Enums = new List<FeederEnumChange>();
    }

    public sealed class FeederEnumUpdatePlan
    {
        public string SheetTypeName;
        public List<FeederEnumFileChange> Files = new List<FeederEnumFileChange>();

        public List<string> Issues = new List<string>();

        public bool HasApplicableChange
        {
            get
            {
                for (int i = 0; i < Files.Count; i++)
                {
                    List<FeederEnumChange> enums = Files[i].Enums;
                    for (int j = 0; j < enums.Count; j++)
                    {
                        if (enums[j].HasWork)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }
    }
}
