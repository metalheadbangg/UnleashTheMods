using SharpCompress.Archives;
using SharpCompress.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnleashTheMods.Merger;

namespace UnleashTheMods
{
    public class ModFile
    {
        public required string FullPathInPak { get; set; }
        public required byte[] Content { get; set; }
        public required string SourcePak { get; set; }
    }
    public class FileLocationInfo
    {
        public string FullPath { get; set; }
        public string SourcePak { get; set; }
    }

    public class StructureError
    {
        public string FileName { get; set; }
        public string IncorrectPath { get; set; }
        public List<FileLocationInfo> SuggestedPaths { get; set; } = new List<FileLocationInfo>();
        public bool IsNewFile { get; set; }
    }

    public class ModCacher
    {
        private readonly Dictionary<string, List<FileLocationInfo>> _gameFileStructure;

        public ModCacher(string sourceDirectory)
        {
            Console.WriteLine(" -> Building game file structure from data0.pak and data1.pak...");
            _gameFileStructure = new Dictionary<string, List<FileLocationInfo>>(StringComparer.OrdinalIgnoreCase);

            string data0Path = Path.Combine(sourceDirectory, "data0.pak");
            string data1Path = Path.Combine(sourceDirectory, "data1.pak");

            if (File.Exists(data0Path))
            {
                BuildGameFileStructure(data0Path, "data0.pak", _gameFileStructure);
            }
            if (File.Exists(data1Path))
            {
                BuildGameFileStructure(data1Path, "data1.pak", _gameFileStructure);
            }

            Console.WriteLine($" -> Found {_gameFileStructure.Count} unique file names in the game map.");
        }

        private string CleanPath(string path)
        {
            int nullCharIndex = path.IndexOf('\0');
            if (nullCharIndex >= 0)
            {
                path = path.Substring(0, nullCharIndex);
            }
            return path.Replace('/', '\\').Trim();
        }

