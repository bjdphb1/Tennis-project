using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TennisScraper
{
    public static class SchedulerService
    {
        /// <summary>
        /// Create cycles from MatchModel list by grouping up to cycleSize matches per cycle.
        /// Persist cycles to CycleStore. Returns created cycles.
        /// </summary>
        public static List<CycleModel> CreateAndPersistCyclesFromMatches(List<Models.MatchModel> matches, int cycleSize = 10, int budgetMbtc = 100)
        {
            var res = new List<CycleModel>();
            if (matches == null || matches.Count == 0) return res;

            // Sort by InitCutOffTime ascending
            var ordered = matches.OrderBy(m => m.InitCutOffTime).ToList();
            for (int i = 0; i < ordered.Count; i += cycleSize)
            {
                var chunk = ordered.Skip(i).Take(cycleSize).ToList();
                var cycle = new CycleModel
                {
                    Id = Guid.NewGuid().ToString("N"),
                    EarliestUnix = chunk.Min(m => m.InitCutOffTime),
                    MatchIds = chunk.Select(m => m.MatchId).ToList(),
                    BudgetMbtc = budgetMbtc,
                    Status = "prepared"
                };
                CycleStore.Add(cycle);
                res.Add(cycle);
            }
            return res;
        }

        /// <summary>
        /// Create cycles from MatchModel list without persisting them. Useful for dry-run previews.
        /// </summary>
        public static List<CycleModel> CreateCyclesFromMatches(List<Models.MatchModel> matches, int cycleSize = 10, int budgetMbtc = 100)
        {
            var res = new List<CycleModel>();
            if (matches == null || matches.Count == 0) return res;

            // Sort by InitCutOffTime ascending
            var ordered = matches.OrderBy(m => m.InitCutOffTime).ToList();
            for (int i = 0; i < ordered.Count; i += cycleSize)
            {
                var chunk = ordered.Skip(i).Take(cycleSize).ToList();
                var cycle = new CycleModel
                {
                    Id = Guid.NewGuid().ToString("N"),
                    EarliestUnix = chunk.Min(m => m.InitCutOffTime),
                    MatchIds = chunk.Select(m => m.MatchId).ToList(),
                    BudgetMbtc = budgetMbtc,
                    Status = "prepared"
                };
                // NOTE: do NOT persist here - caller controls persistence for safety
                res.Add(cycle);
            }
            return res;
        }

        /// <summary>
        /// Schedule predictor runs for prepared cycles. For each cycle schedule a Task to run at (EarliestUnix - 2 hours).
        /// For demo use: if scheduled time already passed, run immediately.
        /// The predictorPath and predictorInputBuilder are used to actually run the predictor.
        /// </summary>
        public static void SchedulePredictorRuns(IEnumerable<CycleModel> cycles, string predictorPath, Func<CycleModel, string> predictorInputBuilder, CancellationToken ct)
        {
            foreach (var cycle in cycles)
            {
                // schedule time = earliest - 2 hours
                var scheduled = DateTimeOffset.FromUnixTimeSeconds(cycle.EarliestUnix).UtcDateTime - TimeSpan.FromHours(2);
                var now = DateTime.UtcNow;
                TimeSpan delay = scheduled > now ? scheduled - now : TimeSpan.Zero;

                Task.Run(async () =>
                {
                    if (delay > TimeSpan.Zero)
                    {
                        try { await Task.Delay(delay, ct); } catch (TaskCanceledException) { return; }
                    }

                    // on trigger run predictor
                    try
                    {
                        var inputPath = predictorInputBuilder(cycle);
                        var csvOut = Path.Combine(Directory.GetCurrentDirectory(), "predictions.csv");
                        var (bets, budget) = PredictorInvoker.RunPredictor(predictorPath, inputPath, csvOut, timeoutMs: 60000);
                        // After predictor returns, attempt to map bets to match URLs from the predictor input file
                        try
                        {
                            var inputLines = System.IO.File.ReadAllLines(inputPath).Select(l => l?.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                            // first line is budget
                            var matchLines = inputLines.Skip(1).ToList();
                            // pair bets with matchLines by index
                            for (int i = 0; i < Math.Min(bets.Count, matchLines.Count); i++)
                            {
                                var bet = bets[i];
                                var matchLine = matchLines[i];
                                // attempt to extract url token if present: format contains '|url:<encoded-url>'
                                string? url = null;
                                var parts = matchLine?.Split('|') ?? Array.Empty<string>();
                                foreach (var p in parts)
                                {
                                    if (p.StartsWith("url:", StringComparison.OrdinalIgnoreCase))
                                    {
                                        url = p.Substring("url:".Length);
                                        break;
                                    }
                                }

                                if (!string.IsNullOrEmpty(url))
                                {
                                    // Launch Playwright runner in dry-run mode (no submit) to populate the stake field
                                    try
                                    {
                                        var userData = Environment.GetEnvironmentVariable("CHROME_USER_DATA_DIR") ?? Environment.GetEnvironmentVariable("PLAYWRIGHT_USER_DATA_DIR") ?? "";
                                        var stakeSelector = Environment.GetEnvironmentVariable("PLAYWRIGHT_STAKE_SELECTOR") ?? "";
                                        var placeSelector = Environment.GetEnvironmentVariable("PLAYWRIGHT_PLACE_SELECTOR") ?? "";

                                        var args = new List<string>();
                                        if (!string.IsNullOrEmpty(userData)) args.AddRange(new[] { "--user-data-dir", userData });
                                        args.AddRange(new[] { "--url", url });
                                        if (!string.IsNullOrEmpty(stakeSelector)) { args.AddRange(new[] { "--stake-selector", stakeSelector }); }
                                        args.AddRange(new[] { "--stake", bet.Stake.ToString() });
                                        // do not pass --submit here (dry-run)
                                        // exit after a short time so runner won't hang indefinitely
                                        args.AddRange(new[] { "--exit-after-seconds", "8" });

                                        // Build process start info: use dotnet run for the PlaywrightRunner project
                                        var psi = new System.Diagnostics.ProcessStartInfo
                                        {
                                            FileName = "dotnet",
                                            Arguments = $"run --project \"{Path.Combine(Directory.GetCurrentDirectory(), "tools", "PlaywrightRunner")}\" -- {string.Join(' ', args.Select(a => a.Contains(' ') ? '"' + a + '"' : a))}",
                                            UseShellExecute = false,
                                            RedirectStandardOutput = true,
                                            RedirectStandardError = true,
                                            CreateNoWindow = true,
                                        };

                                        var proc = System.Diagnostics.Process.Start(psi);
                                        if (proc != null)
                                        {
                                            // read some output asynchronously and then continue
                                            _ = Task.Run(() =>
                                            {
                                                try
                                                {
                                                    var outText = proc.StandardOutput.ReadToEnd();
                                                    var errText = proc.StandardError.ReadToEnd();
                                                    if (!string.IsNullOrWhiteSpace(outText)) Console.WriteLine($"PlaywrightRunner stdout: {outText}");
                                                    if (!string.IsNullOrWhiteSpace(errText)) Console.WriteLine($"PlaywrightRunner stderr: {errText}");
                                                }
                                                catch { }
                                            });
                                        }
                                    }
                                    catch (Exception pwEx)
                                    {
                                        Console.Error.WriteLine($"PlaywrightRunner launch failed for url {url}: {pwEx.Message}");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine($"No URL present for match line #{i + 1}; skipping Playwright fill.");
                                }
                            }
                        }
                        catch (Exception mapEx)
                        {
                            Console.Error.WriteLine($"Failed to map predictor bets to match URLs: {mapEx.Message}");
                        }
                        // mark cycle as executed
                        cycle.Status = "executed";
                        CycleStore.Update(cycle);
                        Console.WriteLine($"Scheduled predictor run for cycle {cycle.Id} completed. Bets: {bets.Count}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Predictor run for cycle {cycle.Id} failed: {ex.Message}");
                        cycle.Status = "error";
                        CycleStore.Update(cycle);
                    }
                }, ct);
            }
        }
    }
}
