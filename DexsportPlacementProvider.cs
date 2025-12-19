using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using TennisScraper.Models;
using Serilog;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Remote;

namespace TennisScraper
{
    public class DexsportPlacementProvider : IPlacementProvider
    {
        // Converts "Panna Udvardy" to "Udvardy Panna" for Dexsport
        public static string ToDexsportFormat(string tennisAbstractName)
        {
            var parts = tennisAbstractName.Split(' ');
            if (parts.Length == 2)
                return $"{parts[1]} {parts[0]}";
            // For more complex names, handle accordingly
            return tennisAbstractName;
        }
        private readonly ILogger _log;
    private readonly IWebDriver? _sharedDriver;

    public DexsportPlacementProvider(ILogger? log = null, IWebDriver? sharedDriver = null)
        {
            _log = log ?? Log.Logger;
            _sharedDriver = sharedDriver;
        }

        // Internal record for persisted placed bets
        private class PlacedBetRecord
        {
            public string LocalReference { get; set; } = "";
            public string? RemoteReference { get; set; }
            public string? ReferenceId { get; set; }
            public string EventId { get; set; } = "";
            public string Winner { get; set; } = "";
            public double Stake { get; set; }
            public string Currency { get; set; } = "";
            public string Status { get; set; } = "";
            public DateTime TimestampUtc { get; set; }
            public DateTime? CompletedAtUtc { get; set; }
        }

