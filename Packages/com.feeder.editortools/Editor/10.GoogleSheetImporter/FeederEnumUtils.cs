using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
        private static HashSet<string> cachedTypeNames;

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

        public static bool IsProjectTypeName(string fullName)
        {
            if (cachedTypeNames == null)
            {
                GetEnums();
            }

            return cachedTypeNames.Contains(fullName);
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
            cachedTypeNames = null;
        }

        public static void GetEnums()
        {
            cachedEnumTypes = new List<Type>();
            cachedTypeNames = new HashSet<string>(StringComparer.Ordinal);

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

                    cachedTypeNames.Add(ToFullName(type));
                    if (type.IsEnum)
                    {
                        cachedEnumTypes.Add(type);
                    }
                }
            }
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

        public static readonly Regex QualifiedIdentifier =
            new Regex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$");

        public static string ToFullName(Type type)
        {
            return type.FullName?.Replace('+', '.') ?? type.Name;
        }

        public static void SplitFullName(string fullName, out string ns, out string shortName)
        {
            int dot = fullName.LastIndexOf('.');
            ns = dot < 0 ? string.Empty : fullName.Substring(0, dot);
            shortName = dot < 0 ? fullName : fullName.Substring(dot + 1);
        }

        // a header token is a full name: no dot means global scope, nothing is looked up implicitly
        public static FeederEnumResolveStatus TryResolveEnumType(string token, out Type resolved,
            out List<Type> candidates)
        {
            resolved = null;
            candidates = null;
            if (token.IsNullOrWhitespace())
            {
                return FeederEnumResolveStatus.NotFound;
            }

            string wanted = token.Trim();
            List<Type> matches = EnumTypes.Where(x => ToFullName(x) == wanted).ToList();
            if (matches.Count == 0 && wanted.IndexOf('.') >= 0)
            {
                Type direct = Type.GetType(wanted, false);
                if (direct != null && direct.IsEnum)
                {
                    matches.Add(direct);
                }
            }

            if (matches.Count == 0)
            {
                return FeederEnumResolveStatus.NotFound;
            }

            if (matches.Count == 1)
            {
                resolved = matches[0];
                return FeederEnumResolveStatus.Resolved;
            }

            candidates = matches;
            return FeederEnumResolveStatus.Ambiguous;
        }

        public static List<Type> FindEnumsWithSameShortName(string token)
        {
            if (token.IsNullOrWhitespace())
            {
                return new List<Type>();
            }

            string wanted = token.Trim();
            SplitFullName(wanted, out string _, out string shortName);
            return EnumTypes.Where(x => x.Name == shortName && ToFullName(x) != wanted).ToList();
        }

        public static string DescribeTypes(List<Type> types)
        {
            return string.Join(", ", types.ConvertAll(x => $"{ToFullName(x)} ({x.Assembly.GetName().Name})").ToArray());
        }
    }
}
