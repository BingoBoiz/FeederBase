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
        private static List<string> cachedNamespaces;

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

        public static List<string> ProjectNamespaces
        {
            get
            {
                if (cachedNamespaces == null)
                {
                    GetEnums();
                }

                return cachedNamespaces;
            }
        }

        [InitializeOnLoadMethod]
        private static void RegisterCacheInvalidation()
        {
            AssemblyReloadEvents.afterAssemblyReload -= InvalidateCache;
            AssemblyReloadEvents.afterAssemblyReload += InvalidateCache;
        }

        public static void InvalidateCache()
        {
            cachedEnumTypes = null;
            cachedNamespaces = null;
        }

        public static void GetEnums()
        {
            cachedEnumTypes = new List<Type>();
            HashSet<string> namespaceSet = new HashSet<string>(StringComparer.Ordinal);

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
                    if (type == null)
                    {
                        continue;
                    }

                    if (type.IsEnum)
                    {
                        cachedEnumTypes.Add(type);
                    }

                    if (!string.IsNullOrEmpty(type.Namespace))
                    {
                        namespaceSet.Add(type.Namespace);
                    }
                }
            }

            cachedNamespaces = namespaceSet.ToList();
            cachedNamespaces.Sort(StringComparer.Ordinal);
        }

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
                return e.Types ?? Array.Empty<Type>();
            }
            catch (Exception)
            {
                return Array.Empty<Type>();
            }
        }

        public static bool ScopeMatches(Type type, string sheetNamespace)
        {
            if (type == null)
            {
                return false;
            }

            return sheetNamespace.IsNullOrWhitespace()
                ? string.IsNullOrEmpty(type.Namespace)
                : string.Equals(type.Namespace, sheetNamespace.Trim(), StringComparison.Ordinal);
        }

        public static string DescribeScope(string sheetNamespace)
        {
            return sheetNamespace.IsNullOrWhitespace()
                ? "global scope (trường Namespace của sheet đang trống)"
                : $"namespace '{sheetNamespace.Trim()}'";
        }

        public static List<Type> FindEnumsOutsideScope(string shortName, string sheetNamespace)
        {
            if (shortName.IsNullOrWhitespace())
            {
                return new List<Type>();
            }

            string wanted = shortName.Trim();
            return EnumTypes.Where(x => x.Name == wanted && !ScopeMatches(x, sheetNamespace)).ToList();
        }

        public static FeederEnumResolveStatus TryResolveEnumType(string token, string sheetNamespace,
            out Type resolved, out List<Type> candidates)
        {
            resolved = null;
            candidates = null;
            if (token.IsNullOrWhitespace())
            {
                return FeederEnumResolveStatus.NotFound;
            }

            string wanted = token.Trim();
            List<Type> all = EnumTypes;

            if (wanted.IndexOf('.') >= 0)
            {
                Type direct = Type.GetType(wanted, false);
                if (direct != null && direct.IsEnum)
                {
                    resolved = direct;
                    return FeederEnumResolveStatus.Resolved;
                }

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

                return FeederEnumResolveStatus.NotFound;
            }

            List<Type> inScope = all.Where(x => x.Name == wanted && ScopeMatches(x, sheetNamespace)).ToList();
            if (inScope.Count == 0)
            {
                return FeederEnumResolveStatus.NotFound;
            }

            if (inScope.Count == 1)
            {
                resolved = inScope[0];
                return FeederEnumResolveStatus.Resolved;
            }

            List<Type> topLevel = inScope.Where(x => !x.IsNested).ToList();
            if (topLevel.Count == 1)
            {
                resolved = topLevel[0];
                return FeederEnumResolveStatus.Resolved;
            }

            candidates = inScope;
            return FeederEnumResolveStatus.Ambiguous;
        }

        public static FeederEnumResolveStatus TryResolveEnumType(string token, out Type resolved,
            out List<Type> candidates)
        {
            return TryResolveEnumType(token, string.Empty, out resolved, out candidates);
        }

        public static Type GetEnumTypeByName(string enumName, string sheetNamespace)
        {
            if (TryResolveEnumType(enumName, sheetNamespace, out Type exact, out List<Type> _) ==
                FeederEnumResolveStatus.Resolved)
            {
                return exact;
            }

            List<Type> loose = EnumTypes
                .Where(x => x.FullName != null && x.FullName.Contains(enumName) && ScopeMatches(x, sheetNamespace))
                .ToList();
            if (loose.Count != 1)
            {
                return null;
            }

            Debug.LogWarning(
                $"[Feeder] Enum '{enumName}' chỉ khớp kiểu chuỗi con → '{loose[0].FullName}'. " +
                "Nên ghi tên đầy đủ trong header sheet để tránh khớp nhầm.");
            return loose[0];
        }

        public static Type GetEnumTypeByName(string enumName)
        {
            return GetEnumTypeByName(enumName, string.Empty);
        }
    }
}
