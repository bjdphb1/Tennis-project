using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TennisScraper; // DataScraper, DexsportSeleniumScraper
using TennisScraper.Models;
using TennisScraper.Wagering;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using Serilog;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Transactions;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;


namespace TennisScraper
{
    class MainProgram
    {
    // Static fields
    private static readonly string LOG_FILE = Path.Combine("session", "tor_log.txt");
    private static DataScraper? Scraper = null;
    private static readonly object CsvLock = new object();
    private static ConfigReader config = new ConfigReader("Config.ini");
    private static List<MatchModel> matchesValid = new List<MatchModel>();
    private static bool aborted = false;
    // Balance is now managed by BalanceManager class
    private static Dictionary<long, BlockEntry> BlockedMatches = new Dictionary<long, BlockEntry>();
    private static readonly string BlockedMatchesFile = "blocked_matches.json";
    private static readonly object BlockedMatchesLock = new object();
    private static readonly object ActiveCyclesLock = new object();
    private static readonly HashSet<char> ActiveCycles = new HashSet<char>();
    private static Process? torProcess;
    private static IWebDriver? sharedDriver = null;
    
    // Semaphore to ensure only ONE thread uses the shared browser at a time
    private static readonly SemaphoreSlim BrowserSemaphore = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Gets the cross-platform predictor DLL path (works on Windows, Mac, Linux)
    /// </summary>
    /// <param name="predictorName">Name of the predictor (e.g., "Predictor1")</param>
    /// <returns>Absolute path to the predictor DLL</returns>
    private static string GetPredictorPath(string predictorName)
    {
        // Framework-dependent path (no OS-specific folder, works on all platforms)
        return Path.Combine(
            Directory.GetCurrentDirectory(),
            "Predictors",
            predictorName,
            "bin", "Release", "net8.0", "publish",
            $"{predictorName}.dll"
        );
    }

    // Removed stub helper methods. Full implementations are present later in the file.




