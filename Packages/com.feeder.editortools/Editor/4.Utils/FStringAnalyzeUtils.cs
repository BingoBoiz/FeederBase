using System;
using System.Collections.Generic;
using System.Text;

namespace Feeder
{
    public static class FStringAnalyzeUtils
    {
        public static string BuildPatternFromNames(IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0)
                throw new InvalidOperationException("names is empty.");

            var splitNames = new List<string[]>(names.Count);
            for (int i = 0; i < names.Count; i++)
            {
                var name = names[i];
                if (string.IsNullOrEmpty(name))
                    throw new InvalidOperationException($"names[{i}] is empty.");

                var tokens = TokenizeName(name);
                if (tokens.Count == 0)
                    throw new InvalidOperationException("invalid name tokens.");

                splitNames.Add(tokens.ToArray());
            }

            var matches = FindCommonSubsequence(splitNames);
            var patternParts = new List<string>();
            var previousPositions = new int[splitNames.Count];
            for (int i = 0; i < previousPositions.Length; i++)
            {
                previousPositions[i] = -1;
            }

            for (int i = 0; i < matches.Count; i++)
            {
                AppendSegment(patternParts, splitNames, previousPositions, matches[i].Positions);
                patternParts.Add(matches[i].Token);
                for (int j = 0; j < previousPositions.Length; j++)
                {
                    previousPositions[j] = matches[i].Positions[j];
                }
            }

            AppendSegment(patternParts, splitNames, previousPositions, null);
            MergeConsecutiveVariants(patternParts);

            return string.Join("_", patternParts);
        }

        private static void MergeConsecutiveVariants(List<string> parts)
        {
            for (int i = parts.Count - 1; i > 0; i--)
            {
                if (parts[i] == "{variant}" && parts[i - 1] == "{variant}")
                {
                    parts.RemoveAt(i);
                }
            }
        }

        private static bool IsNumericToken(string token)
        {
            return int.TryParse(token, out _);
        }

        private static List<CommonMatch> FindCommonSubsequence(List<string[]> splitNames)
        {
            var matches = new List<CommonMatch>();
            var baseTokens = splitNames[0];
            var searchStarts = new int[splitNames.Count];

            for (int baseIndex = 0; baseIndex < baseTokens.Length; baseIndex++)
            {
                var token = baseTokens[baseIndex];
                var positions = new int[splitNames.Count];
                positions[0] = baseIndex;

                bool foundInAll = true;
                for (int i = 1; i < splitNames.Count; i++)
                {
                    int foundIndex = IndexOfToken(splitNames[i], token, searchStarts[i]);
                    if (foundIndex < 0)
                    {
                        foundInAll = false;
                        break;
                    }

                    positions[i] = foundIndex;
                }

                if (!foundInAll)
                    continue;

                matches.Add(new CommonMatch(token, positions));
                for (int i = 0; i < searchStarts.Length; i++)
                {
                    searchStarts[i] = positions[i] + 1;
                }
            }

            return matches;
        }

