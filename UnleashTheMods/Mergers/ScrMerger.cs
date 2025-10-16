using SharpCompress.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UnleashTheMods.Merger
{
    public interface INodeContent
    {
        string GetSignature();
        int OriginalOrder { get; set; }
    }

    public class ParameterContent : INodeContent
    {
        public string Value { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public int OriginalOrder { get; set; }
        public string GetSignature() => Key;
    }

    public class ChildNodeContent : INodeContent
    {
        public ScrMerger.ScriptNode Node { get; set; }
        public int OriginalOrder { get; set; }
        public string GetSignature() => Node.FullPathKey;
    }

    public enum DeletionDecision { AutoKeep, AutoDelete, Conflict }
    public interface IScriptMerger { (string MergedContent, string? UpdatedPreferredSource) Merge(ModFile original, List<ModFile> mods, string? currentPreferredSource, MergeReporter reporter); }

    public class ScrMerger : IScriptMerger
    {
        public class ScriptNode
        {
            public ScriptNode? Parent { get; set; }
            public string Signature { get; set; } = string.Empty;
            public string FullPathKey { get; set; } = string.Empty;
            public List<string> HeaderComments { get; set; } = new List<string>();
            public List<INodeContent> Contents { get; set; } = new List<INodeContent>();
            public int OriginalOrder { get; set; }
        }

        public (string MergedContent, string? UpdatedPreferredSource) Merge(ModFile original, List<ModFile> mods, string? currentPreferredSource, MergeReporter reporter)
        {
            reporter.StartNewFile(original.FullPathInPak, mods.Select(m => m.SourcePak).ToList());
            var encoding = new UTF8Encoding(false);

            var originalTree = ParseToTree(encoding.GetString(original.Content));
            var modTrees = mods.Select(mod => (Source: mod.SourcePak, Tree: ParseToTree(encoding.GetString(mod.Content)))).ToList();

            var mergedTree = RecursiveMerge(originalTree, modTrees, reporter, original.FullPathInPak);
            var finalContent = RebuildScriptFromTree(mergedTree);
            return (finalContent, null);
        }

        private ScriptNode ParseToTree(string scriptContent)
        {
            var lines = scriptContent.Replace("\r\n", "\n").Split('\n');
            var root = new ScriptNode { Signature = "ROOT", FullPathKey = "ROOT" };
            var nodeStack = new Stack<ScriptNode>();
            nodeStack.Push(root);
            int orderCounter = 0;

            bool inBlockComment = false;
            StringBuilder? currentBlockComment = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                var currentNode = nodeStack.Peek();
                string trimmedLine = line.Trim();

                if (inBlockComment)
                {
                    currentBlockComment!.AppendLine(line);
                    if (trimmedLine.EndsWith("*/"))
                    {
                        string fullCommentBlock = currentBlockComment.ToString();
                        currentNode.Contents.Add(new ParameterContent { Value = fullCommentBlock, Key = $"LITERAL_{orderCounter}", OriginalOrder = orderCounter++ });
                        inBlockComment = false;
                        currentBlockComment = null;
                    }
                    continue;
                }

                if (trimmedLine.StartsWith("}"))
                {
                    if (nodeStack.Count > 1)
                    {
                        nodeStack.Pop();
                    }
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    currentNode.Contents.Add(new ParameterContent { Value = line, Key = $"BLANK_{orderCounter}", OriginalOrder = orderCounter++ });
                    continue;
                }

                if (trimmedLine.StartsWith("//"))
                {
                    currentNode.Contents.Add(new ParameterContent { Value = line, Key = $"LITERAL_{orderCounter}", OriginalOrder = orderCounter++ });
                    continue;
                }

                if (trimmedLine.StartsWith("/*"))
                {
                    if (trimmedLine.Contains("*/"))
                    {
                        currentNode.Contents.Add(new ParameterContent { Value = line, Key = $"LITERAL_{orderCounter}", OriginalOrder = orderCounter++ });
                    }
                    else
                    {
                        inBlockComment = true;
                        currentBlockComment = new StringBuilder();
                        currentBlockComment.AppendLine(line);
                    }
                    continue;
                }

                bool isBlock = trimmedLine.EndsWith("{") || (i + 1 < lines.Length && lines[i + 1].Trim() == "{");

                if (isBlock)
                {
                    string localSignature = GetIdentifierSignature(trimmedLine);
                    string proposedKey = $"{currentNode.FullPathKey}_{localSignature}";

                    var regex = new Regex($"^{Regex.Escape(proposedKey)}(_\\d+)?$");
                    int instanceCount = currentNode.Contents.OfType<ChildNodeContent>().Count(c => regex.IsMatch(c.GetSignature()));

                    string finalFullPathKey = proposedKey;
                    if (instanceCount > 0)
                    {
                        finalFullPathKey = $"{proposedKey}_{instanceCount + 1}";
                    }

                    var newNode = new ScriptNode
                    {
                        Signature = trimmedLine.TrimEnd().TrimEnd('{').Trim(),
                        Parent = currentNode,
                        OriginalOrder = orderCounter++,
                        FullPathKey = finalFullPathKey
                    };

                    currentNode.Contents.Add(new ChildNodeContent { Node = newNode, OriginalOrder = newNode.OriginalOrder });
                    nodeStack.Push(newNode);

                    if (!trimmedLine.EndsWith("{") && (i + 1 < lines.Length && lines[i + 1].Trim() == "{"))
                    {
                        i++;
                    }
                }
                else
                {
                    string localSignature;
                    int nestingLevel = nodeStack.Count;
                    if (nestingLevel > 2)
                    {
                        localSignature = GetFunctionNameOnly(trimmedLine);
                    }
                    else
                    {
                        localSignature = GetIdentifierSignature(trimmedLine);
                    }

                    string proposedKey = $"{currentNode.FullPathKey}_{localSignature}";
                    var regex = new Regex($"^{Regex.Escape(proposedKey)}(_\\d+)?$");
                    int instanceCount = currentNode.Contents.OfType<ParameterContent>().Count(c => regex.IsMatch(c.GetSignature()));

                    string finalKey = proposedKey;
                    if (instanceCount > 0)
                    {
                        finalKey = $"{proposedKey}_{instanceCount + 1}";
                    }

                    var param = new ParameterContent { Value = line, Key = finalKey, OriginalOrder = orderCounter++ };
                    currentNode.Contents.Add(param);
                }
            }
            return root;
        }


        public static string GetIdentifierSignature(string line)
        {
            string cleanLine = line.Trim();

            var funcMatch = Regex.Match(cleanLine, @"^(\w+)\s*\((.*)\)");
            if (!funcMatch.Success) return cleanLine;

            string functionName = funcMatch.Groups[1].Value;
            string allParamsString = funcMatch.Groups[2].Value;

            int lastParenIndex = allParamsString.LastIndexOf(')');
            if (lastParenIndex != -1) allParamsString = allParamsString.Substring(0, lastParenIndex);
            if (string.IsNullOrWhiteSpace(allParamsString)) return functionName;

            var parameters = Regex.Split(allParamsString, @",(?=(?:[^""]*""[^""]*"")*[^""]*$)")
                                  .Select(p => p.Trim().Replace("\"", "")).ToArray();

            if (parameters.Length == 0) return functionName;

            var forceValueBasedFuncs = new HashSet<string> { "MotionTrailFx" };
            if (forceValueBasedFuncs.Contains(functionName))
            {
                return functionName;
            }

            var keyBasedFuncs = new HashSet<string>
            {
                "Param", "VarFloat", "VarVec3", "VarString",
                "LockpickDifficulty", "SafeDifficulty", "FrequncyDifficulty",
                "Preset"
            };

            if (keyBasedFuncs.Contains(functionName))
            {
                string firstParam = parameters.FirstOrDefault();
                if (string.IsNullOrEmpty(firstParam))
                {
                    return functionName;
                }
                string keyPart = firstParam.Split(',').FirstOrDefault()?.Trim();

                return $"{functionName}_{keyPart}";
            }

            var identityParts = new List<string> { functionName };
            foreach (var param in parameters)
            {
                identityParts.Add(param);
            }
            return string.Join("_", identityParts);
        }

        private ScriptNode RecursiveMerge(ScriptNode originalNode, List<(string Source, ScriptNode Tree)> modNodes, MergeReporter reporter, string filePath)
        {
            var emptyingMods = modNodes.Where(m => m.Tree.Contents.Count == 0).ToList();
            if (emptyingMods.Any())
            {
                var conflictingMods = modNodes.Where(m => m.Tree.Contents.Count > 0 && RebuildScriptFromTree(m.Tree) != RebuildScriptFromTree(originalNode)).ToList();
                if (conflictingMods.Any())
                {
                    bool userWantsToEmpty = HandleEmptyVsModifiedBlockConflict(originalNode.Signature, emptyingMods, conflictingMods, filePath);
                    if (userWantsToEmpty)
                    {
                        reporter.LogDeletion($"Entire content of block '{originalNode.Signature}'", emptyingMods.First().Source);
                        return new ScriptNode { Signature = originalNode.Signature, HeaderComments = new List<string>(originalNode.HeaderComments) };
                    }
                    modNodes = modNodes.Except(emptyingMods).ToList();
                }
                else
                {
                    reporter.LogDeletion($"Entire content of block '{originalNode.Signature}'", emptyingMods.First().Source);
                    return new ScriptNode { Signature = originalNode.Signature, HeaderComments = new List<string>(originalNode.HeaderComments) };
                }
            }

            var mergedNode = new ScriptNode { Signature = originalNode.Signature, HeaderComments = new List<string>(originalNode.HeaderComments), FullPathKey = originalNode.FullPathKey };

            var allModContentsBySig = modNodes
                .SelectMany(mod => mod.Tree.Contents.Select(c => new { Mod = mod, Content = c }))
                .GroupBy(x => x.Content.GetSignature())
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var originalContent in originalNode.Contents.OrderBy(c => c.OriginalOrder))
            {
                var signature = originalContent.GetSignature();
                var allModContentForSig = modNodes
                    .Select(m => (Source: m.Source, Content: m.Tree.Contents.FirstOrDefault(c => c.GetSignature() == signature), FullModTree: m.Tree))
                    .ToList();

                var modsThatOmitSignature = allModContentForSig.Where(m => m.Content == null).ToList();
                var modsThatActivelyModified = allModContentForSig.Where(m => m.Content != null && RebuildContent(m.Content).Trim() != RebuildContent(originalContent).Trim()).ToList();

                if (modsThatOmitSignature.Any())
                {
                    if (modsThatActivelyModified.Any())
                    {
                        bool shouldDelete;
                        if (signature.StartsWith("LITERAL_") || signature.StartsWith("BLANK_"))
                        {
                            shouldDelete = false;
                        }
                        else
                        {
                            var deletingSources = modsThatOmitSignature.Select(m => m.Source).ToList();
                            var modifyingSources = modsThatActivelyModified.Select(m => (m.Source, m.Content, m.FullModTree)).ToList();
                            shouldDelete = HandleDeletionConflict(signature, deletingSources, modifyingSources, filePath);
                        }

                        if (shouldDelete)
                        {
                            reporter.LogDeletion(signature, modsThatOmitSignature.First().Source);
                            continue;
                        }
                    }
                    else
                    {
                        reporter.LogDeletion(signature, modsThatOmitSignature.First().Source);
                        continue;
                    }
                }

                var modContentsWithSignature = allModContentForSig.Where(mc => mc.Content != null).ToList();

                if (originalContent is ParameterContent originalParam)
                {
                    var actualChanges = modContentsWithSignature.Where(mc => mc.Content is ParameterContent && GetCodePartOnly((mc.Content as ParameterContent)!.Value) != GetCodePartOnly(originalParam.Value)).Select(mc => ((mc.Content as ParameterContent)!, mc.Source)).ToList();
                    if (!actualChanges.Any())
                    {
                        mergedNode.Contents.Add(originalContent);
                    }
                    else
                    {
                        var distinctChanges = actualChanges.GroupBy(c => GetCodePartOnly(c.Item1.Value)).Select(g => (Param: g.First().Item1, Sources: g.Select(c => c.Source).ToList())).ToList();
                        ParameterContent chosenParam;
                        string sourceForComment;
                        if (distinctChanges.Count == 1)
                        {
                            chosenParam = distinctChanges[0].Param;
                            sourceForComment = string.Join(", ", distinctChanges[0].Sources);
                        }
                        else
                        {
                            chosenParam = HandleUserChoiceForParams(distinctChanges, originalParam, out sourceForComment, filePath);
                        }

                        bool isMultiLineComment = GetCodePartOnly(originalParam.Value).StartsWith("/*") && originalParam.Value.Contains('\n');

                        if (isMultiLineComment)
                        {
                            reporter.LogChange(signature, originalParam.Value.Trim(), chosenParam.Value.Trim(), sourceForComment);

                            string safeComment = $"// -- [UTM Merge] This block was updated by {sourceForComment} --";
                            string finalValue = safeComment + Environment.NewLine + chosenParam.Value;

                            mergedNode.Contents.Add(new ParameterContent { Key = chosenParam.Key, OriginalOrder = chosenParam.OriginalOrder, Value = finalValue });
                        }
                        else
                        {
                            reporter.LogChange(signature, originalParam.Value.Trim(), chosenParam.Value.Trim(), sourceForComment);
                            mergedNode.Contents.Add(new ParameterContent { Key = chosenParam.Key, OriginalOrder = chosenParam.OriginalOrder, Value = AppendUtmComment(chosenParam.Value, $"[UTM Merge] updated from {sourceForComment} (OG Value: {originalParam.Value.Trim()})") });
                        }
                    }
                }
                else if (originalContent is ChildNodeContent originalChild)
                {
                    var modChildren = modContentsWithSignature.Select(m => (m.Source, Tree: (m.Content as ChildNodeContent)!.Node)).ToList();
                    if (modChildren.Any())
                    {
                        var mergedChildNode = RecursiveMerge(originalChild.Node, modChildren, reporter, filePath);
                        mergedNode.Contents.Add(new ChildNodeContent { Node = mergedChildNode, OriginalOrder = originalChild.OriginalOrder });
                    }
                    else
                    {
                        mergedNode.Contents.Add(originalContent);
                    }
                }
            }

            var addedSignatures = allModContentsBySig.Keys.Where(sig => !originalNode.Contents.Any(c => c.GetSignature() == sig)).ToList();
            var orderedAddedSignatures = addedSignatures
                .Select(sig => (Signature: sig, Order: allModContentsBySig[sig].Min(c => c.Content.OriginalOrder)))
                .OrderBy(x => x.Order)
                .ToList();

            foreach (var addedSigInfo in orderedAddedSignatures)
            {
                var signature = addedSigInfo.Signature;
                var modVersions = allModContentsBySig[signature];
                var distinctNewContents = modVersions.GroupBy(m => GetCodePartOnly(RebuildContent(m.Content)))
                    .Select(g => (Content: g.First().Content, Sources: g.Select(s => s.Mod.Source).ToList()))
                    .ToList();

                INodeContent contentToAdd;
                string sourceForComment;

                if (distinctNewContents.Count > 1 && distinctNewContents.First().Content is ParameterContent)
                {
                    var changesForHandler = distinctNewContents.Select(d => ((d.Content as ParameterContent)!, d.Sources)).ToList();
                    var fakeOriginal = new ParameterContent { Key = signature, Value = "N/A (Conflict on newly added item)" };
                    contentToAdd = HandleUserChoiceForParams(changesForHandler, fakeOriginal, out sourceForComment, filePath);
                }
                else
                {
                    contentToAdd = distinctNewContents.First().Content;
                    sourceForComment = string.Join(", ", distinctNewContents.First().Sources);
                }

                reporter.LogAddition(signature, sourceForComment);

                INodeContent contentToAddWithComment;
                if (contentToAdd is ParameterContent paramToAdd)
                {
                    bool isMultiLineComment = GetCodePartOnly(paramToAdd.Value).StartsWith("/*") && paramToAdd.Value.Contains('\n');

                    if (isMultiLineComment)
                    {
                        string safeComment = $"// -- [UTM Merge] This block was added by {sourceForComment} --";
                        string finalValue = safeComment + Environment.NewLine + paramToAdd.Value;
                        contentToAddWithComment = new ParameterContent { Value = finalValue, Key = paramToAdd.Key, OriginalOrder = paramToAdd.OriginalOrder };
                    }
                    else
                    {
                        bool isCommentOrBlank = paramToAdd.Key.StartsWith("BLANK_") || paramToAdd.Key.StartsWith("LITERAL_") || paramToAdd.Value.Trim().StartsWith("//") || paramToAdd.Value.Trim().StartsWith("/*");
                        contentToAddWithComment = isCommentOrBlank ? paramToAdd : new ParameterContent { Value = AppendUtmComment(paramToAdd.Value, $"[UTM Merge] added from {sourceForComment}"), Key = paramToAdd.Key, OriginalOrder = paramToAdd.OriginalOrder };
                    }
                }
                else if (contentToAdd is ChildNodeContent newChild)
                {
                    newChild.Node.HeaderComments.Insert(0, $"// -- [UTM Merge] Block added from {sourceForComment} --");
                    contentToAddWithComment = newChild;
                }
                else
                {
                    contentToAddWithComment = contentToAdd;
                }

                string referenceModSource = sourceForComment.Split(',').First().Trim();
                var sourceModContext = modNodes.First(m => m.Source == referenceModSource).Tree;

                int modLineIndex = sourceModContext.Contents.IndexOf(contentToAdd);

                int insertPosition = -1;
                for (int i = modLineIndex - 1; i >= 0; i--)
                {
                    var precedingModContent = sourceModContext.Contents[i];
                    int finalIndex = mergedNode.Contents.FindIndex(c => c.GetSignature() == precedingModContent.GetSignature());
                    if (finalIndex != -1)
                    {
                        insertPosition = finalIndex;
                        break;
                    }
                }

                if (insertPosition != -1)
                {
                    mergedNode.Contents.Insert(insertPosition + 1, contentToAddWithComment);
                }
                else
                {
                    bool isHeaderContent = contentToAdd is ParameterContent p && (p.Value.Trim().StartsWith("import") || p.Value.Trim().StartsWith("export"));
                    if (isHeaderContent)
                    {
                        mergedNode.Contents.Insert(0, contentToAddWithComment);
                    }
                    else
                    {
                        mergedNode.Contents.Add(contentToAddWithComment);
                    }
                }
            }

            return mergedNode;
        }
        public static string GetUniqueKeyForDlcBlock(ScriptNode node)
        {
            foreach (var content in node.Contents)
            {
                if (content is ParameterContent param)
                {
                    var idMatch = Regex.Match(param.Value.Trim(), @"^ID\s*\(\s*(\d+)\s*\)");
                    if (idMatch.Success) return $"{node.FullPathKey}_ID_{idMatch.Groups[1].Value}";
                }
            }
            return $"{node.FullPathKey}_{Guid.NewGuid()}";
        }

        private string RebuildScriptFromTree(ScriptNode node, int indentLevel = 0)
        {
            var sb = new StringBuilder();
            string indent = new string('\t', indentLevel);
            if (node.Signature != "ROOT")
            {
                foreach (var comment in node.HeaderComments) sb.AppendLine(indent + comment);
                sb.AppendLine(indent + node.Signature);
                sb.AppendLine(indent + "{");
            }

            string contentIndent = node.Signature == "ROOT" ? "" : indent + "\t";

            foreach (var content in node.Contents)
            {
                if (content is ParameterContent param)
                {
                    sb.AppendLine(string.IsNullOrWhiteSpace(param.Value) ? "" : param.Value);
                }
                else if (content is ChildNodeContent child)
                {
                    sb.Append(RebuildScriptFromTree(child.Node, node.Signature == "ROOT" ? indentLevel : indentLevel + 1));
                }
            }

            if (node.Signature != "ROOT")
            {
                sb.AppendLine(indent + "}");
            }
            return sb.ToString();
        }

        private string RebuildContent(INodeContent content)
        {
            if (content is ParameterContent param) return param.Value;
            if (content is ChildNodeContent child) return RebuildScriptFromTree(child.Node);
            return string.Empty;
        }

        private ParameterContent HandleUserChoiceForParams(List<(ParameterContent Param, List<string> Sources)> distinctChanges, ParameterContent originalParam, out string sourceForComment, string filePath)
        {
            var allSourcesInConflict = distinctChanges.SelectMany(c => c.Sources).Distinct().ToList();
            var autoDecision = MergeSessionState.GetDecision(filePath, allSourcesInConflict);

            if (autoDecision != null)
            {
                var chosenChange = distinctChanges.FirstOrDefault(c => c.Sources.Contains(autoDecision));
                if (chosenChange.Param != null)
                {
                    sourceForComment = autoDecision;
                    return chosenChange.Param;
                }
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[CHOICE REQUIRED] In file '{filePath}', conflict for key '{originalParam.Key}'!");
            Console.ResetColor();
            for (int j = 0; j < distinctChanges.Count; j++)
            {
                string sources = string.Join(", ", distinctChanges[j].Sources);
                Console.Write($"    {j + 1}. ({sources}): ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(distinctChanges[j].Param.Value.Trim());
                Console.ResetColor();
            }

            int choice = -1;
            bool yesToAll = false;
            while (choice < 1 || choice > distinctChanges.Count)
            {
                Console.Write($"Please select the version to use (1-{distinctChanges.Count}) or e.g. '1y' for 'Yes to all': ");
                string? input = Console.ReadLine()?.Trim().ToLower();
                if (input != null && input.EndsWith("y"))
                {
                    yesToAll = true;
                    input = input.TrimEnd('y');
                }
                int.TryParse(input, out choice);
            }

            var chosen = distinctChanges[choice - 1];
            sourceForComment = chosen.Sources.First();

            if (yesToAll)
            {
                MergeSessionState.SetDecision(filePath, allSourcesInConflict, sourceForComment);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  -> Choice '{sourceForComment}' applied for this and all subsequent conflicts in this file.");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  -> Choice applied.");
                Console.ResetColor();
            }

            return chosen.Param;
        }

        private bool HandleDeletionConflict(string signature, List<string> deletingModSources, List<(string Source, INodeContent Content, ScriptNode FullModTree)> modifyingMods, string filePath)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[CHOICE REQUIRED] In file '{filePath}', deletion conflict for signature: '{signature}'");
            Console.ResetColor();
            Console.Write($" -> The following mods want to DELETE this parameters: ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(string.Join(", ", deletingModSources.Distinct()));
            Console.ResetColor();
            Console.Write($" -> The following mods want to CHANGE this parameters: ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(string.Join(", ", modifyingMods.Select(m => m.Source).Distinct()));
            Console.ResetColor();

            Console.WriteLine("\nWhat would you like to do?");
            Console.WriteLine("  1. Keep the modified versions (you may be asked to choose between them).");
            Console.WriteLine("  2. Delete the content.");

            int choice = -1;
            while (choice < 1 || choice > 2)
            {
                Console.Write("Please select an option (1-2): ");
                int.TryParse(Console.ReadLine(), out choice);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  -> Choice applied.");
            Console.ResetColor();
            return choice == 2;
        }

        private bool HandleEmptyVsModifiedBlockConflict(string signature, List<(string Source, ScriptNode Tree)> emptyingMods, List<(string Source, ScriptNode Tree)> modifyingMods, string filePath)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[CHOICE REQUIRED] In file '{filePath}', Empty vs. Modified Block conflict for: '{signature}'");
            Console.ResetColor();
            Console.Write($" -> The following mods want to EMPTY this block's parameters: ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(string.Join(", ", emptyingMods.Select(m => m.Source).Distinct()));
            Console.ResetColor();
            Console.Write($" -> The following mods want to MODIFY this block's parameters: ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(string.Join(", ", modifyingMods.Select(m => m.Source).Distinct()));
            Console.ResetColor();

            Console.WriteLine("\nWhat would you like to do?");
            Console.WriteLine("  1. Keep the modified versions (merging will continue with them).");
            Console.WriteLine("  2. Empty the block's content.");

            int choice = -1;
            while (choice < 1 || choice > 2)
            {
                Console.Write("Please select an option (1-2): ");
                int.TryParse(Console.ReadLine(), out choice);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  -> Choice applied.");
            Console.ResetColor();
            return choice == 2;
        }

        private DeletionDecision DecideDeletionAction(INodeContent originalContent, List<(string Source, INodeContent Content, ScriptNode FullModTree)> allModContentForSig)
        {
            var modsThatOmitSignature = allModContentForSig.Where(m => m.Content == null).ToList();

            if (!modsThatOmitSignature.Any())
            {
                return DeletionDecision.AutoKeep;
            }

            var modsThatActivelyModified = allModContentForSig
                .Where(m => m.Content != null && RebuildContent(m.Content).Trim() != RebuildContent(originalContent).Trim())
                .ToList();

            if (modsThatActivelyModified.Any())
            {
                return DeletionDecision.Conflict;
            }

            return DeletionDecision.AutoDelete;
        }

        private bool IsBlockSignature(string trimmedLine)
        {
            if (trimmedLine.Contains("{") && trimmedLine.Contains("}")) return false;
            if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("//")) return false;

            bool isFunctionLike = trimmedLine.Contains("(") && trimmedLine.Contains(")");
            bool isSubroutine = trimmedLine.StartsWith("sub ");
            return isFunctionLike || isSubroutine;
        }

        private string GetFunctionNameOnly(string line)
        {
            var match = Regex.Match(line.Trim(), @"^(\w+)");
            return match.Success ? match.Value : line.Trim();
        }


        private string? ExtractValueFromLine(string line) { if (string.IsNullOrEmpty(line)) return null; var match = Regex.Match(line, @"\((.+)\)"); return match.Success ? match.Groups[1].Value.Trim() : line.Trim(); }

        private static string AppendUtmComment(string line, string comment)
        {
            string trimmedLine = line.TrimEnd();

            if (trimmedLine.Contains("//"))
            {
                return $"{trimmedLine} -- {comment}";
            }
            else
            {
                return $"{trimmedLine}\t// {comment}";
            }
        }
        private string GetCodePartOnly(string line)
        {
            string trimmedLine = line.Trim();
            int commentIndex = trimmedLine.IndexOf("//");
            if (commentIndex == 0) return string.Empty;
            if (commentIndex > -1) return trimmedLine.Substring(0, commentIndex).TrimEnd();
            return trimmedLine;
        }
    }
}