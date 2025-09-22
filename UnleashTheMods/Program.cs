using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;

public class ModFile
{
    public required string FullPathInPak { get; set; }
    public required byte[] Content { get; set; }
    public required string SourcePak { get; set; }
}

class Program
{
    static Dictionary<string, byte[]> finalFileContents = new Dictionary<string, byte[]>();

    static void Main(string[] args)
    {
        Console.Title = "Unleash The Mods - Mod Merge Utility";
        Console.WriteLine("Unleash The Mods - Mod Merge Utility");
        Console.WriteLine("By MetalHeadbang a.k.a @unsc.odst\r\n");

        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        string sourceDirectory = Path.Combine(baseDirectory, "source");
        string modsDirectory = Path.Combine(baseDirectory, "mods");
        string stagingDirectory = Path.Combine(baseDirectory, "staging_area");
        string fixedModsDirectory = Path.Combine(baseDirectory, "fixed_mods");

        if (!Directory.Exists(sourceDirectory) || !Directory.Exists(modsDirectory))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\nERROR: 'source' and 'mods' folders not found!");
            Console.ResetColor();
            Console.ReadKey();
            return;
        }

        Console.WriteLine("'source' and 'mods' folders found.\n");

        string gamePakPath = Path.Combine(sourceDirectory, "data0.pak");
        if (!File.Exists(gamePakPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\nERROR: 'data0.pak' not found in source folder! Tool needs this file to work.");
            Console.ResetColor();
            Console.ReadKey();
            return;
        }

        Directory.CreateDirectory(fixedModsDirectory);
        Console.WriteLine("--- Verifying and Fixing Mod Structures ---");
        var validMods = FixModStructures(gamePakPath, modsDirectory, fixedModsDirectory);
        Console.WriteLine("Mod structure verification complete.\n");

        var sourcePaks = Directory.GetFiles(sourceDirectory, "*.pak");
        List<ModFile> originalFiles = LoadAllModFilesFromPaks(sourcePaks);

        List<ModFile> moddedFiles = LoadAllModFilesFromModsFolder(validMods);

        Console.WriteLine("--- Initializing Merge ---");
        var modFileGroups = moddedFiles.GroupBy(s => s.FullPathInPak)
                                       .ToDictionary(g => g.Key, g => g.ToList());

        string? preferredModSource = null;

        foreach (var group in modFileGroups)
        {
            string filePath = group.Key;
            List<ModFile> modsTouchingThisFile = group.Value;

            if (modsTouchingThisFile.Count == 1)
            {
                finalFileContents[filePath] = modsTouchingThisFile[0].Content;
                continue;
            }

            if (filePath.EndsWith(".scr", StringComparison.OrdinalIgnoreCase))
            {
                var originalFile = originalFiles.FirstOrDefault(f => f.FullPathInPak.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                if (originalFile == null)
                {
                    finalFileContents[filePath] = modsTouchingThisFile[0].Content;
                    continue;
                }
                var result = GenerateMergedFileContent(originalFile, modsTouchingThisFile, preferredModSource);
                finalFileContents[filePath] = new UTF8Encoding(false).GetBytes(result.MergedContent);
                if (result.UpdatedPreferredSource != null)
                {
                    preferredModSource = result.UpdatedPreferredSource;
                }
            }
            else
            {
                ModFile? chosenFile = null;
                if (preferredModSource != null)
                {
                    chosenFile = modsTouchingThisFile.FirstOrDefault(m => m.SourcePak == preferredModSource);
                    if (chosenFile != null)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGreen;
                        Console.WriteLine($"[AUTO-RESOLVED] Conflict for asset '{filePath}' using preferred mod '{preferredModSource}'.");
                        Console.ResetColor();
                    }
                }

                if (chosenFile == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[CHOICE REQUIRED] Conflict for asset file: '{filePath}'");
                    Console.ResetColor();
                    Console.WriteLine("  This asset is included in the following mods:");

                    for (int i = 0; i < modsTouchingThisFile.Count; i++)
                    {
                        Console.Write($"    {i + 1}. ");
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine(modsTouchingThisFile[i].SourcePak);
                        Console.ResetColor();
                    }
                    Console.WriteLine("   To prefer a mod for all future conflicts, add 'y' to your choice (e.g., '1y') (CAREFUL! THIS IS FOR ADVANCED USERS)");

                    int choice = -1;
                    while (choice < 1 || choice > modsTouchingThisFile.Count)
                    {
                        Console.Write($"Please select which mod's version to use (1-{modsTouchingThisFile.Count}): ");
                        string? input = Console.ReadLine()?.ToLowerInvariant();
                        if (input != null && input.EndsWith("y"))
                        {
                            if (int.TryParse(input.TrimEnd('y'), out choice) && choice >= 1 && choice <= modsTouchingThisFile.Count)
                            {
                                preferredModSource = modsTouchingThisFile[choice - 1].SourcePak;
                            }
                        }
                        else
                        {
                            int.TryParse(input, out choice);
                        }
                    }
                    chosenFile = modsTouchingThisFile[choice - 1];
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("  -> Choice applied.");
                    if (preferredModSource != null) Console.WriteLine($"  -> Mod '{preferredModSource}' will be preferred for all future conflicts.");
                    Console.ResetColor();
                }
                finalFileContents[filePath] = chosenFile.Content;
            }
        }

        Console.WriteLine("\n\n--- Merge Completed ---");
        Console.WriteLine("\n--- Creating Final .pak File ---");

        if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
        Directory.CreateDirectory(stagingDirectory);

        foreach (var fileEntry in finalFileContents)
        {
            string fullPath = Path.Combine(stagingDirectory, fileEntry.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? string.Empty);
            File.WriteAllBytes(fullPath, fileEntry.Value);
        }
        Console.WriteLine($"{finalFileContents.Count} files written to temporary staging path.");

        int nextPakNum = 0;
        var existingPaks = Directory.GetFiles(sourceDirectory, "data*.pak");
        foreach (var pak in existingPaks)
        {
            string fileName = Path.GetFileNameWithoutExtension(pak);
            if (fileName.StartsWith("data") && int.TryParse(fileName.AsSpan(4), out int num))
            {
                if (num >= nextPakNum) nextPakNum = num + 1;
            }
        }
        if (nextPakNum < 3) nextPakNum = 3;

        string finalPakPath = Path.Combine(sourceDirectory, $"data{nextPakNum}.pak");
        if (File.Exists(finalPakPath)) File.Delete(finalPakPath);

        ZipFile.CreateFromDirectory(stagingDirectory, finalPakPath, CompressionLevel.Optimal, false);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nSUCCESS! All mods have been merged and saved as '{Path.GetFileName(finalPakPath)}' in the game's source folder!");
        Console.ResetColor();

        Directory.Delete(stagingDirectory, true);
        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }

