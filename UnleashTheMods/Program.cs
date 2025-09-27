using System;
using System.Collections.Generic;
using System.IO;

class Program
{
    static void Main(string[] args)
    {
        Console.Title = "Unleash The Mods - Mod Merge Utility V5.0";
        Console.WriteLine("Unleash The Mods - Mod Merge Utility");
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

        var modCacher = new ModCacher(gamePakPath);
        var originalFiles = modCacher.LoadAllModFilesFromPaks(Directory.GetFiles(sourceDirectory, "*.pak"));
        Console.WriteLine($"{originalFiles.Count} total files loaded from original game packages.");

        var moddedFiles = modCacher.LoadAndProcessMods(modsDirectory);

        if (moddedFiles.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\nWarning: Couldn't find a compatible mod file (.pak, .zip, .rar, .7z) to merge in the 'mods' folder.");
            Console.ResetColor();
            Console.WriteLine("\nÇıkmak için herhangi bir tuşa basın...");
            Console.ReadKey();
            return;
        }

        Console.WriteLine("\n--- Merging Initializing ---");
        var resolver = new ConflictResolver(originalFiles);
        var (finalFileContents, mergeSummary) = resolver.Resolve(moddedFiles);

        Packager.PackageFiles(sourceDirectory, stagingDirectory, finalFileContents, mergeSummary);

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }
}
