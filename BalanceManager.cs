using System;
using System.IO;
using System.Threading.Tasks;
using Serilog;
using TennisScraper.Wagering;

namespace TennisScraper
{
    /// <summary>
    /// Manages the betting balance including persistence, updates, and display.
    /// </summary>
    public static class BalanceManager
    {
        private static readonly string BalanceFilePath = "balance.txt";
        private static readonly string StatsFilePath = "balance_stats.txt";
        private static readonly object BalanceLock = new object();
        private static decimal _currentBalance = 0;
        private static decimal _startingBalance = 100m;
        private static int _totalWins = 0;
        private static int _totalLosses = 0;
        private static int _totalTimeouts = 0;
        private static decimal _totalProfit = 0;
        private static decimal _totalLoss = 0;
        private static bool _initialized = false;

        /// <summary>
        /// Initialize the balance manager. Loads from file or sets default.
        /// </summary>
        public static void Initialize(decimal defaultBalance = 100m, ILogger? logger = null)
        {
            lock (BalanceLock)
            {
                if (_initialized)
                {
                    return;
                }

                if (File.Exists(BalanceFilePath))
                {
                    try
                    {
                        string content = File.ReadAllText(BalanceFilePath).Trim();
                        if (decimal.TryParse(content, out decimal loadedBalance))
                        {
                            _currentBalance = loadedBalance;
                            // logger?.Information($"Balance loaded from file: {_currentBalance:F2}");
                        }
                        else
                        {
                            _currentBalance = defaultBalance;
                            logger?.Warning($"Invalid balance in file. Using default: {defaultBalance:F2}");
                            SaveBalance();
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Error($"Error loading balance file: {ex.Message}. Using default: {defaultBalance:F2}");
                        _currentBalance = defaultBalance;
                        SaveBalance();
                    }
                }
                else
                {
                    _currentBalance = defaultBalance;
                    _startingBalance = defaultBalance;
                    logger?.Information($"No balance file found. Starting with default: {defaultBalance:F2}");
                    SaveBalance();
                }

                // Load statistics
                LoadStats();

                _initialized = true;
            }
        }

        /// <summary>
        /// Get the current balance.
        /// </summary>
        public static decimal GetBalance()
        {
            lock (BalanceLock)
            {
                if (!_initialized)
                {
                    Initialize();
                }
                return _currentBalance;
            }
        }

        /// <summary>
        /// Set the balance to a specific value (used for manual adjustments).
        /// </summary>
        public static void SetBalance(decimal newBalance, ILogger? logger = null)
        {
            lock (BalanceLock)
            {
                decimal oldBalance = _currentBalance;
                _currentBalance = newBalance;
                SaveBalance();
                logger?.Information($"Balance updated: {oldBalance:F2} → {_currentBalance:F2}");
            }
        }

        /// <summary>
        /// Add profit to balance (after winning a bet).
        /// </summary>
        public static void AddProfit(decimal profit, string reason = "", ILogger? logger = null)
        {
            lock (BalanceLock)
            {
                decimal oldBalance = _currentBalance;
                _currentBalance += profit;
                _totalWins++;
                _totalProfit += profit;
                SaveBalance();
                SaveStats();
                
                string logMessage = $"✅ Profit added: +{profit:F2}. Balance: {oldBalance:F2} → {_currentBalance:F2}";
                if (!string.IsNullOrEmpty(reason))
                {
                    logMessage += $" ({reason})";
                }
                logger?.Information(logMessage);
            }
        }

        /// <summary>
        /// Deduct loss from balance (after losing a bet).
        /// </summary>
        public static void DeductLoss(decimal loss, string reason = "", ILogger? logger = null)
        {
            lock (BalanceLock)
            {
                decimal oldBalance = _currentBalance;
                _currentBalance -= loss;
                
                if (reason.Contains("TIMEOUT"))
                {
                    _totalTimeouts++;
                }
                else
                {
                    _totalLosses++;
                }
                
                _totalLoss += loss;
                SaveBalance();
                SaveStats();
                
                string logMessage = $"❌ Loss deducted: -{loss:F2}. Balance: {oldBalance:F2} → {_currentBalance:F2}";
                if (!string.IsNullOrEmpty(reason))
                {
                    logMessage += $" ({reason})";
                }
                logger?.Information(logMessage);
            }
        }

        /// <summary>
        /// Process a match result and update balance accordingly.
        /// </summary>
        public static void ProcessMatchResult(
            string status, 
            decimal stake, 
            decimal odds, 
            long matchId,
            string homePlayer,
            string awayPlayer,
            ILogger? logger = null)
        {
            string matchInfo = $"Match {matchId}: {homePlayer} vs {awayPlayer}";
            
            if (status.Contains("WON") || status.Contains("WIN"))
            {
                decimal profit = stake * ((decimal)odds - 1m);
                AddProfit(profit, matchInfo, logger);
            }
            else if (status.Contains("LOST") || status.Contains("LOSS"))
            {
                DeductLoss(stake, matchInfo, logger);
            }
            else if (status == "TIMEOUT")
            {
                // Treat timeout as loss
                DeductLoss(stake, $"{matchInfo} - TIMEOUT", logger);
            }
            else if (status.Contains("HALF_LOSS"))
            {
                decimal halfLoss = stake / 2m;
                DeductLoss(halfLoss, $"{matchInfo} - HALF_LOSS", logger);
            }
            else if (status.Contains("HALF_WIN"))
            {
                decimal halfProfit = (stake * ((decimal)odds - 1m)) / 2m;
                AddProfit(halfProfit, $"{matchInfo} - HALF_WIN", logger);
            }
            else if (status == "VOID" || status == "CANCELLED")
            {
                // No change to balance
                logger?.Information($"⚪ No balance change: {matchInfo} - {status}");
            }
            else
            {
                logger?.Warning($"⚠️ Unknown status '{status}' for {matchInfo}. No balance update.");
            }
        }

        /// <summary>
        /// Display current balance to console and log.
        /// </summary>
        public static void DisplayBalance(string currency = "USD", ILogger? logger = null)
        {
            decimal balance = GetBalance();
            string display = $"\n{'═',40}\nCurrent Balance: {balance:F2} {currency}\n{'═',40}\n";
            
            Console.WriteLine(display);
            logger?.Information($"Current Balance: {balance:F2} {currency}");
        }

        /// <summary>
        /// Display live dashboard with statistics.
        /// </summary>
        public static void DisplayLiveDashboard(string currency = "USD", ILogger? logger = null)
        {
            lock (BalanceLock)
            {
                decimal balance = GetBalance();
                decimal totalPL = balance - _startingBalance;
                int totalBets = _totalWins + _totalLosses + _totalTimeouts;
                double winRate = totalBets > 0 ? (double)_totalWins / totalBets * 100 : 0;
                double roi = _startingBalance > 0 ? (double)totalPL / (double)_startingBalance * 100 : 0;
                
                var dashboard = new System.Text.StringBuilder();
                dashboard.AppendLine();
                dashboard.AppendLine("╔════════════════════════════════════════════════════╗");
                dashboard.AppendLine("║          LIVE BETTING DASHBOARD                    ║");
                dashboard.AppendLine("╠════════════════════════════════════════════════════╣");
                dashboard.AppendLine($"║ Current Balance:     ${balance,25:F2} {currency,-4} ║");
                dashboard.AppendLine($"║ Starting Balance:    ${_startingBalance,25:F2} {currency,-4} ║");
                dashboard.AppendLine($"║ Total P/L:           ${totalPL,25:F2}       ║");
                dashboard.AppendLine($"║ ROI:                 {roi,25:F2}%      ║");
                dashboard.AppendLine("╠════════════════════════════════════════════════════╣");
                dashboard.AppendLine($"║ Total Bets:          {totalBets,30}       ║");
                dashboard.AppendLine($"║ Wins:                {_totalWins,30} ✅    ║");
                dashboard.AppendLine($"║ Losses:              {_totalLosses,30} ❌    ║");
                dashboard.AppendLine($"║ Timeouts:            {_totalTimeouts,30} ⏱️    ║");
                dashboard.AppendLine($"║ Win Rate:            {winRate,29:F1}%      ║");
                dashboard.AppendLine("╠════════════════════════════════════════════════════╣");
                dashboard.AppendLine($"║ Total Profit:        ${_totalProfit,25:F2}       ║");
                dashboard.AppendLine($"║ Total Loss:          ${_totalLoss,25:F2}       ║");
                dashboard.AppendLine("╠════════════════════════════════════════════════════╣");
                dashboard.AppendLine($"║ Last Updated:        {DateTime.Now:yyyy-MM-dd HH:mm:ss}          ║");
                dashboard.AppendLine("╚════════════════════════════════════════════════════╝");
                dashboard.AppendLine();
                
                string output = dashboard.ToString();
                Console.WriteLine(output);
                logger?.Information("Dashboard displayed");
            }
        }

        /// <summary>
        /// Save balance to file.
        /// </summary>
        private static void SaveBalance()
        {
            try
            {
                File.WriteAllText(BalanceFilePath, _currentBalance.ToString("F2"));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error saving balance: {ex.Message}");
            }
        }

        /// <summary>
        /// Save statistics to file.
        /// </summary>
        private static void SaveStats()
        {
            try
            {
                var stats = new System.Text.StringBuilder();
                stats.AppendLine($"StartingBalance={_startingBalance:F2}");
                stats.AppendLine($"TotalWins={_totalWins}");
                stats.AppendLine($"TotalLosses={_totalLosses}");
                stats.AppendLine($"TotalTimeouts={_totalTimeouts}");
                stats.AppendLine($"TotalProfit={_totalProfit:F2}");
                stats.AppendLine($"TotalLoss={_totalLoss:F2}");
                
                File.WriteAllText(StatsFilePath, stats.ToString());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error saving stats: {ex.Message}");
            }
        }

        /// <summary>
        /// Load statistics from file.
        /// </summary>
        private static void LoadStats()
        {
            try
            {
                if (File.Exists(StatsFilePath))
                {
                    var lines = File.ReadAllLines(StatsFilePath);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('=');
                        if (parts.Length == 2)
                        {
                            var key = parts[0].Trim();
                            var value = parts[1].Trim();
                            
                            switch (key)
                            {
                                case "StartingBalance":
                                    if (decimal.TryParse(value, out decimal sb)) _startingBalance = sb;
                                    break;
                                case "TotalWins":
                                    if (int.TryParse(value, out int tw)) _totalWins = tw;
                                    break;
                                case "TotalLosses":
                                    if (int.TryParse(value, out int tl)) _totalLosses = tl;
                                    break;
                                case "TotalTimeouts":
                                    if (int.TryParse(value, out int tt)) _totalTimeouts = tt;
                                    break;
                                case "TotalProfit":
                                    if (decimal.TryParse(value, out decimal tp)) _totalProfit = tp;
                                    break;
                                case "TotalLoss":
                                    if (decimal.TryParse(value, out decimal tlo)) _totalLoss = tlo;
                                    break;
                            }
                        }
                    }
                }
                else
                {
                    // Initialize with current balance as starting balance
                    _startingBalance = _currentBalance;
                    SaveStats();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading stats: {ex.Message}");
            }
        }

        /// <summary>
        /// Reset all statistics (keeps current balance).
        /// </summary>
        public static void ResetStats(ILogger? logger = null)
        {
            lock (BalanceLock)
            {
                _startingBalance = _currentBalance;
                _totalWins = 0;
                _totalLosses = 0;
                _totalTimeouts = 0;
                _totalProfit = 0;
                _totalLoss = 0;
                SaveStats();
                logger?.Information("Statistics reset");
            }
        }

        /// <summary>
        /// Get balance file path (for testing/debugging).
        /// </summary>
        public static string GetBalanceFilePath() => BalanceFilePath;

        /// <summary>
        /// Check if balance is sufficient for a bet.
        /// </summary>
        public static bool HasSufficientBalance(decimal requiredAmount, ILogger? logger = null)
        {
            decimal currentBalance = GetBalance();
            bool sufficient = currentBalance >= requiredAmount;
            
            if (!sufficient)
            {
                logger?.Warning($"Insufficient balance: Required {requiredAmount:F2}, Available {currentBalance:F2}");
            }
            
            return sufficient;
        }

        /// <summary>
        /// Calculate available balance for new cycle (considering percentage).
        /// </summary>
        public static decimal CalculateCycleAllocation(double percentWagerPerCycle, ILogger? logger = null)
        {
            decimal balance = GetBalance();
            decimal allocation = balance * (decimal)percentWagerPerCycle;
            
            logger?.Information($"Cycle allocation: {allocation:F2} ({percentWagerPerCycle:P0} of {balance:F2})");
            
            return allocation;
        }

        /// <summary>
        /// Check real balance on Dexsport and compare with tracked balance.
        /// </summary>
        public static async Task<(decimal trackedBalance, decimal siteBalance, decimal difference)> CheckSiteBalanceAsync(
            IPlacementProvider? placementProvider = null, 
            string currency = "USD", 
            ILogger? logger = null)
        {
            try
            {
                decimal trackedBalance = GetBalance();
                
                logger?.Information("Fetching balance from Dexsport...");
                decimal siteBalance = 0m;
                
                if (placementProvider != null)
                {
                    siteBalance = await placementProvider.GetBalanceAsync(currency);
                }
                
                decimal difference = trackedBalance - siteBalance;
                
                logger?.Information($"Balance Check:");
                logger?.Information($"  Tracked Balance:  ${trackedBalance:F2}");
                logger?.Information($"  Dexsport Balance: ${siteBalance:F2}");
                logger?.Information($"  Difference:       ${difference:F2}");
                
                if (Math.Abs(difference) > 0.01m)
                {
                    if (difference > 0)
                    {
                        logger?.Warning($"⚠️ Tracked balance is ${difference:F2} HIGHER than Dexsport");
                    }
                    else
                    {
                        logger?.Warning($"⚠️ Tracked balance is ${Math.Abs(difference):F2} LOWER than Dexsport");
                    }
                }
                else
                {
                    logger?.Information("✅ Balances match!");
                }
                
                return (trackedBalance, siteBalance, difference);
            }
            catch (Exception ex)
            {
                logger?.Error($"Error checking site balance: {ex.Message}");
                return (GetBalance(), 0m, 0m);
            }
        }

        /// <summary>
        /// Display balance comparison between tracked and site balance.
        /// </summary>
        public static async Task DisplayBalanceComparisonAsync(
            IPlacementProvider? placementProvider = null,
            string currency = "USD",
            ILogger? logger = null)
        {
            var (tracked, site, diff) = await CheckSiteBalanceAsync(placementProvider, currency, logger);
            
            var display = new System.Text.StringBuilder();
            display.AppendLine();
            display.AppendLine("╔════════════════════════════════════════════════════╗");
            display.AppendLine("║          BALANCE COMPARISON                        ║");
            display.AppendLine("╠════════════════════════════════════════════════════╣");
            display.AppendLine($"║ Tracked Balance:     ${tracked,25:F2} {currency,-4} ║");
            display.AppendLine($"║ Dexsport Balance:    ${site,25:F2} {currency,-4} ║");
            display.AppendLine("╠════════════════════════════════════════════════════╣");
            
            if (Math.Abs(diff) > 0.01m)
            {
                string status = diff > 0 ? "HIGHER ⬆️" : "LOWER ⬇️";
                display.AppendLine($"║ Difference:          ${Math.Abs(diff),25:F2} {status,-6} ║");
                
                if (diff > 0)
                {
                    display.AppendLine("║ ⚠️  Local tracking shows MORE money than site     ║");
                }
                else
                {
                    display.AppendLine("║ ⚠️  Local tracking shows LESS money than site     ║");
                }
            }
            else
            {
                display.AppendLine("║ ✅ Balances Match!                                  ║");
            }
            
            display.AppendLine("╚════════════════════════════════════════════════════╝");
            display.AppendLine();
            
            string output = display.ToString();
            // Console.WriteLine(output);
            logger?.Information("Balance comparison displayed");
        }

        /// <summary>
        /// Sync local balance with site balance (use with caution).
        /// </summary>
        public static async Task<bool> SyncWithSiteBalanceAsync(
            IPlacementProvider? placementProvider = null,
            string currency = "USD",
            ILogger? logger = null)
        {
            try
            {
                logger?.Information("Syncing local balance with Dexsport...");
                
                if (placementProvider == null)
                {
                    logger?.Error("PlacementProvider is required for syncing");
                    return false;
                }
                
                decimal siteBalance = await placementProvider.GetBalanceAsync(currency);
                
                if (siteBalance <= 0)
                {
                    logger?.Warning("Could not fetch valid balance from Dexsport");
                    return false;
                }
                
                decimal oldBalance = GetBalance();
                SetBalance(siteBalance, logger);
                
                logger?.Information($"✅ Balance synced: ${oldBalance:F2} → ${siteBalance:F2}");
                return true;
            }
            catch (Exception ex)
            {
                logger?.Error($"Error syncing balance: {ex.Message}");
                return false;
            }
        }
    }
}
