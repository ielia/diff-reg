using System;
using System.IO;
using System.Text;

namespace diff_reg
{
    /// <summary>
    /// Renders the diff as a .reg file that transforms old into new.
    ///
    /// The two directions are asymmetric by necessity. [-Key] deletes recursively, so a removed
    /// branch needs only its top-most path. Additions have no equivalent shorthand - a key exists
    /// only because a [Key] block declares it - so an added branch must be written out in full,
    /// including bare blocks for keys that hold no values.
    /// </summary>
    internal static class RegPatchWriter
    {
        public const string Header = "Windows Registry Editor Version 5.00";

        public static void Write(TextWriter w, RegDiff diff)
        {
            var sb = new StringBuilder(4096);

            sb.Append(Header).Append("\r\n\r\n");

            foreach (var branch in diff.RemovedBranches)
                sb.Append("[-").Append(branch.Path).Append("]\r\n\r\n");

            foreach (var branch in diff.AddedBranches)
            {
                AppendSubtree(sb, branch);
                Flush(w, sb);
            }

            foreach (var key in diff.ChangedKeys)
            {
                sb.Append('[').Append(key.Path).Append("]\r\n");
                foreach (var change in key.Changes)
                {
                    if (change.IsRemoved) RegValue.DeleteMarker.AppendRegLine(sb, change.Name);
                    else change.New.AppendRegLine(sb, change.Name);
                }
                sb.Append("\r\n");
                Flush(w, sb);
            }

            w.Write(sb.ToString());
        }

        /// <summary>
        /// Writes every key in the subtree, parents before children. Keys that exist only as implied
        /// ancestors are skipped unless they are leaves: regedit creates missing parents when it
        /// imports a deeper key, but an empty leaf key only comes into being via its own block.
        /// </summary>
        private static void AppendSubtree(StringBuilder sb, RegKeyNode node)
        {
            bool isLeaf = node.Children == null || node.Children.Count == 0;
            if (node.IsExplicit || node.Values != null || isLeaf)
            {
                sb.Append('[').Append(node.Path).Append("]\r\n");
                if (node.Values != null)
                {
                    foreach (string name in node.ValueNamesSorted())
                        node.Values[name].AppendRegLine(sb, name);
                }
                sb.Append("\r\n");
            }

            foreach (var child in node.ChildrenSorted())
                AppendSubtree(sb, child);
        }

        /// <summary>Drains the builder periodically so a large patch does not sit entirely in memory.</summary>
        private static void Flush(TextWriter w, StringBuilder sb)
        {
            if (sb.Length < 64 * 1024) return;
            w.Write(sb.ToString());
            sb.Length = 0;
        }
    }
}
