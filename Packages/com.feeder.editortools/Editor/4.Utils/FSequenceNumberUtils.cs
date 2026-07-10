using System;
using System.Text;

namespace Feeder
{
    public static class FSequenceNumberUtils
    {
        public static bool IsSequencePlaceholder(string key)
        {
            int separatorIndex = key.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex == key.Length - 1)
                return false;

            string startText = key.Substring(0, separatorIndex);
            string stepText = key.Substring(separatorIndex + 1);
            return int.TryParse(startText, out _) && int.TryParse(stepText, out _);
        }

        public static string ReplaceSequencePlaceholders(string pattern, int sequenceIndex)
        {
            if (string.IsNullOrEmpty(pattern))
                throw new InvalidOperationException("pattern is empty.");

            var builder = new StringBuilder(pattern.Length);
            int index = 0;

            while (index < pattern.Length)
            {
                int openIndex = pattern.IndexOf('{', index);
                if (openIndex < 0)
                {
                    builder.Append(pattern, index, pattern.Length - index);
                    break;
                }

                if (openIndex > index)
                    builder.Append(pattern, index, openIndex - index);

                int closeIndex = pattern.IndexOf('}', openIndex + 1);
                if (closeIndex < 0)
                    throw new InvalidOperationException("pattern has invalid placeholder.");

                var key = pattern.Substring(openIndex + 1, closeIndex - openIndex - 1);
                if (TryBuildSequenceValue(key, sequenceIndex, out var value))
                {
                    builder.Append(value);
                }
                else
                {
                    builder.Append('{').Append(key).Append('}');
                }

                index = closeIndex + 1;
            }

            return builder.ToString();
        }

        private static bool TryBuildSequenceValue(string key, int sequenceIndex, out string value)
        {
            int separatorIndex = key.IndexOf(':');
            if (separatorIndex < 0)
            {
                value = null;
                return false;
            }

            if (separatorIndex == 0 || separatorIndex == key.Length - 1)
                throw new InvalidOperationException($"invalid sequence placeholder {key}.");

            var startText = key.Substring(0, separatorIndex);
            var stepText = key.Substring(separatorIndex + 1);

            if (!int.TryParse(startText, out var start) || !int.TryParse(stepText, out var step))
                throw new InvalidOperationException($"invalid sequence placeholder {key}.");

            var result = start + (step * sequenceIndex);
            value = result.ToString();
            return true;
        }
    }
}