        private static void AppendSegment(List<string> patternParts, List<string[]> splitNames, int[] previousPositions, int[] nextPositions)
        {
            int count = splitNames.Count;
            var segmentTokens = new List<string[]>(count);
            var segmentLengths = new int[count];

            for (int i = 0; i < count; i++)
            {
                int startIndex = previousPositions[i] + 1;
                int endIndex = nextPositions == null ? splitNames[i].Length - 1 : nextPositions[i] - 1;
                int length = endIndex >= startIndex ? endIndex - startIndex + 1 : 0;
                segmentLengths[i] = length;

                if (length == 0)
                {
                    segmentTokens.Add(new string[0]);
                    continue;
                }

                var segment = new string[length];
                for (int j = 0; j < length; j++)
                {
                    segment[j] = splitNames[i][startIndex + j];
                }

                segmentTokens.Add(segment);
            }

            bool allEmpty = true;
            for (int i = 0; i < segmentLengths.Length; i++)
            {
                if (segmentLengths[i] > 0)
                {
                    allEmpty = false;
                    break;
                }
            }

            if (allEmpty)
                return;

            int expectedLength = segmentLengths[0];
            bool sameLength = true;
            for (int i = 1; i < segmentLengths.Length; i++)
            {
                if (segmentLengths[i] != expectedLength)
                {
                    sameLength = false;
                    break;
                }
            }

            if (!sameLength)
            {
                bool allLeadingNumbers = true;
                for (int i = 0; i < segmentTokens.Count; i++)
                {
                    if (segmentTokens[i].Length == 0 || !IsNumericToken(segmentTokens[i][0]))
                    {
                        allLeadingNumbers = false;
                        break;
                    }
                }

                if (!allLeadingNumbers)
                {
                    patternParts.Add("{variant}");
                    return;
                }

                patternParts.Add("{number}");
                var trimmed = new List<string[]>(segmentTokens.Count);
                var trimmedLengths = new int[segmentTokens.Count];

                for (int i = 0; i < segmentTokens.Count; i++)
                {
                    if (segmentTokens[i].Length <= 1)
                    {
                        trimmed.Add(new string[0]);
                        trimmedLengths[i] = 0;
                        continue;
                    }

                    int len = segmentTokens[i].Length - 1;
                    var tokens = new string[len];
                    for (int j = 0; j < len; j++)
                    {
                        tokens[j] = segmentTokens[i][j + 1];
                    }

                    trimmed.Add(tokens);
                    trimmedLengths[i] = len;
                }

                bool anyRemain = false;
                for (int i = 0; i < trimmedLengths.Length; i++)
                {
                    if (trimmedLengths[i] > 0)
                    {
                        anyRemain = true;
                        break;
                    }
                }

                if (!anyRemain)
                    return;

                int expected = trimmedLengths[0];
                bool sameRemainLength = true;
                for (int i = 1; i < trimmedLengths.Length; i++)
                {
                    if (trimmedLengths[i] != expected)
                    {
                        sameRemainLength = false;
                        break;
                    }
                }

                if (!sameRemainLength)
                {
                    patternParts.Add("{variant}");
                    return;
                }

                for (int index = 0; index < expected; index++)
                {
                    var token = trimmed[0][index];
                    bool allSame = true;
                    bool allNumeric = IsNumericToken(token);

                    for (int i = 1; i < trimmed.Count; i++)
                    {
                        var current = trimmed[i][index];
                        if (current != token)
                            allSame = false;
                        if (!IsNumericToken(current))
                            allNumeric = false;
                    }

                    if (allSame)
                    {
                        patternParts.Add(token);
                    }
                    else if (allNumeric)
                    {
                        patternParts.Add("{number}");
                    }
                    else
                    {
                        patternParts.Add("{variant}");
                    }
                }

                return;
            }

            for (int index = 0; index < expectedLength; index++)
            {
                var token = segmentTokens[0][index];
                bool allSame = true;
                bool allNumeric = IsNumericToken(token);

                for (int i = 1; i < segmentTokens.Count; i++)
                {
                    var current = segmentTokens[i][index];
                    if (current != token)
                        allSame = false;
                    if (!IsNumericToken(current))
                        allNumeric = false;
                }

                if (allSame)
                {
                    patternParts.Add(token);
                }
                else if (allNumeric)
                {
                    patternParts.Add("{number}");
                }
                else
                {
                    patternParts.Add("{variant}");
                }
            }
        }

        private static int IndexOfToken(string[] tokens, string token, int startIndex)
        {
            for (int i = startIndex; i < tokens.Length; i++)
            {
                if (tokens[i] == token)
                    return i;
            }

            return -1;
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

        private readonly struct CommonMatch
        {
            public readonly string Token;
            public readonly int[] Positions;

            public CommonMatch(string token, int[] positions)
            {
                Token = token;
                Positions = positions;
            }
        }
    }
}
