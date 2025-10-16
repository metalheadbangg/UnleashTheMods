using System.Collections.Generic;
using System.Linq;

namespace UnleashTheMods.Merger
{
    public static class MergeSessionState
    {
        private static string? _fileScope;
        private static HashSet<string>? _conflictSet;
        private static string? _preferredSource;

        public static void SetDecision(string filePath, IEnumerable<string> conflictSources, string preferredSource)
        {
            _fileScope = filePath;
            _conflictSet = new HashSet<string>(conflictSources.OrderBy(s => s));
            _preferredSource = preferredSource;
        }

        public static string? GetDecision(string filePath, IEnumerable<string> conflictSources)
        {
            var currentConflictSet = new HashSet<string>(conflictSources.OrderBy(s => s));

            if (_fileScope == filePath && _conflictSet != null && _conflictSet.SetEquals(currentConflictSet))
            {
                return _preferredSource;
            }
            Reset();
            return null;
        }

        public static void Reset()
        {
            _fileScope = null;
            _conflictSet = null;
            _preferredSource = null;
        }
    }
}