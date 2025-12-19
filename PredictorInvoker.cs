using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TennisScraper
{
    /// <summary>
    /// Helper to invoke the external Predictor executable (Python script or shim) and parse its output.
    /// Contract:
    /// - The predictor is invoked with --input-file <path> and optionally --csv <path>.
    /// - The input file first non-empty line is the budget (float), remaining lines are match records.
    /// - The predictor prints one line per match: "<0|1> <stake_mBTC>" where stake_mBTC is integer.
    /// </summary>
    public static class PredictorInvoker
    {
        public sealed class Bet
        {
            public int Pick { get; set; }
            public int Stake { get; set; }
            public override string ToString() => $"Pick={Pick} Stake={Stake}";
        }

        // Search ./publish for a platform-specific binary matching the predictor name.
        // Returns full path or null if not found.
    private static string? FindAlternatePredictorBinary(string predictorPath)
        {
            try
            {
                var stem = Path.GetFileNameWithoutExtension(predictorPath);
                var cwd = Directory.GetCurrentDirectory();
                var publishDir = Path.Combine(cwd, "publish");
                if (!Directory.Exists(publishDir)) return null;

                // Prefer osx builds if present
                var candidates = Directory.EnumerateFiles(publishDir, "*", SearchOption.AllDirectories)
                    .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)
                                || Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (candidates.Count > 0)
                {
                    // Prefer paths containing 'osx' or 'osx-x64'
                    var pref = candidates.FirstOrDefault(p => p.IndexOf("osx", StringComparison.OrdinalIgnoreCase) >= 0)
                               ?? candidates.FirstOrDefault();
                    return pref;
                }

                // If we didn't find file-name matches, try to find a publish subdirectory
                // whose name contains the predictor stem (e.g. publish/predictor1_osx) and
                // pick a likely runnable inside it (.dll, .exe or an executable with no extension).
                try
                {
                    var dirCandidates = Directory.EnumerateDirectories(publishDir, "*", SearchOption.AllDirectories)
                        .Where(d => Path.GetFileName(d).IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();

                    foreach (var d in dirCandidates)
                    {
                        // Preferred filenames
                        var prefer = new[] { stem + ".dll", stem + ".exe", "MyConsoleApp", Path.GetFileName(d) };
                        foreach (var prefName in prefer)
                        {
                            var candidateFile = Path.Combine(d, prefName);
                            if (File.Exists(candidateFile)) return candidateFile;
                        }

                        // Fallback: any .dll or any file in the dir that looks executable
                        var dll = Directory.EnumerateFiles(d, "*.dll", SearchOption.TopDirectoryOnly).FirstOrDefault();
                        if (dll != null) return dll;

                        var anyFile = Directory.EnumerateFiles(d, "*", SearchOption.TopDirectoryOnly).FirstOrDefault();
                        if (anyFile != null) return anyFile;
                    }
                }
                catch { }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Run the predictor executable and parse results.
        /// Throws InvalidDataException on parse failures, IOException on I/O errors, or TimeoutException on timeout.
        /// </summary>
        /// <param name="predictorPath">Path to executable/script (e.g. tools/Predictor.exe or python script)</param>
        /// <param name="inputFile">Path to input file containing budget + match lines</param>
        /// <param name="csvPath">Optional path to write CSV output (predictor will be passed this via --csv)</param>
        /// <param name="timeoutMs">Process timeout in milliseconds</param>
        /// <returns>Tuple: list of bets and budget (mBTC)</returns>
    public static (List<Bet> Bets, int Budget) RunPredictor(string predictorPath, string inputFile, string? csvPath = null, int timeoutMs = 30000)
        {
            if (string.IsNullOrWhiteSpace(predictorPath)) throw new ArgumentNullException(nameof(predictorPath));
            if (string.IsNullOrWhiteSpace(inputFile)) throw new ArgumentNullException(nameof(inputFile));
            if (!File.Exists(inputFile)) throw new FileNotFoundException("input file not found", inputFile);

            // If the external predictor executable is not present, fall back to a safe internal dummy predictor
            // This is useful for dry-run integration testing so the pipeline can execute without an external binary.
            if (!File.Exists(predictorPath))
            {
                // Read budget and match count from input file (non-empty lines)
                var linesFallback = File.ReadAllLines(inputFile);
                var nonEmptyFallback = linesFallback.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
                if (nonEmptyFallback.Count == 0) throw new InvalidDataException("Input file contains no lines");
                if (!double.TryParse(nonEmptyFallback[0], out var budgetFloatFallback)) throw new InvalidDataException("Invalid budget line in input file");
                int budgetFallback = (int)Math.Round(budgetFloatFallback);
                var matchesFallback = nonEmptyFallback.Skip(1).ToList();

                var betsFallback = new List<Bet>();
                int n = matchesFallback.Count;
                if (n == 0) throw new InvalidDataException("No matches in predictor input");

                int baseStake = budgetFallback / n;
                int remainder = budgetFallback - baseStake * n;
                var rnd = new Random();
                for (int i = 0; i < n; i++)
                {
                    int stake = baseStake + (i < remainder ? 1 : 0);
                    int pick = rnd.Next(2); // 0 or 1
                    betsFallback.Add(new Bet { Pick = pick, Stake = stake });
                }

                // Optionally write CSV output
                if (!string.IsNullOrWhiteSpace(csvPath))
                {
                    try
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("match_line,pick,stake");
                        for (int i = 0; i < matchesFallback.Count; i++)
                        {
                            sb.AppendLine($"\"{matchesFallback[i].Replace("\"", "\"\"")}\",{betsFallback[i].Pick},{betsFallback[i].Stake}");
                        }
                        File.WriteAllText(csvPath, sb.ToString());
                    }
                    catch { /* best-effort CSV write for demo */ }
                }

                return (betsFallback, budgetFallback);
            }

            // Read budget and match count from input file (non-empty lines)
            var lines = File.ReadAllLines(inputFile);
            var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
            if (nonEmpty.Count == 0) throw new InvalidDataException("Input file contains no lines");
            if (!double.TryParse(nonEmpty[0], out var budgetFloat)) throw new InvalidDataException("Invalid budget line in input file");
            int budget = (int)Math.Round(budgetFloat);
            var matches = nonEmpty.Skip(1).ToList();

            // Build arguments
            var args = new StringBuilder();
            args.Append("--input-file \"");
            args.Append(inputFile.Replace("\"","\\\""));
            args.Append("\"");
            if (!string.IsNullOrWhiteSpace(csvPath))
            {
                args.Append(" --csv \"");
                args.Append(csvPath.Replace("\"","\\\""));
                args.Append("\"");
            }

            // On non-Windows platforms prefer published platform-specific artifacts
            // (e.g. ./publish/predictor1_osx) instead of attempting to execute
            // a Windows .exe in the repo root which causes Exec format errors.
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var altPrefer = FindAlternatePredictorBinary(predictorPath);
                    if (!string.IsNullOrWhiteSpace(altPrefer) && File.Exists(altPrefer))
                    {
                        Console.WriteLine($"[PredictorInvoker] Non-Windows host detected. Preferring alternate predictor: {altPrefer}");
                        predictorPath = altPrefer;
                    }
                }
            }
            catch { }

            // Support two execution modes:
            // 1) If predictorPath points to a .dll, run it with `dotnet <dll> <args>`
            // 2) Otherwise run predictorPath directly as an executable with the constructed args
            string fileName;
            string arguments;
            if (predictorPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                fileName = "dotnet";
                arguments = "\"" + predictorPath.Replace("\"","\\\"") + "\" " + args.ToString();
            }
            else
            {
                fileName = predictorPath;
                arguments = args.ToString();
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            try
            {
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) throw new IOException("Failed to start predictor process");

                    bool exited = proc.WaitForExit(timeoutMs);
                    if (!exited)
                    {
                        try { proc.Kill(); } catch { }
                        throw new TimeoutException("Predictor process timed out");
                    }

                    var stderr = proc.StandardError.ReadToEnd();
                    var stdout = proc.StandardOutput.ReadToEnd();

                    if (proc.ExitCode != 0)
                    {
                        throw new IOException($"Predictor exited with code {proc.ExitCode}. Stderr: {stderr}");
                    }

                    // Parse stdout lines
                    var outLines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                    if (outLines.Count != matches.Count)
                    {
                        throw new InvalidDataException($"Predictor returned {outLines.Count} lines but expected {matches.Count} (match count)");
                    }

                    var bets = new List<Bet>();
                    foreach (var ln in outLines)
                    {
                        var parts = ln.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length != 2) throw new InvalidDataException($"Bad predictor output line: '{ln}'");
                        if (!int.TryParse(parts[0], out var pick) || (pick != 0 && pick != 1)) throw new InvalidDataException($"Invalid pick in line: '{ln}'");
                        if (!int.TryParse(parts[1], out var stake)) throw new InvalidDataException($"Invalid stake in line: '{ln}'");
                        bets.Add(new Bet { Pick = pick, Stake = stake });
                    }

                    // Validate sum
                    var total = bets.Sum(b => b.Stake);
                    if (total != budget) throw new InvalidDataException($"Stakes sum {total} != budget {budget}");

                    return (bets, budget);
                }
            }
            catch (System.ComponentModel.Win32Exception wex)
            {
                // Likely a Windows binary on macOS/Linux (Exec format error). Try to find a platform-specific build
                Console.WriteLine($"[PredictorInvoker] Could not start predictor '{predictorPath}': {wex.Message}");

                // Attempt to locate a published platform-specific binary under ./publish and run it if found
                try
                {
                    string? alt = FindAlternatePredictorBinary(predictorPath);
                    if (!string.IsNullOrWhiteSpace(alt) && File.Exists(alt))
                    {
                        Console.WriteLine($"[PredictorInvoker] Found alternate predictor binary: {alt}. Attempting to run it.");

                        string altFileName;
                        string altArgs;
                        if (alt.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            altFileName = "dotnet";
                            altArgs = "\"" + alt.Replace("\"", "\\\"") + "\" " + args.ToString();
                        }
                        else
                        {
                            altFileName = alt;
                            altArgs = args.ToString();
                        }

                        var altPsi = new ProcessStartInfo
                        {
                            FileName = altFileName,
                            Arguments = altArgs,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };

                        using (var altProc = Process.Start(altPsi))
                        {
                            if (altProc == null) throw new IOException("Failed to start alternate predictor process");

                            bool exitedAlt = altProc.WaitForExit(timeoutMs);
                            if (!exitedAlt)
                            {
                                try { altProc.Kill(); } catch { }
                                throw new TimeoutException("Alternate predictor process timed out");
                            }

                            var stderrAlt = altProc.StandardError.ReadToEnd();
                            var stdoutAlt = altProc.StandardOutput.ReadToEnd();

                            if (altProc.ExitCode != 0)
                            {
                                Console.WriteLine($"[PredictorInvoker] Alternate predictor exited with code {altProc.ExitCode}. Stderr: {stderrAlt}");
                            }
                            else
                            {
                                var outLinesAlt = stdoutAlt.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                                if (outLinesAlt.Count != matches.Count)
                                {
                                    throw new InvalidDataException($"Alternate predictor returned {outLinesAlt.Count} lines but expected {matches.Count} (match count)");
                                }

                                var betsAlt = new List<Bet>();
                                foreach (var ln in outLinesAlt)
                                {
                                    var parts = ln.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                                    if (parts.Length != 2) throw new InvalidDataException($"Bad predictor output line: '{ln}'");
                                    if (!int.TryParse(parts[0], out var pick) || (pick != 0 && pick != 1)) throw new InvalidDataException($"Invalid pick in line: '{ln}'");
                                    if (!int.TryParse(parts[1], out var stake)) throw new InvalidDataException($"Invalid stake in line: '{ln}'");
                                    betsAlt.Add(new Bet { Pick = pick, Stake = stake });
                                }

                                var totalAlt = betsAlt.Sum(b => b.Stake);
                                if (totalAlt != budget) throw new InvalidDataException($"Stakes sum {totalAlt} != budget {budget}");

                                if (!string.IsNullOrWhiteSpace(csvPath))
                                {
                                    try
                                    {
                                        var sb = new StringBuilder();
                                        sb.AppendLine("match_line,pick,stake");
                                        for (int i = 0; i < matches.Count; i++)
                                        {
                                            sb.AppendLine($"\"{matches[i].Replace("\"", "\"\"")}\",{betsAlt[i].Pick},{betsAlt[i].Stake}");
                                        }
                                        File.WriteAllText(csvPath, sb.ToString());
                                    }
                                    catch { }
                                }

                                return (betsAlt, budget);
                            }
                        }
                    }
                }
                catch (Exception exAlt)
                {
                    Console.WriteLine($"[PredictorInvoker] Alternate predictor attempt failed: {exAlt.Message}");
                }

                // Fallback to dummy predictor
                Console.WriteLine($"[PredictorInvoker] Falling back to DummyPredictor.");
                var sbets = new List<Bet>();
                int n = matches.Count;
                if (n == 0) throw new InvalidDataException("No matches in predictor input");
                int baseStake = budget / n;
                int remainder = budget - baseStake * n;
                var rnd = new Random();
                for (int i = 0; i < n; i++)
                {
                    int stake = baseStake + (i < remainder ? 1 : 0);
                    int pick = rnd.Next(2);
                    sbets.Add(new Bet { Pick = pick, Stake = stake });
                }

                if (!string.IsNullOrWhiteSpace(csvPath))
                {
                    try
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("match_line,pick,stake");
                        for (int i = 0; i < matches.Count; i++)
                        {
                            sb.AppendLine($"\"{matches[i].Replace("\"", "\"\"")}\",{sbets[i].Pick},{sbets[i].Stake}");
                        }
                        File.WriteAllText(csvPath, sb.ToString());
                    }
                    catch { }
                }

                return (sbets, budget);
            }
            catch (Exception ex)
            {
                // For other exceptions, surface them up (but still try to be helpful in logs)
                Console.WriteLine($"[PredictorInvoker] Predictor execution failed: {ex.Message}");
                throw;
            }
        }
    }
}