        // Start the Tor process with the given executable and config file
        static Process StartTor(string torExecutable, string torConfigFile)
        {
            string arguments = $"-f \"{torConfigFile}\"";

            ProcessStartInfo processStartInfo = new ProcessStartInfo
            {
                FileName = torExecutable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true, // Optionally, you can handle output if needed
                RedirectStandardError = true   // Optionally, handle error output
            };

            try
            {
                Process process = Process.Start(processStartInfo);
                if (process != null)
                {
                    return process; // Return the process object to keep track of it
                }
                else
                {
                    throw new Exception("Failed to start Tor process.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting Tor: {ex.Message}");
                return null;
            }
        }

        // Stop the Tor process (if it was started by the application)
        static void StopTor()
        {
            if (torProcess != null && !torProcess.HasExited)
            {
                try
                {
                    torProcess.Kill(); // Stop the Tor process
                    torProcess.WaitForExit(); // Wait for the process to exit
                    Console.WriteLine("Tor process has been stopped.");
                }
                catch (System.Exception ex)
                {
                    Console.WriteLine($"Error stopping Tor: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Validates that predictors are properly configured with correct stake amounts
        /// </summary>
        static bool ValidatePredictorStakes()
        {
            Log.Information("🔍 Validating predictor stake configuration...");
            
            string[] predictorNames = { "Predictor1", "Predictor2" };
            bool allValid = true;

            foreach (var name in predictorNames)
            {
                try
                {
                    string predictorPath = Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "Predictors",
                        name,
                        "bin", "Release", "net8.0", "publish",
                        $"{name}.dll"
                    );

                    if (!File.Exists(predictorPath))
                    {
                        Log.Error($"  ✗ {name}: DLL not found at {predictorPath}");
                        Log.Warning($"    Run: ./build_predictors.sh");
                        allValid = false;
                        continue;
                    }

                    // Create test input CSV
                    string testDir = Path.Combine(Path.GetTempPath(), "predictor_validation");
                    Directory.CreateDirectory(testDir);
                    string testInput = Path.Combine(testDir, $"test_input_{name}.csv");
                    string testOutput = Path.Combine(testDir, $"test_output_{name}.csv");

                    File.WriteAllText(testInput, "MatchId,HomePlayer,AwayPlayer,HomeOdds,AwayOdds\n12345,TestPlayerA,TestPlayerB,1500,2500\n");

                    // Run predictor (using dotnet for platform-independent DLL)
                    var psi = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"\"{predictorPath}\" \"{testInput}\" \"{testOutput}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var proc = Process.Start(psi))
                    {
                        if (proc == null)
                        {
                            Log.Error($"  ✗ {name}: Failed to start process");
                            allValid = false;
                            continue;
                        }

                        proc.WaitForExit(5000);

                        if (!proc.HasExited)
                        {
                            proc.Kill();
                            Log.Error($"  ✗ {name}: Timed out");
                            allValid = false;
                            continue;
                        }
                    }

                    // Read output and verify stake
                    if (File.Exists(testOutput))
                    {
                        var lines = File.ReadAllLines(testOutput);
                        if (lines.Length > 0)
                        {
                            var parts = lines[0].Split(',');
                            if (parts.Length >= 2)
                            {
                                string stakeStr = parts[1].Trim();
                                if (int.TryParse(stakeStr, out int stake))
                                {
                                    // Read expected stake from Config.ini (robust lookup)
                                    int expectedStake = GetExpectedStake(config, 10);
                                    
                                    if (stake == expectedStake)
                                    {
                                        Log.Information($"  ✓ {name}: Stake = ${stake} (correct)");
                                    }
                                    else
                                    {
                                        Log.Error($"  ✗ {name}: Stake = ${stake} (expected ${expectedStake})");
                                        Log.Warning($"    The predictor is outputting the wrong stake amount!");
                                        Log.Warning($"    Fix: Run './build_predictors.sh' to rebuild with current Config.ini");
                                        allValid = false;
                                    }
                                }
                                else
                                {
                                    Log.Warning($"  ⚠ {name}: Could not parse stake from output: {stakeStr}");
                                }
                            }
                        }
                    }
                    else
                    {
                        Log.Warning($"  ⚠ {name}: No output file generated");
                    }

                    // Cleanup test files
                    try
                    {
                        File.Delete(testInput);
                        File.Delete(testOutput);
                        Directory.Delete(testDir, true);
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    Log.Error($"  ✗ {name}: Validation failed - {ex.Message}");
                    allValid = false;
                }
            }

            if (allValid)
            {
                Log.Information("✅ All predictors validated successfully");
            }
            else
            {
                Log.Error("❌ Predictor validation failed - please rebuild predictors");
            }

            return allValid;
        }

        static async Task Main(string[] args)
        {
            SetupLogger();

            // ========================================
            // CLEANUP: Delete old cycle files at startup
            // ========================================
            Log.Information("=== CLEANING UP OLD CYCLE FILES ===");
            var filesToClean = new[] 
            { 
                "thread_1.csv", 
                "thread_2.csv", 
                "output_1.csv", 
                "output_2.csv" 
            };
            
            int cleanedCount = 0;
            foreach (var file in filesToClean)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                        Log.Information($"  ✓ Deleted old file: {file}");
                        cleanedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"  ⚠ Could not delete {file}: {ex.Message}");
                }
            }
            
            if (cleanedCount > 0)
            {
                Log.Information($"✓ Cleanup complete - deleted {cleanedCount} old file(s). Starting fresh.");
            }
            else
            {
                Log.Information("✓ No old files to clean - starting fresh.");
            }
            // ========================================

            // Validate predictor stakes before running
            if (!ValidatePredictorStakes())
            {
                Log.Error("⛔ Aborting: Predictor validation failed");
                Log.Information("Run './rebuild_all.sh' to rebuild with correct stake amounts");
                return;
            }

            // Auto-login test demo - DISABLED (runs before sharedDriver is initialized)
            // TennisScraper.DexsportSeleniumScraper.RunAutoLoginTest();

            // Quick demo: run the predictor against tools/sample_input.txt and print parsed bets
            if (args != null && args.Length > 0 && (args.Any(a => a == "predictor-demo" || a == "--predictor-demo")))
            {
                try
                {
                    var predictorPath = Environment.GetEnvironmentVariable("PREDICTOR_PATH");
                    if (string.IsNullOrWhiteSpace(predictorPath))
                    {
                        predictorPath = GetPredictorPath("Predictor1"); // Cross-platform path
                    }
                    var sampleInput = Path.Combine(Directory.GetCurrentDirectory(), "tools", "sample_input.txt");
                    var csvOut = Path.Combine(Directory.GetCurrentDirectory(), "predictions.csv");
                    Console.WriteLine($"Running predictor demo: {predictorPath} --input-file {sampleInput} --csv {csvOut}");
                    var (bets, budget) = PredictorInvoker.RunPredictor(predictorPath, sampleInput, csvOut, timeoutMs: 30000);
                    Console.WriteLine($"Budget (mBTC): {budget}");
                    Console.WriteLine("Bets:");
                    for (int i = 0; i < bets.Count; i++)
                    {
                        Console.WriteLine($"  {i+1}. {bets[i]}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Predictor demo failed: " + ex.Message);
                }
                return;
            }

            // Quick cycle demo: build a simple cycle from tools/sample_input.txt, prepare predictor input and run predictor
            if (args != null && args.Length > 0 && (args.Any(a => a == "cycle-demo" || a == "--cycle-demo")))
            {
                try
                {
                    var sampleInput = Path.Combine(Directory.GetCurrentDirectory(), "tools", "sample_input.txt");
                    var cycle = CycleScheduler.BuildCycleFromPredictorSample(sampleInput, cycleSize: 10);
                    var prepared = Path.Combine(Directory.GetCurrentDirectory(), "session", $"cycle_input_{cycle.Id}.txt");
                    var budget = 100; // demo budget in mBTC
                    CycleScheduler.PreparePredictorInput(cycle, budget, prepared);

                    var predictorPath = GetPredictorPath("Predictor1"); // Cross-platform path
                    var csvOut = Path.Combine(Directory.GetCurrentDirectory(), "predictions.csv");
                    Console.WriteLine($"Running cycle-demo predictor for cycle {cycle.Id} (matches={cycle.MatchLines.Count})");
                    var (bets, returnedBudget) = PredictorInvoker.RunPredictor(predictorPath, prepared, csvOut, timeoutMs: 30000);
                    Console.WriteLine($"Returned budget {returnedBudget} mBTC; Bets:");
                    for (int i = 0; i < bets.Count; i++) Console.WriteLine($"  {i+1}. {bets[i]}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Cycle demo failed: " + ex.Message);
                }
                return;
            }

            // Cycle scheduler demo: build cycles from sample_input and schedule predictor runs (persist cycles)
            if (args != null && args.Length > 0 && (args.Any(a => a == "cycle-schedule-demo" || a == "--cycle-schedule-demo")))
            {
                try
                {
                    // Support live fetch: if --live is provided, use DexsportSeleniumScraper to fetch matches
                    bool live = args.Any(a => a == "--live" || a == "live");
                    bool confirmPersist = args.Any(a => a == "--confirm" || a == "--persist");

                    List<Models.MatchModel> matches = new List<Models.MatchModel>();
                    if (live)
                    {
                        Console.WriteLine("Running live match fetch from Dexsport (this will open a ChromeDriver instance).\n" +
                            "By default this is a dry-run and cycles will NOT be persisted unless you pass --confirm.");
                        using (var dexsportScraper = new DexsportSeleniumScraper(headless: false))
                        {
                            var fetched = await dexsportScraper.GetMatchesAsync();
                            if (fetched != null && fetched.Count > 0)
                            {
                                matches = fetched;
                                Console.WriteLine($"Fetched {matches.Count} matches from Dexsport.");
                            }
                            else
                            {
                                Console.Error.WriteLine("No matches fetched from Dexsport. Aborting.");
                                return;
                            }
                        }
                    }
                    else
                    {
                        // Create MatchModel objects from sample_input.txt lines (simple parser)
                        var sampleInput = Path.Combine(Directory.GetCurrentDirectory(), "tools", "sample_input.txt");
                        var lines = System.IO.File.ReadAllLines(sampleInput).Select(l => l?.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                        if (lines.Count <= 1) { Console.Error.WriteLine("sample_input.txt must contain budget + match lines"); return; }
                        var matchLines = lines.Skip(1).ToList();
                        long idCounter = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        foreach (var ml in matchLines)
                        {
                            var parts = ml.Split('|');
                            string names = parts.Length > 0 ? parts[0] : ml;
                            string startToken = parts.FirstOrDefault(p => p.StartsWith("start:")) ?? "start:" + DateTime.UtcNow.AddHours(3).ToString("o");
                            string startStr = startToken.Substring("start:".Length);
                            var nameParts = names.Split(new[] { "_vs_" }, StringSplitOptions.None);
                            string home = nameParts.Length > 0 ? nameParts[0] : "Home";
                            string away = nameParts.Length > 1 ? nameParts[1] : "Away";
                            var mm = new Models.MatchModel(idCounter++, home, away, 1.5, 2.5, startStr);
                            matches.Add(mm);
                        }
                    }

                    // build cycles from matches. If running live and not confirmed, do a dry-run (no persist)
                    List<CycleModel> cycles;
                    if (live && !confirmPersist)
                    {
                        cycles = SchedulerService.CreateCyclesFromMatches(matches, cycleSize: 10, budgetMbtc: 100);
                        Console.WriteLine($"[DRY-RUN] Created {cycles.Count} cycles (not persisted). Use --confirm to persist.");
                    }
                    else
                    {
                        cycles = SchedulerService.CreateAndPersistCyclesFromMatches(matches, cycleSize: 10, budgetMbtc: 100);
                        Console.WriteLine($"Created and persisted {cycles.Count} cycles to session/cycles.json");
                    }

                    // For each cycle, call RunCycle to show step-by-step logs
                    var predictorPath = Path.Combine(Directory.GetCurrentDirectory(), "Predictors", "Predictor2", "bin", "Release", "net8.0", "osx-arm64", "publish", "Predictor2");
                    int cycleNum = 1;
                    foreach (var cycle in cycles)
                    {
                        Console.WriteLine($"\n=== Running Cycle {cycleNum} (ID: {cycle.Id}) ===");
                        // Prepare matches for this cycle
                        var idSet = new HashSet<long>(cycle.MatchIds);
                        var cycleMatches = matches.Where(m => idSet.Contains(m.MatchId)).ToList();
                        // Prepare dummy CSV/output paths for demo
                        string threadCsv = $"thread_demo_{cycleNum}.csv";
                        string outputCsv = $"output_demo_{cycleNum}.csv";
                        // Run the cycle and await
                        await RunCycle(cycleMatches, threadCsv, outputCsv, predictorPath, cycle.BudgetMbtc, (char)('A' + cycleNum - 1));
                        cycleNum++;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Cycle schedule demo failed: " + ex.Message);
                }
                return;
            }

            // CLI helpers: list-blocked, unblock <id>
            if (args != null && args.Length > 0)
            {
                var cmd = args[0].Trim().ToLowerInvariant();
                if (cmd == "list-blocked")
                {
                    LoadBlockedMatches();
                    Console.WriteLine("Blocked matches:");
                    lock (BlockedMatchesLock)
                    {
                        foreach (var kv in BlockedMatches.OrderBy(k => k.Key))
                        {
                            Console.WriteLine($"{kv.Key} -> status={kv.Value.Status} timestamp={kv.Value.Timestamp:o} count={kv.Value.Count}");
                        }
                    }
                    return;
                }
                else if (cmd == "migrate-blocked")
                {
                    // Load existing file (supports legacy array or new map) and immediately persist
                    LoadBlockedMatches();
                    lock (BlockedMatchesLock)
                    {
                        SaveBlockedMatches();
                        Console.WriteLine($"Migrated and saved {BlockedMatches.Count} blocked matches to {BlockedMatchesFile} (backup created).");
                    }
                    return;
                }
                else if (cmd == "unblock" && args.Length > 1)
                {
                    if (long.TryParse(args[1], out var toUnblock))
                    {
                        LoadBlockedMatches();
                        lock (BlockedMatchesLock)
                        {
                            if (BlockedMatches.Remove(toUnblock))
                            {
                                SaveBlockedMatches();
                                Console.WriteLine($"Unblocked {toUnblock}");
                            }
                            else Console.WriteLine($"Id {toUnblock} not found in blocked list.");
                        }
                    }
                    else Console.WriteLine("Usage: unblock <matchId>");
                    return;
                }
            }

            // Load blocked matches persisted from prior runs
            LoadBlockedMatches();
            
            // Auto-detect Tor path based on OS
            string torPath;
            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                // macOS/Linux - use system Tor (installed via Homebrew or apt)
                torPath = "/opt/homebrew/bin/tor"; // macOS Homebrew default
                if (!File.Exists(torPath))
                {
                    torPath = "/usr/bin/tor"; // Linux default
                }
                if (!File.Exists(torPath))
                {
                    torPath = "/usr/local/bin/tor"; // Alternative location
                }
            }
            else
            {
                // Windows
                torPath = Path.Combine(Directory.GetCurrentDirectory(), "tor", "tor.exe");
            }
            
            // DISABLED: Tor not needed for Dexsport scraping
            // string torrcPath = Path.Combine(Directory.GetCurrentDirectory(), "torrc");
            // StartTor(torPath, torrcPath);
            // Thread.Sleep(3000);
            // Log.Information("Waiting for Tor to initialize...");

            // Create a shared ChromeDriver instance to reuse for all Dexsport scraping
            // This avoids opening multiple browser windows and makes Cloudflare solving only once.
            try
            {
                var sharedOptions = new ChromeOptions();
                sharedOptions.AddArgument("--no-sandbox");
                sharedOptions.AddArgument("--disable-dev-shm-usage");
                sharedOptions.AddArgument("--window-size=1920,1080");
                sharedOptions.AddArgument("--disable-blink-features=AutomationControlled");
                sharedOptions.AddExcludedArgument("enable-automation");
                sharedOptions.AddUserProfilePreference("credentials_enable_service", false);
                sharedOptions.AddUserProfilePreference("profile.password_manager_enabled", false);
                
                // Suppress console logs to avoid "Connection refused" spam during ChromeDriver startup
                sharedOptions.AddArgument("--log-level=3"); // Only show fatal errors
                sharedOptions.AddArgument("--silent");

                var sharedService = ChromeDriverService.CreateDefaultService();
                sharedService.SuppressInitialDiagnosticInformation = true;
                sharedService.HideCommandPromptWindow = true;
                sharedService.EnableVerboseLogging = false; // Suppress ChromeDriver verbose logs

                // Temporarily suppress console output during ChromeDriver initialization
                // (This hides "Connection refused" messages from Selenium's HTTP client)
                Log.Information("Initializing ChromeDriver (this may take 5-10 seconds)...");
                var originalOut = Console.Out;
                var originalError = Console.Error;
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);

                sharedDriver = new ChromeDriver(sharedService, sharedOptions, TimeSpan.FromSeconds(180));
                
                // Restore console output
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                sharedDriver.Manage().Timeouts().PageLoad = TimeSpan.FromMinutes(5);
                sharedDriver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(10);
                sharedDriver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromMinutes(5);

                Log.Information("Shared ChromeDriver initialized for Dexsport scraping");

                // ✅ IMPROVED: Initial load with refresh to ensure full site load
                Log.Information("🌐 Opening Dexsport.io...");
                sharedDriver.Navigate().GoToUrl("https://dexsport.io/sports/tennis");
                
                // Wait for initial load (increased timeout to 60 seconds)
                Log.Information("⏳ Waiting for initial page load (up to 60 seconds)...");
                var wait = new WebDriverWait(sharedDriver, TimeSpan.FromSeconds(60));
                
                try
                {
                    wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState").Equals("complete"));
                    Log.Information("✓ Initial page load complete");
                }
                catch (WebDriverTimeoutException)
                {
                    Log.Warning("⚠️ Page load timeout, but continuing anyway...");
                }
                
                Log.Information("⏳ Waiting 10 seconds for JavaScript to initialize...");
                await Task.Delay(10000);
                
                // ✅ FORCE REFRESH to ensure full load
                Log.Information("🔄 Refreshing page to ensure full site load...");
                sharedDriver.Navigate().Refresh();
                
                Log.Information("⏳ Waiting for page to reload...");
                try
                {
                    wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState").Equals("complete"));
                    Log.Information("✓ Page refresh complete");
                }
                catch (WebDriverTimeoutException)
                {
                    Log.Warning("⚠️ Refresh timeout, but continuing anyway...");
                }
                
                await Task.Delay(5000);
                
                Log.Information("✅ Site fully loaded and ready for login");
                
                // Manual login prompt (only once per run)
                Log.Information("Please log in manually in the browser window. After login and page load, press ENTER in the terminal to continue scraping.");
                Console.WriteLine("\n>>> MANUAL LOGIN REQUIRED <<<");
                Console.WriteLine(">>> 1. Log in to Dexsport in the browser window");
                Console.WriteLine(">>> 2. Complete any email code or challenge");
                Console.WriteLine(">>> 3. Wait until the tennis matches are visible");
                Console.WriteLine(">>> 4. Then press ENTER here to continue...\n");
                Console.ReadLine();
                Log.Information("✓ User confirmed manual login. Proceeding...");
            }
            catch (Exception ex)
            {
                Log.Error($"❌ Could not initialize shared ChromeDriver: {ex.Message}");
                Log.Error($"Stack trace: {ex.StackTrace}");
                sharedDriver = null;
            }
            
            // ===== SYNC LIVE BALANCE WITH BALANCE MANAGER =====
            Log.Information("=== SYNCING LIVE BALANCE ===");
            decimal liveBalance = 100m; // Default fallback
            try
            {
                // IMPORTANT: Switch back to main page context (out of iframe)
                if (sharedDriver != null)
                {
                    sharedDriver.SwitchTo().DefaultContent();
                    Log.Information("✓ Switched back to main page context for balance scraping");
                }
                
                using (var dexsportScraper = new DexsportSeleniumScraper(sharedDriver, headless: false))
                {
                    Log.Information("📊 Fetching live balance from Dexsport...");
                    liveBalance = await dexsportScraper.GetBalanceAsync(config.Currency);
                    
                    if (liveBalance > 0)
                    {
                        Log.Information($"✅ Live balance retrieved: {config.Currency} {liveBalance:F2}");
                    }
                    else
                    {
                        Log.Warning("⚠️ Balance returned 0. Using default balance of $100.00");
                        liveBalance = 100m;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"❌ Error fetching live balance: {ex.Message}");
                Log.Warning("Using default balance of $100.00");
                liveBalance = 100m;
            }
            
            // Initialize Balance Manager with live balance
            Log.Information($"💰 Initializing Balance Manager with live balance: ${liveBalance:F2}");
            BalanceManager.Initialize(defaultBalance: liveBalance, logger: Log.Logger);
            // BalanceManager.DisplayLiveDashboard(config.Currency, Log.Logger);
            Log.Information("=== END BALANCE SYNC ===");
            
            bool b = true;

            while (b)
            {
                if (aborted) { break; }
                try
                {
                    // ---------------------- Load or Initialize Dataset ----------------------
                    DataTable df2;

                    if (Scraper == null) { Scraper = new DataScraper(); }

                    // For full player-history parsing on TennisAbstract (DOB/hand/splits),
                    // ensure existence-only short-circuit is disabled so matches are
                    // marked viable only when full player history is parsed.
                    Scraper.SearchForName = false;


                    // ---------------------- Fetch upcoming matches / odds ----------------------
                    List<MatchModel> matches;
                    try
                    {
                        // Fetch from Dexsport using Selenium (visible browser - bypasses Cloudflare)
                        if (sharedDriver == null)
                        {
                            Log.Error("Shared ChromeDriver is not available. Cannot continue scraping or bet placement.");
                            break;
                        }
                        using (var dexsportScraper = new DexsportSeleniumScraper(sharedDriver, headless: false))
                        {
                            matches = await dexsportScraper.GetMatchesAsync();
                        }
                        Log.Information("Fetched {Count} matches from Dexsport.", matches.Count);

                        if (matches.Count == 0)
                        {
                            Log.Warning("No matches found. Retrying in 1 minute...");
                            Thread.Sleep(1 * 60 * 1000);
                            continue;
                        }

                        // Prefetch TennisAbstract player fragments in batch to warm PlayerLookupService cache
                        try
                        {
                            var playerNames = matches.SelectMany(m => new[] { m.HomePlayerName, m.AwayPlayerName })
                                                     .Where(n => !string.IsNullOrWhiteSpace(n))
                                                     .Select(n => n.Trim())
                                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                                     .ToList();

                            if (playerNames.Count > 0)
                            {
                                Log.Information($"Prefetching TennisAbstract data for {playerNames.Count} unique players (batch)...");
                                var prefetchResults = await Scraper.FetchPlayersBatchAsync(playerNames, overallTimeoutSeconds: 45);
                                Log.Information($"Prefetch complete. Cached results: {prefetchResults.Count}");
                            }
                        }
                        catch (Exception exPrefetch)
                        {
                            Log.Warning("Prefetch failed or timed out: {Message}", exPrefetch.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Error fetching matches: {Message}", ex.Message);
                        Thread.Sleep(1 * 60 * 1000);
                        continue;
                    }
                    // if the matches list is less than required for even 1 cycle even before filtering it, then no need to do this, 
                    // skip these matches & wait 2 hours and then try to get a new list of matches
                    if (matches.Count >= config.NumMatchesPerCycle)
                    {
                        Log.Information("=== TASK 2: FILTERING VIABLE MATCHES ===");
                        
                        // ---------------------- Filter Out Doubles and Live Matches First ----------------------
                        var singlesMatches = new List<MatchModel>();
                        int doublesSkipped = 0;
                        int liveSkipped = 0;
                        int tooSoonSkipped = 0;
                        
                        foreach (var match in matches)
                        {
                            // Skip doubles matches (contain "/" in player names)
                            if (match.HomePlayerName.Contains("/") || match.AwayPlayerName.Contains("/"))
                            {
                                Log.Information("❌ Skipped (Doubles): {Home} vs {Away}", match.HomePlayerName, match.AwayPlayerName);
                                doublesSkipped++;
                                continue;
                            }
                            
                            // Skip live matches (can't bet on matches already in progress)
                            if (!string.IsNullOrEmpty(match.Status) && match.Status.Equals("live", StringComparison.OrdinalIgnoreCase))
                            {
                                Log.Information("❌ Skipped (Live/In Progress - Status): {Home} vs {Away} — Status: {Status}", 
                                    match.HomePlayerName, match.AwayPlayerName, match.Status);
                                liveSkipped++;
                                continue;
                            }
                            
                            // ✅ ALSO check if CutOffTime string indicates "Live" (from Dexsport scraper)
                            if (!string.IsNullOrEmpty(match.CutOffTime) && match.CutOffTime.Equals("Live", StringComparison.OrdinalIgnoreCase))
                            {
                                Log.Information("❌ Skipped (Live/In Progress - Time): {Home} vs {Away} — Start Time: Live", 
                                    match.HomePlayerName, match.AwayPlayerName);
                                liveSkipped++;
                                continue;
                            }
                            
                            // Skip matches starting in less than 2 hours (minimum time before match)
                            if (!string.IsNullOrEmpty(match.CutOffTime))
                            {
                                try
                                {
                                    // Time zone handling: use config or auto-detect
                                    TimeZoneInfo matchTimeZone;
                                    if (config.TimeZoneId.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                                    {
                                        matchTimeZone = TimeZoneInfo.Local;
                                        Log.Information($"[TIMEZONE] Using system time zone: {matchTimeZone.Id}");
                                    }
                                    else
                                    {
                                        matchTimeZone = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId);
                                        Log.Information($"[TIMEZONE] Using configured time zone: {matchTimeZone.Id}");
                                    }
                                    DateTime localMatchTime = DateTime.Parse(match.CutOffTime, CultureInfo.InvariantCulture, DateTimeStyles.None);
                                    localMatchTime = DateTime.SpecifyKind(localMatchTime, DateTimeKind.Unspecified);
                                    DateTime utcMatchTime = TimeZoneInfo.ConvertTimeToUtc(localMatchTime, matchTimeZone);
                                    TimeSpan timeUntilMatch = utcMatchTime - DateTime.UtcNow;
                                    if (timeUntilMatch.TotalMinutes < 30)
                                    {
                                        Log.Information("❌ Skipped (Too Soon): {Home} vs {Away} — Starts in {Minutes:F0}m (minimum 30m required)", 
                                            match.HomePlayerName, match.AwayPlayerName, timeUntilMatch.TotalMinutes);
                                        tooSoonSkipped++;
                                        continue;
                                    }
                                    Log.Information("⏰ Match Time OK: {Home} vs {Away} — Starts in {Hours:F1}h", 
                                        match.HomePlayerName, match.AwayPlayerName, timeUntilMatch.TotalHours);
                                }
                                catch (Exception timeEx)
                                {
                                    Log.Warning("⚠️ Could not parse match time for {Home} vs {Away}: {Error} — Skipping for safety", 
                                        match.HomePlayerName, match.AwayPlayerName, timeEx.Message);
                                    tooSoonSkipped++;
                                    continue;
                                }
                            }
                            
                            singlesMatches.Add(match);
                        }
                        
                        Log.Information("Filtered out {Doubles} doubles, {Live} live, and {TooSoon} too-soon matches. {Singles} singles matches remaining.", 
                            doublesSkipped, liveSkipped, tooSoonSkipped, singlesMatches.Count);

                        // ---------------------- Convert Names for TennisAbstract Validation ----------------------
                        // Dexsport API gives names in "Last First" format: "Zhang Shuai"
                        // TennisAbstract needs "First Last" format: "Shuai Zhang"
                        // Original names are preserved in HomePlayerNameOriginal/AwayPlayerNameOriginal for bet placement
                        foreach (var match in singlesMatches)
                        {
                            match.HomePlayerName = DexsportSeleniumScraper.ConvertToFirstLastFormat(match.HomePlayerName);
                            match.AwayPlayerName = DexsportSeleniumScraper.ConvertToFirstLastFormat(match.AwayPlayerName);
                        }

                        // ---------------------- Check TennisAbstract for Player Data ----------------------

                        foreach (var match in singlesMatches)
                        {
                            Log.Information("Checking: {Home} vs {Away}", match.HomePlayerName, match.AwayPlayerName);

                            var a = await MainProgram.GetMatchDataAsync(match);
                            if (a != null && !MainProgram.IsRowEmpty(a))
                            {
                                match.MatchRow = a;
                                Log.Information($"{a.ItemArray[a.ItemArray.Length - 2]}");
                                matchesValid.Add(match);
                                Log.Information("✅ VIABLE: {Home} vs {Away} (Both players found in TennisAbstract)", 
                                    match.HomePlayerName, match.AwayPlayerName);
                            }
                            else
                            {
                                Log.Information("❌ Skipped: {Home} vs {Away} (Player data not found in TennisAbstract)", 
                                    match.HomePlayerName, match.AwayPlayerName);
                            }
                        }
                        
                        Log.Information("=== FILTERING COMPLETE: {Viable} viable matches (out of {Total} total) ===", 
                            matchesValid.Count, matches.Count);

                        // Check the conditions to start the threads
                        if (matchesValid.Count >= config.NumMatchesPerCycle)
                        {
                            // Start thread 1 and/or thread 2 based on the conditions
                            Task? thread1Task = null;
                            Task? thread2Task = null;

                            if (matchesValid.Count >= 2 * config.NumMatchesPerCycle)
                            {
                                var matchesThread1 = new List<MatchModel>();
                                var matchesThread2 = new List<MatchModel>();

                                // Split the matchesValid into 2 seperate match lists
                                for (int idx = 0; idx < matchesValid.Count; idx++)
                                {
                                    if (idx % 2 == 0)
                                        matchesThread1.Add(matchesValid[idx]);
                                    else
                                        matchesThread2.Add(matchesValid[idx]);
                                }

                                thread1Task = RunThread1(matchesThread1);
                                thread2Task = RunThread2(matchesThread2);

                                await Task.WhenAll(thread1Task, thread2Task);
                            }
                            else
                            {
                                // run only 1 thread if number is too low for another thread

                                thread1Task = RunThread1(matchesValid);
                                await thread1Task;
                            }

                        }
                        else
                        {
                            Log.Information("Matches too low to even start one Cycle... Skipping.");
                            Log.Information("Waiting 1 minute before fetching new matches...");
                            Thread.Sleep(1 * 60 * 1000);
                        }
                    }
                    else
                    {
                        Log.Information("Waiting 1 minute before fetching new matches...");
                        Thread.Sleep(1 * 60 * 1000);
                    }
                    ;

                }
                catch (Exception ex)
                {
                    // Log full exception (including stack) to help diagnose intermittent NREs
                    Log.Error(ex, "Unexpected error in main loop");
                    Thread.Sleep(1 * 60 * 1000);
                }
            }
        }

        public static void Shuffle<T>(List<T> list)
        {
            Random rng = new Random();
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }

        private static async Task RunThread1(List<MatchModel> matchesThread1)
        {
            var OddIndexLetters = new List<char> { 'A', 'C', 'E', 'G', 'I', 'K', 'M', 'O', 'Q', 'S', 'U', 'W', 'Y' };
            string thread1Csv = "thread_1.csv";
            string output1Csv = "output_1.csv";
            string predictor1Path = GetPredictorPath("Predictor1"); // Cross-platform path
            int iteration = 0;
            while (true)
            {
                decimal amountForThisCycle = BalanceManager.CalculateCycleAllocation(config.PercentWagerPerCycle, Log.Logger);
                char cycleLetter = OddIndexLetters[iteration % OddIndexLetters.Count];
                bool started = await RunCycle(matchesThread1, thread1Csv, output1Csv, predictor1Path, (double)amountForThisCycle, cycleLetter);
                if (!started)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1));
                }
                else
                {
                    iteration = (iteration + 1) % OddIndexLetters.Count;
                }
                if (matchesThread1.Count < config.NumMatchesPerCycle)
                {
                    break;
                }
            }
        }

        private static async Task RunThread2(List<MatchModel> matchesThread2)
        {
            var EvenIndexLetters = new List<char> { 'B', 'D', 'F', 'H', 'J', 'L', 'N', 'P', 'R', 'T', 'V', 'X', 'Z' };
            string thread2Csv = "thread_2.csv";
            string output2Csv = "output_2.csv";
            string predictor2Path = GetPredictorPath("Predictor2"); // Cross-platform path
            int iteration = 0;
            while (true)
            {
                decimal amountForThisCycle = BalanceManager.CalculateCycleAllocation(config.PercentWagerPerCycle, Log.Logger);
                char cycleLetter = EvenIndexLetters[iteration % EvenIndexLetters.Count];
                Log.Information("Starting Cycle: {Cycle}", cycleLetter);
                bool started = await RunCycle(matchesThread2, thread2Csv, output2Csv, predictor2Path, (double)amountForThisCycle, cycleLetter);
                if (!started)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1));
                }
                else
                {
                    iteration = (iteration + 1) % EvenIndexLetters.Count;
                }
                if (matchesThread2.Count < config.NumMatchesPerCycle)
                {
                    break;
                }
            }
        }

