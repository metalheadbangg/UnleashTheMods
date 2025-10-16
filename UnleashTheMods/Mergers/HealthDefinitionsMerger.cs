using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UnleashTheMods.Merger
{
    public static class HealthDefinitionsMerger
    {
        private class HealthDefinition
        {
            public string BlockType { get; set; }
            public string Name { get; set; }
            public string Value { get; set; }
            public string OriginalContent { get; set; }
            public string SourceMod { get; set; }
            public string? UtmComment { get; set; }
        }

        public static (string MergedContent, string? UpdatedPreferredSource) Merge(ModFile original, List<ModFile> mods, MergeReporter reporter)
        {
            reporter.StartNewFile(original.FullPathInPak, mods.Select(m => m.SourcePak).ToList());
            var encoding = new UTF8Encoding(false);

            var originalContent = encoding.GetString(original.Content);
            var originalDefinitions = Parse(originalContent, "Original");

            var definitionOrder = originalDefinitions.Select(d => d.Name).ToList();
            var mergedDefinitions = originalDefinitions.ToDictionary(d => d.Name, d => d);
            var allModChanges = new Dictionary<string, List<HealthDefinition>>();
            foreach (var mod in mods)
            {
                var modContent = encoding.GetString(mod.Content);
                var modDefinitions = Parse(modContent, mod.SourcePak);
                foreach (var modDef in modDefinitions)
                {
                    if (!allModChanges.ContainsKey(modDef.Name))
                    {
                        allModChanges[modDef.Name] = new List<HealthDefinition>();
                    }
                    allModChanges[modDef.Name].Add(modDef);
                }
            }
            foreach (var change in allModChanges)
            {
                string defName = change.Key;
                var versions = change.Value;

                mergedDefinitions.TryGetValue(defName, out var originalDef);

                var actualChanges = versions.Where(v => originalDef == null || v.Value != originalDef.Value).ToList();

                if (!actualChanges.Any()) continue;

                var distinctChanges = actualChanges.GroupBy(v => v.Value).Select(g => g.First()).ToList();

                HealthDefinition chosenVersion;
                if (distinctChanges.Count == 1)
                {
                    chosenVersion = distinctChanges.First();
                }
                else
                {
                    chosenVersion = HandleHealthConflict(defName, distinctChanges, original.FullPathInPak);
                }

                if (originalDef != null)
                {
                    if (originalDef.Value != chosenVersion.Value)
                    {
                        chosenVersion.UtmComment = $"// [UTM Merge] updated from {chosenVersion.SourceMod} (OG Value: {originalDef.Value})";
                        mergedDefinitions[defName] = chosenVersion;
                        reporter.LogChange(defName, originalDef.Value, chosenVersion.Value, chosenVersion.SourceMod);
                    }
                }
                else
                {
                    chosenVersion.UtmComment = $"// [UTM Merge] added from {chosenVersion.SourceMod}";
                    mergedDefinitions.Add(defName, chosenVersion);
                    definitionOrder.Add(defName);
                    reporter.LogAddition(defName, chosenVersion.SourceMod);
                }
            }

            var finalContent = Rebuild(mergedDefinitions, definitionOrder);
            return (finalContent, null);
        }
        private static HealthDefinition HandleHealthConflict(string definitionName, List<HealthDefinition> conflictingChanges, string filePath)
        {
            var allSourcesInConflict = conflictingChanges.Select(c => c.SourceMod).Distinct().ToList();
            var autoDecision = MergeSessionState.GetDecision(filePath, allSourcesInConflict);

            if (autoDecision != null)
            {
                var chosen = conflictingChanges.FirstOrDefault(c => c.SourceMod == autoDecision);
                if (chosen != null) return chosen;
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[CHOICE REQUIRED] Conflict for health definition: '{definitionName}'");
            Console.ResetColor();
            Console.WriteLine("  The following mods provide different values:");

            for (int i = 0; i < conflictingChanges.Count; i++)
            {
                var change = conflictingChanges[i];
                Console.Write($"    {i + 1}. From mod '");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write(change.SourceMod);
                Console.ResetColor();
                Console.Write("': ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(change.Value);
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

            var chosenChange = conflictingChanges[choice - 1];

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

            return chosenChange;
        }

        private static List<HealthDefinition> Parse(string content, string source)
        {
            var definitions = new List<HealthDefinition>();
            var regex = new Regex(@"(Health|HealthMul|HealthTotalMul)\s*\(""([^""]+)""\)\s*\{([^}]+)\}", RegexOptions.Multiline);

            foreach (Match match in regex.Matches(content))
            {
                var innerContent = match.Groups[3].Value.Trim();
                var innerMatch = Regex.Match(innerContent, @"Health\s*\(""([^""]+)""\)");
                if (innerMatch.Success)
                {
                    definitions.Add(new HealthDefinition
                    {
                        BlockType = match.Groups[1].Value,
                        Name = match.Groups[2].Value,
                        Value = innerMatch.Groups[1].Value,
                        OriginalContent = match.Value,
                        SourceMod = source
                    });
                }
            }
            return definitions;
        }

        private static string Rebuild(Dictionary<string, HealthDefinition> definitions, List<string> order)
        {
            var sb = new StringBuilder();
            sb.AppendLine("sub main()");
            sb.AppendLine("{");

            foreach (var name in order)
            {
                if (definitions.TryGetValue(name, out var def))
                {
                    sb.AppendLine($"\t{def.BlockType}(\"{def.Name}\")");
                    sb.AppendLine("\t{");

                    string healthLine = $"\t\tHealth(\"{def.Value}\");";
                    if (!string.IsNullOrEmpty(def.UtmComment))
                    {
                        healthLine = $"{healthLine.PadRight(80)}{def.UtmComment}";
                    }
                    sb.AppendLine(healthLine);

                    sb.AppendLine("\t}");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}