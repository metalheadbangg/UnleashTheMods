using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;

public class ScriptFile
{
    public required string FullPathInPak { get; set; }
    public required string Content { get; set; }
    public required string SourcePak { get; set; }
}

class Program
{
    static Dictionary<string, string> finalFileContents = new Dictionary<string, string>();

    static void Main(string[] args)
    {
        Console.Title = "Unleash The Mods - Mod Merge Utility";
        Console.WriteLine("By MetalHeadbang a.k.a @unsc.odst");
        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        string sourceDirectory = Path.Combine(baseDirectory, "source");
        string modsDirectory = Path.Combine(baseDirectory, "mods");
        string stagingDirectory = Path.Combine(baseDirectory, "staging_area");

        if (!Directory.Exists(sourceDirectory) || !Directory.Exists(modsDirectory))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\nERROR: 'source' and 'mods' folders not found!");
            Console.ResetColor();
            Console.ReadKey();
            return;
        }

        Console.WriteLine($"'source' and 'mods' folders found.\n");
        var sourcePaks = Directory.GetFiles(sourceDirectory, "*.pak");

        List<ScriptFile> originalScripts = LoadScriptsFromPakFiles(sourcePaks);
        Console.WriteLine($"{originalScripts.Count} scripts loaded from original game packages.");

        List<ScriptFile> moddedScripts = LoadAllScriptsFromModsFolder(modsDirectory);
        Console.WriteLine($"{moddedScripts.Count} scripts loaded from the mods folder.\n");
        Console.WriteLine("---  Merging Initializing ---");