        public async Task<BetStatusResult?> PlaceBetAsync(long eventId, string winner, double winnerOdds, string uuid, double stake, string currency, string homePlayerName, string awayPlayerName)
        {
            // Strict gating: hardcoded to allow live placements
            var submit = true;
            var confirm = true;
            // If you want to disable live bets, set submit/confirm to false above.
            if (!submit || !confirm)
            {
                _log?.Information("Live bet placement disabled by hardcoded flags; skipping Dexsport PlaceBet for event {Event}. Returning DRY_RUN.", eventId);
                return new BetStatusResult { ReferenceId = uuid ?? Guid.NewGuid().ToString(), Status = "DRY_RUN", RawResponse = "Dry-run: live submission disabled (hardcoded flags)" };
            }

            try
            {
                // Persist idempotency/audit record before attempting live placement
                var sessionDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "session");
                if (!System.IO.Directory.Exists(sessionDir)) System.IO.Directory.CreateDirectory(sessionDir);
                var placedPath = System.IO.Path.Combine(sessionDir, "placed_bets.json");

                List<PlacedBetRecord> records = new List<PlacedBetRecord>();
                try
                {
                    if (System.IO.File.Exists(placedPath))
                    {
                        var txt = await System.IO.File.ReadAllTextAsync(placedPath);
                        if (!string.IsNullOrWhiteSpace(txt))
                        {
                            records = JsonSerializer.Deserialize<List<PlacedBetRecord>>(txt) ?? new List<PlacedBetRecord>();
                        }
                    }
                }
                catch (Exception rex)
                {
                    _log?.Warning("Could not read placed_bets.json: {Msg}", rex.Message);
                }

                // Duplicate check disabled - always place fresh bets on restart
                // This allows the system to place bets every time without session history
                /*
                // Only skip duplicates if previous record is a real bet (not NOT_IMPLEMENTED, DRY_RUN, UNKNOWN, EXCEPTION)
                if (!string.IsNullOrWhiteSpace(uuid))
                {
                    var existing = records.Find(r => r.ReferenceId == uuid);
                    if (existing != null && !(existing.Status == "NOT_IMPLEMENTED" || existing.Status == "DRY_RUN" || existing.Status == "UNKNOWN" || existing.Status == "EXCEPTION"))
                    {
                        _log?.Information("Found existing placed bet for uuid {Uuid} with status {Status}, skipping duplicate.", uuid, existing.Status);
                        return new BetStatusResult { ReferenceId = existing.RemoteReference ?? existing.LocalReference, Status = "DUPLICATE", RawResponse = JsonSerializer.Serialize(existing) };
                    }
                }

                var eventExisting = records.Find(r => r.EventId == eventId.ToString());
                if (eventExisting != null && !(eventExisting.Status == "NOT_IMPLEMENTED" || eventExisting.Status == "DRY_RUN" || eventExisting.Status == "UNKNOWN" || eventExisting.Status == "EXCEPTION"))
                {
                    _log?.Information("Found existing placed bet for event {Event} with status {Status}, skipping duplicate.", eventId, eventExisting.Status);
                    return new BetStatusResult { ReferenceId = eventExisting.RemoteReference ?? eventExisting.LocalReference, Status = "DUPLICATE", RawResponse = JsonSerializer.Serialize(eventExisting) };
                }
                */

                // create a local audit record (pending)
                var localRef = Guid.NewGuid().ToString();
                var newRec = new PlacedBetRecord
                {
                    LocalReference = localRef,
                    EventId = eventId.ToString(),
                    Winner = winner,
                    Stake = stake,
                    Currency = currency,
                    ReferenceId = uuid,
                    Status = "PENDING",
                    TimestampUtc = DateTime.UtcNow
                };
                records.Add(newRec);

                try
                {
                    var serialized = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
                    await System.IO.File.WriteAllTextAsync(placedPath, serialized);
                }
                catch (Exception wex)
                {
                    _log?.Warning("Failed to persist placed_bets.json before placement: {Msg}", wex.Message);
                }

                // Use the Dexsport scraper to attempt placement. Prefer connecting to a remote Selenium
                // server when SELENIUM_URL is provided (e.g. selenium/standalone-chrome container).
                var headless = Environment.GetEnvironmentVariable("HEADLESS")?.ToLowerInvariant() == "true";
                var seleniumUrl = Environment.GetEnvironmentVariable("SELENIUM_URL");
                string? remoteRef = null;

                // Player names are already in original Dexsport format (e.g., "Udvardy Panna")
                // No conversion needed

                if (_sharedDriver != null)
                {
                    using (var scraper = new DexsportSeleniumScraper(_sharedDriver, headless: headless))
                    {
                        var selection = winner.ToLowerInvariant();
                        decimal odds = (decimal)winnerOdds;
                        decimal stakeDec = (decimal)stake;
                        var res = await scraper.PlaceBetAsync(eventId.ToString(), selection, odds, stakeDec, homePlayerName, awayPlayerName);
                        remoteRef = res;
                    }
                }
                else if (!string.IsNullOrEmpty(seleniumUrl))
                {
                    var opts = new ChromeOptions();
                    if (headless) opts.AddArgument("--headless=new");
                    opts.AddArgument("--no-sandbox");
                    opts.AddArgument("--disable-dev-shm-usage");
                    opts.AddArgument("--window-size=1920,1080");

                    using (var driver = new RemoteWebDriver(new Uri(seleniumUrl), opts.ToCapabilities(), TimeSpan.FromSeconds(AppConfig.WebDriverConnectTimeoutSeconds)))
                    using (var scraper = new DexsportSeleniumScraper(driver, headless: headless))
                    {
                        var selection = winner.ToLowerInvariant();
                        decimal odds = (decimal)winnerOdds;
                        decimal stakeDec = (decimal)stake;
                        var res = await scraper.PlaceBetAsync(eventId.ToString(), selection, odds, stakeDec, homePlayerName, awayPlayerName);
                        remoteRef = res;
                    }
                }
                else
                {
                    using (var scraper = new DexsportSeleniumScraper(headless: headless))
                    {
                        var selection = winner.ToLowerInvariant();
                        decimal odds = (decimal)winnerOdds;
                        decimal stakeDec = (decimal)stake;
                        var res = await scraper.PlaceBetAsync(eventId.ToString(), selection, odds, stakeDec, homePlayerName, awayPlayerName);
                        remoteRef = res;
                    }
                }

                // update record with remote reference and status
                try
                {
                    var rec = records.Find(r => r.LocalReference == localRef);
                    if (rec != null)
                    {
                        rec.RemoteReference = remoteRef;
                        if (string.IsNullOrEmpty(remoteRef)) rec.Status = "UNKNOWN";
                        else if (remoteRef == "NOT_IMPLEMENTED") rec.Status = "NOT_IMPLEMENTED";
                        else if (remoteRef == "INSUFFICIENT_FUNDS") rec.Status = "INSUFFICIENT_FUNDS";
                        else if (remoteRef == "REJECTED") rec.Status = "REJECTED";
                        else rec.Status = "PENDING_ACCEPTANCE";
                        rec.CompletedAtUtc = DateTime.UtcNow;

                        var serialized2 = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
                        await System.IO.File.WriteAllTextAsync(placedPath, serialized2);
                    }
                }
                catch (Exception uex)
                {
                    _log?.Warning("Failed to update placed_bets.json after placement: {Msg}", uex.Message);
                }

                if (string.IsNullOrEmpty(remoteRef))
                {
                    return new BetStatusResult { ReferenceId = uuid ?? localRef, Status = "UNKNOWN", RawResponse = "" };
                }

                if (remoteRef == "NOT_IMPLEMENTED")
                {
                    _log?.Warning("Dexsport placement not implemented; returning NOT_IMPLEMENTED for event {Event}", eventId);
                    return new BetStatusResult { ReferenceId = uuid ?? localRef, Status = "NOT_IMPLEMENTED", RawResponse = remoteRef };
                }

                if (remoteRef == "INSUFFICIENT_FUNDS")
                {
                    _log?.Warning("❌ Dexsport: Insufficient funds for event {Event}", eventId);
                    return new BetStatusResult { ReferenceId = uuid ?? localRef, Status = "INSUFFICIENT_FUNDS", RawResponse = remoteRef };
                }

                if (remoteRef == "REJECTED")
                {
                    _log?.Warning("❌ Dexsport: Bet rejected for event {Event}", eventId);
                    return new BetStatusResult { ReferenceId = uuid ?? localRef, Status = "REJECTED", RawResponse = remoteRef };
                }

                // ✅ NEW: Immediately verify bet was accepted by checking "My Bets" modal
                _log?.Information("💾 Bet saved with status: PENDING_ACCEPTANCE (Ref: {Ref})", remoteRef);
                _log?.Information("🔍 Checking if bet was accepted by opening 'My Bets' modal...");
                
                // Wait a bit for bet to be processed on server side
                await Task.Delay(3000);
                
                try
                {
                    string? verificationStatus = null;
                    
                    if (_sharedDriver != null)
                    {
                        using (var scraper = new DexsportSeleniumScraper(_sharedDriver, headless: headless))
                        {
                            // Pass eventId for reliable bet identification
                            verificationStatus = await scraper.LookupTicketStatusAsync(remoteRef, eventId.ToString());
                        }
                    }
                    else if (!string.IsNullOrEmpty(seleniumUrl))
                    {
                        var opts = new ChromeOptions();
                        if (headless) opts.AddArgument("--headless=new");
                        opts.AddArgument("--no-sandbox");
                        opts.AddArgument("--disable-dev-shm-usage");
                        opts.AddArgument("--window-size=1920,1080");

                        using (var driver = new RemoteWebDriver(new Uri(seleniumUrl), opts.ToCapabilities(), TimeSpan.FromSeconds(AppConfig.WebDriverConnectTimeoutSeconds)))
                        using (var scraper = new DexsportSeleniumScraper(driver, headless: headless))
                        {
                            verificationStatus = await scraper.LookupTicketStatusAsync(remoteRef, eventId.ToString());
                        }
                    }
                    else
                    {
                        using (var scraper = new DexsportSeleniumScraper(headless: headless))
                        {
                            verificationStatus = await scraper.LookupTicketStatusAsync(remoteRef, eventId.ToString());
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(verificationStatus))
                    {
                        // Normalize verificationStatus using central parser (we expect this to be from the Unsettled tab)
                        var parsed = TennisScraper.BetStatusParser.ParseStatus(verificationStatus, verificationStatus, "unsettled");

                        // Update the record with verified status
                        var rec = records.Find(r => r.LocalReference == localRef);
                        if (rec != null)
                        {
                            rec.Status = parsed ?? verificationStatus;
                            var serialized3 = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
                            await System.IO.File.WriteAllTextAsync(placedPath, serialized3);

                            var effective = (rec.Status ?? string.Empty).ToUpperInvariant();
                            if (effective == "ACCEPTED")
                            {
                                _log?.Information("✅ Bet confirmed ACCEPTED (found in Unsettled tab)");
                                return new BetStatusResult { ReferenceId = remoteRef, Status = "ACCEPTED", RawResponse = remoteRef };
                            }
                            else if (effective == "REJECTED")
                            {
                                _log?.Warning("❌ Bet was REJECTED by Dexsport");
                                return new BetStatusResult { ReferenceId = remoteRef, Status = "REJECTED", RawResponse = remoteRef };
                            }
                            else
                            {
                                _log?.Information("⏳ Bet status: {Status}, will check again later", rec.Status);
                            }
                        }
                    }
                }
                catch (Exception vex)
                {
                    _log?.Warning("⚠️ Could not verify bet status immediately: {Msg}", vex.Message);
                }

                return new BetStatusResult { ReferenceId = remoteRef, Status = "PENDING_ACCEPTANCE", RawResponse = remoteRef };
            }
            catch (Exception ex)
            {
                _log?.Information($"Dexsport PlaceBet exception for {eventId}: {ex.Message}");
                return new BetStatusResult { ReferenceId = uuid ?? Guid.NewGuid().ToString(), Status = "EXCEPTION", RawResponse = ex.Message };
            }
        }

