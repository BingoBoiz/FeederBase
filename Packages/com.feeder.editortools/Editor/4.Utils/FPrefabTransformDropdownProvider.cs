using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEngine;

namespace Feeder
{
    public sealed class FPrefabTransformDropdownProvider
    {
        private GameObject cachedPrefab;
        private List<ValueDropdownItem<Transform>> cachedItems;

        public IEnumerable<ValueDropdownItem<Transform>> Get(GameObject basePrefab)
        {
            if (basePrefab == null)
                return new List<ValueDropdownItem<Transform>>(0);

            if (cachedPrefab != basePrefab || cachedItems == null)
            {
                cachedPrefab = basePrefab;
                cachedItems = FHierarchyAnalyzeUtils.BuildTransformDropdown(basePrefab.transform);
            }

            return cachedItems;
        }

        public void Reset()
        {
            cachedPrefab = null;
            cachedItems = null;
        }
    }
}
