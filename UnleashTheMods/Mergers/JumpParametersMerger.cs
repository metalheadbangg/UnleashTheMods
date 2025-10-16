using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UnleashTheMods.Merger
{
    public static class JumpParametersMerger
    {
        private class Parameter { public string Content { get; set; } }
        private class AdvancedParkourBlock { public List<Parameter> Parameters { get; set; } = new List<Parameter>(); }
        private class JumpBlock
        {
            public string Name { get; set; }
            public string? InheritsFrom { get; set; }
            public string OriginalSignatureLine { get; set; }
            public List<Parameter> Parameters { get; set; } = new List<Parameter>();
            public AdvancedParkourBlock? AdvancedParkour { get; set; }
            public List<string> HeaderComments { get; set; } = new List<string>();
        }
        private class JumpParametersFile
        {
            public List<string> Header { get; set; } = new List<string>();
            public List<JumpBlock> JumpBlocks { get; set; } = new List<JumpBlock>();
        }
        public static (string MergedContent, string? UpdatedPreferredSource) Merge(ModFile original, List<ModFile> mods, MergeReporter reporter)
        {
            reporter.StartNewFile(original.FullPathInPak, mods.Select(m => m.SourcePak).ToList());
            var encoding = new UTF8Encoding(false);

            var originalFileModel = Parse(encoding.GetString(original.Content));
            var mergedFileModel = Parse(encoding.GetString(original.Content));

            var allBlockChanges = new Dictionary<string, List<(JumpBlock Block, string SourceMod)>>();
            foreach (var mod in mods)
            {
                var modFileModel = Parse(encoding.GetString(mod.Content));
                foreach (var modBlock in modFileModel.JumpBlocks)
                {
                    if (!allBlockChanges.ContainsKey(modBlock.Name))
                    {
                        allBlockChanges[modBlock.Name] = new List<(JumpBlock Block, string SourceMod)>();
                    }
                    allBlockChanges[modBlock.Name].Add((modBlock, mod.SourcePak));
                }
            }

            foreach (var change in allBlockChanges)
            {
                string blockName = change.Key;
                var modVersions = change.Value;

                JumpBlock chosenBlock;
                string chosenSource;

                var allSourcesInConflict = modVersions.Select(v => v.SourceMod).Distinct().ToList();
                var autoDecision = MergeSessionState.GetDecision(original.FullPathInPak, allSourcesInConflict);

                if (autoDecision != null)
                {
                    var preferredVersion = modVersions.FirstOrDefault(v => v.SourceMod == autoDecision);
                    if (preferredVersion.Block != null)
                    {
                        chosenBlock = preferredVersion.Block;
                        chosenSource = preferredVersion.SourceMod;
                        goto ApplyChange;
                    }
                }

                if (modVersions.Count > 1)
                {
                    var originalBlock = originalFileModel.JumpBlocks.FirstOrDefault(b => b.Name == blockName);
                    string displaySignature = originalBlock?.OriginalSignatureLine.Trim() ?? $"New Block '{blockName}'";

                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[CHOICE REQUIRED] In file '{original.FullPathInPak}', conflict for block: '{displaySignature}'");
                    Console.WriteLine($"\nWe can't and we don't want to merge different parameters on this file! It could break the game! So be careful! Yes to all (numY) Recommended!");
                    Console.ResetColor();
                    Console.WriteLine("  The following mods provide different versions for this entire block:");

                    var choices = new List<(JumpBlock Block, string SourceMod)>();
                    if (originalBlock != null) choices.Add((originalBlock, "Original Game File"));
                    choices.AddRange(modVersions.GroupBy(v => v.SourceMod).Select(g => g.First()));

                    for (int i = 0; i < choices.Count; i++)
                    {
                        Console.Write($"    {i + 1}. Use version from: ");
                        ConsoleColor color = (choices[i].SourceMod == "Original Game File") ? ConsoleColor.DarkGray : ConsoleColor.Cyan;
                        Console.ForegroundColor = color;
                        Console.WriteLine(choices[i].SourceMod);
                        Console.ResetColor();
                    }

                    string userInput = "";
                    int choiceNumber = -1;
                    bool isYesToAll = false;

                    while (choiceNumber < 1 || choiceNumber > choices.Count)
                    {
                        Console.Write($"Select a version (e.g., '2' or '2y' for 'Yes to all'): ");
                        userInput = Console.ReadLine()?.Trim().ToLower() ?? "";
                        if (userInput.EndsWith("y"))
                        {
                            isYesToAll = true;
                            userInput = userInput.TrimEnd('y');
                        }
                        int.TryParse(userInput, out choiceNumber);
                    }

                    chosenBlock = choices[choiceNumber - 1].Block;
                    chosenSource = choices[choiceNumber - 1].SourceMod;

                    if (isYesToAll && chosenSource != "Original Game File")
                    {
                        MergeSessionState.SetDecision(original.FullPathInPak, allSourcesInConflict.Where(s => s != "Original Game File"), chosenSource);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  -> Applying choice '{chosenSource}' for this and all conflicts in this file.");
                        Console.ResetColor();
                    }
                }
                else
                {
                    chosenBlock = modVersions.First().Block;
                    chosenSource = modVersions.First().SourceMod;
                }

            ApplyChange:
                var originalIndex = mergedFileModel.JumpBlocks.FindIndex(b => b.Name == blockName);
                if (originalIndex != -1)
                {
                    if (Rebuild(new JumpParametersFile { JumpBlocks = new List<JumpBlock> { mergedFileModel.JumpBlocks[originalIndex] } }) != Rebuild(new JumpParametersFile { JumpBlocks = new List<JumpBlock> { chosenBlock } }))
                    {
                        chosenBlock.HeaderComments.Insert(0, $"// -- [UTM Merge] block updated from {chosenSource} --");
                        reporter.LogBlockReplacement(blockName, chosenSource);
                    }
                    mergedFileModel.JumpBlocks[originalIndex] = chosenBlock;
                }
                else
                {
                    chosenBlock.HeaderComments.Insert(0, $"// -- [UTM Merge] block added from {chosenSource} --");
                    mergedFileModel.JumpBlocks.Add(chosenBlock);
                }
            }

            return (Rebuild(mergedFileModel), null);
        }
        private static JumpParametersFile Parse(string content)
        {
            var fileModel = new JumpParametersFile();
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            JumpBlock? currentJumpBlock = null;
            bool inMain = false;
            bool inAdvancedParkour = false;
            var headerComments = new List<string>();

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                if (!inMain)
                {
                    if (trimmedLine.StartsWith("sub main()")) inMain = true;
                    else fileModel.Header.Add(line);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(trimmedLine)) continue;
                if (trimmedLine.StartsWith("/*")) continue;

                if (trimmedLine.StartsWith("//"))
                {
                    headerComments.Add(line);
                    continue;
                }

                var jumpMatch = Regex.Match(trimmedLine, @"^\s*Jump\s*\(\s*""([^""]+)""");
                if (jumpMatch.Success)
                {
                    var name = jumpMatch.Groups[1].Value;
                    var inheritsMatch = Regex.Match(trimmedLine, @"""(?:,\s*""([^""]+)"")?\s*\)");
                    currentJumpBlock = new JumpBlock { Name = name, InheritsFrom = inheritsMatch.Groups[1].Success ? inheritsMatch.Groups[1].Value : null, OriginalSignatureLine = line, HeaderComments = new List<string>(headerComments) };
                    headerComments.Clear();
                    fileModel.JumpBlocks.Add(currentJumpBlock);
                    inAdvancedParkour = false;
                    continue;
                }

                if (trimmedLine.StartsWith("AdvancedParkour()")) { if (currentJumpBlock != null) { currentJumpBlock.AdvancedParkour = new AdvancedParkourBlock(); inAdvancedParkour = true; } continue; }
                if (trimmedLine == "{") continue;
                if (trimmedLine == "}") { if (inAdvancedParkour) inAdvancedParkour = false; else if (currentJumpBlock != null) currentJumpBlock = null; continue; }

                if (currentJumpBlock != null)
                {
                    var parameter = new Parameter { Content = line };
                    if (inAdvancedParkour && currentJumpBlock.AdvancedParkour != null) currentJumpBlock.AdvancedParkour.Parameters.Add(parameter);
                    else currentJumpBlock.Parameters.Add(parameter);
                }
            }
            return fileModel;
        }

        private static string Rebuild(JumpParametersFile fileModel)
        {
            var sb = new StringBuilder();
            foreach (var line in fileModel.Header.Where(l => !string.IsNullOrWhiteSpace(l))) sb.AppendLine(line);
            sb.AppendLine("\nsub main()");
            sb.AppendLine("{");
            foreach (var block in fileModel.JumpBlocks)
            {
                foreach (var comment in block.HeaderComments) sb.AppendLine(comment);
                sb.Append(block.OriginalSignatureLine);
                sb.AppendLine("\n\t{");
                foreach (var param in block.Parameters) sb.AppendLine(param.Content);
                if (block.AdvancedParkour != null && block.AdvancedParkour.Parameters.Any())
                {
                    sb.AppendLine("\n\t\tAdvancedParkour()");
                    sb.AppendLine("\t\t{");
                    foreach (var param in block.AdvancedParkour.Parameters) sb.AppendLine(param.Content);
                    sb.AppendLine("\t\t}");
                }
                sb.AppendLine("\t}");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}