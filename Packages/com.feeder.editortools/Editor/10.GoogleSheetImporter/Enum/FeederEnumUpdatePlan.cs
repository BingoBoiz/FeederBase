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

    /// <summary>Một giá trị đọc được từ một ô trong cột enum.</summary>
    public struct FeederEnumSheetValue
    {
        // Đúng như trong sheet sau Trim(). Tên member PHẢI bám theo đây vì
        // FeederDataAssetGenerator gọi Enum.Parse trên chính chuỗi này.
        public string RawValue;

        // Tên viết ra file .cs — chỉ khác RawValue ở tiền tố '@' khi trùng từ khoá C#.
        public string MemberName;

        public int FirstRow;
        public string SourceTab;
        public FeederEnumValueStatus Status;
        public string StatusDetail;

        public bool IsWritable => Status == FeederEnumValueStatus.Ok || Status == FeederEnumValueStatus.Keyword;
    }

    /// <summary>Kết quả quét một cột "s_Field:EnumType".</summary>
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

    /// <summary>Thay đổi đề xuất cho MỘT enum.</summary>
    public sealed class FeederEnumChange
    {
        public bool Include = true;

        public string EnumToken;
        public string EnumName;
        public string EnumFullName;
        public bool IsNew;

        [NonSerialized] public Type ExistingType;

        // Chỉ đổi được khi IsNew.
        public string UnderlyingTypeKeyword = "byte";
        public bool InsertNoneZero = true;

        // Vị trí chèn tính trên OriginalText của file (không đổi khi bật/tắt enum khác).
        public int InsertOffset;
        public bool NeedsLeadingComma;
        public bool BodyIsEmpty;
        public string Indent = "    ";

        public List<FeederEnumNewMember> NewMembers = new List<FeederEnumNewMember>();
        public List<FeederEnumSheetValue> RejectedValues = new List<FeederEnumSheetValue>();
        public List<string> Orphans = new List<string>();
        public List<string> Warnings = new List<string>();

        // Khác null => không bao giờ được ghi.
        public string BlockedReason;

        public List<string> SourceTabs = new List<string>();

        public bool IsBlocked => !string.IsNullOrEmpty(BlockedReason);
        public bool HasWork => !IsBlocked && NewMembers.Count > 0;
    }

    /// <summary>Tập thay đổi của một file .cs (có thể chứa nhiều enum).</summary>
    public sealed class FeederEnumFileChange
    {
        // Luôn "Assets/..." forward slash.
        public string AssetPath;
        public bool IsNewFile;
        public string OriginalText;
        public bool OriginalHadBom;
        public string Newline = Environment.NewLine;
        public List<FeederEnumChange> Enums = new List<FeederEnumChange>();
    }

    public sealed class FeederEnumUpdatePlan
    {
        public string SheetTypeName;
        public List<FeederEnumFileChange> Files = new List<FeederEnumFileChange>();

        // Vấn đề không gắn với file cụ thể (enum trùng tên, nằm ngoài Assets/, không tìm được source...).
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
