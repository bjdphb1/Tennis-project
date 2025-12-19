using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TennisScraper;
using TennisScraper.Models;
using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;

namespace TennisScraper.Wagering
{
    /// <summary>
    /// One entry per match in the cycle dictionary.
    /// matchObj: any object representing the match (you said match)
    /// winner: 0 => home, 1 => away
    /// winnerOdds: the odds at time of decision
    /// referenceId: uuid/reference returned by the API (may be null until placed)
    /// stake: stake amount
    /// currency: e.g. "PLAY_EUR"
    /// retryCount: internal counter for retries
    /// </summary>
    public class MatchBetEntry
    {
        public MatchModel Match { get; set; }
        public int Winner { get; set; }
        public double WinnerOdds { get; set; }
        public string ReferenceId { get; set; } = "";
    public decimal Stake { get; set; } = 1;
    public string Currency { get; set; } = "PLAY_USD";
        public int RetryCount { get; set; } = 0;
        public int RejectionCount { get; set; } = 0;
        public bool Debug { get; set; } = false;
    }

    /// <summary>
    /// Lightweight result from PlaceBetAsync
    /// </summary>
    public class PlaceBetResult
    {
        public string ReferenceId { get; set; } = "";
        public string Status { get; set; } = ""; // e.g. "ACCEPTED", "PENDING_ACCEPTANCE", "PRICE_ABOVE_MARKET", ...
        public string MarketUrl { get; set; } = "";
        public double Price { get; set; } = 0;
    }

    /// <summary>
    /// Lightweight result from GetBetStatusAsync
    /// </summary>


    public static class WageringManager
    {
        public class CriticalFailureEntry
        {
            public long MatchId { get; set; }
            public string Status { get; set; }
        }

        public class CheckDictResult
        {
            public List<MatchModel> AcceptedBets { get; set; } = new List<MatchModel>();
            public bool Aborted { get; set; } = false; // true when critical failure caused an abort
            public List<CriticalFailureEntry> CriticalFailures { get; set; } = new List<CriticalFailureEntry>();
        }
        // Configurable parameters
        private const int MAX_RETRY_FOR_PRICE_OR_STAKE = 2;   // number of times we re-attempt PRICE_ABOVE_MARKET / STAKE_ABOVE_MAX re-bet
        private const int MAX_REJECTION_RETRIES = 2;         // if rejected twice, drop
        private static readonly TimeSpan SHORT_RETRY_DELAY = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PENDING_FIRST_WAIT = TimeSpan.FromMinutes(5); // initial wait before checking pending bets (changed from 1 to 5 minutes)
        private static readonly TimeSpan PENDING_SUBSEQUENT_WAIT = TimeSpan.FromMinutes(30); // check every 30 minutes for match result (changed from 1 to 30 minutes)
        private static readonly TimeSpan POLL_SETTLED_WAIT = TimeSpan.FromMinutes(30); // when waiting for results, poll every 30 minutes (changed from 1 to 30 minutes)

