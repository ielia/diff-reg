using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace diff_reg
{
    /// <summary>Renders the diff as the three-section human-readable report.</summary>
    internal static class ReportWriter
    {
        private const int PathColumnMax = 66;

        public static void Write(TextWriter w, RegDiff diff, DetailLevel detail)
        {
            bool full = detail == DetailLevel.Full;

            WriteBranchSection(w, "REMOVED BRANCHES", diff.RemovedBranches, detail);
            WriteBranchSection(w, "ADDED BRANCHES", diff.AddedBranches, detail);
            WriteChangedValues(w, diff.ChangedKeys, full);

            if (!diff.HasDifferences)
            {
                w.WriteLine("No differences.");
                return;
            }

            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Summary: {0} removed, {1} added, {2} with value changes.",
                Plural(diff.RemovedBranches.Count, "branch", "branches"),
                diff.AddedBranches.Count.ToString("N0", CultureInfo.InvariantCulture),
                Plural(diff.ChangedKeys.Count, "key", "keys")));
        }

        private static void WriteBranchSection(TextWriter w, string title, List<RegKeyNode> branches,
                                               DetailLevel detail)
        {
            if (branches.Count == 0) return;

            w.WriteLine(string.Format(CultureInfo.InvariantCulture, "=== {0} ({1}) ===", title, branches.Count));

            if (detail == DetailLevel.Full)
            {
                foreach (var branch in branches)
                {
                    WriteBranchRecursive(w, branch, 1);
                    w.WriteLine();
                }
                return;
            }

            // Pre-compute the column so the counts line up without a second pass over the data.
            int column = 0;
            if (detail == DetailLevel.Counts)
            {
                foreach (var branch in branches)
                {
                    int len = branch.Path.Length + 2;
                    if (len > column) column = len;
                }
                if (column > PathColumnMax) column = PathColumnMax;
            }

            foreach (var branch in branches)
            {
                string bracketed = "[" + branch.Path + "]";
                if (detail == DetailLevel.Path)
                {
                    w.WriteLine("  " + bracketed);
                    continue;
                }

                int subkeys, values;
                branch.GetDescendantCounts(out subkeys, out values);
                string counts = string.Format(CultureInfo.InvariantCulture, "({0}, {1})",
                    Plural(subkeys, "subkey", "subkeys"), Plural(values, "value", "values"));

                // PadRight is a no-op past the column, so an over-long path just pushes its own
                // counts right rather than shifting the whole section.
                w.WriteLine("  " + bracketed.PadRight(column) + "  " + counts);
            }

            w.WriteLine();
        }

        private static void WriteBranchRecursive(TextWriter w, RegKeyNode node, int depth)
        {
            string indent = new string(' ', depth * 2);
            w.WriteLine(indent + "[" + node.Path + "]");

            if (node.Values != null)
            {
                string valueIndent = indent + "  ";
                foreach (string name in node.ValueNamesSorted())
                    w.WriteLine(valueIndent + DisplayName(name) + " = " + node.Values[name].ToDisplay(true));
            }

            foreach (var child in node.ChildrenSorted())
                WriteBranchRecursive(w, child, depth + 1);
        }

        private static void WriteChangedValues(TextWriter w, List<KeyChange> changed, bool full)
        {
            if (changed.Count == 0) return;

            w.WriteLine(string.Format(CultureInfo.InvariantCulture, "=== CHANGED VALUES ({0}) ===",
                Plural(changed.Count, "key", "keys")));

            foreach (var key in changed)
            {
                w.WriteLine("  [" + key.Path + "]");

                int column = 0;
                foreach (var change in key.Changes)
                {
                    int len = DisplayName(change.Name).Length;
                    if (len > column) column = len;
                }

                foreach (var change in key.Changes)
                {
                    string name = DisplayName(change.Name).PadRight(column);
                    if (change.IsAdded)
                        w.WriteLine("    + " + name + "   " + change.New.ToDisplay(full));
                    else if (change.IsRemoved)
                        w.WriteLine("    - " + name + "   " + change.Old.ToDisplay(full));
                    else
                        w.WriteLine("    ~ " + name + "   " + change.Old.ToDisplay(full)
                                    + "  ->  " + change.New.ToDisplay(full));
                }

                w.WriteLine();
            }
        }

        /// <summary>The default value prints as @, matching .reg syntax.</summary>
        private static string DisplayName(string name)
        {
            return name.Length == 0 ? "@" : "\"" + RegValue.EscapeRegText(name) + "\"";
        }

        private static string Plural(int count, string singular, string plural)
        {
            return count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural);
        }
    }
}