        var modFileGroups = moddedScripts.GroupBy(s => s.FullPathInPak)
                                         .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var group in modFileGroups)
        {
            string filePath = group.Key;
            List<ScriptFile> modsTouchingThisFile = group.Value;

            if (modsTouchingThisFile.Count == 1)
            {
                finalFileContents[filePath] = modsTouchingThisFile[0].Content;
                continue;
            }

            var originalFile = originalScripts.FirstOrDefault(f => f.FullPathInPak.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            if (originalFile == null)
            {
                finalFileContents[filePath] = modsTouchingThisFile[0].Content;
                continue;
            }

            string mergedContent = GenerateMergedFileContent(originalFile, modsTouchingThisFile);
            finalFileContents[filePath] = mergedContent;
        }

        Console.WriteLine("\n\n--- Merge Completed ---");
        Console.WriteLine($"{finalFileContents.Count} modded files are ready to be packaged.");

        Console.WriteLine("\n--- Creating .pak File ---");

        if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
        Directory.CreateDirectory(stagingDirectory);

        var safeEncoding = new UTF8Encoding(false);

        foreach (var fileEntry in finalFileContents)
        {
            string fullPath = Path.Combine(stagingDirectory, fileEntry.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            File.WriteAllText(fullPath, fileEntry.Value, safeEncoding);
        }
        Console.WriteLine($"{finalFileContents.Count} files packing");

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

    static string GenerateMergedFileContent(ScriptFile original, List<ScriptFile> mods)
    {
        var originalMap = original.Content.Replace("\r\n", "\n").Split('\n')
            .Select(l => new { Key = TryParseKey(l), Line = l })
            .Where(x => x.Key != null)
            .GroupBy(x => x.Key!)
            .ToDictionary(g => g.Key, g => g.First().Line);

        var modMaps = mods.Select(mod => new
        {
            SourcePak = mod.SourcePak,
            Map = mod.Content.Replace("\r\n", "\n").Split('\n')
                .Select(l => new { Key = TryParseKey(l), Line = l })
                .Where(x => x.Key != null)
                .GroupBy(x => x.Key!)
                .ToDictionary(g => g.Key, g => g.First().Line)
        }).ToList();

        var finalContent = new StringBuilder();
        var originalLines = original.Content.Replace("\r\n", "\n").Split('\n');

        var resolutions = new Dictionary<string, string>();
        string? preferredModSource = null;
        int autoResolvedCount = 0;

        foreach (var originalLine in originalLines)
        {
            var key = TryParseKey(originalLine);

            if (key == null)
            {
                finalContent.AppendLine(originalLine);
                continue;
            }

            if (resolutions.TryGetValue(key, out var resolvedLine))
            {
                finalContent.AppendLine(resolvedLine);
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
                finalContent.AppendLine(originalLine);
            }
            else if (actualChanges.Count == 1)
            {
                finalContent.AppendLine(actualChanges[0].Line);
                resolutions[key] = actualChanges[0].Line;
            }
            else
            {
                var distinctChanges = actualChanges.GroupBy(v => v.Line)
                                                   .Select(g => (Line: g.Key, Sources: g.Select(v => v.SourcePak).ToList()))
                                                   .ToList();

                if (distinctChanges.Count == 1)
                {
                    finalContent.AppendLine(distinctChanges[0].Line);
                    resolutions[key] = distinctChanges[0].Line;
                }
                else
                {
                    string chosenLine;
                    if (preferredModSource != null)
                    {
                        var preferredVersion = distinctChanges.FirstOrDefault(v => v.Sources.Contains(preferredModSource));
                        chosenLine = preferredVersion.Line ?? distinctChanges[0].Line;
                        autoResolvedCount++;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n[CHOICE REQUIRED] Conflict in '{original.FullPathInPak}'!");
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  -> Conflict for key '{key}':");
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

                        Console.WriteLine("   To prefer a mod for all conflicts in this file, add 'y' to your choice (e.g., '1y') (CAREFUL! THIS IS FOR ADVANCED USERS).");

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
                        if (chosenSource != null)
                        {
                            preferredModSource = chosenSource;
                        }

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("  -> Choice applied.");
                        Console.ResetColor();
                    }

                    finalContent.AppendLine(chosenLine);
                    resolutions[key] = chosenLine;
                }
            }
        }

        if (autoResolvedCount > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGreen;
            Console.WriteLine($"-> {autoResolvedCount} other conflicts in this file merged using your preference: '{preferredModSource}'.\n");
            Console.ResetColor();
        }

        return finalContent.ToString();
    }

    static List<ScriptFile> LoadAllScriptsFromModsFolder(string modsDirectory)
    {
        var allScripts = new List<ScriptFile>();
        var supportedExtensions = new[] { ".pak", ".zip", ".rar", ".7z" };
        var modFiles = Directory.GetFiles(modsDirectory)
                                .Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

        foreach (var modFilePath in modFiles)
        {
            try
            {
                string sourceName = Path.GetFileName(modFilePath);
                if (Path.GetExtension(modFilePath).ToLowerInvariant() == ".pak")
                {
                    allScripts.AddRange(ReadScriptsFromSinglePak(modFilePath, sourceName));
                }
                else
                {
                    using (var archive = ArchiveFactory.Open(modFilePath))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (!entry.IsDirectory && entry.Key.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                            {
                                using (var pakEntryStream = entry.OpenEntryStream())
                                {
                                    using (var memoryStream = new MemoryStream())
                                    {
                                        pakEntryStream.CopyTo(memoryStream);
                                        memoryStream.Position = 0;
                                        allScripts.AddRange(ReadScriptsFromSinglePak(memoryStream, sourceName));
                                    }
                                }
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
        return allScripts;
    }

    static List<ScriptFile> ReadScriptsFromSinglePak(Stream pakStream, string sourceName)
    {
        var scripts = new List<ScriptFile>();
        try
        {
            using (var pakArchive = ArchiveFactory.Open(pakStream))
            {
                foreach (var entry in pakArchive.Entries)
                {
                    if (!entry.IsDirectory && entry.Key.EndsWith(".scr", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var scrStream = entry.OpenEntryStream())
                        using (var reader = new StreamReader(scrStream, Encoding.UTF8))
                        {
                            scripts.Add(new ScriptFile
                            {
                                Content = reader.ReadToEnd(),
                                FullPathInPak = entry.Key.Replace('\\', '/'),
                                SourcePak = sourceName
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine($"ERROR: Failed to read scripts from '{sourceName}'. Reason: {ex.Message}");
            Console.ResetColor();
        }
        return scripts;
    }

    static List<ScriptFile> ReadScriptsFromSinglePak(string pakPath, string sourceName)
    {
        using (var stream = File.OpenRead(pakPath))
        {
            return ReadScriptsFromSinglePak(stream, sourceName);
        }
    }

    static List<ScriptFile> LoadScriptsFromPakFiles(string[] pakFilePaths)
    {
        var allScripts = new List<ScriptFile>();
        foreach (var path in pakFilePaths)
        {
            allScripts.AddRange(ReadScriptsFromSinglePak(path, Path.GetFileName(path)));
        }
        return allScripts;
    }
}