        /// <summary>
        /// Checks and places bets for matchDict. This method:
        /// - Places bets for all items in matchDict
        /// - Handles immediate placement responses (ACCEPTED, PENDING_ACCEPTANCE, PRICE_ABOVE_MARKET, STAKE_ABOVE_MAX, etc.)
        /// - Retries rejections a couple times
        /// - Returns a filtered dictionary where all surviving bets are currently ACCEPTED or PENDING (and reference IDs are set)
        ///
        /// <paramref name="cutoffTimeUtcSeconds"/> not used directly in placing bets but might be used by caller.
        /// </summary>
        public static async Task<CheckDictResult> CheckDictAsync(
            ILogger Log,
            List<MatchModel> matchDict,
            IPlacementProvider? placementProvider = null,
            Action<string>? logInfo = null,
            Action<string>? logError = null,
            CancellationToken cancellationToken = default
        )
        {
            logInfo ??= s => Log.Information(s);
            logError ??= s => Log.Information(s);

            // local structures
            var pendingDict = new List<MatchModel>();
            var rejectedDict = new List<MatchModel>();
            bool abortEverything = false;
            var criticalFailures = new List<CriticalFailureEntry>();

            // Ensure we have a placement provider (defaults to Dexsport Selenium provider).
            placementProvider ??= new DexsportPlacementProvider(Log);

            // --- 1) Place bets for everything in matchDict ---
            logInfo($"[CheckDict] Placing initial bets for {matchDict.Count} matches...");

            foreach (var match in matchDict)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // call into your Cloudbet API wrapper; expected to return referenceId and status
                    // signature assumed: PlaceBetAsync(eventId, winner, winnerOdds, uuid, stake, currency) 
                    // but we use the delegate passed as parameter to keep this method independent.
                    string[] winners = { "home", "away" };
                    string winner = winners[match.Winner];

                    double[] Odds = { match.HomeOdds ?? 0, match.AwayOdds ?? 0 };
                    double WinnerOdds = Odds[match.Winner];
                    

                    // Use converted "Last First" format names for bet placement (matches Dexsport website display)
                    var placeResult = await placementProvider.PlaceBetAsync(match.MatchId, winner, WinnerOdds, match.ReferenceId, (double)match.Stake, match.Currency, match.HomePlayerName, match.AwayPlayerName);
                    match.ReferenceId = placeResult.ReferenceId ?? match.ReferenceId;

                    string status = (placeResult.Status ?? "").ToUpperInvariant();
                    logInfo($"[Place] Match {match.MatchId}: initial status = {status} (ref={match.ReferenceId})");

                    // handle critical statuses -> abort all further processing
                    if (IsCriticalFailure(status))
                    {
                        logError($"[Place] Critical status '{status}' for match {match.MatchId}. Aborting cycle.");
                        // record which match failed critically
                        criticalFailures.Add(new CriticalFailureEntry { MatchId = match.MatchId, Status = status });
                        abortEverything = true;
                        break;
                    }

                    switch (status)
                    {
                        case "ACCEPTED":
                            // Good — keep it as accepted. We don't need to add to pending yet.
                            break;

                        case "PENDING_ACCEPTANCE":
                            pendingDict.Add(match);
                            break;

                        case "PRICE_ABOVE_MARKET":
                        case "STAKE_ABOVE_MAX":
                            // Update stake or odds as necessary, then try again (retry limited)
                            if (match.RetryCount >= MAX_RETRY_FOR_PRICE_OR_STAKE)
                            {
                                logError($"[Place] Match {match.MatchId}: exceeded retry limit for {status}. Marking as rejected.");
                                rejectedDict.Add(match);
                            }
                            else
                            {
                                match.RetryCount++;
                                // Update entry fields if API provided a new price/market in placeResult (we return price)
                                if (placeResult.Price > 0)
                                {


                                    WinnerOdds = placeResult.Price;
                                }
                                // Adjust stake if stake too big — shrink by 10% as python did for STAKE_ABOVE_MAX
                                if (status == "STAKE_ABOVE_MAX")
                                {
                                    match.Stake = decimal.Round(match.Stake * 0.90m, 8);
                                    logInfo($"[Place] Match {match.MatchId}: STAKE_ABOVE_MAX -> new stake {match.Stake}");
                                }

                                // add to pending so we will re-place/handle them later
                                rejectedDict.Add(match);
                            }
                            break;

                        case "REJECTED":
                            rejectedDict.Add(match);
                            break;

                        case "STAKE_BELOW_MIN":
                            // bad predictor output; permanently ignore this match
                            logError($"[Place] Match {match.MatchId}: STAKE_BELOW_MIN. Removing from cycle.");
                            //.Add(match);
                            if (matchDict.Contains(match)) matchDict.Remove(match);
                            if (pendingDict.Contains(match)) pendingDict.Remove(match);
                            break;

                        case "PUSH":
                            // market not applicable — remove
                            logInfo($"[Place] Match {match.MatchId}: PUSH. Removing from cycle.");
                            if (matchDict.Contains(match)) matchDict.Remove(match);
                            if (pendingDict.Contains(match)) pendingDict.Remove(match);
                            break;

                        default:
                            // Unknown but not critical — treat as pending to be safe
                            pendingDict.Add(match);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    logError($"[Place] Exception placing bet for match {match.MatchId}: {ex.Message}");
                    // treat as transient error — we'll retry later. Add to pending to reprocess.
                    pendingDict.Add(match);
                }
            }

                if (abortEverything)
                {
                    // If a critical failure happened, return early and do not proceed.
                    logError("[CheckDict] Aborting cycle due to critical failure reported by API.");
                    return new CheckDictResult { AcceptedBets = new List<MatchModel>(), Aborted = true, CriticalFailures = criticalFailures };
                }

            // remove permanently invalid matches

