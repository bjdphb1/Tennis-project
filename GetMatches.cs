using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using TennisScraper.Models; // for MatchModel
using TennisScraper; // for Utils headers
using System.Globalization;
using System.Net;
using SocksClient;
using Serilog;

namespace TennisScraper
{
    public static class CloudbetApi
    {
        public class BetPayload
        {
            public string currency { get; set; }
            public string eventId { get; set; }
            public string marketUrl { get; set; }
            public string price { get; set; }
            public string referenceId { get; set; }
            public string stake { get; set; }
        }


        // Create HttpClient to use the SOCKS5 proxy
        private static readonly HttpClient Client = ProxyHttpClientFactory.CreateHttpClientUsingSocks();

        static CloudbetApi()
        {
            // Force TLS 1.2 or 1.3
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
        }

        /// <summary>
        /// Returns current and future UTC epoch times.
        /// </summary>
        private static (long fromEpoch, long toEpoch) GetTimeInterval()
        {
            long currentUtcEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            int hourInterval = 2;
            int minuteInterval = 50;

            long futureUtcEpoch = currentUtcEpoch + (hourInterval * 3600) + (minuteInterval * 60);
            long additionalMinutes = 23 * 60; // 20 minutes in seconds

            currentUtcEpoch += additionalMinutes;
            futureUtcEpoch += additionalMinutes;

            return (currentUtcEpoch, futureUtcEpoch);
        }

        /// <summary>
        /// Fetches upcoming tennis matches from Cloudbet API.
        /// </summary>
        public static async Task<List<MatchModel>> GetMatchesAsync(ILogger Log)
        {
            var matches = new List<MatchModel>();
            var (fromTime, toTime) = GetTimeInterval();

            string baseUrl = "https://sports-api.cloudbet.com/pub/v2/odds/events?sport=tennis&";

            Log.Information($"Requesting from {fromTime} to {toTime}");

            try
            {
                Log.Information($"{baseUrl}from={fromTime}&to={toTime}");
                var response = await Client.GetAsync($"{baseUrl}from={fromTime}&to={toTime}");
                Log.Information($"Response status code: {response.StatusCode}");
                // test
                Log.Information("=== HEADERS BEING SENT ===");

                if (!response.IsSuccessStatusCode)
                {
                    Log.Information($"Request failed with status code: {response.StatusCode}");
                    return matches;
                }

                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(contentStream);

                if (!doc.RootElement.TryGetProperty("competitions", out var competitions))
                    return matches;

                foreach (var competition in competitions.EnumerateArray())
                {
                    if (!competition.TryGetProperty("events", out var events))
                        continue;

                    foreach (var ev in events.EnumerateArray())
                    {
                        // --- Parse matchId safely ---
                        long matchId = 0;
                        if (ev.TryGetProperty("id", out var idElement))
                        {
                            matchId = idElement.ValueKind switch
                            {
                                JsonValueKind.Number => idElement.GetInt64(),
                                JsonValueKind.String => long.TryParse(idElement.GetString(), out var tmpId) ? tmpId : 0,
                                _ => 0
                            };
                        }

                        // --- Parse cutoffTime safely ---
                        string cutoffTime = "";
                        if (ev.TryGetProperty("cutoffTime", out var cutoffElement))
                        {
                            cutoffTime = cutoffElement.ValueKind switch
                            {
                                JsonValueKind.String => cutoffElement.GetString(), // Directly get the string
                                _ => "" // If it's not a string, just assign an empty string
                            };
                        }

                        // --- Player names ---
                        string homePlayer = ev.TryGetProperty("home", out var homeObj) &&
                                            homeObj.TryGetProperty("name", out var homeNameEl)
                                            ? homeNameEl.GetString() ?? ""
                                            : "";

                        string awayPlayer = ev.TryGetProperty("away", out var awayObj) &&
                                            awayObj.TryGetProperty("name", out var awayNameEl)
                                            ? awayNameEl.GetString() ?? ""
                                            : "";

                        // --- Odds ---
                        double homeWinnerOdds = 0;
                        double awayWinnerOdds = 0;

                        if (ev.TryGetProperty("markets", out var markets) &&
                            markets.TryGetProperty("tennis.winner", out var tennisWinner) &&
                            tennisWinner.TryGetProperty("submarkets", out var submarkets) &&
                            submarkets.TryGetProperty(
                                "period=default&period=wo&period=set1&period=set2&period=set3&period=set4&period=set5",
                                out var selectionsContainer) &&
                            selectionsContainer.TryGetProperty("selections", out var selections) &&
                            selections.GetArrayLength() >= 2)
                        {
                            homeWinnerOdds = selections[0].TryGetProperty("price", out var hPrice) ? hPrice.GetDouble() : 0;
                            awayWinnerOdds = selections[1].TryGetProperty("price", out var aPrice) ? aPrice.GetDouble() : 0;
                        }



                        // --- Add MatchModel ---
                        var match = new MatchModel(
                            matchId,
                            homePlayer,
                            awayPlayer,
                            homeWinnerOdds,
                            awayWinnerOdds,
                            cutoffTime
                        );

                        matches.Add(match);
                    }

                }

                return matches;
            }
            catch (Exception ex)
            {
                Log.Information($"Main Thread: Exception fetching matches: {ex.Message}");
                Log.Information($"Inner: {ex.InnerException?.Message}");
                return matches;
            }
        }

