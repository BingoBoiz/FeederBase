using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Session-only revert support for AssetDatabase operations that Unity's Undo system cannot cover.
    /// Instead of deleting, assets are moved into Assets/_FeederTrash/&lt;timestamp&gt;/ with
    /// AssetDatabase.MoveAsset — the GUID and .meta are preserved, so restoring an asset re-links every
    /// reference automatically. Moves and renames are recorded so they can be played back in reverse.
    /// History lives in memory only; it is lost on domain reload.
    /// </summary>
    public static class FAssetRevertService
    {
        public const string TrashRoot = "Assets/_FeederTrash";

        public sealed class RevertEntry
        {
            public enum Kind { TrashedAsset, MovedAsset, RenamedAsset, RenamedSprite }

            public Kind EntryKind;
            public string OriginalPath; // TrashedAsset/MovedAsset: restore destination
            public string CurrentPath;  // TrashedAsset: trash path; MovedAsset: new path; RenamedAsset: path after rename; RenamedSprite: texture path
            public string OldName;      // RenamedAsset / RenamedSprite
            public string NewName;      // RenamedSprite: name after rename (reverse pair source)
        }

        public sealed class RevertOperation
        {
            public string Label;
            public string UserNote;
            public readonly List<RevertEntry> Entries = new List<RevertEntry>();
            public bool IsEmpty => Entries.Count == 0;
            internal string TrashFolder; // lazily created per operation
        }

        public static RevertOperation Begin(string label, string userNote = null)
        {
            return new RevertOperation { Label = label, UserNote = userNote };
        }

        /// <summary>
        /// Moves the asset into the operation's trash folder instead of deleting it.
        /// Must be called OUTSIDE any StartAssetEditing batch (folder creation needs an import).
        /// Returns the trash path, or null on failure.
        /// </summary>
        public static string MoveToTrash(RevertOperation op, string assetPath)
        {
            if (op == null || string.IsNullOrEmpty(assetPath))
                return null;

            string folder = EnsureTrashFolder(op);
            if (folder == null)
                return null;

            string trashPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Path.GetFileName(assetPath)}");
            string error = AssetDatabase.MoveAsset(assetPath, trashPath);
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError($"[{op.Label}] Không thể chuyển '{assetPath}' vào trash: {error}");
                return null;
            }

            op.Entries.Add(new RevertEntry
            {
                EntryKind = RevertEntry.Kind.TrashedAsset,
                OriginalPath = assetPath,
                CurrentPath = trashPath
            });
            return trashPath;
        }

        public static void RecordMove(RevertOperation op, string fromPath, string toPath)
        {
            op?.Entries.Add(new RevertEntry
            {
                EntryKind = RevertEntry.Kind.MovedAsset,
                OriginalPath = fromPath,
                CurrentPath = toPath
            });
        }

        public static void RecordAssetRename(RevertOperation op, string pathAfterRename, string oldName)
        {
            op?.Entries.Add(new RevertEntry
            {
                EntryKind = RevertEntry.Kind.RenamedAsset,
                CurrentPath = pathAfterRename,
                OldName = oldName
            });
        }

        public static void RecordSpriteRename(RevertOperation op, string texturePath, string oldName, string newName)
        {
            op?.Entries.Add(new RevertEntry
            {
                EntryKind = RevertEntry.Kind.RenamedSprite,
                CurrentPath = texturePath,
                OldName = oldName,
                NewName = newName
            });
        }

        /// <summary>Plays the operation back in reverse order. Returns true when every entry reverted.</summary>
        public static bool Revert(RevertOperation op, out string report)
        {
            var sb = new StringBuilder();
            if (op == null || op.IsEmpty)
            {
                report = "Không có thao tác nào để revert.";
                return false;
            }

            int ok = 0, failed = 0;
            // Sprite renames are batched per texture so each texture reimports once.
            var spriteRenamesByTexture = new Dictionary<string, List<(string oldName, string newName)>>();

            for (int i = op.Entries.Count - 1; i >= 0; i--)
            {
                RevertEntry e = op.Entries[i];
                switch (e.EntryKind)
                {
                    case RevertEntry.Kind.TrashedAsset:
                    case RevertEntry.Kind.MovedAsset:
                    {
                        string dest = e.OriginalPath;
                        if (AssetDatabase.LoadAssetAtPath<Object>(dest) != null)
                        {
                            dest = AssetDatabase.GenerateUniqueAssetPath(dest);
                            sb.AppendLine($"⚠ '{e.OriginalPath}' đã bị chiếm — restore về '{dest}'.");
                        }
                        string error = AssetDatabase.MoveAsset(e.CurrentPath, dest);
                        if (string.IsNullOrEmpty(error)) { ok++; sb.AppendLine($"✔ {e.CurrentPath} → {dest}"); }
                        else { failed++; sb.AppendLine($"✘ {e.CurrentPath}: {error}"); }
                        break;
                    }
                    case RevertEntry.Kind.RenamedAsset:
                    {
                        string error = AssetDatabase.RenameAsset(e.CurrentPath, e.OldName);
                        if (string.IsNullOrEmpty(error)) { ok++; sb.AppendLine($"✔ {e.CurrentPath} → {e.OldName}"); }
                        else { failed++; sb.AppendLine($"✘ {e.CurrentPath}: {error}"); }
                        break;
                    }
                    case RevertEntry.Kind.RenamedSprite:
                    {
                        if (!spriteRenamesByTexture.TryGetValue(e.CurrentPath, out var list))
                            spriteRenamesByTexture[e.CurrentPath] = list = new List<(string, string)>();
                        list.Add((e.NewName, e.OldName)); // reverse: new → old
                        break;
                    }
                }
            }

            foreach (var pair in spriteRenamesByTexture)
            {
                int renamed = FSpriteRenameUtils.RenameSprites(pair.Key, pair.Value, saveAndReimport: true);
                ok += renamed;
                failed += pair.Value.Count - renamed;
                sb.AppendLine($"Sprite revert '{pair.Key}': {renamed}/{pair.Value.Count}");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            CleanupTrashFolderIfEmpty(op);

            sb.Insert(0, $"[{op.Label}] Revert: {ok} thành công, {failed} lỗi.\n");
            if (!string.IsNullOrEmpty(op.UserNote))
                sb.AppendLine(op.UserNote);
            report = sb.ToString();
            return failed == 0;
        }

        [MenuItem("Feeder/Empty Feeder Trash")]
        public static void EmptyTrash()
        {
            if (!AssetDatabase.IsValidFolder(TrashRoot))
            {
                EditorUtility.DisplayDialog("Empty Feeder Trash", "Trash đang trống.", "OK");
                return;
            }

            int count = AssetDatabase.FindAssets("", new[] { TrashRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Count(p => !AssetDatabase.IsValidFolder(p));
            if (!EditorUtility.DisplayDialog("Empty Feeder Trash",
                    $"Delete {count} trashed asset(s) permanently? This cannot be undone.", "Delete", "Cancel"))
                return;

            AssetDatabase.DeleteAsset(TrashRoot);
            AssetDatabase.Refresh();
            Debug.Log($"[FeederTrash] Đã xoá vĩnh viễn {count} asset.");
        }

        private static string EnsureTrashFolder(RevertOperation op)
        {
            if (!string.IsNullOrEmpty(op.TrashFolder) && AssetDatabase.IsValidFolder(op.TrashFolder))
                return op.TrashFolder;

            if (!AssetDatabase.IsValidFolder(TrashRoot))
                AssetDatabase.CreateFolder("Assets", "_FeederTrash");
            if (!AssetDatabase.IsValidFolder(TrashRoot))
            {
                Debug.LogError($"[{op.Label}] Không tạo được folder {TrashRoot}.");
                return null;
            }

            string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string guid = AssetDatabase.CreateFolder(TrashRoot, stamp);
            op.TrashFolder = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(op.TrashFolder) ? null : op.TrashFolder;
        }

        private static void CleanupTrashFolderIfEmpty(RevertOperation op)
        {
            if (string.IsNullOrEmpty(op.TrashFolder) || !AssetDatabase.IsValidFolder(op.TrashFolder))
                return;
            bool empty = AssetDatabase.FindAssets("", new[] { op.TrashFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .All(AssetDatabase.IsValidFolder);
            if (empty)
                AssetDatabase.DeleteAsset(op.TrashFolder);
        }
    }
}
