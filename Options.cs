using System;
using System.IO;

namespace diff_reg
{
    internal enum DetailLevel
    {
        /// <summary>Branch path only.</summary>
        Path,
        /// <summary>Branch path plus the subkey and value totals beneath it.</summary>
        Counts,
        /// <summary>Recursive dump of every subkey and value, with untruncated data.</summary>
        Full
    }

    internal sealed class Options
    {
        public string OldPath;
        public string NewPath;
        public string OutPath;
        public DetailLevel Detail = DetailLevel.Counts;
        public bool DetailExplicit;
        public bool RegOutput;
        public bool NoProgress;

        public static string Usage
        {
            get
            {
                return
@"diff-reg - diff two Windows Registry (.reg) exports

usage:
  diff-reg <old.reg> <new.reg> [options]

options:
  --detail <path|counts|full>  how much to show per added/removed branch (default: counts)
                                 path    the top-most key path only
                                 counts  the path plus subkey/value totals beneath it
                                 full    every subkey and value, with untruncated data
  --reg                        emit an applicable .reg patch instead of the readable report
  -o, --output <file>          write the report to a file instead of stdout
  --no-progress                suppress the load progress bar
  -h, --help                   show this help

notes:
  The report goes to stdout and progress to stderr, so the report stays pipeable.
  Values are compared as the (type, bytes) pair Windows actually stores, so different
  .reg spellings of identical data - dword:00000001 vs hex(4):01,00,00,00, hex line
  wrapping, hex digit case - are not reported as changes, while a change of type is.

  --detail is ignored with --reg. A .reg patch has no 'create branch' directive, so
  added branches must be written out in full, while [-Key] already deletes recursively
  and so needs only the top-most path. Note that [-Key] deletes whatever is on the
  target machine under that key, which may be more than the old export listed if that
  export was partial.

  With --reg, prefer -o: a file gets the UTF-16 LE BOM that regedit expects.

exit codes:
  0  no differences
  1  differences found
  2  usage or I/O error";
            }
        }

        /// <summary>Parses the command line. Returns false with an error, or false with a null error for --help.</summary>
        public static bool TryParse(string[] args, out Options options, out string error)
        {
            options = new Options();
            error = null;

            var positional = new System.Collections.Generic.List<string>(2);

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "-h":
                    case "--help":
                    case "/?":
                        return false;

                    case "--reg":
                        options.RegOutput = true;
                        break;

                    case "--no-progress":
                        options.NoProgress = true;
                        break;

                    case "--detail":
                        if (++i >= args.Length) { error = "--detail needs a value"; return false; }
                        switch (args[i].ToLowerInvariant())
                        {
                            case "path": options.Detail = DetailLevel.Path; break;
                            case "counts": options.Detail = DetailLevel.Counts; break;
                            case "full": options.Detail = DetailLevel.Full; break;
                            default:
                                error = "--detail must be path, counts or full (got: " + args[i] + ")";
                                return false;
                        }
                        options.DetailExplicit = true;
                        break;

                    case "-o":
                    case "--output":
                        if (++i >= args.Length) { error = "--output needs a file path"; return false; }
                        options.OutPath = args[i];
                        break;

                    default:
                        if (a.Length > 0 && a[0] == '-') { error = "unknown option: " + a; return false; }
                        positional.Add(a);
                        break;
                }
            }

            if (positional.Count != 2)
            {
                error = positional.Count < 2
                    ? "two .reg files are required"
                    : "expected exactly two .reg files, got " + positional.Count;
                return false;
            }

            options.OldPath = positional[0];
            options.NewPath = positional[1];

            if (!File.Exists(options.OldPath)) { error = "file not found: " + options.OldPath; return false; }
            if (!File.Exists(options.NewPath)) { error = "file not found: " + options.NewPath; return false; }

            return true;
        }
    }
}
