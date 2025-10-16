using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UnleashTheMods.Merger
{
    public static class AlternateMerger
    {
        private static readonly HashSet<string> JumpParametersStyleFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "jump_parameters.scr", "jump_parameters_new.scr",
        };

        private static readonly HashSet<string> HealthDefinitionsStyleFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
           // "healthdefinitions.scr", "healingdefinitions.scr", Main parse method perfectly parses these mfs no need for extra parser
        };

        private static readonly HashSet<string> LineBasedScriptFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "inputs_keyboard.scr",
            "logic_script.scr", "logic_script_game.scr", "logic_script_game_overlay.scr",
            "frame_script.scr", "frame_script_game.scr", "render_script.scr",
        };

        private static readonly HashSet<string> PlayerVariablesStyleFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "player_variables.scr"
        };

        public static bool ShouldUseAlternateMerge(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            return JumpParametersStyleFiles.Contains(fileName) ||
                   HealthDefinitionsStyleFiles.Contains(fileName) ||
                   LineBasedScriptFiles.Contains(fileName) ||
                   PlayerVariablesStyleFiles.Contains(fileName);
        }

        public static (string MergedContent, string? UpdatedPreferredSource) Merge(ModFile original, List<ModFile> mods, MergeReporter reporter, IScriptMerger standardMerger)
        {
            string fileName = Path.GetFileName(original.FullPathInPak);

            if (JumpParametersStyleFiles.Contains(fileName))
            {
                return JumpParametersMerger.Merge(original, mods, reporter);
            }
            if (HealthDefinitionsStyleFiles.Contains(fileName))
            {
                return HealthDefinitionsMerger.Merge(original, mods, reporter);
            }
            if (PlayerVariablesStyleFiles.Contains(fileName))
            {
                return MergePlayerVariablesFile(original, mods, reporter);
            }
            if (LineBasedScriptFiles.Contains(fileName))
            {
                return MergeLineByLineFile(original, mods, reporter);
            }

            return standardMerger.Merge(original, mods, null, reporter);
        }

        private static string GetCodePartOnly(string line)
        {
            string trimmedLine = line.Trim();
            int commentIndex = trimmedLine.IndexOf("//");
            if (commentIndex == 0) return string.Empty;
            if (commentIndex > -1) return trimmedLine.Substring(0, commentIndex).TrimEnd();
            return trimmedLine;
        }

        private static (string MergedContent, string? UpdatedPreferredSource) MergePlayerVariablesFile(ModFile original, List<ModFile> mods, MergeReporter reporter)
        {
            reporter.StartNewFile(original.FullPathInPak, mods.Select(m => m.SourcePak).ToList());
            var encoding = new UTF8Encoding(false);
            var originalLines = encoding.GetString(original.Content).Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
            var finalParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lineOrder = new List<string>();
            string closingBraceLine = "}";
            int closingBraceIndex = -1;
            for (int i = 0; i < originalLines.Count; i++) { if (originalLines[i].Trim() == "}") { closingBraceIndex = i; closingBraceLine = originalLines[i]; break; } }
            var linesToParse = closingBraceIndex != -1 ? originalLines.Take(closingBraceIndex).ToList() : originalLines;
            foreach (var line in linesToParse)
            {
                var match = Regex.Match(line.Trim(), @"^Param\s*\(\s*""([^""]+)""");
                if (match.Success)
                {
                    string key = match.Groups[1].Value;
                    if (!finalParams.ContainsKey(key)) { finalParams[key] = line; lineOrder.Add(key); }
                }
                else { string uniqueKey = $"NON_PARAM_{lineOrder.Count}"; finalParams[uniqueKey] = line; lineOrder.Add(uniqueKey); }
            }
            var allModChanges = new Dictionary<string, List<(string SourceMod, string Line)>>();
            foreach (var mod in mods)
            {
                var modLines = encoding.GetString(mod.Content).Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                var seenKeysInMod = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in modLines)
                {
                    var match = Regex.Match(line.Trim(), @"^Param\s*\(\s*""([^""]+)""");
                    if (match.Success)
                    {
                        string key = match.Groups[1].Value;
                        if (!allModChanges.ContainsKey(key)) allModChanges[key] = new List<(string, string)>();
                        if (seenKeysInMod.Contains(key)) { allModChanges[key].RemoveAll(m => m.SourceMod == mod.SourcePak); }
                        allModChanges[key].Add((mod.SourcePak, line));
                        seenKeysInMod.Add(key);
                    }
                }
            }
            foreach (var change in allModChanges)
            {
                string key = change.Key;
                var versions = change.Value;

                finalParams.TryGetValue(key, out var originalLine);
                string originalCode = originalLine != null ? GetCodePartOnly(originalLine) : string.Empty;

                var actualChanges = versions.Where(v => originalLine == null || GetCodePartOnly(v.Line) != originalCode).ToList();

                if (!actualChanges.Any()) continue;

                var distinctChanges = actualChanges.GroupBy(v => GetCodePartOnly(v.Line)).Select(g => g.First()).ToList();

                (string SourceMod, string Line) chosenVersion;
                if (distinctChanges.Count == 1)
                {
                    chosenVersion = distinctChanges.First();
                }
                else
                {
                    var conflictOptions = distinctChanges.Select(dv => (dv.SourceMod, dv.Line)).ToList();
                    string chosenLineRaw = HandleLineConflict(key, conflictOptions, original.FullPathInPak);
                    chosenVersion = versions.First(v => v.Line == chosenLineRaw);
                }

                if (originalLine != null)
                {
                    if (GetCodePartOnly(originalLine) != GetCodePartOnly(chosenVersion.Line))
                    {
                        finalParams[key] = AppendUtmComment(chosenVersion.Line, $"[UTM Merge] updated from {chosenVersion.SourceMod} (OG Value: {originalLine.Trim()})");
                        reporter.LogChange(key, originalLine.Trim(), chosenVersion.Line.Trim(), chosenVersion.SourceMod);
                    }
                }
                else
                {
                    finalParams[key] = AppendUtmComment(chosenVersion.Line, $"[UTM Merge] added from {chosenVersion.SourceMod}");
                    lineOrder.Add(key);
                    reporter.LogAddition(key, chosenVersion.SourceMod);
                }
            }
            var finalContent = new StringBuilder();
            foreach (var key in lineOrder) { if (finalParams.ContainsKey(key)) { finalContent.AppendLine(finalParams[key]); } }
            if (closingBraceIndex != -1) { finalContent.AppendLine(closingBraceLine); }
            return (finalContent.ToString(), null);
        }

        private static (string MergedContent, string? UpdatedPreferredSource) MergeLineByLineFile(ModFile original, List<ModFile> mods, MergeReporter reporter)
        {
            reporter.StartNewFile(original.FullPathInPak, mods.Select(m => m.SourcePak).ToList());
            var encoding = new UTF8Encoding(false);
            var originalLines = encoding.GetString(original.Content).Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
            var (originalMap, _) = MapLinesWithInstanceCounting(originalLines);
            var allModChanges = new Dictionary<string, List<(string SourceMod, string Line)>>();
            var allModMaps = new List<Dictionary<string, string>>();
            foreach (var mod in mods)
            {
                var modLines = encoding.GetString(mod.Content).Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
                var (modMap, _) = MapLinesWithInstanceCounting(modLines);
                allModMaps.Add(modMap);
                foreach (var entry in modMap)
                {
                    if (!allModChanges.ContainsKey(entry.Key)) { allModChanges[entry.Key] = new List<(string SourceMod, string Line)>(); }
                    allModChanges[entry.Key].Add((mod.SourcePak, entry.Value));
                }
            }
            var finalLines = new List<string>(originalLines);
            foreach (var change in allModChanges)
            {
                string uniqueSignature = change.Key;
                var modsTouchingThisLine = change.Value;

                if (originalMap.TryGetValue(uniqueSignature, out var originalLine))
                {
                    var actualChanges = modsTouchingThisLine.Where(m => GetCodePartOnly(m.Line) != GetCodePartOnly(originalLine)).ToList();

                    if (!actualChanges.Any()) continue;

                    var distinctModLines = actualChanges.GroupBy(m => GetCodePartOnly(m.Line)).Select(g => g.First()).ToList();

                    (string SourceMod, string Line) chosenVersion;
                    if (distinctModLines.Count == 1)
                    {
                        chosenVersion = distinctModLines.First();
                    }
                    else
                    {
                        string chosenLineRaw = HandleLineConflict(uniqueSignature, distinctModLines.Select(dm => (dm.SourceMod, dm.Line)).ToList(), original.FullPathInPak);
                        chosenVersion = distinctModLines.First(c => c.Line == chosenLineRaw);
                    }
                    if (GetCodePartOnly(chosenVersion.Line) != GetCodePartOnly(originalLine))
                    {
                        int index = finalLines.IndexOf(originalLine);
                        if (index != -1)
                        {
                            finalLines[index] = AppendUtmComment(chosenVersion.Line, $"[UTM Merge] updated from {chosenVersion.SourceMod} (OG Value: {originalLine.Trim()})");
                            reporter.LogChange(uniqueSignature, originalLine.Trim(), chosenVersion.Line.Trim(), chosenVersion.SourceMod);
                        }
                    }
                }
                else
                {
                    var distinctModLines = modsTouchingThisLine.GroupBy(m => GetCodePartOnly(m.Line)).Select(g => g.First()).ToList();
                    (string SourceMod, string Line) chosenVersion;
                    if (distinctModLines.Count == 1)
                    {
                        chosenVersion = distinctModLines.First();
                    }
                    else
                    {
                        string chosenLineRaw = HandleLineConflict(uniqueSignature, distinctModLines.Select(dm => (dm.SourceMod, dm.Line)).ToList(), original.FullPathInPak);
                        chosenVersion = distinctModLines.First(c => c.Line == chosenLineRaw);
                    }

                    var sourceModFile = mods.First(m => m.SourcePak == chosenVersion.SourceMod);
                    var sourceModLines = encoding.GetString(sourceModFile.Content).Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                    int modLineIndex = Array.FindIndex(sourceModLines, l => l == chosenVersion.Line);

                    int insertPosition = -1;
                    for (int i = modLineIndex - 1; i >= 0; i--)
                    {
                        var precedingModLine = sourceModLines[i];
                        var precedingSig = GetBaseSignature(precedingModLine);
                        if (precedingSig != null && originalMap.ContainsKey(precedingSig))
                        {
                            var currentIndexOfPreceding = finalLines.FindIndex(l => GetBaseSignature(l) == precedingSig);
                            if (currentIndexOfPreceding != -1)
                            {
                                insertPosition = currentIndexOfPreceding;
                                break;
                            }
                        }
                    }

                    string lineToInsert = AppendUtmComment(chosenVersion.Line, $"[UTM Merge] added from {chosenVersion.SourceMod}");

                    if (insertPosition != -1)
                    {
                        finalLines.Insert(insertPosition + 1, lineToInsert);
                    }
                    else
                    {
                        finalLines.Add(lineToInsert);
                    }
                    reporter.LogAddition(uniqueSignature, chosenVersion.SourceMod);
                }
            }
            var linesToRemove = new List<string>();
            foreach (var originalEntry in originalMap)
            {
                string signature = originalEntry.Key;
                if (allModChanges.ContainsKey(signature)) { continue; }
                bool isOmittedByAll = allModMaps.All(modMap => !modMap.ContainsKey(signature));
                if (isOmittedByAll && mods.Count > 0) { linesToRemove.Add(originalEntry.Value); reporter.LogDeletion(signature, string.Join(", ", mods.Select(m => m.SourcePak).Distinct())); }
            }
            if (linesToRemove.Any()) { finalLines.RemoveAll(l => linesToRemove.Contains(l)); }
            return (string.Join(Environment.NewLine, finalLines), null);
        }

        private static (Dictionary<string, string> MappedLines, List<string> OrderedSignatures) MapLinesWithInstanceCounting(List<string> lines)
        {
            var mappedLines = new Dictionary<string, string>();
            var orderedSignatures = new List<string>();
            var baseSignatureCounts = new Dictionary<string, int>();

            foreach (var line in lines)
            {
                var baseSignature = GetBaseSignature(line);
                if (baseSignature != null)
                {
                    baseSignatureCounts.TryGetValue(baseSignature, out int count);
                    string uniqueSignature = count > 0 ? $"{baseSignature}_{count + 1}" : baseSignature;
                    baseSignatureCounts[baseSignature] = count + 1;

                    if (!mappedLines.ContainsKey(uniqueSignature))
                    {
                        mappedLines[uniqueSignature] = line;
                        orderedSignatures.Add(uniqueSignature);
                    }
                }
            }
            return (mappedLines, orderedSignatures);
        }

        private static string? GetBaseSignature(string line)
        {
            string trimmedLine = line.Trim();
            string potentialCode = trimmedLine;

            if (trimmedLine.StartsWith("//"))
            {
                potentialCode = trimmedLine.Substring(2).TrimStart();
            }

            if (string.IsNullOrWhiteSpace(potentialCode) || trimmedLine.StartsWith("/*"))
            {
                return null;
            }

            var match = Regex.Match(potentialCode, @"^(\w+)\s*\(\s*""([^""]+)""");
            if (match.Success)
            {
                return $"{match.Groups[1].Value}_{match.Groups[2].Value}";
            }

            string codePart = potentialCode.Split(new[] { "//" }, StringSplitOptions.None)[0].TrimEnd();

            int assignmentIndex = codePart.IndexOf('=');
            if (assignmentIndex > -1)
            {
                codePart = codePart.Substring(0, assignmentIndex).Trim();
            }

            string normalized = Regex.Replace(codePart, @"\s+", " ");
            if (string.IsNullOrWhiteSpace(normalized)) return null;

            return normalized;
        }

        private static string HandleLineConflict(string signature, List<(string SourceMod, string Line)> conflictingChanges, string filePath)
        {
            var allSourcesInConflict = conflictingChanges.Select(c => c.SourceMod).Distinct().ToList();
            var autoDecision = MergeSessionState.GetDecision(filePath, allSourcesInConflict);

            (string SourceMod, string Line) chosenChange;

            if (autoDecision != null)
            {
                chosenChange = conflictingChanges.FirstOrDefault(c => c.SourceMod == autoDecision);
                if (chosenChange.Line != null) return chosenChange.Line;
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[CHOICE REQUIRED] In file '{filePath}', conflict for: '{signature}'");
            Console.ResetColor();
            for (int i = 0; i < conflictingChanges.Count; i++)
            {
                Console.Write($"    {i + 1}. From mod '");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write(conflictingChanges[i].SourceMod);
                Console.ResetColor();
                Console.WriteLine("':");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"      {conflictingChanges[i].Line.Trim()}");
                Console.ResetColor();
            }

            int choice = -1;
            bool yesToAll = false;
            while (choice < 1 || choice > conflictingChanges.Count)
            {
                Console.Write($"Please select which version to use (1-{conflictingChanges.Count}) or e.g. '1y' for 'Yes to all': ");
                string? input = Console.ReadLine()?.Trim().ToLower();
                if (input != null && input.EndsWith("y"))
                {
                    yesToAll = true;
                    input = input.TrimEnd('y');
                }
                int.TryParse(input, out choice);
            }
            chosenChange = conflictingChanges[choice - 1];

            if (yesToAll)
            {
                MergeSessionState.SetDecision(filePath, allSourcesInConflict, chosenChange.SourceMod);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  -> Choice '{chosenChange.SourceMod}' applied for this and all subsequent conflicts in this file.");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  -> Choice applied.");
                Console.ResetColor();
            }

            return chosenChange.Line;
        }

        private static List<ModFile> RemoveDuplicateParams(List<ModFile> mods)
        {
            var deduplicatedMods = new List<ModFile>();
            var encoding = new UTF8Encoding(false);

            foreach (var mod in mods)
            {
                var lines = encoding.GetString(mod.Content).Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                var processedSignatures = new HashSet<string>();
                var cleanLines = new List<string>();

                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("Param("))
                    {
                        var signature = ScrMerger.GetIdentifierSignature(line);
                        if (!processedSignatures.Contains(signature))
                        {
                            cleanLines.Add(line);
                            processedSignatures.Add(signature);
                        }
                    }
                    else
                    {
                        cleanLines.Add(line);
                    }
                }

                var newContentBytes = encoding.GetBytes(string.Join(Environment.NewLine, cleanLines));
                deduplicatedMods.Add(new ModFile
                {
                    Content = newContentBytes,
                    FullPathInPak = mod.FullPathInPak,
                    SourcePak = mod.SourcePak
                });
            }
            return deduplicatedMods;
        }
        private static string AppendUtmComment(string line, string comment)
        {
            string trimmedLine = line.TrimEnd();
            if (trimmedLine.Contains("//"))
            {
                return $"{trimmedLine} -- {comment}";
            }
            else
            {
                return $"{trimmedLine}\t// {comment}";
            }
        }
    }
}