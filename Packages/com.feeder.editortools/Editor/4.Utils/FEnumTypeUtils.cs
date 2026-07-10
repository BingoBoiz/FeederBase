using System;
using System.Collections.Generic;
using System.Reflection;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEditor.Compilation;
using CompiledAssembly = UnityEditor.Compilation.Assembly;
using LoadedAssembly = System.Reflection.Assembly;

namespace Feeder
{
    public static class FEnumTypeUtils
    {
        private static List<ValueDropdownItem<string>> s_cachedDropdownItems;

        public static Type ResolveEnumType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            Type resolvedType = Type.GetType(typeName, false);
            if (resolvedType != null && resolvedType.IsEnum) return resolvedType;
            LoadedAssembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loadedAssemblies.Length; i++)
            {
                LoadedAssembly assembly = loadedAssemblies[i];
                resolvedType = assembly?.GetType(typeName, false);
                if (resolvedType != null && resolvedType.IsEnum) return resolvedType;
            }
            return null;
        }

        public static bool ShouldSkipEnumMember(string enumMemberName)
        {
            return string.Equals(enumMemberName, "None", StringComparison.OrdinalIgnoreCase);
        }

        public static IEnumerable<ValueDropdownItem<string>> GetEnumTypeDropdown()
        {
            if (s_cachedDropdownItems == null)
                s_cachedDropdownItems = BuildEnumTypeDropdownItems();

            for (int i = 0; i < s_cachedDropdownItems.Count; i++)
                yield return s_cachedDropdownItems[i];
        }

        [InitializeOnLoadMethod]
        private static void RegisterDropdownCacheInvalidation()
        {
            AssemblyReloadEvents.afterAssemblyReload += InvalidateDropdownCache;
        }

        private static void InvalidateDropdownCache()
        {
            s_cachedDropdownItems = null;
        }

        private static bool HasSourceFilesInAssets(CompiledAssembly compiledAssembly)
        {
            string[] sourceFiles = compiledAssembly.sourceFiles;
            if (sourceFiles == null) return false;
            for (int i = 0; i < sourceFiles.Length; i++)
            {
                string path = sourceFiles[i];
                if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static List<ValueDropdownItem<string>> BuildEnumTypeDropdownItems()
        {
            List<(string display, string value)> sortedPairs = new List<(string display, string value)>();
            CompiledAssembly[] compiledAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor);

            for (int i = 0; i < compiledAssemblies.Length; i++)
            {
                CompiledAssembly compiledAssembly = compiledAssemblies[i];
                if (!HasSourceFilesInAssets(compiledAssembly))
                    continue;

                LoadedAssembly loadedAssembly = FindLoadedAssembly(compiledAssembly.name);
                if (loadedAssembly == null)
                    continue;

                Type[] types = GetAssemblyTypes(loadedAssembly);
                for (int t = 0; t < types.Length; t++)
                {
                    Type enumType = types[t];
                    if (enumType?.IsEnum != true)
                        continue;
                    if (!IsVisibleEnumType(enumType))
                        continue;

                    string fullName = enumType.FullName ?? enumType.Name;
                    string qualifiedName = enumType.AssemblyQualifiedName ?? fullName;
                    sortedPairs.Add((fullName, qualifiedName));
                }
            }

            sortedPairs.Sort((a, b) => string.Compare(a.display, b.display, StringComparison.Ordinal));

            List<ValueDropdownItem<string>> dropdownItems = new List<ValueDropdownItem<string>>(sortedPairs.Count);
            for (int i = 0; i < sortedPairs.Count; i++)
            {
                (string display, string value) pair = sortedPairs[i];
                dropdownItems.Add(new ValueDropdownItem<string>(pair.display, pair.value));
            }

            return dropdownItems;
        }

        private static LoadedAssembly FindLoadedAssembly(string assemblyName)
        {
            LoadedAssembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loadedAssemblies.Length; i++)
            {
                LoadedAssembly assembly = loadedAssemblies[i];
                if (assembly.GetName().Name == assemblyName)
                    return assembly;
            }
            return null;
        }

        private static Type[] GetAssemblyTypes(LoadedAssembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types;
            }
        }

        private static bool IsVisibleEnumType(Type enumType)
        {
            if (enumType.IsPublic)
                return true;
            return enumType.IsNested && enumType.IsNestedPublic;
        }
    }
}
