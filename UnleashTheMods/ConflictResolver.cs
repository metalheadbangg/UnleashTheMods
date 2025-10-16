using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnleashTheMods.Merger;

namespace UnleashTheMods
{
    public class ConflictResolver
    {
        private readonly List<ModFile> _originalFiles;
        private readonly IScriptMerger _scriptMerger;

        public ConflictResolver(List<ModFile> originalFiles)
        {
            _originalFiles = originalFiles;
            _scriptMerger = new ScrMerger();
        }

        public (Dictionary<string, byte[]> FinalFiles, Dictionary<string, List<string>> MergeSummary) Resolve(List<ModFile> moddedFiles)
        {
            var uniqueModFiles = moddedFiles
                .GroupBy(f => new { f.SourcePak, f.FullPathInPak })
                .Select(g => g.First())
                .ToList();

            var reporter = new MergeReporter();
            var finalFileContents = new Dictionary<string, byte[]>();

            var modFileGroups = uniqueModFiles.GroupBy(s => s.FullPathInPak)
                                           .ToDictionary(g => g.Key, g => g.ToList());

            var mergeSummary = new Dictionary<string, List<string>>();

            MergeSessionState.Reset();

            foreach (var group in modFileGroups)
            {
                string filePath = group.Key;
                List<ModFile> modsTouchingThisFile = group.Value;

                mergeSummary[filePath] = modsTouchingThisFile.Select(m => m.SourcePak).Distinct().ToList();

                if (filePath.EndsWith(".scr", StringComparison.OrdinalIgnoreCase))
                {
                    var originalFile = _originalFiles.FirstOrDefault(f => f.FullPathInPak.Equals(filePath, StringComparison.OrdinalIgnoreCase));

                    if (originalFile == null)
                    {
                        if (modsTouchingThisFile.Count > 1)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"\n[CHOICE REQUIRED] Conflict for NEWLY ADDED script file: '{filePath}'");
                            Console.ResetColor();
                            Console.WriteLine("  This new script is provided by the following mods:");
                            var chosenFile = HandleAssetConflict(filePath, modsTouchingThisFile);
                            finalFileContents[filePath] = chosenFile.Content;
                        }
                        else
                        {
                            finalFileContents[filePath] = modsTouchingThisFile[0].Content;
                        }
                        continue;
                    }

                    if (AlternateMerger.ShouldUseAlternateMerge(filePath))
                    {
                        var result = AlternateMerger.Merge(originalFile, modsTouchingThisFile, reporter, _scriptMerger);
                        finalFileContents[filePath] = new UTF8Encoding(false).GetBytes(result.MergedContent);
                    }
                    else
                    {
                        var result = _scriptMerger.Merge(originalFile, modsTouchingThisFile, null, reporter);
                        finalFileContents[filePath] = new UTF8Encoding(false).GetBytes(result.MergedContent);
                    }
                }
                else
                {
                    if (modsTouchingThisFile.Count == 1)
                    {
                        finalFileContents[filePath] = modsTouchingThisFile[0].Content;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n[CHOICE REQUIRED] Conflict for asset file: '{filePath}'");
                        Console.ResetColor();
                        Console.WriteLine("  This asset is included in the following mods:");
                        var chosenFile = HandleAssetConflict(filePath, modsTouchingThisFile);
                        finalFileContents[filePath] = chosenFile.Content;
                    }
                }
            }

            if (reporter.HasEntries())
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Merge_Log.txt");
                File.WriteAllText(logPath, reporter.GetReport());
            }

            return (finalFileContents, mergeSummary);
        }

        private ModFile HandleAssetConflict(string filePath, List<ModFile> mods)
        {
            var modSources = mods.Select(m => m.SourcePak).ToList();
            var autoDecision = MergeSessionState.GetDecision(filePath, modSources);

            if (autoDecision != null)
            {
                var chosen = mods.FirstOrDefault(m => m.SourcePak == autoDecision);
                if (chosen != null) return chosen;
            }

            for (int i = 0; i < mods.Count; i++)
            {
                Console.Write($"    {i + 1}. ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(mods[i].SourcePak);
                Console.ResetColor();
            }

            int choice = -1;
            bool yesToAll = false;
            while (choice < 1 || choice > mods.Count)
            {
                Console.Write($"Please select which mod's version to use (1-{mods.Count}) or e.g. '1y' for 'Yes to all': ");
                string? input = Console.ReadLine()?.Trim().ToLower();
                if (input != null && input.EndsWith("y"))
                {
                    yesToAll = true;
                    input = input.TrimEnd('y');
                }
                int.TryParse(input, out choice);
            }
            var chosenFile = mods[choice - 1];

            if (yesToAll)
            {
                MergeSessionState.SetDecision(filePath, modSources, chosenFile.SourcePak);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  -> Choice '{chosenFile.SourcePak}' applied for this and all subsequent conflicts in this file.");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  -> Choice applied.");
                Console.ResetColor();
            }

            return chosenFile;
        }
    }
}