    static string AppendUtmComment(string line, string comment)
    {
        string trimmedLine = line.TrimEnd();
        if (trimmedLine.Contains("//"))
        {
            return $"{trimmedLine} ; {comment}";
        }
        else
        {
            return $"{trimmedLine.PadRight(70)}// {comment}";
        }
    }

    static string? ExtractValueFromLine(string line)
    {
        var match = Regex.Match(line, @"\(\s*""[^""]+""\s*,\s*([^)]+)\)");
        if (match.Success)
        {
            return match.Groups[1].Value.Trim().TrimEnd(';');
        }
        return null;
    }

    static List<string> FixModStructures(string gamePakPath, string modsDirectory, string fixedModsDirectory)
    {
        var fileStructure = GetGameFileStructure(gamePakPath);
        var validMods = new List<string>();
        var supportedExtensions = new[] { ".pak", ".zip", ".rar", ".7z" };
        var modFiles = Directory.GetFiles(modsDirectory)
                                .Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

        foreach (var modFile in modFiles)
        {
            string modName = Path.GetFileNameWithoutExtension(modFile);
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                bool needsFixing = false;
                List<string> unknownFiles = new List<string>();
                List<(string Name, string Path, Stream Content)> modAssets = new List<(string, string, Stream)>();

                if (Path.GetExtension(modFile).ToLowerInvariant() == ".pak")
                {
                    modAssets = ReadModAssetsForFixing(modFile, ref needsFixing, fileStructure, ref unknownFiles);
                }
                else
                {
                    using (var archive = ArchiveFactory.Open(modFile))
                    {
                        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory && e.Key.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
                        {
                            using (var pakEntryStream = entry.OpenEntryStream())
                            using (var memoryStream = new MemoryStream())
                            {
                                pakEntryStream.CopyTo(memoryStream);
                                memoryStream.Position = 0;
                                modAssets.AddRange(ReadModAssetsForFixing(memoryStream, ref needsFixing, fileStructure, ref unknownFiles));
                            }
                        }
                    }
                }

                if (modAssets.Count == 0 && !unknownFiles.Any())
                {
                    Console.WriteLine($"Mod '{modName}' contains no processable files. Skipping.");
                    continue;
                }


                bool isNonStandard = needsFixing || unknownFiles.Any();
                int userChoice = 0;

                if (isNonStandard)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"\nMod '{modName}' appears to have a non-standard file structure.");
                    Console.ResetColor();
                    Console.WriteLine("What would you like to do?");
                    Console.WriteLine(" (1) Use As-Is: Keeps the mod's original folder structure.");
                    Console.WriteLine("     -> Recommended for complex mods with new files.");
                    Console.WriteLine(" (2) Attempt Automatic Fix: Tries to move known files to their correct paths.");
                    Console.WriteLine("     -> Recommended for simple mods with files in the wrong folder.");
                    Console.WriteLine(" (3) Exclude this Mod: Skips this mod entirely.");

                    while (userChoice < 1 || userChoice > 3)
                    {
                        Console.Write("Please select an option (1, 2, or 3): ");
                        int.TryParse(Console.ReadLine()?.Trim(), out userChoice);
                    }
                }

