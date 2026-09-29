using System;
using System.Collections.Generic;
using System.Text;

namespace diff_reg
{
    /// <summary>
    /// One node of the registry tree. Children and values are allocated lazily: in a large export
    /// most keys are leaves with a handful of values, so eagerly allocating both dictionaries per
    /// node would dominate memory.
    /// </summary>
    internal sealed class RegKeyNode
    {
        /// <summary>Registry key and value names are both case-insensitive; value data is not.</summary>
        public static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

        private static readonly Dictionary<string, string> HiveAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "HKLM", "HKEY_LOCAL_MACHINE" },
                { "HKCU", "HKEY_CURRENT_USER" },
                { "HKCR", "HKEY_CLASSES_ROOT" },
                { "HKU", "HKEY_USERS" },
                { "HKCC", "HKEY_CURRENT_CONFIG" }
            };

        private int _subkeyCount = -1;
        private int _valueCount = -1;

        public string Name { get; private set; }
        public RegKeyNode Parent { get; private set; }
        public Dictionary<string, RegKeyNode> Children { get; private set; }
        public Dictionary<string, RegValue> Values { get; private set; }

        /// <summary>True when the key appeared as its own [...] section, false when only implied as an ancestor.</summary>
        public bool IsExplicit { get; set; }

        /// <summary>True when the key appeared as [-Key], the .reg deletion directive.</summary>
        public bool IsDeleteMarker { get; set; }

        private RegKeyNode(string name, RegKeyNode parent)
        {
            Name = name;
            Parent = parent;
        }

        public static RegKeyNode CreateRoot() { return new RegKeyNode(null, null); }

        public bool IsRoot { get { return Parent == null; } }

        /// <summary>The full backslash-separated path, built by walking up to the root.</summary>
        public string Path
        {
            get
            {
                if (IsRoot) return "";
                var parts = new List<string>(8);
                for (RegKeyNode n = this; !n.IsRoot; n = n.Parent) parts.Add(n.Name);
                var sb = new StringBuilder();
                for (int i = parts.Count - 1; i >= 0; i--)
                {
                    sb.Append(parts[i]);
                    if (i > 0) sb.Append('\\');
                }
                return sb.ToString();
            }
        }

        public RegKeyNode GetOrAddChild(string name)
        {
            if (Children == null) Children = new Dictionary<string, RegKeyNode>(NameComparer);
            RegKeyNode child;
            if (!Children.TryGetValue(name, out child))
            {
                child = new RegKeyNode(name, this);
                Children.Add(name, child);
            }
            return child;
        }

        public void SetValue(string name, RegValue value)
        {
            if (Values == null) Values = new Dictionary<string, RegValue>(NameComparer);
            Values[name] = value;
        }

        public int ValueCountHere { get { return Values == null ? 0 : Values.Count; } }

        /// <summary>
        /// Walks the subtree from this node, creating intermediate nodes as needed. Ancestors created
        /// on the way are left non-explicit, which is what lets the diff report a branch whose parent
        /// was only ever implied by a deeper key.
        /// </summary>
        public RegKeyNode GetOrAddPath(string path)
        {
            RegKeyNode node = this;
            int start = 0;
            bool first = true;
            while (start <= path.Length)
            {
                int sep = path.IndexOf('\\', start);
                int end = sep < 0 ? path.Length : sep;
                if (end > start)
                {
                    string segment = path.Substring(start, end - start).Trim();
                    if (segment.Length > 0)
                    {
                        if (first)
                        {
                            string full;
                            if (HiveAliases.TryGetValue(segment, out full)) segment = full;
                        }
                        node = node.GetOrAddChild(segment);
                        first = false;
                    }
                }
                if (sep < 0) break;
                start = sep + 1;
            }
            return node;
        }

        /// <summary>
        /// Total subkeys and values beneath this node, cached. Computed on demand so the cost is
        /// bounded by the branches actually reported rather than by the whole tree.
        /// </summary>
        public void GetDescendantCounts(out int subkeys, out int values)
        {
            if (_subkeyCount < 0) ComputeCounts();
            subkeys = _subkeyCount;
            values = _valueCount;
        }

        private void ComputeCounts()
        {
            int subkeys = 0;
            int values = ValueCountHere;
            if (Children != null)
            {
                foreach (var child in Children.Values)
                {
                    int cs, cv;
                    child.GetDescendantCounts(out cs, out cv);
                    subkeys += 1 + cs;
                    values += cv;
                }
            }
            _subkeyCount = subkeys;
            _valueCount = values;
        }

        /// <summary>Children ordered by name, for deterministic output. Sorting happens only here,
        /// at output time, so parsing keeps O(1) inserts.</summary>
        public List<RegKeyNode> ChildrenSorted()
        {
            var list = new List<RegKeyNode>(Children == null ? 0 : Children.Count);
            if (Children != null) list.AddRange(Children.Values);
            list.Sort(delegate(RegKeyNode a, RegKeyNode b) { return NameComparer.Compare(a.Name, b.Name); });
            return list;
        }

        /// <summary>Value names ordered with the default value first, then alphabetically.</summary>
        public List<string> ValueNamesSorted()
        {
            var list = new List<string>(Values == null ? 0 : Values.Count);
            if (Values != null) list.AddRange(Values.Keys);
            list.Sort(CompareValueNames);
            return list;
        }

        public static int CompareValueNames(string a, string b)
        {
            bool da = a.Length == 0, db = b.Length == 0;
            if (da || db) return da == db ? 0 : (da ? -1 : 1);
            return NameComparer.Compare(a, b);
        }

        public override string ToString() { return Path; }
    }
}
