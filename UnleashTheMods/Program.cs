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
            Console.WriteLine("\nERROR: 'data0.pak' not found in source folder!");
            Console.ResetColor();
            Console.ReadKey();
            return;
        }

        Directory.CreateDirectory(fixedModsDirectory);
        var validMods = FixModStructures(gamePakPath, modsDirectory, fixedModsDirectory);

        var sourcePaks = Directory.GetFiles(sourceDirectory, "*.pak");
        List<ScriptFile> originalScripts = LoadScriptsFromPakFiles(sourcePaks);
        Console.WriteLine($"{originalScripts.Count} scripts loaded from original game packages.");

        List<ScriptFile> moddedScripts = LoadAllScriptsFromModsFolder(validMods);
        Console.WriteLine($"{moddedScripts.Count} scripts loaded from the mods folder.\n");

        Console.WriteLine("--- Merging Initializing ---");
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

    static List<string> FixModStructures(string gamePakPath, string modsDirectory, string fixedModsDirectory)
    {
        var fileStructure = GetGameFileStructure(gamePakPath);
        var validMods = new List<string>();
        var supportedExtensions = new[] { ".pak", ".zip", ".rar", ".7z" };
        var modFiles = Directory.GetFiles(modsDirectory)
                                .Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

        foreach (var modFile in modFiles)
        {
            bool needsFixing = false;
            string modName = Path.GetFileNameWithoutExtension(modFile);
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            string fixedPakPath = Path.Combine(tempDir, "fixed.pak");
            Directory.CreateDirectory(tempDir);

            try
            {
                List<(string Name, string Path, Stream Content)> modScripts = new List<(string, string, Stream)>();
                List<string> unknownFiles = new List<string>();

                if (Path.GetExtension(modFile).ToLowerInvariant() == ".pak")
                {
                    modScripts = ReadScriptsFromSinglePakForFixing(modFile, ref needsFixing, fileStructure, ref unknownFiles);
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
                                modScripts.AddRange(ReadScriptsFromSinglePakForFixing(memoryStream, ref needsFixing, fileStructure, ref unknownFiles));
                            }
                        }
                    }
                }

                if (unknownFiles.Any())
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\nWARNING: Mod '{modName}' contains files not found in data0.pak:");
                    foreach (var file in unknownFiles)
                    {
                        Console.WriteLine($" - {file}");
                    }
                    Console.WriteLine("Options: (1) Keep original structure, (2) Exclude this mod.");
                    Console.Write("Please select an option (1 or 2): ");
                    string input = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "2";
                    Console.ResetColor();

                    if (input != "1")
                    {
                        Console.WriteLine($"Mod '{modName}' excluded due to unknown files.");
                        continue;
                    }
                    else
                    {
                        Console.WriteLine($"Mod '{modName}' will be used with its original structure.");
                        validMods.Add(modFile);
                        continue;
                    }
                }

                if (needsFixing)
                {
                    using (var fixedPakStream = File.OpenWrite(fixedPakPath))
                    using (var writer = WriterFactory.Open(fixedPakStream, ArchiveType.Zip, new WriterOptions(CompressionType.Deflate)))
                    {
                        foreach (var (fileName, correctPath, contentStream) in modScripts)
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
                    Console.WriteLine($"Mod '{modName}' fixed and saved as '{Path.GetFileName(fixedZipPath)}'.");
                }
                else
                {
                    validMods.Add(modFile);
                    Console.WriteLine($"Mod '{modName}' already has correct folder structure.");
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
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory && e.Key.EndsWith(".scr", StringComparison.OrdinalIgnoreCase)))
            {
                string fileName = Path.GetFileName(entry.Key);
                string fullPath = entry.Key.Replace("/", "\\");
                if (!structure.TryAdd(fileName, fullPath))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Warning: Duplicate file '{fileName}' found at '{fullPath}' in data0.pak.");
                    Console.ResetColor();
                }
            }
        }
        return structure;
    }

    static List<(string Name, string Path, Stream Content)> ReadScriptsFromSinglePakForFixing(Stream pakStream, ref bool needsFixing, Dictionary<string, string> fileStructure, ref List<string> unknownFiles)
    {
        var scripts = new List<(string Name, string Path, Stream Content)>();
        using (var pakArchive = ArchiveFactory.Open(pakStream))
        {
            foreach (var entry in pakArchive.Entries.Where(e => !e.IsDirectory && e.Key.EndsWith(".scr", StringComparison.OrdinalIgnoreCase)))
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
                    scripts.Add((fileName, correctPath, memoryStream));
                }
                else
                {
                    unknownFiles.Add(modPath);
                }
            }
        }
        return scripts;
    }

    static List<(string Name, string Path, Stream Content)> ReadScriptsFromSinglePakForFixing(string pakPath, ref bool needsFixing, Dictionary<string, string> fileStructure, ref List<string> unknownFiles)
    {
        using (var stream = File.OpenRead(pakPath))
        {
            return ReadScriptsFromSinglePakForFixing(stream, ref needsFixing, fileStructure, ref unknownFiles);
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

    static string GenerateMergedFileContent(ScriptFile original, List<ScriptFile> mods)
    {
        var originalMap = original.Content.Replace("\r\n", "\n").Split('\n')
            .Select(l => new { Key = TryParseKey(l), Line = l }).Where(x => x.Key != null)
            .GroupBy(x => x.Key!).ToDictionary(g => g.Key, g => g.First().Line);

        var modMaps = mods.Select(mod => new
        {
            SourcePak = mod.SourcePak,
            Map = mod.Content.Replace("\r\n", "\n").Split('\n')
                .Select(l => new { Key = TryParseKey(l), Line = l }).Where(x => x.Key != null)
                .GroupBy(x => x.Key!).ToDictionary(g => g.Key, g => g.First().Line)
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
                    .Select(g => (Line: g.Key, Sources: g.Select(v => v.SourcePak).ToList())).ToList();
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
                        Console.WriteLine($" -> Conflict for key '{key}':");
                        Console.ResetColor();
                        for (int i = 0; i < distinctChanges.Count; i++)
                        {
                            string sources = string.Join(", ", distinctChanges[i].Sources);
                            Console.Write($" {i + 1}. (");
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.Write(sources);
                            Console.ResetColor();
                            Console.Write("): ");
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine(distinctChanges[i].Line.Trim());
                            Console.ResetColor();
                        }
                        Console.WriteLine(" To prefer a mod for all conflicts in this file, add 'y' to your choice (e.g., '1y') (CAREFUL! THIS IS FOR ADVANCED USERS).");
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
                        Console.WriteLine(" -> Choice applied.");
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

    static List<ScriptFile> LoadAllScriptsFromModsFolder(IEnumerable<string> modFiles)
    {
        var allScripts = new List<ScriptFile>();
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
                        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory && e.Key.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
                        {
                            using (var pakEntryStream = entry.OpenEntryStream())
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
                foreach (var entry in pakArchive.Entries.Where(e => !e.IsDirectory && e.Key.EndsWith(".scr", StringComparison.OrdinalIgnoreCase)))
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
