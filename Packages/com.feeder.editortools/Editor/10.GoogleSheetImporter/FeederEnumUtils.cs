using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NabaGame.Core.Runtime.Extensions;

namespace Feeder
{
    public static class FeederEnumUtils
    {
        public static List<Type> EnumTypes;

        static FeederEnumUtils()
        {
            GetEnums();
        }

        public static void GetEnums()
        {
            List<Assembly> assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(assembly =>
                    assembly.GetName().Name == "Assembly-CSharp" || assembly.GetName().Name == "Assembly-CSharp-Editor")
                .ToList();
            EnumTypes = new List<Type>();
            if (!assemblies.IsNullOrEmpty())
            {
                foreach (Assembly assembly in assemblies)
                {
                    EnumTypes.AddRange(assembly.GetTypes().Where(type => type.IsEnum).ToList());
                }
            }
        }

        public static Type GetEnumTypeByName(string enumName)
        {
            return EnumTypes.FirstOrDefault(x => x.FullName.Contains(enumName));
        }
    }
}