            // If any rejected entries exist — attempt to re-place them once immediately
            if (rejectedDict.Count > 0)
            {
                logInfo($"[CheckDict] Retrying {rejectedDict.Count} initially rejected matches...");

                
                foreach (var match in rejectedDict)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double[] Odds = { match.HomeOdds ?? 0, match.AwayOdds ?? 0 };
                    double WinnerOdds = Odds[match.Winner];

                    try
                    {
                        string[] winners = { "home", "away" };
                        string winner = winners[match.Winner];
                        // Use converted "Last First" format names for bet placement (matches Dexsport website display)
                        var placeResult = await placementProvider.PlaceBetAsync(match.MatchId, winner, WinnerOdds, match.ReferenceId, (double)match.Stake, match.Currency, match.HomePlayerName, match.AwayPlayerName);
                        match.ReferenceId = placeResult.ReferenceId ?? match.ReferenceId;
                        string status = (placeResult.Status ?? "").ToUpperInvariant();
                        logInfo($"[RetryPlace] Match {match.MatchId}: status = {status}");

                        if (IsCriticalFailure(status))
                        {
                            logError($"[RetryPlace] Critical status '{status}'. Aborting cycle.");
                            criticalFailures.Add(new CriticalFailureEntry { MatchId = match.MatchId, Status = status });
                            abortEverything = true;
                            break;
                        }

                        if (status == "ACCEPTED")
                        {
                            // good
                            rejectedDict.Remove(match);
                        }
                        else if (status == "PENDING_ACCEPTANCE")
                        {
                            pendingDict.Add(match);
                            rejectedDict.Remove(match);
                        }
                        else if (status == "PRICE_ABOVE_MARKET" || status == "STAKE_ABOVE_MAX")
                        {
                            match.RejectionCount++;
                            if (match.RejectionCount > MAX_REJECTION_RETRIES)
                            {
                                logError($"[RetryPlace] Match {match.MatchId}: too many rejections. Removing.");
                                rejectedDict.Remove(match);
                                matchDict.Remove(match);
                            }
                            else
                            {
                                // keep in rejectedDict for next pass
                                rejectedDict.Add(match);
                            }
                        }
                        else if (status == "REJECTED")
                        {
                            match.RejectionCount++;
                            if (match.RejectionCount > MAX_REJECTION_RETRIES)
                            {
                                logError($"[RetryPlace] Match {match.MatchId}: rejected repeatedly. Removing.");
                                rejectedDict.Remove(match);
                                matchDict.Remove(match);
                            }
                            else
                            {
                                // try later
                                rejectedDict.Add(match);
                            }
                        }
                        else if (status == "STAKE_BELOW_MIN")
                        {
                            logError($"[RetryPlace] Match {match.MatchId}: STAKE_BELOW_MIN on retry. Removing.");
                            rejectedDict.Remove(match);
                            matchDict.Remove(match);
                        }
                        else
                        {
                            // treat as pending
                            pendingDict.Add(match);
                            rejectedDict.Remove(match);
                        }
                    }
                    catch (Exception ex)
                    {
                        logError($"[RetryPlace] Exception retrying match {match.MatchId}: {ex.Message}");
                        // keep it in rejectedDict for another attempted retry later
                        // (we'll wait and re-check pending/rejected next)
                    }

                    // small delay to avoid hammering the API
                    await Task.Delay(500, cancellationToken);
                }

                if (abortEverything)
                {
                    logError("[CheckDict] Aborting cycle due to critical failure during rejected retries.");
                    return new CheckDictResult { AcceptedBets = new List<MatchModel>(), Aborted = true, CriticalFailures = criticalFailures };
                }
            }

            // Now we have pendingDict and accepted ones remain in matchDict (but may not have ref ids)
            // Move accepted ones into a map of active accepted bets with reference ids
            var acceptedMap = new List<MatchModel>();
            foreach (var match in matchDict)
            {
                // If an entry was NOT in pendingDict or rejectedDict nor marked to remove, treat it as accepted only if API told us ACCEPTED earlier.
                // But we didn't keep an "accepted" list earlier; we can consider that entries that are NOT in pendingDict and still in matchDict are accepted.
                if (!pendingDict.Contains(match) && !rejectedDict.Contains(match) && matchDict.Contains(match))
                {
                    acceptedMap.Add(match);
                }
            }

            // Merge acceptedMap and pendingDict into currentActive for follow-up processing
            var currentActive = new List<MatchModel>();
            foreach (var match in acceptedMap) currentActive.Add(match);
            foreach (var match in pendingDict) currentActive.Add(match);

