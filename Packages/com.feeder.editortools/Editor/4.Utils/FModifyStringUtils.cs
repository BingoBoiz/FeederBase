using System;
using System.Collections.Generic;
using System.Text;

namespace Feeder
{
    public static class FModifyStringUtils
    {
        public static string ApplyPattern(string originalName, string inputPattern, string outputPattern)
        {
            return ApplyPattern(originalName, inputPattern, outputPattern, 0);
        }

        public static string ApplyPattern(string originalName, string inputPattern, string outputPattern, int sequenceIndex)
        {
            if (string.IsNullOrEmpty(originalName))
                throw new InvalidOperationException("originalName is empty.");
            if (string.IsNullOrEmpty(inputPattern))
                throw new InvalidOperationException("inputPattern is empty.");
            if (string.IsNullOrEmpty(outputPattern))
                throw new InvalidOperationException("outputPattern is empty.");

            var nameTokens = TokenizeName(originalName);
            if (nameTokens.Count == 0)
                throw new InvalidOperationException("name tokens is empty.");

            var patternTokens = ParsePatternTokens(inputPattern);
            if (patternTokens.Count == 0)
                throw new InvalidOperationException("inputPattern tokens is empty.");

            var captures = new CaptureState();
            if (!TryMatchTokens(nameTokens, patternTokens, 0, 0, captures))
                throw new InvalidOperationException("inputPattern does not match name.");

            var resolvedOutputPattern = FSequenceNumberUtils.ReplaceSequencePlaceholders(outputPattern, sequenceIndex);
            return BuildOutputNameFromPattern(resolvedOutputPattern, captures);
        }

        private static string BuildOutputNameFromPattern(string outputPattern, CaptureState captures)
        {
            if (string.IsNullOrEmpty(outputPattern))
                throw new InvalidOperationException("outputPattern is empty.");

            var builder = new StringBuilder(outputPattern.Length);
            int index = 0;

            while (index < outputPattern.Length)
            {
                int openIndex = outputPattern.IndexOf('{', index);
                if (openIndex < 0)
                {
                    builder.Append(outputPattern, index, outputPattern.Length - index);
                    break;
                }

                if (openIndex > index)
                    builder.Append(outputPattern, index, openIndex - index);

                int closeIndex = outputPattern.IndexOf('}', openIndex + 1);
                if (closeIndex < 0)
                    throw new InvalidOperationException("outputPattern has invalid placeholder.");

                var key = outputPattern.Substring(openIndex + 1, closeIndex - openIndex - 1);
                if (key != PatternToken.Number && key != PatternToken.Variant)
                    throw new InvalidOperationException($"unsupported placeholder {key}.");

                var value = captures.Consume(key);
                if (value == null)
                    throw new InvalidOperationException($"missing value for {key}.");
                if (value.Length == 0 && key != PatternToken.Variant)
                    throw new InvalidOperationException($"missing value for {key}.");

                builder.Append(value);
                index = closeIndex + 1;
            }

            return builder.ToString();
        }

        private static bool TryMatchTokens(IReadOnlyList<string> nameTokens, IReadOnlyList<PatternToken> patternTokens, int nameIndex, int patternIndex, CaptureState captures)
        {
            if (patternIndex == patternTokens.Count)
                return nameIndex == nameTokens.Count;

            if (nameIndex > nameTokens.Count)
                return false;

            var token = patternTokens[patternIndex];
            if (!token.IsPlaceholder)
            {
                if (nameIndex >= nameTokens.Count)
                    return false;

                if (nameTokens[nameIndex] != token.Value)
                    return false;

                return TryMatchTokens(nameTokens, patternTokens, nameIndex + 1, patternIndex + 1, captures);
            }

            if (token.Value == PatternToken.Number)
            {
                if (nameIndex >= nameTokens.Count)
                    return false;
                if (!IsNumericToken(nameTokens[nameIndex]))
                    return false;

                captures.Add(token.Value, nameTokens[nameIndex]);
                return TryMatchTokens(nameTokens, patternTokens, nameIndex + 1, patternIndex + 1, captures);
            }

            int minRemaining = GetMinimumRemainingTokens(patternTokens, patternIndex + 1);
            int maxLen = nameTokens.Count - nameIndex - minRemaining;
            if (maxLen < 0)
                return false;

            for (int len = 0; len <= maxLen; len++)
            {
                var snapshot = captures.Snapshot();
                string value = len == 0 ? "" : string.Join("_", SliceTokens(nameTokens, nameIndex, len));
                captures.Add(token.Value, value);

                if (TryMatchTokens(nameTokens, patternTokens, nameIndex + len, patternIndex + 1, captures))
                    return true;

                captures.Restore(snapshot);
            }

            return false;
        }

