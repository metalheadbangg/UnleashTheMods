using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnleashTheMods.Merger;

namespace UnleashTheMods
{
    public static class Packager
    {
        public static void PackageFiles(string sourceDirectory, string stagingDirectory, Dictionary<string, byte[]> finalFileContents, Dictionary<string, List<string>> mergeSummary)
        {
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
            Console.WriteLine($"{finalFileContents.Count} files written to temporary staging directory.");

            string finalPakPath = Path.Combine(sourceDirectory, "data7.pak");
            if (File.Exists(finalPakPath)) File.Delete(finalPakPath);

            ZipFile.CreateFromDirectory(stagingDirectory, finalPakPath, CompressionLevel.Optimal, false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nSUCCESS! All mods have been merged and saved as '{Path.GetFileName(finalPakPath)}' in the game's source folder!");
            Console.ResetColor();

            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Merge_Log.txt");
            if (File.Exists(logPath))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"A detailed merge log has been created: Merge_Log.txt");
                Console.ResetColor();
            }

            if (mergeSummary.Any())
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
                Console.WriteLine("║                      QUICK MERGE SUMMARY                 ║");
                Console.WriteLine("╠══════════════════════════════════════════════════════════╣");

                var totalMods = mergeSummary.Values.SelectMany(mods => mods).Distinct().Count();

                string modsLine = $"  Total Mods Processed: {totalMods}";
                string filesLine = $"  Total Files Merged  : {mergeSummary.Count}";

                Console.ForegroundColor = ConsoleColor.White;

                Console.Write("║");
                Console.Write(modsLine.PadRight(58));
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("║");

                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("║");
                Console.Write(filesLine.PadRight(58));
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("║");

                Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
                Console.ResetColor();
                Console.WriteLine();

                var groupedByDirectory = mergeSummary
                    .Select(kvp => new { Directory = Path.GetDirectoryName(kvp.Key), FileName = Path.GetFileName(kvp.Key), Mods = kvp.Value })
                    .GroupBy(f => f.Directory)
                    .OrderBy(g => g.Key);
            }

            Directory.Delete(stagingDirectory, true);
        }
    }
}