    private static async Task<bool> RunCycle(List<MatchModel> threadMatches, string threadCsv, string outputCsv, string predictorPath, double amountForThisCycle, char cycleLetter)
        {
            // Remove any numeric first line left from previous runs
            if (File.Exists(threadCsv))
            {
                var lines = File.ReadAllLines(threadCsv).ToList();
                if (lines.Count > 0 && double.TryParse(lines[0].Trim(), out _))
                {
                    lines.RemoveAt(0);
                    File.WriteAllLines(threadCsv, lines);
                }
            }
            if (File.Exists(threadCsv))
            {
                var lines = File.ReadAllLines(threadCsv).ToList();
                if (lines.Count > 0 && double.TryParse(lines[0].Trim(), out _))
                {
                    lines.RemoveAt(0);
                    File.WriteAllLines(threadCsv, lines);
                }
            }

            Log.Information($"==================== CYCLE {cycleLetter} START ====================");

            // Enforce max-2-active-cycles globally
            while (true)
            {
                lock (ActiveCyclesLock)
                {
                    if (ActiveCycles.Count < 2)
                    {
                        ActiveCycles.Add(cycleLetter);
                        break;
                    }
                }
                Log.Information("Max active cycles reached. Waiting 30s before retrying to start cycle {Cycle}", cycleLetter);
                await Task.Delay(30000);
            }

            try
            {
                Log.Information($"Preparing input CSV for cycle {cycleLetter}: {threadCsv}");
                // Remove any numeric first line left from previous runs
                if (File.Exists(threadCsv))
                {
                    var lines = File.ReadAllLines(threadCsv).ToList();
                    if (lines.Count > 0 && double.TryParse(lines[0].Trim(), out _))
                    {
                        lines.RemoveAt(0);
                        File.WriteAllLines(threadCsv, lines);
                    }
                }

                var matchesForThisCycle = new List<MatchModel>();
                var threshold = DateTimeOffset.UtcNow.AddMinutes(10);
                List<MatchModel>? eligible = null;
                lock (threadMatches)
                {
                    eligible = threadMatches
                        .Where(m => !IsExceeded(m.CutOffTime) &&
                                    !BlockedMatches.ContainsKey(m.MatchId) &&
                                    DateTimeOffset.TryParse(m.CutOffTime, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var c) &&
                                    c >= threshold)
                        .OrderBy(m => DateTimeOffset.Parse(m.CutOffTime))
                        .ToList();
                }

                if (eligible.Count < config.NumMatchesPerCycle)
                {
                    Log.Information("Not enough matches ready for cycle {Cycle}. Need {Need}, have {Have} (respecting 2-hour rule).", cycleLetter, config.NumMatchesPerCycle, eligible.Count);
                    Log.Information($"==================== CYCLE {cycleLetter} END ====================");
                    return false; // no cycle started
                }

                var selected = eligible.Take(config.NumMatchesPerCycle).ToList();
                Log.Information($"Selected {selected.Count} matches for cycle {cycleLetter}:");
                foreach (var match in selected)
                {
                    Log.Information($"  - MatchId: {match.MatchId}, {match.HomePlayerName} vs {match.AwayPlayerName}, Odds: {match.HomeOdds}/{match.AwayOdds}, CutOff: {match.CutOffTime}");
                }

                // Remove selected from threadMatches and write CSV rows
                lock (threadMatches)
                {
                    foreach (var match in selected)
                    {
                        if (match.MatchRow != null)
                        {
                            lock (CsvLock)
                            {
                                Log.Information($"Writing match to input CSV: {match.HomePlayerName} vs {match.AwayPlayerName}");
                                WriteDataRowToCsv(match.MatchRow, threadCsv);
                            }
                        }
                        matchesForThisCycle.Add(match);
                        threadMatches.Remove(match);
                    }
                }

                Log.Information($"Input CSV for cycle {cycleLetter} written: {threadCsv}");
                Log.Information($"Running predictor: {predictorPath} (input: {threadCsv}, output: {outputCsv})");
                RunPredictor(predictorPath);

                if (!File.Exists(outputCsv))
                {
                    Log.Information($"Predictor output file not found: {outputCsv}");
                    Log.Information($"==================== CYCLE {cycleLetter} END ====================");
                    return false;
                }

                Log.Information($"Reading predictor output: {outputCsv}");
                var dataList = new List<GameData>();
                try
                {
                    using (var reader = new StreamReader(outputCsv))
                    {
                        int lineNum = 0;
                        while (!reader.EndOfStream)
                        {
                            var line = reader.ReadLine();
                            var values = line.Split(',');
                            if (values.Length < 2) continue;
                            string winner = values[0].Trim();
                            string stakes = values[1].Trim();
                            dataList.Add(new GameData(winner, stakes));
                            Log.Information($"  Bet {lineNum + 1}: Winner={winner}, Stake={stakes}");
                            lineNum++;
                        }
                    }
                }
                catch
                {
                    Log.Information($"Error reading {outputCsv} for Cycle {cycleLetter}");
                }

                // Place all bets in parallel for this cycle
                var betTasks = new List<Task>();
                for (int i = 0; i < matchesForThisCycle.Count && i < dataList.Count; i++)
                {
                    var match = matchesForThisCycle[i];
                    var row = dataList[i];
                    if (int.TryParse(row.Winner, out int winnerInt))
                        match.Winner = winnerInt;
                    if (decimal.TryParse(row.Stakes, out decimal stakeDec))
                        match.Stake = stakeDec;
                    match.Currency = config.Currency;
                    Log.Information($"  Match {i + 1}: {match.HomePlayerName} vs {match.AwayPlayerName} | Winner={row.Winner} | Stake={row.Stakes} {config.Currency}");

                    // Launch each bet as a separate task
                    betTasks.Add(Task.Run(async () =>
                    {
                        Log.Information($"[{cycleLetter}] Waiting for browser access to place bet...");
                        await BrowserSemaphore.WaitAsync();
                        Log.Information($"[{cycleLetter}] Browser access acquired. Placing bet...");
                        var placementProvider = new DexsportPlacementProvider(Log.Logger, sharedDriver);
                        WageringManager.CheckDictResult? checkResult = null;
                        try
                        {
                            checkResult = await WageringManager.CheckDictAsync(Log.Logger, new List<MatchModel> { match }, placementProvider);
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[{cycleLetter}] Error during CheckDictAsync for match {match.MatchId}: {ex.Message}");
                            BrowserSemaphore.Release();
                            return;
                        }

                        if (checkResult == null || checkResult.Aborted || (checkResult.AcceptedBets?.Count ?? 0) == 0)
                        {
                            Log.Information($"No bet accepted for match {match.MatchId} in cycle {cycleLetter}.");
                            BrowserSemaphore.Release();
                            return;
                        }

                        var acceptedBet = checkResult.AcceptedBets?.FirstOrDefault();
                        if (acceptedBet == null)
                        {
                            Log.Information($"No accepted bet returned for match {match.MatchId} in cycle {cycleLetter}.");
                            BrowserSemaphore.Release();
                            return;
                        }
                        Log.Information($"Accepted bet: {acceptedBet.HomePlayerName} vs {acceptedBet.AwayPlayerName} | Winner={acceptedBet.Winner} | Stake={acceptedBet.Stake} {acceptedBet.Currency}");

                        // Wait for settlement with polling: 5 min, then every 30 min
                        string? status = null;
                        int pollCount = 0;
                        TimeSpan firstWait = TimeSpan.FromMinutes(5);
                        TimeSpan nextWait = TimeSpan.FromMinutes(30);
                        Log.Information($"[{cycleLetter}] Waiting 5 minutes before first settlement check...");
                        await Task.Delay(firstWait);
                        while (true)
                        {
                            pollCount++;
                            Log.Information($"[{cycleLetter}] Polling for settlement (attempt {pollCount})...");
                            // Use DexsportSeleniumScraper directly for status polling
                            string refId = acceptedBet.ReferenceId ?? string.Empty;
                            string eventId = acceptedBet.MatchId.ToString();
                            string? polledStatus = null;
                            try
                            {
                                if (sharedDriver != null)
                                {
                                    using (var scraper = new DexsportSeleniumScraper(sharedDriver, headless: false))
                                    {
                                        polledStatus = await scraper.LookupTicketStatusAsync(refId, eventId);
                                    }
                                }
                                else
                                {
                                    Log.Error($"[{cycleLetter}] sharedDriver is null. Cannot poll for settlement status.");
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warning($"[{cycleLetter}] Exception during status polling: {ex.Message}");
                            }
                            status = polledStatus;
                            Log.Information($"[{cycleLetter}] Settlement status: {status}");
                            if (status != null && (status.ToUpperInvariant().Contains("WON") || status.ToUpperInvariant().Contains("WIN") || status.ToUpperInvariant().Contains("LOST") || status.ToUpperInvariant().Contains("LOSS") || status.ToUpperInvariant().Contains("VOID")))
                            {
                                Log.Information($"[{cycleLetter}] Bet settled: {status}");
                                // Update balance
                                double oddsValue = acceptedBet.Winner == 0 ? (acceptedBet.HomeOdds ?? 1.0) : (acceptedBet.AwayOdds ?? 1.0);
                                decimal odds = (decimal)oddsValue;
                                BalanceManager.ProcessMatchResult(
                                    status,
                                    acceptedBet.Stake,
                                    odds,
                                    acceptedBet.MatchId,
                                    acceptedBet.HomePlayerName,
                                    acceptedBet.AwayPlayerName,
                                    Log.Logger
                                );

                                // Update placed_bets.json with settlement status and timestamp
                                try
                                {
                                    var sessionDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "session");
                                    var placedPath = System.IO.Path.Combine(sessionDir, "placed_bets.json");
                                    if (System.IO.File.Exists(placedPath))
                                    {
                                        var txt = await System.IO.File.ReadAllTextAsync(placedPath);
                                        var records = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<System.Text.Json.Nodes.JsonObject>>(txt) ?? new System.Collections.Generic.List<System.Text.Json.Nodes.JsonObject>();
                                        // Try to match the persisted record by any known reference field
                                        var rec = records.FirstOrDefault(r =>
                                            (string?)r["ReferenceId"] == acceptedBet.ReferenceId
                                            || (string?)r["RemoteReference"] == acceptedBet.ReferenceId
                                            || (string?)r["LocalReference"] == acceptedBet.ReferenceId
                                        );

                                        // Fallback: if not found by references, try to find by EventId (most recent pending)
                                        if (rec == null)
                                        {
                                            var ev = acceptedBet.MatchId.ToString();
                                            rec = records
                                                .Where(r => (string?)r["EventId"] == ev)
                                                .OrderByDescending(r =>
                                                    {
                                                        var ts = (string?)r["TimestampUtc"];
                                                        if (DateTime.TryParse(ts, out var dt)) return dt;
                                                        return DateTime.MinValue;
                                                    }
                                                ).FirstOrDefault();
                                            if (rec != null)
                                            {
                                                Log.Information($"[{cycleLetter}] placed_bets.json: fallback matched record by EventId {ev} for ReferenceId {acceptedBet.ReferenceId}");
                                            }
                                        }

                                        if (rec != null)
                                        {
                                            // Use centralized parser to normalize messy status text when possible.
                                            // We don't always have the full UI blob here, so pass the current status as both
                                            // statusText and fullText and indicate we're in the Settled tab.
                                            string? parsed = TennisScraper.BetStatusParser.ParseStatus(status ?? string.Empty, status ?? string.Empty, "settled");

                                            if (!string.IsNullOrWhiteSpace(parsed))
                                            {
                                                rec["Status"] = parsed;
                                            }
                                            else
                                            {
                                                // Fallback to previous conservative normalization
                                                var su = (status ?? string.Empty).ToUpperInvariant();
                                                rec["Status"] = su.Contains("WON") ? "WON" :
                                                                (su.Contains("LOST") || su.Contains("LOSS")) ? "LOST" :
                                                                su.Contains("VOID") ? "VOID" : (status ?? string.Empty);
                                            }

                                            rec["CompletedAtUtc"] = DateTime.UtcNow.ToString("o");
                                            var newJson = System.Text.Json.JsonSerializer.Serialize(records, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                                            await System.IO.File.WriteAllTextAsync(placedPath, newJson);
                                            Log.Information($"[{cycleLetter}] Updated placed_bets.json for ReferenceId {acceptedBet.ReferenceId} with status {rec["Status"]}");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.Warning($"[{cycleLetter}] Could not update placed_bets.json: {ex.Message}");
                                }

                                // Remove this match from the thread's pending list after settlement
                                lock (threadMatches)
                                {
                                    if (threadMatches.Contains(match))
                                    {
                                        threadMatches.Remove(match);
                                        Log.Information($"[{cycleLetter}] Removed match {match.MatchId} from pending list after settlement.");
                                    }
                                }

                                // Wait 10 seconds before moving to the next bet/cycle
                                Log.Information($"[{cycleLetter}] Waiting 10 seconds before moving to next bet/cycle after settlement.");
                                await Task.Delay(10000);

                                // Exit the polling loop immediately after settlement
                                break;
                            }
                            else
                            {
                                Log.Information($"[{cycleLetter}] Bet not settled yet. Waiting 30 minutes before next check...");
                                await Task.Delay(nextWait);
                            }
                        }
                        BrowserSemaphore.Release();
                        Log.Information($"[{cycleLetter}] Browser access released after settlement.");
                    }));
                }
                await Task.WhenAll(betTasks);
                Log.Information($"==================== CYCLE {cycleLetter} END ====================");
            }
            finally
            {
                lock (ActiveCyclesLock)
                {
                    if (ActiveCycles.Contains(cycleLetter)) ActiveCycles.Remove(cycleLetter);
                }
            }

            return true; // cycle executed
        }

        static void RunPredictor(string predictorPath)
        {
            try
            {
                // If predictorPath exists as a file, run it using dotnet (for DLL files)
                if (File.Exists(predictorPath))
                {
                    using (Process process = new Process())
                    {
                        // Use dotnet to run DLL files (platform-independent)
                        process.StartInfo.FileName = "dotnet";
                        process.StartInfo.Arguments = $"\"{predictorPath}\"";
                        process.StartInfo.UseShellExecute = false;
                        process.StartInfo.RedirectStandardOutput = true;
                        process.StartInfo.RedirectStandardError = true;
                        process.Start();
                        string output = process.StandardOutput.ReadToEnd();
                        string error = process.StandardError.ReadToEnd();
                        if (!process.WaitForExit(120000))
                        {
                            process.Kill(true);
                            throw new TimeoutException($"{predictorPath} timed out.");
                        }
                        Log.Information("Output: Predictor running...");
                        if (!string.IsNullOrEmpty(error)) Log.Information("Predictor error:\n" + error);
                        return;
                    }
                }

                // Otherwise look for a dotnet-built DLL under Predictors/<Name>/bin/Release/net8.0/
                var baseName = Path.GetFileNameWithoutExtension(predictorPath).Replace(".exe", "");
                var candidateDll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? Directory.GetCurrentDirectory(), "Predictors", baseName, "bin", "Release", "net8.0", baseName + ".dll");
                if (File.Exists(candidateDll))
                {
                    using (Process process = new Process())
                    {
                        process.StartInfo.FileName = "dotnet";
                        process.StartInfo.Arguments = $"\"{candidateDll}\"";
                        process.StartInfo.UseShellExecute = false;
                        process.StartInfo.RedirectStandardOutput = true;
                        process.StartInfo.RedirectStandardError = true;
                        process.Start();
                        string output = process.StandardOutput.ReadToEnd();
                        string error = process.StandardError.ReadToEnd();
                        if (!process.WaitForExit(120000))
                        {
                            process.Kill(true);
                            throw new TimeoutException($"dotnet {candidateDll} timed out.");
                        }
                        Log.Information("Output: Predictor (dotnet) running...");
                        if (!string.IsNullOrEmpty(error)) Log.Information("Predictor error:\n" + error);
                        return;
                    }
                }

                Log.Information($"Error running {predictorPath}: file not found and no built predictor dll at {candidateDll}");
            }
            catch (Exception ex)
            {
                Log.Information($"Error running {predictorPath}: {ex.Message}");
            }
        }

        private static bool IsExceeded(string cutoffTime)
        {
            if (DateTimeOffset.TryParse(cutoffTime, null, DateTimeStyles.AdjustToUniversal, out var cutoff))
            { return DateTime.UtcNow > cutoff.UtcDateTime; }
            else
            { return false; }
        
        }

    static async Task<DataRow?> GetMatchDataAsync(MatchModel match)
        {
            DataTable df2;
            df2 = InitializeDataTable();
            DataRow? newRow = null;

            // Note: Doubles filtering now happens before this function is called
            // Fetch home player data from TennisAbstract (with timeout to avoid long stalls)
            var homeDataTuple = await GetDataWithTimeoutAsync(match.CutOffTime, match.HomePlayerName, "Home Name", TimeSpan.FromMinutes(1));
            if (homeDataTuple == null)
            {
                Log.Warning("  ⚠ {Player} not found in TennisAbstract or timed out", match.HomePlayerName);
                return newRow;
            }

            // Fetch away player data from TennisAbstract (with timeout)
            var awayDataTuple = await GetDataWithTimeoutAsync(match.CutOffTime, match.AwayPlayerName, "Away Name", TimeSpan.FromMinutes(1));
            if (awayDataTuple == null)
            {
                Log.Warning("  ⚠ {Player} not found in TennisAbstract or timed out", match.AwayPlayerName);
                return newRow;
            }



            // Combine only the data, not the headers
            var combinedData = new List<string>();
            combinedData.AddRange(homeDataTuple.Item2);
            combinedData.AddRange(awayDataTuple.Item2);
            // Add odds
            combinedData.Add((match.HomeOdds?.ToString()) ?? "0");
            combinedData.Add((match.AwayOdds?.ToString()) ?? "0");

            // Fix percentages & odds
            combinedData = FixMatchPercentages(combinedData);

            // Ensure DataTable has enough columns before creating the DataRow
            for (int i = df2.Columns.Count; i < combinedData.Count; i++)
            {
                var extraName = $"ExtraCol_{i}";
                Log.Warning("Adding missing data column dynamically (pre-row): {Index} -> {Name}", i, extraName);
                df2.Columns.Add(extraName, typeof(string));
            }

            // Now that the table schema matches the data length, create the row and assign
            newRow = df2.NewRow();
            for (int i = 0; i < combinedData.Count; i++)
            {
                newRow[i] = combinedData[i] ?? string.Empty;
            }

            df2.Rows.Add(newRow);
            // Log.Information("Added match {Home} vs {Away} to match wager file.", match.HomePlayerName, match.AwayPlayerName);

            // Write row immediately to CSV (thread-safe)
            return newRow;
        }

        public static bool IsRowEmpty(DataRow row)
        {
            // Null-safe check: return true when the row is null, or when every field
            // is null/empty/whitespace or exactly "," (legacy marker).
            if (row == null) return true;

            return row.ItemArray.All(value =>
            {
                if (value == null) return true;
                var s = value?.ToString();
                if (string.IsNullOrWhiteSpace(s)) return true;
                return s == ",";
            });
        }

        private static void SetupLogger()
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .WriteTo.File(LOG_FILE, rollingInterval: Serilog.RollingInterval.Day)
                .CreateLogger();

            Log.Information("================== Start ==================");
        }

        private static DataTable InitializeDataTable()
        {
            DataTable dt = new DataTable();
            var addedColumns = new HashSet<string>();

            Log.Information($"Starting with number of data columns: {Utils.NewColumnNames.Count}");

            foreach (var col in Utils.NewColumnNames)
            {
                if (!addedColumns.Contains(col))
                {
                    dt.Columns.Add(col, typeof(string));
                    addedColumns.Add(col);
                }
            }

            return dt;
        }

        // Helper: call Scraper.GetDataAsync with an external timeout. The underlying task is not
        // cancellable (GetDataAsync doesn't accept a token), so we await it but only wait up to
        // the timeout; if it doesn't complete we return null and log a warning.
        private async static Task<Tuple<List<string>, List<string>>?> GetDataWithTimeoutAsync(string cutOffTime, string playerName, string playerType, TimeSpan timeout)
        {
            try
            {
                if (Scraper == null)
                {
                    Log.Warning("Scraper instance is null. Cannot get data for {Player}", playerName);
                    return null;
                }
                var task = Scraper.GetDataAsync(cutOffTime, playerName, playerType);
                var delay = Task.Delay(timeout);
                var completed = await Task.WhenAny(task, delay);
                if (completed == task)
                {
                    return await task; // completed within timeout
                }
                else
                {
                    Log.Warning("GetDataAsync for {Player} timed out after {Seconds}s", playerName, timeout.TotalSeconds);
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("GetDataAsync for {Player} failed: {Msg}", playerName, ex.Message);
                return null;
            }
        }

        private static void LoadBlockedMatches()
        {
            lock (BlockedMatchesLock)
            {
                try
                {
                    if (!File.Exists(BlockedMatchesFile))
                    {
                        BlockedMatches = new Dictionary<long, BlockEntry>();
                        return;
                    }

                    var txt = File.ReadAllText(BlockedMatchesFile);

                    try
                    {
                        // Try to parse as the new dictionary form: keys -> BlockEntry
                        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, BlockEntry>>(txt);
                        if (map != null)
                        {
                            var dict = new Dictionary<long, BlockEntry>();
                            foreach (var kv in map)
                            {
                                if (long.TryParse(kv.Key, out var id))
                                {
                                    dict[id] = kv.Value ?? new BlockEntry { Status = "UNKNOWN", Timestamp = DateTime.UtcNow, Count = 1 };
                                }
                            }
                            BlockedMatches = dict;
                            Log.Information("Loaded {Count} blocked matches (map) from {File}", BlockedMatches.Count, BlockedMatchesFile);
                            return;
                        }
                    }
                    catch (Exception)
                    {
                        // fall through to legacy parsing
                    }

                    try
                    {
                        // Legacy format: array of numeric ids
                        var arr = System.Text.Json.JsonSerializer.Deserialize<List<long>>(txt);
                        if (arr != null)
                        {
                            BlockedMatches = arr.ToDictionary(id => id, id => new BlockEntry { Status = "LEGACY", Timestamp = DateTime.UtcNow, Count = 1 });
                            Log.Information("Loaded {Count} blocked matches (legacy) from {File}", BlockedMatches.Count, BlockedMatchesFile);
                            return;
                        }
                    }
                    catch (Exception)
                    {
                        // ignore and backup
                    }

                    // If we reach here, the file was present but unrecognized/malformed -> backup
                    var bak = BlockedMatchesFile + ".bak." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    File.Copy(BlockedMatchesFile, bak, true);
                    Log.Warning("Blocked matches file unrecognized or malformed. Backed up to {FileBak}", bak);
                    BlockedMatches = new Dictionary<long, BlockEntry>();
                }
                catch (Exception ex)
                {
                    Log.Warning("Failed to load blocked matches file: {Msg}", ex.Message);
                    BlockedMatches = new Dictionary<long, BlockEntry>();
                }
            }
        }

        private static void SaveBlockedMatches()
        {
            lock (BlockedMatchesLock)
            {
                try
                {
                    // Serialize as dictionary with string keys to be JSON-friendly
                    var outMap = BlockedMatches.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
                    var txt = System.Text.Json.JsonSerializer.Serialize(outMap, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                    // Backup current file before overwrite
                    if (File.Exists(BlockedMatchesFile))
                    {
                        var bak = BlockedMatchesFile + ".bak." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                        File.Copy(BlockedMatchesFile, bak, true);
                    }

                    File.WriteAllText(BlockedMatchesFile, txt);
                    Log.Information("Saved {Count} blocked matches to {File}", BlockedMatches.Count, BlockedMatchesFile);
                }
                catch (Exception ex)
                {
                    Log.Warning("Failed to save blocked matches file: {Msg}", ex.Message);
                }
            }
        }

        private static bool CheckMatchWinner(DataRow row)
        {
            return !row.Table.Columns.Contains("Match Winner") || string.IsNullOrEmpty(row["Match Winner"]?.ToString());
        }
        
        private static List<string> FixMatchPercentages(List<string> data)
        {
            var fixedData = new List<string>(data);
            var indices = new List<int> { 9, 22, 35, 48, 61, 74, 87, 100, 118, 131, 144, 157, 170, 183, 196, 209 };

            foreach (var index in indices)
            {
                // Ensure all indices we need exist before computing
                if (index - 2 >= 0 && index - 1 >= 0 && index < fixedData.Count)
                    fixedData[index] = ComputeWinPercentage(fixedData[index - 2], fixedData[index - 1]).ToString();

                if (index + 1 < fixedData.Count && index + 2 < fixedData.Count && index + 3 < fixedData.Count)
                    fixedData[index + 3] = ComputeWinPercentage(fixedData[index + 1], fixedData[index + 2]).ToString();

                if (index + 4 < fixedData.Count && index + 5 < fixedData.Count && index + 6 < fixedData.Count)
                    fixedData[index + 6] = ComputeWinPercentage(fixedData[index + 4], fixedData[index + 5]).ToString();
            }

            return fixedData;
        }


        private static int ComputeWinPercentage(string winsStr, string losesStr)
        {
            int wins = int.TryParse(winsStr, out var w) ? w : 0;
            int loses = int.TryParse(losesStr, out var l) ? l : 0;
            int total = wins + loses;

            if (total == 0) return 0;

            double winRatio = (double)wins / total;
            return (int)(winRatio * 10000);
        }

        private static void WriteDataTableHeaders(double firstRowValue, string filePath)
        {
            using (var sw = new StreamWriter(filePath, false))
            {
                // Write the first row as the integer value passed as an argument
                sw.WriteLine(firstRowValue);  // Write the integer as the first row
            }
        }

        private static void WriteDataRowToCsv(DataRow row, string filePath)
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            // Check if file is new or empty
            bool writeHeader = !File.Exists(filePath) || new FileInfo(filePath).Length == 0;

            lock (CsvLock)
            {
                using (var stream = File.Open(filePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream))
                {
                    if (writeHeader)
                    {
                        foreach (var colName in Utils.NewColumnNames)
                            writer.Write(colName + ",");
                        writer.WriteLine();
                    }

                    for (int i = 0; i < row.Table.Columns.Count; i++)
                    {
                        writer.Write(row[i]?.ToString() ?? "");
                        if (i < row.Table.Columns.Count - 1)
                            writer.Write(",");
                    }
                    writer.WriteLine();
                }
            }
        }

        // Robust helper: try to find a stake amount on the provided ConfigReader via common property/field names or accessor methods,
        // returning the fallback when no suitable value is found.
        private static int GetExpectedStake(ConfigReader cfg, int fallback = 10)
        {
            if (cfg == null) return fallback;
            try
            {
                var t = cfg.GetType();

                // Common property names to try
                var propertyNames = new[] { "DefaultStakeAmount", "DefaultStake", "StakeAmount", "DefaultStakeMbtc", "DefaultStakeMilliBTC" };
                foreach (var pn in propertyNames)
                {
                    var prop = t.GetProperty(pn, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                    if (prop != null)
                    {
                        var val = prop.GetValue(cfg);
                        if (val is int iv) return iv;
                        if (val is long lv) return (int)lv;
                        if (val is string sv && int.TryParse(sv, out var parsed)) return parsed;
                    }
                }

                // Try common field names
                foreach (var fn in propertyNames)
                {
                    var field = t.GetField(fn, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                    if (field != null)
                    {
                        var val = field.GetValue(cfg);
                        if (val is int iv2) return iv2;
                        if (val is long lv2) return (int)lv2;
                        if (val is string sv2 && int.TryParse(sv2, out var parsed2)) return parsed2;
                    }
                }

                // Try methods that might provide configuration values: GetInt(name, default) or GetValue(name, default)
                var tryMethods = new[] { "GetInt", "GetValue", "GetIntValue" };
                foreach (var mn in tryMethods)
                {
                    var method = t.GetMethod(mn, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                    if (method != null)
                    {
                        try
                        {
                            var res = method.Invoke(cfg, new object[] { "DefaultStakeAmount", fallback });
                            if (res is int ires) return ires;
                            if (res is long lres) return (int)lres;
                            if (res is string sres && int.TryParse(sres, out var pres)) return pres;
                        }
                        catch { /* ignore invocation failures */ }
                    }
                }
            }
            catch
            {
                // ignore and fall through to fallback
            }

            return fallback;
        }
    }
}