            // --- 2) Poll pending bets until everything is either ACCEPTED or removed/rejected ---
            // We will loop: check pending -> if still pending re-add for next check -> handle rejections and price/stake adjustments
            int globalPendingCycle = 0;
            var transientPending = new List<MatchModel>(pendingDict); // pending we need to check
            while (transientPending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                globalPendingCycle++;
                logInfo($"[PendingLoop] Checking {transientPending.Count} pending bets (cycle {globalPendingCycle})...");

                var stillPending = new List<MatchModel>();

                foreach (var match in transientPending)
                {

                    try
                    {
                        // If no referenceId present, we might need to re-place. But prefer to call status if we have referenceId
                        BetStatusResult statusRes = null;
                        if (!string.IsNullOrEmpty(match.ReferenceId))
                        {
                            statusRes = await placementProvider.GetBetStatusAsync(match.ReferenceId);
                        }
                        else
                        {
                            // no reference id -> attempt placing again
                            string[] winners = { "home", "away" };
                            string winner = winners[match.Winner];

                            double[] Odds = { match.HomeOdds ?? 0, match.AwayOdds ?? 0 };
                            double WinnerOdds = Odds[match.Winner];
                            // Use converted "Last First" format names for bet placement (matches Dexsport website display)
                            var placeRes = await placementProvider.PlaceBetAsync(match.MatchId, winner, WinnerOdds, match.ReferenceId, (double)match.Stake, match.Currency, match.HomePlayerName, match.AwayPlayerName);
                            match.ReferenceId = placeRes.ReferenceId ?? match.ReferenceId;
                            statusRes = new BetStatusResult { Status = (placeRes.Status ?? "").ToUpperInvariant(), Price = placeRes.Price, MarketUrl = placeRes.MarketUrl };
                        }

                        string status = (statusRes?.Status ?? "").ToUpperInvariant();
                        logInfo($"[PendingCheck] Match {match.MatchId}: status={status}");

                                if (IsCriticalFailure(status))
                                {
                                    logError($"[PendingCheck] Critical '{status}' for match {match.MatchId}. Aborting entire cycle.");
                                    criticalFailures.Add(new CriticalFailureEntry { MatchId = match.MatchId, Status = status });
                                    abortEverything = true;
                                    break;
                                }

                        if (status == "ACCEPTED" || status == "OPEN") // OPEN often indicates placed/accepted but not settled
                        {
                            // move to acceptedMap
                            acceptedMap.Add(match);
                        }
                        else if (status == "PENDING_ACCEPTANCE")
                        {
                            // remains pending; will be checked in next cycle
                            stillPending.Add(match);
                        }
                        else if (status == "PRICE_ABOVE_MARKET" || status == "STAKE_ABOVE_MAX")
                        {
                            // update info and attempt to re-place (limited retries)
                            match.RejectionCount++;
                            if (match.RejectionCount > MAX_REJECTION_RETRIES)
                            {
                                logError($"[PendingCheck] Match {match.MatchId} too many PRICE/STAKE adjustments -> removing.");
                                matchDict.Remove(match);
                                continue;
                            }

                            double[] Odds = { match.HomeOdds ?? 0, match.AwayOdds ?? 0 };
                            double WinnerOdds = Odds[match.Winner];

                            // Update odds if returned
                            if (statusRes?.Price > 0) WinnerOdds = statusRes.Price;

                            if (status == "STAKE_ABOVE_MAX")
                            {
                                match.Stake = decimal.Round(match.Stake * 0.90m, 8);
                                logInfo($"[PendingCheck] Match {match.MatchId}: lowered stake to {match.Stake}");
                            }

                            // Re-place bet
                            try
                            {
                                string[] winners = { "home", "away" };
                                string winner = winners[match.Winner];
                                // Use converted "Last First" format names for bet placement (matches Dexsport website display)
                                var placeResult = await placementProvider.PlaceBetAsync(match.MatchId, winner, WinnerOdds, match.ReferenceId, (double)match.Stake, match.Currency, match.HomePlayerName, match.AwayPlayerName);
                                match.ReferenceId = placeResult.ReferenceId ?? match.ReferenceId;
                                string newStatus = (placeResult.Status ?? "").ToUpperInvariant();
                                logInfo($"[PendingCheck] Match {match}: re-place status={newStatus}");
                                if (IsCriticalFailure(newStatus))
                                {
                                    logError($"[PendingCheck] Critical '{newStatus}' for match {match.MatchId}. Aborting.");
                                    abortEverything = true;
                                    break;
                                }
                                if (newStatus == "ACCEPTED")
                                {
                                    acceptedMap.Add(match);
                                }
                                else if (newStatus == "PENDING_ACCEPTANCE")
                                {
                                    stillPending.Add(match);
                                }
                                else
                                {
                                    // if rejected again, keep in stillPending or drop depending on retry counts
                                    stillPending.Add(match);
                                }
                            }
                            catch (Exception pex)
                            {
                                logError($"[PendingCheck] Exception re-placing match {match.MatchId}: {pex.Message}. Will try later.");
                                stillPending.Add(match);
                            }
                        }
                        else if (status == "REJECTED")
                        {
                            match.RejectionCount++;
                            if (match.RejectionCount > MAX_REJECTION_RETRIES)
                            {
                                logError($"[PendingCheck] Match {match.MatchId}: rejected repeatedly ({match.RejectionCount}). Removing.");
                                matchDict.Remove(match);
                            }
                            else
                            {
                                // try to re-place
                                try
                                {
                                    string[] winners = { "home", "away" };
                                    string winner = winners[match.Winner];

                                    double[] Odds = { match.HomeOdds ?? 0, match.AwayOdds ?? 0 };
                                    double WinnerOdds = Odds[match.Winner];
                                    // Use converted "Last First" format names for bet placement (matches Dexsport website display)
                                    var placeResult = await placementProvider.PlaceBetAsync(match.MatchId, winner, WinnerOdds, match.ReferenceId, (double)match.Stake, match.Currency, match.HomePlayerName, match.AwayPlayerName);
                                    match.ReferenceId = placeResult.ReferenceId ?? match.ReferenceId;
                                    string newStatus = (placeResult.Status ?? "").ToUpperInvariant();
                                    if (newStatus == "ACCEPTED") acceptedMap.Add(match);
                                    else stillPending.Add(match);
                                }
                                catch
                                {
                                    stillPending.Add(match);
                                }
                            }
                        }
                        else if (status == "STAKE_BELOW_MIN")
                        {
                            // irrecoverable for this predictor row - remove
                            logError($"[PendingCheck] Match {match.MatchId}: STAKE_BELOW_MIN. Removing.");
                            matchDict.Remove(match);
                        }
                        else
                        {
                            // unknown state — keep pending to check later
                            stillPending.Add(match);
                        }
                    }
                    catch (Exception ex)
                    {
                        logError($"[PendingCheck] Exception checking status for match {match.MatchId}: {ex.Message}. Will retry.");
                        stillPending.Add(match);
                    }

                    // small pacing
                    await Task.Delay(300, cancellationToken);
                } // foreach transientPending

                if (abortEverything)
                {
                    logError("[PendingLoop] Aborting due to critical failure.");
                    return new CheckDictResult { AcceptedBets = new List<MatchModel>(), Aborted = true, CriticalFailures = criticalFailures };
                }

                // prepare next transientPending
                transientPending = stillPending;

                if (transientPending.Count > 0)
                {
                    // Wait before next pending check (first wait shorter)
                    var wait = (globalPendingCycle == 1) ? PENDING_FIRST_WAIT : PENDING_SUBSEQUENT_WAIT;
                    logInfo($"[PendingLoop] {transientPending.Count} still pending. Waiting {wait.TotalSeconds} seconds before next check.");
                    await Task.Delay(wait, cancellationToken);
                }
            } // while pending

