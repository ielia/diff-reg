using System;
using System.Collections.Generic;

namespace diff_reg
{
    /// <summary>One value that differs between the two files. A null Old means added, a null New removed.</summary>
    internal sealed class ValueChange
    {
        public string Name;
        public RegValue Old;
        public RegValue New;

        public bool IsAdded { get { return Old == null; } }
        public bool IsRemoved { get { return New == null; } }
        public bool IsModified { get { return Old != null && New != null; } }
    }

    /// <summary>A key present in both files whose values differ.</summary>
    internal sealed class KeyChange
    {
        public string Path;
        public List<ValueChange> Changes = new List<ValueChange>();
    }

    internal sealed class RegDiff
    {
        /// <summary>Top-most branches present only in the old file.</summary>
        public List<RegKeyNode> RemovedBranches = new List<RegKeyNode>();

        /// <summary>Top-most branches present only in the new file.</summary>
        public List<RegKeyNode> AddedBranches = new List<RegKeyNode>();

        public List<KeyChange> ChangedKeys = new List<KeyChange>();

        public bool HasDifferences
        {
            get { return RemovedBranches.Count > 0 || AddedBranches.Count > 0 || ChangedKeys.Count > 0; }
        }
    }

    internal static class RegDiffEngine
    {
        public static RegDiff Compare(RegKeyNode oldRoot, RegKeyNode newRoot)
        {
            var diff = new RegDiff();

            // An explicit stack rather than recursion: registry paths are shallow in practice, but a
            // malformed input should not be able to overflow the stack.
            var stack = new Stack<KeyValuePair<RegKeyNode, RegKeyNode>>();
            stack.Push(new KeyValuePair<RegKeyNode, RegKeyNode>(oldRoot, newRoot));

            while (stack.Count > 0)
            {
                var pair = stack.Pop();
                RegKeyNode o = pair.Key, n = pair.Value;

                if (o.Children != null)
                {
                    foreach (var entry in o.Children)
                    {
                        RegKeyNode match;
                        if (n.Children != null && n.Children.TryGetValue(entry.Key, out match))
                            stack.Push(new KeyValuePair<RegKeyNode, RegKeyNode>(entry.Value, match));
                        else
                            // Top-most by construction: we never descend past a branch that is gone.
                            diff.RemovedBranches.Add(entry.Value);
                    }
                }

                if (n.Children != null)
                {
                    foreach (var entry in n.Children)
                    {
                        if (o.Children == null || !o.Children.ContainsKey(entry.Key))
                            diff.AddedBranches.Add(entry.Value);
                    }
                }

                // Values are compared only for keys present on both sides, so changes inside an
                // added or removed branch are not reported twice.
                CompareValues(o, n, diff);
            }

            diff.RemovedBranches.Sort(ByPath);
            diff.AddedBranches.Sort(ByPath);
            diff.ChangedKeys.Sort(delegate(KeyChange a, KeyChange b)
            {
                return RegKeyNode.NameComparer.Compare(a.Path, b.Path);
            });

            return diff;
        }

        private static int ByPath(RegKeyNode a, RegKeyNode b)
        {
            return RegKeyNode.NameComparer.Compare(a.Path, b.Path);
        }

        private static void CompareValues(RegKeyNode o, RegKeyNode n, RegDiff diff)
        {
            if (o.Values == null && n.Values == null) return;

            List<ValueChange> changes = null;

            if (o.Values != null)
            {
                foreach (var entry in o.Values)
                {
                    RegValue newValue = null;
                    bool present = n.Values != null && n.Values.TryGetValue(entry.Key, out newValue);
                    if (present && entry.Value.Equals(newValue)) continue;

                    if (changes == null) changes = new List<ValueChange>();
                    changes.Add(new ValueChange
                    {
                        Name = entry.Key,
                        Old = entry.Value,
                        New = present ? newValue : null
                    });
                }
            }

            if (n.Values != null)
            {
                foreach (var entry in n.Values)
                {
                    if (o.Values != null && o.Values.ContainsKey(entry.Key)) continue;
                    if (changes == null) changes = new List<ValueChange>();
                    changes.Add(new ValueChange { Name = entry.Key, Old = null, New = entry.Value });
                }
            }

            if (changes == null) return;

            changes.Sort(delegate(ValueChange a, ValueChange b)
            {
                return RegKeyNode.CompareValueNames(a.Name, b.Name);
            });

            diff.ChangedKeys.Add(new KeyChange { Path = n.Path, Changes = changes });
        }
    }
}
