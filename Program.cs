using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace diff_reg
{
    internal static class Program
    {
        private const int ExitNoDifferences = 0;
        private const int ExitDifferences = 1;
        private const int ExitError = 2;

        public static int Main(string[] args)
        {
            Options options;
            string error;
            if (!Options.TryParse(args, out options, out error))
            {
                if (error != null) Console.Error.WriteLine("error: " + error);
                Console.Error.WriteLine();
                Console.Error.WriteLine(Options.Usage);
                return error == null ? ExitNoDifferences : ExitError;
            }

            if (options.RegOutput && options.DetailExplicit)
            {
                Console.Error.WriteLine(
                    "warning: --detail is ignored with --reg; a patch must spell out added branches "
                    + "in full and needs only [-Key] for removals");
            }

            try
            {
                return Run(options);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return ExitError;
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return ExitError;
            }
        }

        private static int Run(Options options)
        {
            var progress = new ProgressReporter(!options.NoProgress);

            // Loaded one after the other on purpose: sequential reads beat interleaving two large
            // files across the disk, and one progress bar at a time stays readable.
            ParseResult oldFile = Load(options.OldPath, "old", progress);
            ParseResult newFile = Load(options.NewPath, "new", progress);

            RegDiff diff = RegDiffEngine.Compare(oldFile.Root, newFile.Root);

            using (TextWriter writer = OpenOutput(options))
            {
                if (options.RegOutput) RegPatchWriter.Write(writer, diff);
                else ReportWriter.Write(writer, diff, options.Detail);
                writer.Flush();
            }

            return diff.HasDifferences ? ExitDifferences : ExitNoDifferences;
        }

        private static ParseResult Load(string path, string label, ProgressReporter progress)
        {
            string name = Path.GetFileName(path);
            ParseResult result = RegFileParser.Parse(path, "Loading " + label + " " + name, progress);

            progress.Complete(string.Format(CultureInfo.InvariantCulture,
                "Loaded {0} {1}: {2}, {3} keys, {4} values ({5:F1}s)",
                label, name,
                ProgressReporter.FormatBytes(result.Bytes),
                result.KeyCount.ToString("N0", CultureInfo.InvariantCulture),
                result.ValueCount.ToString("N0", CultureInfo.InvariantCulture),
                result.Elapsed.TotalSeconds));

            return result;
        }

        private static TextWriter OpenOutput(Options options)
        {
            if (options.OutPath == null)
                return new NonClosingWriter(Console.Out);

            // regedit expects UTF-16 LE with a BOM; the readable report is plain UTF-8.
            Encoding encoding = options.RegOutput
                ? (Encoding)new UnicodeEncoding(false, true)
                : new UTF8Encoding(false);

            return new StreamWriter(options.OutPath, false, encoding);
        }

        /// <summary>Lets the output be disposed uniformly without closing the console.</summary>
        private sealed class NonClosingWriter : TextWriter
        {
            private readonly TextWriter _inner;

            public NonClosingWriter(TextWriter inner) { _inner = inner; }

            public override Encoding Encoding { get { return _inner.Encoding; } }
            public override void Write(char value) { _inner.Write(value); }
            public override void Write(string value) { _inner.Write(value); }
            public override void WriteLine(string value) { _inner.WriteLine(value); }
            public override void Flush() { _inner.Flush(); }
            protected override void Dispose(bool disposing) { Flush(); }
        }
    }
}