                if (userChoice == 1)
                {
                    Console.WriteLine($" -> Use As-Is. '{modName}' will be used with its original structure.");
                    validMods.Add(modFile);
                    continue;
                }
                else if (userChoice == 3)
                {
                    Console.WriteLine($" -> '{modName}' will be excluded.");
                    continue;
                }

                if (unknownFiles.Any())
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\nWARNING: Mod '{modName}' contains files not found in data0.pak:");
                    foreach (var file in unknownFiles) Console.WriteLine($" - {file}");
                    Console.ResetColor();
                }

                if (needsFixing)
                {
                    string fixedPakPath = Path.Combine(tempDir, "fixed.pak");
                    using (var fixedPakStream = File.OpenWrite(fixedPakPath))
                    using (var writer = WriterFactory.Open(fixedPakStream, ArchiveType.Zip, new WriterOptions(CompressionType.Deflate)))
                    {
                        foreach (var (fileName, correctPath, contentStream) in modAssets)
                        {
                            contentStream.Position = 0;
                            writer.Write(correctPath.Replace('\\', '/'), contentStream);
                            contentStream.Dispose();
                        }
                    }

                    string fixedZipPath = Path.Combine(fixedModsDirectory, $"{modName}_fixed.zip");
                    using (var fixedZipStream = File.OpenWrite(fixedZipPath))
                    using (var writer = WriterFactory.Open(fixedZipStream, ArchiveType.Zip, new WriterOptions(CompressionType.Deflate)))
                    {
                        writer.Write("mod.pak", fixedPakPath);
                    }
                    validMods.Add(fixedZipPath);
                    Console.WriteLine($" -> Mod '{modName}' structure fixed and saved as '{Path.GetFileName(fixedZipPath)}'.");
                }
                else
                {
                    validMods.Add(modFile);
                    Console.WriteLine($" -> Mod '{modName}' already has correct folder structure.");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine($"ERROR: Could not process mod '{modName}'. Reason: {ex.Message}");
                Console.ResetColor();
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }
        return validMods;
    }

    static Dictionary<string, string> GetGameFileStructure(string gamePakPath)
    {
        var structure = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ArchiveFactory.Open(gamePakPath))
        {
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            {
                string fileName = Path.GetFileName(entry.Key);
                string fullPath = entry.Key.Replace("/", "\\");
                if (!structure.TryAdd(fileName, fullPath))
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"Warning: Duplicate file name '{fileName}' found in data0.pak. First instance at '{structure[fileName]}' will be used.");
                    Console.ResetColor();
                }
            }
        }
        return structure;
    }

    static List<(string Name, string Path, Stream Content)> ReadModAssetsForFixing(Stream pakStream, ref bool needsFixing, Dictionary<string, string> fileStructure, ref List<string> unknownFiles)
    {
        var assets = new List<(string Name, string Path, Stream Content)>();
        using (var pakArchive = ArchiveFactory.Open(pakStream))
        {
            foreach (var entry in pakArchive.Entries.Where(e => !e.IsDirectory))
            {
                string fileName = Path.GetFileName(entry.Key);
                string modPath = entry.Key.Replace("/", "\\");

                if (fileStructure.TryGetValue(fileName, out var correctPath))
                {
                    if (modPath != correctPath)
                    {
                        needsFixing = true;
                    }

                    var memoryStream = new MemoryStream();
                    using (var entryStream = entry.OpenEntryStream())
                    {
                        entryStream.CopyTo(memoryStream);
                    }
                    memoryStream.Position = 0;
                    assets.Add((fileName, correctPath, memoryStream));
                }
                else
                {
                    unknownFiles.Add(modPath);
                }
            }
        }
        return assets;
    }

    static List<(string Name, string Path, Stream Content)> ReadModAssetsForFixing(string pakPath, ref bool needsFixing, Dictionary<string, string> fileStructure, ref List<string> unknownFiles)
    {
        using (var stream = File.OpenRead(pakPath))
        {
            return ReadModAssetsForFixing(stream, ref needsFixing, fileStructure, ref unknownFiles);
        }
    }

    static string? TryParseKey(string line)
    {
        string trimmedLine = line.Trim();
        Match match = Regex.Match(trimmedLine, @"^(\w+)\s*\(\s*""([^""]+)""");
        if (match.Success)
        {
            string functionName = match.Groups[1].Value;
            string firstParam = match.Groups[2].Value;
            return $"{functionName}_{firstParam}";
        }
        return null;
    }

    static (string MergedContent, string? UpdatedPreferredSource) GenerateMergedFileContent(ModFile original, List<ModFile> mods, string? currentPreferredSource)
    {
        var encoding = Encoding.UTF8;
        var originalContentString = encoding.GetString(original.Content);
        string? newPreferredSource = null;

        var originalMap = originalContentString.Replace("\r\n", "\n").Split('\n')
            .Select(l => new { Key = TryParseKey(l), Line = l }).Where(x => x.Key != null)
            .GroupBy(x => x.Key!).ToDictionary(g => g.Key, g => g.First().Line);

        var modMaps = mods.Select(mod => new
        {
            SourcePak = mod.SourcePak,
            Map = encoding.GetString(mod.Content).Replace("\r\n", "\n").Split('\n')
                .Select(l => new { Key = TryParseKey(l), Line = l }).Where(x => x.Key != null)
                .GroupBy(x => x.Key!).ToDictionary(g => g.Key, g => g.First().Line)
        }).ToList();

        var finalLines = new List<string>();
        var resolutions = new Dictionary<string, string>();
        int autoResolvedCount = 0;
        var processedKeys = new HashSet<string>();
        var originalLinesList = new List<string>(originalContentString.Replace("\r\n", "\n").Split('\n'));

        foreach (var originalLine in originalLinesList)
        {
            var key = TryParseKey(originalLine);
            if (key == null)
            {
                finalLines.Add(originalLine);
                continue;
            }

            processedKeys.Add(key);

            if (resolutions.TryGetValue(key, out var resolvedLine))
            {
                finalLines.Add(resolvedLine);
                continue;
            }

            var actualChanges = new List<(string Line, string SourcePak)>();
            originalMap.TryGetValue(key, out var baseLine);
            foreach (var mod in modMaps)
            {
                if (mod.Map.TryGetValue(key, out var modLine) && modLine != baseLine)
                {
                    actualChanges.Add((modLine, mod.SourcePak));
                }
            }

            if (actualChanges.Count == 0)
            {
                finalLines.Add(originalLine);
            }
            else
            {
                var distinctChanges = actualChanges.GroupBy(v => v.Line)
                    .Select(g => (Line: g.Key, Sources: g.Select(v => v.SourcePak).ToList())).ToList();

                string chosenLine;
                string sourceForComment;

                if (distinctChanges.Count == 1)
                {
                    chosenLine = distinctChanges[0].Line;
                    sourceForComment = distinctChanges[0].Sources.First();
                }
                else
                {
                    if (currentPreferredSource != null)
                    {
                        var preferredVersion = distinctChanges.FirstOrDefault(v => v.Sources.Contains(currentPreferredSource));
                        chosenLine = preferredVersion.Line ?? distinctChanges[0].Line;
                        sourceForComment = currentPreferredSource;
                        autoResolvedCount++;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n[CHOICE REQUIRED] Conflict in '{original.FullPathInPak}'!");
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($" -> Conflict for key '{key}':");
                        Console.ResetColor();
                        for (int i = 0; i < distinctChanges.Count; i++)
                        {
                            string sources = string.Join(", ", distinctChanges[i].Sources);
                            Console.Write($"    {i + 1}. (");
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.Write(sources);
                            Console.ResetColor();
                            Console.Write("): ");
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine(distinctChanges[i].Line.Trim());
                            Console.ResetColor();
                        }

                        Console.WriteLine("   To prefer a mod for all conflicts in this file, add 'y' to your choice (e.g., '1y') (CAREFUL! THIS IS FOR ADVANCED USERS)");
                        int choice = -1;
                        string? chosenSource = null;
                        while (choice < 1 || choice > distinctChanges.Count)
                        {
                            Console.Write($"Please select the version to use (1-{distinctChanges.Count}): ");
                            string? input = Console.ReadLine()?.ToLowerInvariant();
                            if (input != null && input.EndsWith("y"))
                            {
                                if (int.TryParse(input.TrimEnd('y'), out choice) && choice >= 1 && choice <= distinctChanges.Count)
                                {
                                    chosenSource = distinctChanges[choice - 1].Sources.First();
                                }
                            }
                            else
                            {
                                int.TryParse(input, out choice);
                            }
                        }
                        chosenLine = distinctChanges[choice - 1].Line;
                        sourceForComment = distinctChanges[choice - 1].Sources.First();
                        if (chosenSource != null)
                        {
                            newPreferredSource = chosenSource;
                        }
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("  -> Choice applied.");
                        Console.ResetColor();
                    }
                }
                string ogValue = ExtractValueFromLine(baseLine ?? "") ?? "N/A";
                finalLines.Add(AppendUtmComment(chosenLine, $"[UTM Merge] updated from {sourceForComment} (OG Value: {ogValue})"));
                resolutions[key] = chosenLine;
            }
        }

        var newKeyConflicts = new Dictionary<string, List<(string Line, string SourcePak)>>();
        foreach (var mod in modMaps)
        {
            foreach (var entry in mod.Map)
            {
                if (!processedKeys.Contains(entry.Key))
                {
                    if (!newKeyConflicts.ContainsKey(entry.Key))
                    {
                        newKeyConflicts[entry.Key] = new List<(string Line, string SourcePak)>();
                    }
                    newKeyConflicts[entry.Key].Add((entry.Value, mod.SourcePak));
                }
            }
        }

        if (newKeyConflicts.Any())
        {
            var newLinesToAdd = new List<string>();
            foreach (var conflict in newKeyConflicts)
            {
                var key = conflict.Key;
                var versions = conflict.Value;
                var distinctVersions = versions.GroupBy(v => v.Line)
                    .Select(g => (Line: g.Key, Sources: g.Select(v => v.SourcePak).ToList())).ToList();

                string chosenLine;
                string sourceForComment;

                if (distinctVersions.Count == 1)
                {
                    chosenLine = distinctVersions[0].Line;
                    sourceForComment = distinctVersions[0].Sources.First();
                }
                else
                {
                    if (currentPreferredSource != null)
                    {
                        var preferredVersion = distinctVersions.FirstOrDefault(v => v.Sources.Contains(currentPreferredSource));
                        chosenLine = preferredVersion.Line ?? distinctVersions[0].Line;
                        sourceForComment = currentPreferredSource;
                        autoResolvedCount++;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n[CHOICE REQUIRED] Conflict for NEWLY ADDED key in '{original.FullPathInPak}'!");
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($" -> Conflict for key '{key}':");
                        Console.ResetColor();
                        for (int i = 0; i < distinctVersions.Count; i++)
                        {
                            string sources = string.Join(", ", distinctVersions[i].Sources);
                            Console.Write($"    {i + 1}. (");
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.Write(sources);
                            Console.ResetColor();
                            Console.Write("): ");
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine(distinctVersions[i].Line.Trim());
                            Console.ResetColor();
                        }
                        Console.WriteLine("   To prefer a mod for all conflicts in this file, add 'y' to your choice (e.g., '1y') (CAREFUL! THIS IS FOR ADVANCED USERS)");
                        int choice = -1;
                        string? chosenSource = null;
                        while (choice < 1 || choice > distinctVersions.Count)
                        {
                            Console.Write($"Please select the version to use (1-{distinctVersions.Count}): ");
                            string? input = Console.ReadLine()?.ToLowerInvariant();
                            if (input != null && input.EndsWith("y"))
                            {
                                if (int.TryParse(input.TrimEnd('y'), out choice) && choice >= 1 && choice <= distinctVersions.Count)
                                {
                                    chosenSource = distinctVersions[choice - 1].Sources.First();
                                }
                            }
                            else
                            {
                                int.TryParse(input, out choice);
                            }
                        }
                        chosenLine = distinctVersions[choice - 1].Line;
                        sourceForComment = distinctVersions[choice - 1].Sources.First();
                        if (chosenSource != null)
                        {
                            newPreferredSource = chosenSource;
                        }
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("  -> Choice applied.");
                        Console.ResetColor();
                    }
                }
                newLinesToAdd.Add(AppendUtmComment(chosenLine, $"[UTM Merge] added new line from {sourceForComment}"));
            }

            int lastBraceIndex = finalLines.FindLastIndex(l => l.Trim() == "}");
            if (lastBraceIndex != -1)
            {
                finalLines.InsertRange(lastBraceIndex, newLinesToAdd);
            }
            else
            {
                finalLines.AddRange(newLinesToAdd);
            }
        }

        if (autoResolvedCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGreen;
            Console.WriteLine($"-> {autoResolvedCount} other conflicts in this file were auto-resolved using your preference: '{currentPreferredSource}'.\n");
            Console.ResetColor();
        }

        return (string.Join(Environment.NewLine, finalLines), newPreferredSource);
    }

    static List<ModFile> LoadAllModFilesFromModsFolder(IEnumerable<string> modFiles)
    {
        var allFiles = new List<ModFile>();
        foreach (var modFilePath in modFiles)
        {
            try
            {
                string sourceName = Path.GetFileName(modFilePath);
                if (Path.GetExtension(modFilePath).ToLowerInvariant() == ".pak")
                {
                    allFiles.AddRange(ReadModFilesFromSinglePak(modFilePath, sourceName));
                }
                else
                {
                    using (var archive = ArchiveFactory.Open(modFilePath))
                    {
                        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory && e.Key.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
                        {
                            using (var pakEntryStream = entry.OpenEntryStream())
                            using (var memoryStream = new MemoryStream())
                            {
                                pakEntryStream.CopyTo(memoryStream);
                                memoryStream.Position = 0;
                                allFiles.AddRange(ReadModFilesFromSinglePak(memoryStream, sourceName));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine($"ERROR: Could not read '{Path.GetFileName(modFilePath)}'. Reason: {ex.Message}");
                Console.ResetColor();
            }
        }
        return allFiles;
    }

    static List<ModFile> ReadModFilesFromSinglePak(Stream pakStream, string sourceName)
    {
        var files = new List<ModFile>();
        try
        {
            using (var pakArchive = ArchiveFactory.Open(pakStream))
            {
                foreach (var entry in pakArchive.Entries.Where(e => !e.IsDirectory))
                {
                    using (var entryStream = entry.OpenEntryStream())
                    using (var ms = new MemoryStream())
                    {
                        entryStream.CopyTo(ms);
                        files.Add(new ModFile
                        {
                            Content = ms.ToArray(),
                            FullPathInPak = entry.Key.Replace('\\', '/'),
                            SourcePak = sourceName
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine($"ERROR: Failed to read files from '{sourceName}'. Reason: {ex.Message}");
            Console.ResetColor();
        }
        return files;
    }

    static List<ModFile> ReadModFilesFromSinglePak(string pakPath, string sourceName)
    {
        using (var stream = File.OpenRead(pakPath))
        {
            return ReadModFilesFromSinglePak(stream, sourceName);
        }
    }

    static List<ModFile> LoadAllModFilesFromPaks(string[] pakFilePaths)
    {
        var allFiles = new List<ModFile>();
        foreach (var path in pakFilePaths)
        {
            allFiles.AddRange(ReadModFilesFromSinglePak(path, Path.GetFileName(path)));
        }
        return allFiles;
    }
}