        private List<StructureError> FindStructureErrors(List<(string OriginalPath, byte[] Content)> assets)
        {
            var errors = new List<StructureError>();
            foreach (var asset in assets)
            {
                string fileName = Path.GetFileName(asset.OriginalPath);
                string modPath = CleanPath(asset.OriginalPath);

                if (_gameFileStructure.TryGetValue(fileName, out var validLocations))
                {
                    if (!validLocations.Any(loc => loc.FullPath.Equals(modPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add(new StructureError
                        {
                            FileName = fileName,
                            IncorrectPath = modPath,
                            SuggestedPaths = validLocations,
                            IsNewFile = false
                        });
                    }
                }
                else
                {
                    errors.Add(new StructureError
                    {
                        FileName = fileName,
                        IncorrectPath = modPath,
                        SuggestedPaths = new List<FileLocationInfo>(),
                        IsNewFile = true
                    });
                }
            }
            return errors;
        }

        public List<ModFile> LoadAndProcessMods(string modsDirectory)
        {
            var allModFiles = new List<ModFile>();
            var supportedExtensions = new[] { ".pak", ".zip", ".rar", ".7z" };
            var modArchives = Directory.GetFiles(modsDirectory, "*.*", SearchOption.AllDirectories)
                .Where(f => supportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

            foreach (var modArchivePath in modArchives)
            {
                string modName;
                string fileNameOnly = Path.GetFileName(modArchivePath);
                string pakNameWithoutExt = Path.GetFileNameWithoutExtension(fileNameOnly);

                if (pakNameWithoutExt.StartsWith("data", StringComparison.OrdinalIgnoreCase) && int.TryParse(pakNameWithoutExt.AsSpan(4), out _))
                {
                    string? parentDir = Path.GetDirectoryName(modArchivePath);
                    if (parentDir != null && !parentDir.Equals(modsDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        modName = CleanModName(Path.GetFileName(parentDir));
                    }
                    else
                    {
                        modName = CleanModName(fileNameOnly);
                    }
                }
                else
                {
                    modName = CleanModName(fileNameOnly);
                }
                try
                {
                    var assetsInMod = ReadAllAssetsFromModArchive(modArchivePath);
                    if (assetsInMod.Count == 0) continue;

                    var errors = FindStructureErrors(assetsInMod);
                    int userChoice = 0;

                    if (errors.Any())
                    {
                        Console.ForegroundColor = ConsoleColor.Magenta;
                        Console.WriteLine($"\n'{modName}' Mod, appears use a different structure than the original game files.");
                        int counter = 1;
                        foreach (var error in errors)
                        {
                            if (error.IsNewFile)
                            {
                                Console.WriteLine($" {counter}.) NEW FILE: [{error.IncorrectPath}] (Not found in the original game files)");
                            }
                            else
                            {
                                string suggestions = string.Join(", ", error.SuggestedPaths.Select(p => $"[{p.SourcePak}: {p.FullPath}]"));
                                Console.WriteLine($" {counter}.) [PATH ERROR] Mod Path: [{error.IncorrectPath}] ---> Suggested Path(s): {suggestions}");
                            }
                            counter++;
                        }
                        Console.ResetColor();

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine("\nWhat would you like to do?");
                        Console.ResetColor();
                        Console.WriteLine(" (1) Auto-Fix: Moves known files to their correct paths.");
                        Console.WriteLine("    -> Works for almost all cases. (Recommended)");
                        Console.WriteLine(" (2) Use As-Is: Keeps the mod's original folder structure.");
                        Console.WriteLine("    -> Try this if Auto-Fix doesn't work.");
                        Console.WriteLine(" (3) Exclude this Mod: Skips this mod entirely.");

                        while (userChoice < 1 || userChoice > 3)
                        {
                            Console.Write("Please select an option (1, 2, or 3): ");
                            int.TryParse(Console.ReadLine()?.Trim(), out userChoice);
                        }
                    }

                    if (userChoice == 3)
                    {
                        Console.WriteLine($" -> '{modName}' will be excluded.");
                        continue;
                    }

                    foreach (var asset in assetsInMod)
                    {
                        string finalPath = asset.OriginalPath;
                        if (userChoice == 1)
                        {
                            var error = errors.FirstOrDefault(e => e.IncorrectPath == CleanPath(asset.OriginalPath));
                            if (error != null && !error.IsNewFile && error.SuggestedPaths.Any())
                            {
                                finalPath = error.SuggestedPaths.FirstOrDefault(p => p.SourcePak.Equals("data0.pak", StringComparison.OrdinalIgnoreCase))?.FullPath
                                            ?? error.SuggestedPaths.First().FullPath;
                            }
                        }

                        allModFiles.Add(new ModFile
                        {
                            FullPathInPak = finalPath.Replace('\\', '/'),
                            Content = asset.Content,
                            SourcePak = modName
                        });
                    }
                    if (errors.Any())
                    {
                        if (userChoice == 2) Console.WriteLine($" -> Use As-Is. '{modName}' will be used with its original structure.");
                        else if (userChoice == 1) Console.WriteLine($" -> Auto-fix applied for '{modName}' ");
                    }
                    else
                    {
                        Console.WriteLine($" -> '{modName}' has a correct structure and has been processed.");
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                    Console.WriteLine($"ERROR: Could not process mod '{modName}'. Reason: {ex.Message}");
                    Console.ResetColor();
                }
            }
            return allModFiles;
        }

        private List<(string OriginalPath, byte[] Content)> ReadAllAssetsFromModArchive(string archivePath)
        {
            var assets = new List<(string, byte[])>();
            string extension = Path.GetExtension(archivePath).ToLowerInvariant();

            using (var archiveStream = File.OpenRead(archivePath))
            {
                if (extension == ".pak")
                {
                    using var pakArchive = ArchiveFactory.Open(archiveStream);
                    foreach (var entry in pakArchive.Entries.Where(e => !e.IsDirectory))
                    {
                        using var ms = new MemoryStream();
                        entry.OpenEntryStream().CopyTo(ms);
                        assets.Add((entry.Key, ms.ToArray()));
                    }
                }
                else
                {
                    using var archive = ArchiveFactory.Open(archiveStream);
                    var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                    var paksInside = entries.Where(e => e.Key.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)).ToList();

                    if (paksInside.Any())
                    {
                        foreach (var pakEntry in paksInside)
                        {
                            using var pakStream = pakEntry.OpenEntryStream();
                            using var ms = new MemoryStream();
                            pakStream.CopyTo(ms);
                            ms.Position = 0;
                            using var innerPak = ArchiveFactory.Open(ms);
                            foreach (var assetEntry in innerPak.Entries.Where(e => !e.IsDirectory))
                            {
                                using var assetStream = assetEntry.OpenEntryStream();
                                using var assetMs = new MemoryStream();
                                assetStream.CopyTo(assetMs);
                                assets.Add((assetEntry.Key, assetMs.ToArray()));
                            }
                        }
                    }
                    else
                    {
                        foreach (var entry in entries)
                        {
                            using var stream = entry.OpenEntryStream();
                            using var ms = new MemoryStream();
                            stream.CopyTo(ms);
                            assets.Add((entry.Key, ms.ToArray()));
                        }
                    }
                }
            }
            return assets;
        }

        private void BuildGameFileStructure(string pakPath, string sourcePakName, Dictionary<string, List<FileLocationInfo>> structure)
        {
            try
            {
                using (var archive = ArchiveFactory.Open(pakPath))
                {
                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    {
                        string fileName = Path.GetFileName(entry.Key);
                        string fullPath = CleanPath(entry.Key);

                        if (!structure.TryGetValue(fileName, out var locationList))
                        {
                            locationList = new List<FileLocationInfo>();
                            structure[fileName] = locationList;
                        }

                        if (!locationList.Any(loc => loc.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
                        {
                            locationList.Add(new FileLocationInfo { FullPath = fullPath, SourcePak = sourcePakName });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"Warning: Could not read '{Path.GetFileName(pakPath)}'. It might be corrupted, encrypted or an unsupported format. {ex.Message}");
                Console.ResetColor();
            }
        }

        public List<ModFile> LoadAllModFilesFromPaks(string[] pakFilePaths)
        {
            var allFiles = new List<ModFile>();
            foreach (var path in pakFilePaths)
            {
                allFiles.AddRange(ReadModFilesFromSinglePak(path, Path.GetFileName(path)));
            }
            return allFiles;
        }

        private List<ModFile> ReadModFilesFromSinglePak(string pakPath, string sourceName)
        {
            var files = new List<ModFile>();
            try
            {
                using (var stream = File.OpenRead(pakPath))
                using (var pakArchive = ArchiveFactory.Open(stream))
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

        public static string CleanModName(string fileName)
        {
            string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

            var match = Regex.Match(nameWithoutExtension, @"^(.*?)(?:-[0-9])");

            if (match.Success)
            {
                return match.Groups[1].Value.TrimEnd('-', '_', ' ');
            }

            return nameWithoutExtension;
        }
    }
}