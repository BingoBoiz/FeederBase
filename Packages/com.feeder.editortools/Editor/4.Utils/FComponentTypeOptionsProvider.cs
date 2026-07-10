using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    public static class FComponentTypeOptionsProvider
    {
        public static IEnumerable<ValueDropdownItem<Type>> GetComponentTypeOptions()
        {
            var types = TypeCache.GetTypesDerivedFrom<Component>();
            var list = new List<Type>(types.Count);

            for (int i = 0; i < types.Count; i++)
            {
                var type = types[i];
                if (type.IsAbstract) continue;
                list.Add(type);
            }

            list.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));

            for (int i = 0; i < list.Count; i++)
            {
                var type = list[i];
                var name = type.FullName ?? throw new InvalidOperationException("component full name is null.");
                yield return new ValueDropdownItem<Type>(name, type);
            }
        }
    }
}