        public async Task<BetStatusResult?> GetBetStatusAsync(string referenceId)
        {
            // Best-effort: consult local placed_bets.json first, then attempt to query Dexsport via Selenium
            try
            {
                var sessionDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "session");
                var placedPath = System.IO.Path.Combine(sessionDir, "placed_bets.json");

                List<PlacedBetRecord> records = new List<PlacedBetRecord>();
                try
                {
                    if (System.IO.File.Exists(placedPath))
                    {
                        var txt = await System.IO.File.ReadAllTextAsync(placedPath);
                        if (!string.IsNullOrWhiteSpace(txt))
                            records = JsonSerializer.Deserialize<List<PlacedBetRecord>>(txt) ?? new List<PlacedBetRecord>();
                    }
                }
                catch (Exception rex)
                {
                    _log?.Warning("Could not read placed_bets.json during status check: {Msg}", rex.Message);
                }

                // Try to locate a record matching referenceId (remote or local)
                PlacedBetRecord? rec = null;
                if (!string.IsNullOrWhiteSpace(referenceId))
                {
                    rec = records.Find(r => r.RemoteReference == referenceId || r.LocalReference == referenceId || r.ReferenceId == referenceId);
                }

                if (rec == null)
                {
                    // Not in local records — return UNKNOWN
                    _log?.Information("Dexsport GetBetStatus: reference {Ref} not found in placed_bets.json", referenceId);
                    return new BetStatusResult { ReferenceId = referenceId, Status = "UNKNOWN", RawResponse = "NotFoundLocal" };
                }

                // ✅ If we already have a FINAL/TERMINAL status, return it immediately (no need to check again)
                var current = (rec.Status ?? "").ToUpperInvariant();
                if (current == "WON" || current == "LOST" || current == "VOID" || current == "REJECTED" || current == "NOT_IMPLEMENTED")
                {
                    _log?.Information("📊 Bet {Ref} already has terminal status: {Status}", referenceId, current);
                    return new BetStatusResult { 
                        ReferenceId = rec.RemoteReference ?? rec.LocalReference ?? rec.ReferenceId ?? "", 
                        Status = current, 
                        RawResponse = JsonSerializer.Serialize(rec) 
                    };
                }

                // Otherwise, if status is PENDING_ACCEPTANCE or ACCEPTED, query Dexsport for latest status
                _log?.Information("🔍 Checking latest status for bet {Ref} (current: {Current})", referenceId, current);
                
                // Otherwise attempt to query Dexsport for a live status (best-effort)
                try
                {
                    var headless = Environment.GetEnvironmentVariable("HEADLESS")?.ToLowerInvariant() == "true";
                    string? liveStatus = null;
                    
                    if (_sharedDriver != null)
                    {
                        using (var scraper = new DexsportSeleniumScraper(_sharedDriver, headless: headless))
                        {
                            // Pass EventId for reliable bet identification
                            liveStatus = await scraper.LookupTicketStatusAsync(
                                rec.RemoteReference ?? rec.LocalReference ?? rec.ReferenceId ?? "",
                                rec.EventId ?? ""
                            );
                        }
                    }
                    else
                    {
                        using (var scraper = new DexsportSeleniumScraper(headless: headless))
                        {
                            // Pass EventId for reliable bet identification
                            liveStatus = await scraper.LookupTicketStatusAsync(
                                rec.RemoteReference ?? rec.LocalReference ?? rec.ReferenceId ?? "",
                                rec.EventId ?? ""
                            );
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(liveStatus))
                    {
                        // Normalize liveStatus using central parser. We don't always know which tab the
                        // scraper inspected, so allow parser to make the best guess.
                        string? parsed = TennisScraper.BetStatusParser.ParseStatus(liveStatus, liveStatus, string.Empty);
                        string mapped = (parsed ?? liveStatus).ToUpperInvariant();

                        // Update persisted record
                        try
                        {
                            var oldStatus = rec.Status;
                            rec.Status = mapped;

                            // Mark as completed if terminal status
                            if (mapped == "WON" || mapped == "LOST" || mapped == "VOID" || mapped == "REJECTED")
                            {
                                rec.CompletedAtUtc = DateTime.UtcNow;

                                if (mapped == "WON")
                                    _log?.Information("🎉 Bet {Ref} WON! (was: {Old})", referenceId, oldStatus);
                                else if (mapped == "LOST")
                                    _log?.Information("💔 Bet {Ref} LOST (was: {Old})", referenceId, oldStatus);
                                else if (mapped == "VOID")
                                    _log?.Information("⚠️ Bet {Ref} VOIDED (was: {Old})", referenceId, oldStatus);
                            }
                            else if (mapped == "ACCEPTED" && oldStatus != "ACCEPTED")
                            {
                                _log?.Information("✅ Bet {Ref} status changed: {Old} → {New}", referenceId, oldStatus, mapped);
                            }

                            var serialized = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
                            await System.IO.File.WriteAllTextAsync(placedPath, serialized);
                        }
                        catch (Exception wex)
                        {
                            _log?.Warning("Failed to persist placed_bets.json after status lookup: {Msg}", wex.Message);
                        }

                        return new BetStatusResult { 
                            ReferenceId = rec.RemoteReference ?? rec.LocalReference ?? rec.ReferenceId ?? "", 
                            Status = mapped, 
                            RawResponse = liveStatus 
                        };
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warning("Error querying Dexsport for ticket status: {Msg}", ex.Message);
                }

                // Fallback: still at current status
                return new BetStatusResult { 
                    ReferenceId = rec.RemoteReference ?? rec.LocalReference ?? rec.ReferenceId ?? "", 
                    Status = current, 
                    RawResponse = JsonSerializer.Serialize(rec) 
                };
            }
            catch (Exception ex)
            {
                _log?.Information($"Dexsport GetBetStatus exception for {referenceId}: {ex.Message}");
                return new BetStatusResult { ReferenceId = referenceId, Status = "EXCEPTION", RawResponse = ex.Message };
            }
        }

        public async Task<decimal> GetBalanceAsync(string currency = "USD")
        {
            try
            {
                var headless = Environment.GetEnvironmentVariable("HEADLESS")?.ToLowerInvariant() == "true";
                using (var scraper = new DexsportSeleniumScraper(headless: headless))
                {
                    var res = await scraper.GetBalanceAsync(currency);
                    return res;
                }
            }
            catch (Exception ex)
            {
                _log?.Information($"Dexsport GetBalance exception: {ex.Message}");
                return 0m;
            }
        }
    }
}
