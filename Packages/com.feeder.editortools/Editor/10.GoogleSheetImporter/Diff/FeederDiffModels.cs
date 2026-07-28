using System;
using System.Collections.Generic;

namespace Feeder
{
    public enum FeederDiffOp
    {
        Equal,
        Added,
        Removed,
    }

    public struct FeederDiffLine
    {
        public FeederDiffOp Op;

        // 1-based; -1 khi dòng không tồn tại ở phía đó.
        public int OldLine;
        public int NewLine;

        // Text đã đổi tab thành space, dùng để vẽ (mọi phép tính pixel dựa vào nó nên phải là mono-safe).
        public string Text;

        // Text gốc giữ nguyên tab, dùng khi export unified diff ra clipboard.
        public string RawText;

        // Vùng highlight trong Text (word-level). HlEnd <= HlStart nghĩa là không highlight.
        public int HlStart;
        public int HlEnd;
    }

    public sealed class FeederDiffHunk
    {
        public int OldStart;
        public int OldCount;
        public int NewStart;
        public int NewCount;
        public string Header;
        public List<FeederDiffLine> Lines = new List<FeederDiffLine>();
    }

    public sealed class FeederDiffFileResult
    {
        public List<FeederDiffHunk> Hunks = new List<FeederDiffHunk>();
        public int AddedCount;
        public int RemovedCount;

        // Số ký tự của dòng dài nhất — dùng tính chiều rộng content, tránh gọi GUIStyle.CalcSize mỗi dòng.
        public int MaxLineChars;

        // true khi diff quá lớn và đã fallback sang "thay toàn bộ file".
        public bool BailedOut;

        public bool HasChanges => AddedCount > 0 || RemovedCount > 0;
    }

    /// <summary>
    /// Một mục con có thể bật/tắt riêng trong một file (ví dụ: một enum trong file enum collection).
    /// </summary>
    public sealed class FeederChangeItem
    {
        public string Title;
        public string Subtitle;

        // Không apply được (ví dụ enum [Flags], tràn byte, file ngoài Assets/). Khác null => luôn bị loại.
        public string BlockedReason;

        public List<string> Warnings = new List<string>();

        // Payload để producer nhận diện lại item khi Rebuild (thường là FeederEnumChange).
        public object Payload;

        public bool IsBlocked => !string.IsNullOrEmpty(BlockedReason);
    }

    public sealed class FeederFileChange
    {
        // Luôn là đường dẫn "Assets/..." dùng forward slash.
        public string AssetPath;

        // null => file mới hoàn toàn.
        public string OriginalText;

        // Nội dung đề xuất khi TẤT CẢ item được chọn.
        public string NewText;

        public bool OriginalHadBom;
        public string Newline = Environment.NewLine;

        public List<FeederChangeItem> Items = new List<FeederChangeItem>();

        // Producer cung cấp: dựng lại nội dung file cho một tập con item.
        // null => cửa sổ diff ẩn checkbox cấp item, chỉ cho bật/tắt cả file.
        public Func<IReadOnlyList<FeederChangeItem>, string> Rebuild;

        public bool IsNewFile => OriginalText == null;
    }

    public sealed class FeederChangeSet
    {
        public List<FeederFileChange> Files = new List<FeederFileChange>();

        // Cảnh báo cấp toàn bộ change set, hiện trên đầu cửa sổ diff (không chặn apply).
        public List<string> Issues = new List<string>();
    }

    public sealed class FeederDiffApplyResult
    {
        public bool Accepted;

        // Chỉ các file người dùng chọn; NewText đã được tính lại theo đúng tập item đang chọn.
        public List<FeederFileChange> AcceptedFiles = new List<FeederFileChange>();
    }
}
