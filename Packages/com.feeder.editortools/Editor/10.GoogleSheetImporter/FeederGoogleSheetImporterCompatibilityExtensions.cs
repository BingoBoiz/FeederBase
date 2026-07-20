using System;
using System.Collections.Generic;

namespace NabaGame.Core.Runtime.Extensions
{
    // Compatibility methods used by the Feeder importer copy.
    // They preserve the behavior of the two source-package dependencies without
    // requiring Feeder Editor Tools consumers to install those packages.
    internal static class FeederGoogleSheetImporterCompatibilityExtensions
    {
        public static bool IsNullOrWhitespace(this string str)
        {
            if (!string.IsNullOrEmpty(str))
            {
                for (int index = 0; index < str.Length; ++index)
                {
                    if (!char.IsWhiteSpace(str[index]))
                        return false;
                }
            }

            return true;
        }

        public static bool IsNullOrEmpty<T>(this IList<T> list)
        {
            return list == null || list.Count == 0;
        }

        public static string Capitalize(this string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            char[] characters = value.ToCharArray();
            characters[0] = char.ToUpper(characters[0]);
            return new string(characters);
        }
    }
}