        private static int GetMinimumRemainingTokens(IReadOnlyList<PatternToken> patternTokens, int startIndex)
        {
            int min = 0;
            for (int i = startIndex; i < patternTokens.Count; i++)
            {
                PatternToken remainingToken = patternTokens[i];
                if (!remainingToken.IsPlaceholder || remainingToken.Value == PatternToken.Number)
                    min += 1;
            }

            return min;
        }

        private static IReadOnlyList<string> SliceTokens(IReadOnlyList<string> tokens, int startIndex, int length)
        {
            var result = new string[length];
            for (int i = 0; i < length; i++)
            {
                result[i] = tokens[startIndex + i];
            }

            return result;
        }

        private static bool IsNumericToken(string token)
        {
            return int.TryParse(token, out _);
        }

        private static List<PatternToken> ParsePatternTokens(string pattern)
        {
            var tokens = new List<PatternToken>();
            int index = 0;

            while (index < pattern.Length)
            {
                int openIndex = pattern.IndexOf('{', index);
                if (openIndex < 0)
                {
                    AddLiteralTokens(tokens, pattern.Substring(index));
                    break;
                }

                if (openIndex > index)
                    AddLiteralTokens(tokens, pattern.Substring(index, openIndex - index));

                int closeIndex = pattern.IndexOf('}', openIndex + 1);
                if (closeIndex < 0)
                    throw new InvalidOperationException("pattern has invalid placeholder.");

                var key = pattern.Substring(openIndex + 1, closeIndex - openIndex - 1);
                if (key != PatternToken.Number && key != PatternToken.Variant)
                    throw new InvalidOperationException($"unsupported placeholder {key}.");

                tokens.Add(PatternToken.Placeholder(key));
                index = closeIndex + 1;
            }

            return tokens;
        }

        private static void AddLiteralTokens(List<PatternToken> tokens, string literal)
        {
            var nameTokens = TokenizeName(literal);
            for (int i = 0; i < nameTokens.Count; i++)
            {
                tokens.Add(PatternToken.Literal(nameTokens[i]));
            }
        }

        private static List<string> TokenizeName(string name)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(name))
                return tokens;

            var builder = new StringBuilder();
            int previousType = 0;

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                int currentType = GetCharType(c);

                if (currentType == 0)
                {
                    FlushToken(builder, tokens);
                    previousType = 0;
                    continue;
                }

                if (previousType != 0 && currentType != previousType)
                {
                    FlushToken(builder, tokens);
                }

                builder.Append(c);
                previousType = currentType;
            }

            FlushToken(builder, tokens);
            return tokens;
        }

        private static int GetCharType(char c)
        {
            if (char.IsDigit(c))
                return 1;
            if (char.IsLetter(c))
                return 2;
            return 0;
        }

        private static void FlushToken(StringBuilder builder, List<string> tokens)
        {
            if (builder.Length == 0)
                return;

            tokens.Add(builder.ToString());
            builder.Length = 0;
        }

        private sealed class CaptureState
        {
            private readonly Dictionary<string, List<string>> values = new Dictionary<string, List<string>>();
            private readonly Dictionary<string, int> indices = new Dictionary<string, int>();

            public void Add(string key, string value)
            {
                if (!values.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    values.Add(key, list);
                }

                list.Add(value);
            }

            public string Consume(string key)
            {
                if (!values.TryGetValue(key, out var list) || list.Count == 0)
                    return null;

                if (!indices.TryGetValue(key, out var index))
                    index = 0;

                if (index >= list.Count)
                    return null;

                indices[key] = index + 1;
                return list[index];
            }

            public SnapshotState Snapshot()
            {
                return new SnapshotState(values, indices);
            }

            public void Restore(SnapshotState snapshot)
            {
                values.Clear();
                indices.Clear();

                foreach (var pair in snapshot.Values)
                {
                    values.Add(pair.Key, new List<string>(pair.Value));
                }

                foreach (var pair in snapshot.Indices)
                {
                    indices.Add(pair.Key, pair.Value);
                }
            }
        }

        private readonly struct SnapshotState
        {
            public readonly Dictionary<string, List<string>> Values;
            public readonly Dictionary<string, int> Indices;

            public SnapshotState(Dictionary<string, List<string>> values, Dictionary<string, int> indices)
            {
                Values = new Dictionary<string, List<string>>(values.Count);
                foreach (var pair in values)
                {
                    Values.Add(pair.Key, new List<string>(pair.Value));
                }

                Indices = new Dictionary<string, int>(indices);
            }
        }

        private readonly struct PatternToken
        {
            public const string Number = "number";
            public const string Variant = "variant";

            public readonly bool IsPlaceholder;
            public readonly string Value;

            private PatternToken(bool isPlaceholder, string value)
            {
                IsPlaceholder = isPlaceholder;
                Value = value;
            }

            public static PatternToken Placeholder(string value)
            {
                return new PatternToken(true, value);
            }

            public static PatternToken Literal(string value)
            {
                return new PatternToken(false, value);
            }
        }
    }
}