        /// <summary>
        /// Gets the balance for the specified currency (default: PLAY_EUR).
        /// </summary>
        public static async Task<string> GetBalanceAsync(string currency, ILogger Log)
        {
            string endpoint = $"https://sports-api.cloudbet.com/pub/v1/account/currencies/{currency}/balance";

            try
            {
                var response = await Client.GetAsync(endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Information($"GetBalance failed: {response.StatusCode} - {response.ReasonPhrase}");
                    return "0.0";
                }

                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(contentStream);

                if (doc.RootElement.TryGetProperty("amount", out var amountElement))
                {
                    Log.Information(amountElement.ToString());
                    return amountElement.ToString();
                }
                else
                {
                    Log.Information("No 'amount' property in balance response.");
                    return "0.0";
                }
            }
            catch (Exception ex)
            {
                Log.Information($"Exception while getting balance: {ex.Message}");
                return "0.0";
            }
        }

        /// <summary>
        /// Places a bet on a match.
        /// winner: "home" or "away"
        /// </summary>
        /// <summary>
        /// Places a bet on a match.
        /// winner: "home" or "away"
        /// Returns BetStatusResult with ReferenceId + Status
        /// </summary>
        /// 
        public static decimal ModifyPrice(double price)
        {
            decimal newPrice;

            if (price >= 1000)
            {
                newPrice = (decimal)(price * 0.001);  // For price >= 1000, multiply by 0.001
            }
            else if (price >= 100)
            {
                newPrice = (decimal)(price * 0.01);   // For price >= 100 but < 1000, multiply by 0.01
            }
            else if (price >= 10)
            {
                newPrice = (decimal)(price * 0.1);    // For price >= 10 but < 100, multiply by 0.1
            }
            else
            {
                newPrice = (decimal)price;  // For price < 10, keep the same value
            }

            return newPrice;
        }
        