            // At this stage, acceptedMap contains bets that are accepted (live).
            // Filter matchDict to only those matches that are acceptedMap keys


            logInfo($"[CheckDict] Placing/acceptance phase finished. {acceptedMap.Count} bets accepted/active.");

            // --- 3) Sleep until nearest cutoff + 1.5h, then poll for settlement (results) ---
            // The caller asked that we sleep until nearest cutoff + 1.5h and then poll every 15 minutes.
            // We won't perform the sleep here — instead return acceptedFinal and let caller call WaitForSettlementsAsync(acceptedFinal)
            // Alternatively, you can include the settlement loop here. I provide a helper below to do that.

            return new CheckDictResult { AcceptedBets = acceptedMap, Aborted = false, CriticalFailures = criticalFailures };
        }

        /// <summary>
        /// Helper to detect if a status indicates immediate abort/critical failure.
        /// </summary>
        private static bool IsCriticalFailure(string status)
        {
            switch (status?.ToUpperInvariant())
            {
                case "VERIFICATION_REQUIRED":
                case "MARKET_SUSPENDED":
                case "RESTRICTED":
                case "INSUFFICIENT_FUNDS":
                case "INTERNAL_SERVER_ERROR":
                case "ERROR_HTTP_BADREQUEST":
                case "ERROR_HTTP_FORBIDDEN":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// After bets are accepted, this helper polls until all bets in acceptedBets are settled.
        /// It returns a dictionary mapping matchId -> final bet status (e.g. WON/LOST/HALF_LOSS/etc.)
        /// Callers may choose to run this in background for each cycle.
        /// </summary>
        public static async Task<Dictionary<long, string>> WaitForSettlementsAsync(
            ILogger Log,
            List<MatchModel> acceptedBets,
            double matchTimeoutHours, // from config.MatchTimeoutDuration
            IPlacementProvider? placementProvider = null,
            Action<string>? logInfo = null,
            Action<string>? logError = null,
            CancellationToken cancellationToken = default)
        {
            logInfo ??= s => Console.WriteLine(s);
            logError ??= s => Console.Error.WriteLine(s);

            placementProvider ??= new DexsportPlacementProvider(Log);

            var results = new Dictionary<long, string>();
            var toCheck = new List<MatchModel>(acceptedBets);

            if (toCheck.Count == 0)
            {
                logInfo("[WaitForSettlements] No bets to track.");
                return results;
            }

            // Find the nearest cutoff
            var nearestCutoff = acceptedBets
            .Select(m => DateTimeOffset.Parse(m.CutOffTime, null, DateTimeStyles.AdjustToUniversal))
            .Min();
            
            var wakeTime = nearestCutoff.AddHours(1.5);
            var sleepUntil = wakeTime - DateTimeOffset.UtcNow;

            if (sleepUntil > TimeSpan.Zero)
            {
                logInfo($"[WaitForSettlements] Sleeping until {wakeTime:u} (cutoff+1.5h) before polling.");
                await Task.Delay(sleepUntil, cancellationToken);
            }
            else
            {
                logInfo("[WaitForSettlements] Cutoff+1.5h already passed, starting polling immediately.");
            }

            // 2. Polling loop
            var maxRetries = (int)((matchTimeoutHours * 60) / 15);
            int retryCount = 0;

            while (toCheck.Count > 0 && retryCount <= maxRetries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stillWaiting = new List<MatchModel>();

                foreach (var match in toCheck)
                {
                    
                    try
                    {
                        if (string.IsNullOrEmpty(match.ReferenceId))
                        {
                            logError($"[WaitForSettlements] Missing referenceId for match {match.MatchId}. Skipping.");
                            // var match2 = new MatchModel(match.MatchId, match.HomePlayerName, match.AwayPlayerName, match.HomeOdds, match.AwayOdds, match.InitCutOffTime);
                            results[match.MatchId] = "MISSING_REF";
                            continue;
                        }

                        var statusRes = await placementProvider.GetBetStatusAsync(match.ReferenceId);
                        var status = (statusRes?.Status ?? "").ToUpperInvariant();
                        logInfo($"[WaitForSettlements] Match {match.MatchId}: status={status}");

                        if (status.Contains("WON") || status.Contains("WIN") ||
                            status.Contains("LOSS") || status.Contains("LOST") ||
                            status == "SETTLED")
                        {
                            // final result
                            results[match.MatchId] = status;
                        }
                        else
                        {
                            stillWaiting.Add(match);
                        }
                    }
                    catch (Exception ex)
                    {
                        logError($"[WaitForSettlements] Exception checking settlement for {match.MatchId}: {ex.Message}");
                        stillWaiting.Add(match);
                    }

                    // tiny stagger between calls
                    await Task.Delay(300, cancellationToken);
                }

                if (stillWaiting.Count == 0)
                {
                    break;
                }

                retryCount++;
                if (retryCount > maxRetries)
                {
                    logError($"[WaitForSettlements] Max retries ({maxRetries}) exceeded. Giving up on {stillWaiting.Count} matches.");
                    foreach (var match in stillWaiting)
                    {
                        results[match.MatchId] = "TIMEOUT";
                    }
                    break;
                }

                logInfo($"[WaitForSettlements] {stillWaiting.Count} matches still not settled. Sleeping 1 minute (retry {retryCount}/{maxRetries}).");
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);

                toCheck = new List<MatchModel>(stillWaiting);
            }

            logInfo("[WaitForSettlements] All tracked bets settled or finished checking. Cycle Ending.");
            return results;
        }
    }
}
