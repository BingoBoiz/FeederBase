using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NabaGame.Core.Runtime.Extensions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Feeder
{
    public enum FeederEnumResolveStatus
    {
        Resolved,
        NotFound,
        Ambiguous,
    }

    public static class FeederEnumUtils
    {
        private static List<Type> cachedEnumTypes;

        public static List<Type> EnumTypes
        {
            get
            {
                if (cachedEnumTypes == null)
                {
                    GetEnums();
                }

                return cachedEnumTypes;
            }
        }

        [InitializeOnLoadMethod]
        private static void RegisterCacheInvalidation()
        {
            AssemblyReloadEvents.afterAssemblyReload -= InvalidateCache;
            AssemblyReloadEvents.afterAssemblyReload += InvalidateCache;
        }

        // Sau khi ghi file .cs mới + AssetDatabase.Refresh() phải gọi cái này, nếu không cache vẫn là bản cũ.
        public static void InvalidateCache()
        {
            cachedEnumTypes = null;
        }

        public static void GetEnums()
        {
            cachedEnumTypes = new List<Type>();

            HashSet<string> projectAssemblyNames = GetProjectAssemblyNames();
            System.Reflection.Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loaded.Length; i++)
            {
                string assemblyName = loaded[i].GetName().Name;
                if (!projectAssemblyNames.Contains(assemblyName))
                {
                    continue;
                }

                foreach (Type type in GetAssemblyTypes(loaded[i]))
                {
                    if (type != null && type.IsEnum)
                    {
                        cachedEnumTypes.Add(type);
                    }
                }
            }
        }

        // Assembly "của project" = assembly có ít nhất một source file nằm dưới Assets/.
        // Rộng hơn cách cũ (chỉ Assembly-CSharp + Assembly-CSharp-Editor) nên không hụt khi ai đó thêm asmdef.
        private static HashSet<string> GetProjectAssemblyNames()
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                UnityEditor.Compilation.Assembly[] compiled =
                    CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                for (int i = 0; i < compiled.Length; i++)
                {
                    if (HasSourceFilesInAssets(compiled[i]))
                    {
                        names.Add(compiled[i].name);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Feeder] Không đọc được CompilationPipeline, dùng danh sách assembly mặc định: {e.Message}");
            }

            if (names.Count == 0)
            {
                names.Add("Assembly-CSharp");
                names.Add("Assembly-CSharp-Editor");
            }

            return names;
        }

        private static bool HasSourceFilesInAssets(UnityEditor.Compilation.Assembly assembly)
        {
            string[] sourceFiles = assembly?.sourceFiles;
            if (sourceFiles == null)
            {
                return false;
            }

            for (int i = 0; i < sourceFiles.Length; i++)
            {
                if (sourceFiles[i] != null &&
                    sourceFiles[i].Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<Type> GetAssemblyTypes(System.Reflection.Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                // Assembly có type hỏng vẫn trả về phần load được thay vì làm hỏng cả lượt quét.
                return e.Types ?? Array.Empty<Type>();
            }
            catch (Exception)
            {
                return Array.Empty<Type>();
            }
        }

        /// <summary>
        /// Phân giải token enum viết trong header sheet (ví dụ "SkillConfigType" hoặc "Yolo.testEnum.TreeType").
        /// Khác <see cref="GetEnumTypeByName"/> ở chỗ phân biệt được "không có" và "trùng tên nhiều nơi".
        /// </summary>
        public static FeederEnumResolveStatus TryResolveEnumType(string token, out Type resolved, out List<Type> candidates)
        {
            resolved = null;
            candidates = null;
            if (token.IsNullOrWhitespace())
            {
                return FeederEnumResolveStatus.NotFound;
            }

            string wanted = token.Trim();

            Type direct = Type.GetType(wanted, false);
            if (direct != null && direct.IsEnum)
            {
                resolved = direct;
                return FeederEnumResolveStatus.Resolved;
            }

            List<Type> all = EnumTypes;

            // FullName tuyệt đối, hoặc dạng người dùng hay gõ: đổi '+' (nested) thành '.'.
            for (int i = 0; i < all.Count; i++)
            {
                string fullName = all[i].FullName;
                if (fullName == null)
                {
                    continue;
                }

                if (fullName == wanted || fullName.Replace('+', '.') == wanted)
                {
                    resolved = all[i];
                    return FeederEnumResolveStatus.Resolved;
                }
            }

            // Tên ngắn: chỉ chấp nhận khi duy nhất, còn lại báo Ambiguous để người dùng ghi tên đầy đủ.
            List<Type> byName = all.Where(x => x.Name == wanted).ToList();
            if (byName.Count == 1)
            {
                resolved = byName[0];
                return FeederEnumResolveStatus.Resolved;
            }

            if (byName.Count > 1)
            {
                candidates = byName;
                return FeederEnumResolveStatus.Ambiguous;
            }

            return FeederEnumResolveStatus.NotFound;
        }

        public static Type GetEnumTypeByName(string enumName)
        {
            if (TryResolveEnumType(enumName, out Type exact, out List<Type> _) == FeederEnumResolveStatus.Resolved)
            {
                return exact;
            }

            // Tương thích ngược với hành vi cũ (FullName.Contains): chỉ nhận khi khớp DUY NHẤT và luôn log.
            List<Type> loose = EnumTypes.Where(x => x.FullName != null && x.FullName.Contains(enumName)).ToList();
            if (loose.Count != 1)
            {
                return null;
            }

            Debug.LogWarning(
                $"[Feeder] Enum '{enumName}' chỉ khớp kiểu chuỗi con → '{loose[0].FullName}'. " +
                "Nên ghi tên đầy đủ trong header sheet để tránh khớp nhầm.");
            return loose[0];
        }
    }
}