        public static async Task<BetStatusResult?> PlaceBetAsync(
            long eventId,
            string winner,
            double winnerOdds,
            string uuid,
            double stake,
            string currency,
            ILogger Log)
        {
            // Safety gating: do not perform live placements unless SUBMIT=true is set in the environment.
            var submitEnabled = (Environment.GetEnvironmentVariable("SUBMIT") ?? "").ToLowerInvariant() == "true";
            if (!submitEnabled)
            {
                Log?.Information("SUBMIT not enabled; skipping live PlaceBet for event {Event}. Returning DRY_RUN.", eventId);
                return new BetStatusResult { ReferenceId = uuid ?? Guid.NewGuid().ToString(), Status = "DRY_RUN", RawResponse = "Dry-run: live submission disabled" };
            }

            string endpoint = "https://sports-api.cloudbet.com/pub/v3/bets/place";
            decimal newPrice = ModifyPrice(winnerOdds);

            var payload = new BetPayload
            {
                currency = currency,
                eventId = eventId.ToString(),
                marketUrl = $"tennis.winner/{winner}",
                price = newPrice.ToString(CultureInfo.InvariantCulture),
                referenceId = uuid,
                stake = stake.ToString(CultureInfo.InvariantCulture)
            };


            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            try
            {
                // Ensure API key is present when submitting live bets to Cloudbet. If missing, abort to avoid Unauthorized attempts.
                var apiKey = Environment.GetEnvironmentVariable("CLOUD_BET_API_KEY");
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    Log?.Information("CLOUD_BET_API_KEY not set; aborting live PlaceBet to avoid Unauthorized responses.");
                    return new BetStatusResult { ReferenceId = uuid, Status = "ERROR_NO_API_KEY", RawResponse = "CLOUD_BET_API_KEY not set" };
                }

                // Add x-api-key header for Cloudbet API
                if (Client.DefaultRequestHeaders.Contains("x-api-key"))
                {
                    Client.DefaultRequestHeaders.Remove("x-api-key");
                }
                Client.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);

                var response = await Client.PostAsync(endpoint, content);
                string body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Log.Information($"PlaceBet failed: {response.StatusCode} - {response.ReasonPhrase}");
                    Log.Information($"Payload: {json}");
                    return new BetStatusResult
                    {
                        ReferenceId = uuid,
                        Status = "ERROR_HTTP_" + response.StatusCode,
                        RawResponse = body
                    };
                }

                using var doc = JsonDocument.Parse(body);
                string status = doc.RootElement.TryGetProperty("status", out var statusEl)
                    ? statusEl.GetString() ?? "UNKNOWN"
                    : "UNKNOWN";

                return new BetStatusResult
                {
                    ReferenceId = uuid,
                    Status = status,
                    RawResponse = body
                };
            }

            catch (Exception ex)
            {
                Log.Information($"Exception while placing bet: {ex.Message}");
                return new BetStatusResult
                {
                    ReferenceId = uuid,
                    Status = "EXCEPTION",
                    RawResponse = ex.Message
                };
            }
        }

        /// <summary>
        /// Checks the status of a bet by reference ID.
        /// Returns BetStatusResult with status.
        /// </summary>
        public static async Task<BetStatusResult?> GetBetStatusAsync(string referenceId, ILogger Log)
        {
            string endpoint = $"https://sports-api.cloudbet.com/pub/v3/bets/{referenceId}/status";

            try
            {
                var response = await Client.GetAsync(endpoint);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Log.Information($"GetBetStatus failed: {response.StatusCode} - {response.ReasonPhrase}");
                    return new BetStatusResult
                    {
                        ReferenceId = referenceId,
                        Status = "ERROR_HTTP_" + response.StatusCode,
                        RawResponse = body
                    };
                }

                using var doc = JsonDocument.Parse(body);
                string status = doc.RootElement.TryGetProperty("status", out var statusEl)
                    ? statusEl.GetString() ?? "UNKNOWN"
                    : "UNKNOWN";

                return new BetStatusResult
                {
                    ReferenceId = referenceId,
                    Status = status,
                    RawResponse = body
                };
            }
            catch (Exception ex)
            {
                Log.Information($"Exception while getting bet status: {ex.Message}");
                return new BetStatusResult
                {
                    ReferenceId = referenceId,
                    Status = "EXCEPTION",
                    RawResponse = ex.Message
                };
            }

        }

    }
}