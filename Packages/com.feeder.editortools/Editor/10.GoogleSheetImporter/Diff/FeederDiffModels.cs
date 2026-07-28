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

        public int OldLine;
        public int NewLine;

        public string Text;

        public string RawText;

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

        public int MaxLineChars;

        public bool BailedOut;

        public bool HasChanges => AddedCount > 0 || RemovedCount > 0;
    }

    public sealed class FeederChangeItem
    {
        public string Title;
        public string Subtitle;

        public string BlockedReason;

        public List<string> Warnings = new List<string>();

        public object Payload;

        public bool IsBlocked => !string.IsNullOrEmpty(BlockedReason);
    }

    public sealed class FeederFileChange
    {
        public string AssetPath;

        public string OriginalText;

        public string NewText;

        public bool OriginalHadBom;
        public string Newline = Environment.NewLine;

        public List<FeederChangeItem> Items = new List<FeederChangeItem>();

        public Func<IReadOnlyList<FeederChangeItem>, string> Rebuild;

        public bool IsNewFile => OriginalText == null;
    }

    public sealed class FeederChangeSet
    {
        public List<FeederFileChange> Files = new List<FeederFileChange>();

        public List<string> Issues = new List<string>();
    }

    public sealed class FeederDiffApplyResult
    {
        public bool Accepted;

        public List<FeederFileChange> AcceptedFiles = new List<FeederFileChange>();
    }
}
