using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEngine;

namespace Feeder
{
    public sealed class FHierarchyPathOptionsProvider
    {
        public const string RootToken = "<Root>";

        public IEnumerable<ValueDropdownItem<string>> Build(GameObject target)
        {
            if (target == null)
                throw new InvalidOperationException("target is null.");

            var rootTransform = FPrefabRootResolver.GetRootTransform(target, out GameObject prefabRoot, out bool shouldUnload);

            try
            {
                var options = new List<ValueDropdownItem<string>>
                {
                    new ValueDropdownItem<string>(RootToken, RootToken)
                };

                var infos = FHierarchyAnalyzeUtils.CollectHierarchyInfo(rootTransform, false);
                var paths = new List<string>(infos.Count);
                for (int i = 0; i < infos.Count; i++)
                {
                    paths.Add(infos[i].Path);
                }

                paths.Sort(StringComparer.Ordinal);
                for (int i = 0; i < paths.Count; i++)
                {
                    options.Add(new ValueDropdownItem<string>(paths[i], paths[i]));
                }

                return options;
            }
            finally
            {
                if (shouldUnload && prefabRoot != null)
                {
                    UnityEditor.PrefabUtility.UnloadPrefabContents(prefabRoot);
                }
            }
        }
    }
}
