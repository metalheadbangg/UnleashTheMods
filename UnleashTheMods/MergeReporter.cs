using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using UnleashTheMods.Merger;

namespace UnleashTheMods
{
    public class MergeReporter
    {
        private readonly StringBuilder _log = new StringBuilder();

        public void StartNewFile(string filePath, List<string> modSources)
        {
            if (_log.Length > 0) _log.AppendLine("\n");
            _log.AppendLine("==============================================================================");
            _log.AppendLine($"MERGED FILE: {filePath}");
            _log.AppendLine("==============================================================================");
            _log.AppendLine($"\nContributing Mods:\n - {string.Join("\n - ", modSources.Distinct())}\n");
        }

        public void LogChange(string signature, string originalValue, string chosenValue, string sourceMod)
        {
            _log.AppendLine($"-- UPDATED -- Signature: '{signature}'");
            _log.AppendLine($" -> Original Value: {originalValue}");
            _log.AppendLine($" -> Chosen Value from '{sourceMod}': {chosenValue}\n");
        }

        public void LogAddition(string signature, string sourceMod)
        {
            _log.AppendLine($"-- ADDED -- Signature: '{signature}'");
            _log.AppendLine($" -> Added from mod: '{sourceMod}'\n");
        }

        public void LogDeletion(string signature, string sourceMod)
        {
            _log.AppendLine($"-- DELETED -- Signature: '{signature}'");
            _log.AppendLine($" -> Deletion was performed by mod: '{sourceMod}'\n");
        }

        public void LogBlockReplacement(string blockName, string sourceMod)
        {
            _log.AppendLine($"-- BLOCK REPLACED -- Block: '{blockName}'");
            _log.AppendLine($" -> The entire block was replaced with the version from mod: '{sourceMod}'\n");
        }

        public bool HasEntries() => _log.Length > 0;
        public string GetReport() => _log.ToString();
    }
}