// DataScraper.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using System.Text.RegularExpressions;
using System.IO;

namespace TennisScraper
{
    public class DataScraper : IDisposable
    {
    private PlayerLookupService _playerLookup;
    // When true, GetDataAsync will only check whether the TennisAbstract player fragment exists
    // and return a minimal non-null result. This enables quick "existence-only" runs.
    private bool _disposed = false;
    public bool SearchForName { get; set; } = false;

        public DataScraper()
        {
            DotNetEnv.Env.Load();
            // Create a shared player lookup service (in-memory + on-disk cache)
            _playerLookup = new PlayerLookupService(maxConcurrency: 6, httpTimeout: TimeSpan.FromSeconds(25));
        }

        // Batch parallelism for player lookups
        private const int _batchParallelism = 6;

        /// <summary>
        /// Batch-fetch lookup results for a set of player names using the existing PlayerLookupService.
        /// Returns a map: originalName -> (Found, RawJs, DisplayName).
        /// Bounded concurrency + overall timeout so one slow player can't block the whole pipeline.
        /// </summary>
        public async Task<Dictionary<string, (bool Found, string RawJs, string DisplayName)>> FetchPlayersBatchAsync(
            IEnumerable<string> playerNames,
            int overallTimeoutSeconds = 60)
        {
            var unique = playerNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var results = new System.Collections.Concurrent.ConcurrentDictionary<string, (bool, string, string)>(StringComparer.OrdinalIgnoreCase);
            using var sem = new SemaphoreSlim(_batchParallelism);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(overallTimeoutSeconds));

            var tasks = unique.Select(async name =>
            {
                try
                {
                    await sem.WaitAsync(cts.Token);
                    try
                    {
                        var lookup = await _playerLookup.GetPlayerAsync(name);
                        if (lookup != null && lookup.Found)
                            results[name] = (true, lookup.RawHtmlFragment, lookup.DisplayName);
                        else
                            results[name] = (false, null, null);
                    }
                    catch
                    {
                        results[name] = (false, null, null);
                    }
                    finally
                    {
                        sem.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    results[name] = (false, null, null);
                }
            }).ToList();

            try { await Task.WhenAll(tasks); } catch { /* ignore cancellations/timeouts, partial results are OK */ }

            return results.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            _disposed = true;
            try { _playerLookup?.Dispose(); } catch { }
        }

        /// <summary>
        /// Returns Tuple(headers, playerData) or null if no record.
        /// Mirrors the python get_data(cut_off_time, player_name, player_type, retries=0)
        /// </summary>
        public async Task<Tuple<List<string>, List<string>>?> GetDataAsync(
            string cutOffTime,
            string playerName,
            string playerType,
            int retries = 0)
        {

            Console.WriteLine($"Main Thread: {playerName}");

            // Build candidate permutations (url-friendly concatenated name + spaced display name)
            var parts = Regex.Split(playerName.Trim(), "\\s+").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            var candidates = new List<(string urlName, string displayName)>();

            // Local permutation helper
            IEnumerable<string[]> Permute(string[] arr)
            {
                if (arr.Length == 1) yield return arr;
                else
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var left = arr[i];
                        var rest = arr.Where((_, idx) => idx != i).ToArray();
                        foreach (var tail in Permute(rest))
                        {
                            yield return (new[] { left }).Concat(tail).ToArray();
                        }
                    }
                }
            }

            if (parts.Length == 0)
            {
                candidates.Add(("", ""));
            }
            else if (parts.Length == 1)
            {
                candidates.Add((parts[0], parts[0]));
            }
            else if (parts.Length == 2)
            {
                candidates.Add((parts[0] + parts[1], parts[0] + " " + parts[1])); // FirstLast / "First Last"
                candidates.Add((parts[1] + parts[0], parts[1] + " " + parts[0])); // LastFirst / "Last First"
            }
            else
            {
                var permuteParts = parts.Length <= 4 ? parts : parts.Take(4).ToArray();
                foreach (var p in Permute(permuteParts))
                {
                    var urlJoined = string.Concat(p);
                    var spaceJoined = string.Join(" ", p);
                    candidates.Add((urlJoined, spaceJoined));
                }

                // also try full concatenation and reversed full (and space forms)
                candidates.Add((string.Concat(parts), string.Join(" ", parts)));
                candidates.Add((string.Concat(parts.Reverse()), string.Join(" ", parts.Reverse())));
            }

            // sanitize and dedupe candidates by urlName
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var finalCandidates = new List<(string urlName, string displayName)>();
            foreach (var c in candidates)
            {
                var sanitized = Regex.Replace(c.urlName ?? string.Empty, "[^A-Za-z0-9\\-]", "");
                if (string.IsNullOrWhiteSpace(sanitized)) continue;
                if (seen.Add(sanitized)) finalCandidates.Add((sanitized, c.displayName));
            }

            // Fast path: consult PlayerLookupService cache / bounded fetch before doing full permutation fetches
            string preFetchedJs = null;
            string preFetchedHtml = null;
            bool preFetchedFound = false;
            string matchedDisplayName = null;
            try
            {
                var lookup = await _playerLookup.GetPlayerAsync(playerName);
                if (lookup != null && lookup.Found)
                {
                    preFetchedJs = lookup.RawHtmlFragment;
                    preFetchedHtml = DataScraperHelpers.ExtractHtmlFromJs(preFetchedJs) ?? preFetchedJs;
                    matchedDisplayName = lookup.DisplayName;
                    preFetchedFound = !string.IsNullOrWhiteSpace(preFetchedHtml);
                }
            }
            catch (Exception exLookup)
            {
                Console.WriteLine($"[DEBUG] PlayerLookupService lookup failed for {playerName}: {exLookup.Message}");
            }

            Tuple<List<string>, List<string>>? recordDic = null;

            // Parse HTML
            try
            {
                // Create an HttpClient (probe+fallback is handled in the factory)
                var client = ProxyHttpClientFactoryBasic.CreateHttpClientWithProxy();
                using var _httpClient = client;
                // Fail faster: use a shorter per-request timeout so slow requests don't stall the run
                try { _httpClient.Timeout = TimeSpan.FromSeconds(20); } catch { }

                // --- Add explicit rotated permutations for 3+ word names (e.g. "Kent Trotter James" -> "JamesKentTrotter")
                if (parts.Length >= 3)
                {
                    for (int rot = 0; rot < parts.Length; rot++)
                    {
                        var r = parts.Skip(rot).Concat(parts.Take(rot)).ToArray();
                        var urlJoined = string.Concat(r);
                        var spaceJoined = string.Join(" ", r);
                        var sanitized = Regex.Replace(urlJoined ?? string.Empty, "[^A-Za-z0-9\\-]", "");
                        if (!string.IsNullOrWhiteSpace(sanitized) && !finalCandidates.Any(fc => string.Equals(fc.urlName, sanitized, StringComparison.OrdinalIgnoreCase)))
                        {
                            finalCandidates.Add((sanitized, spaceJoined));
                        }
                    }
                }

                // Try jsfrags/jsmatches for each candidate name permutation (prefer jsfrags)
                string jsText = "";
                string html = "";
                bool htmlFound = false;

                // If we had a pre-fetched result from the lookup service, use it and skip network fetches
                if (preFetchedFound)
                {
                    jsText = preFetchedJs ?? "";
                    html = preFetchedHtml ?? "";
                    htmlFound = !string.IsNullOrWhiteSpace(html);
                    if (htmlFound)
                        Console.WriteLine($"[DEBUG] Used cached lookup for {playerName} — skipping network fetch.");
                }
                foreach (var cand in finalCandidates)
                {
                    var candidateUrlName = cand.urlName;
                    var urlJsLocal = $"https://www.tennisabstract.com/jsfrags/{candidateUrlName}.js";
                    var urlLocal = $"https://www.tennisabstract.com/jsmatches/{candidateUrlName}.js";
                    var tryUrlsLocal = new[] { urlJsLocal, urlLocal };

                    foreach (var tryUrl in tryUrlsLocal)
                    {
                        for (int attempt = 1; attempt <= 2 && !htmlFound; attempt++)
                        {
                            try
                            {
                                Console.WriteLine($"[DEBUG] Fetching TennisAbstract JS URL: {tryUrl} (attempt {attempt}) — candidate={candidateUrlName}");
                                var response = await _httpClient.GetAsync(tryUrl);
                                Console.WriteLine($"[DEBUG] HTTP Status: {(int)response.StatusCode} {response.StatusCode} for {tryUrl}");
                                jsText = await response.Content.ReadAsStringAsync();
                                Console.WriteLine($"[DEBUG] Response snippet: {jsText.Substring(0, Math.Min(240, jsText.Length)).Replace("\n", " ")}");

                                html = DataScraperHelpers.ExtractHtmlFromJs(jsText);
                                if (!string.IsNullOrWhiteSpace(html))
                                {
                                    Console.WriteLine($"[DEBUG] Extracted HTML successfully from {tryUrl}");
                                    htmlFound = true;
                                    matchedDisplayName = cand.displayName;
                                    break;
                                }
                                else
                                {
                                    Console.WriteLine($"[DEBUG] Could not extract HTML from {tryUrl}, trying next attempt/URL.");
                                }
                            }
                            catch (Exception exInner)
                            {
                                Console.WriteLine($"[DEBUG] Fetch failed for {tryUrl} (attempt {attempt}): {exInner.Message}");
                                if (attempt < 2) await Task.Delay(500 * attempt);
                            }
                        }
                        if (htmlFound) break;
                    }
                    if (htmlFound) break;
                }

                if (string.IsNullOrWhiteSpace(html))
                {
                    Console.WriteLine($"[DEBUG] Could not extract HTML from any TennisAbstract JS fragment for {playerName}.");

                    // Fallback heuristic: sometimes the JS fragment doesn't contain a wrapped HTML
                    // but the response still contains evidence that the player exists on the site
                    // (links, player id, or the name itself). If we detect such evidence treat the
                    // player as found for existence-only runs or loosened checks.
                    bool fallbackFound = false;
                        var lowerJs = (jsText ?? string.Empty).ToLowerInvariant();
                        var lowerName = playerName.ToLowerInvariant();

                        if (!string.IsNullOrWhiteSpace(jsText))
                        {
                            // If the full display name appears in the JS response, that's a direct hint
                            if (lowerJs.Contains(lowerName.Replace(" ", "")) || lowerJs.Contains(lowerName))
                                fallbackFound = true;

                            // TennisAbstract sometimes includes a link to the player page like
                            // /cgi-bin/player-classic.cgi?p=FirstnameLastname — check for that too
                            if (!fallbackFound && System.Text.RegularExpressions.Regex.IsMatch(jsText, @"player-classic\.cgi\?p=", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                                fallbackFound = true;

                            // More permissive heuristic: check last name + at least a prefix of first name
                            // to catch variations like 'Jones, Maximus' or 'Max Jones'.
                            if (!fallbackFound)
                            {
                                var lowerNameParts = Regex.Split(lowerName, "[^a-z0-9]+").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                                if (lowerNameParts.Length >= 2)
                                {
                                    var first = lowerNameParts[0];
                                    var last = lowerNameParts[lowerNameParts.Length - 1];

                                    // last name must appear
                                    if (lowerJs.Contains(last))
                                    {
                                        // either full first name, a short prefix (3 chars), or reversed 'Last, First'
                                        if (lowerJs.Contains(first) || (first.Length >= 3 && lowerJs.Contains(first.Substring(0, 3))) || lowerJs.Contains($"{last}, {first}"))
                                        {
                                            fallbackFound = true;
                                        }
                                    }
                                }
                            }
                        }

                        if (fallbackFound)
                    {
                        Console.WriteLine($"[DEBUG] Fallback heuristic: JS response contains player hints for {playerName}. Treating as present.");
                        // If in SearchForName mode, return minimal record immediately
                        if (this.SearchForName)
                        {
                            Console.WriteLine($"[DEBUG] SearchForName enabled and fallback matched — returning minimal record for {playerName}.");
                            var headersShort = new List<string> { "Found", playerType };
                            var dataShort = new List<string> { "1", cutOffTime };
                            return Tuple.Create(headersShort, dataShort);
                        }

                        // Otherwise, set html to the raw jsText so downstream parsing can try to extract info
                        html = jsText;
                    }
                    else
                    {
                        // As a last-ditch fallback, try the player-classic CGI page directly
                        // with several common name permutations (FirstnameLastname, LastnameFirstname,
                        // hyphenated, and cleaned alphanumeric). If that page exists and contains
                        // the player's name, treat the player as present.
                        try
                        {
                            var cleaned = Regex.Replace(playerName ?? string.Empty, "[^A-Za-z0-9 ]", " ").Trim();
                            var cleanedParts = cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            var cgiCandidates = new List<string>();

                            // Helper: generate permutations up to 4 tokens (avoid explosion)
                            IEnumerable<string[]> PermuteParts(string[] arr)
                            {
                                if (arr.Length == 1) yield return arr;
                                else
                                {
                                    for (int i = 0; i < arr.Length; i++)
                                    {
                                        var left = arr[i];
                                        var rest = arr.Where((_, idx) => idx != i).ToArray();
                                        foreach (var tail in PermuteParts(rest))
                                        {
                                            yield return (new[] { left }).Concat(tail).ToArray();
                                        }
                                    }
                                }
                            }

                            if (cleanedParts.Length >= 1)
                            {
                                // Add straight concatenation (original order)
                                cgiCandidates.Add(string.Join("", cleanedParts));
                                // Add hyphenated full name
                                cgiCandidates.Add(string.Join("-", cleanedParts));
                            }

                            // Add permutations (limit to first 4 tokens for performance)
                            var permParts = cleanedParts.Length <= 4 ? cleanedParts : cleanedParts.Take(4).ToArray();
                            foreach (var p in PermuteParts(permParts))
                            {
                                cgiCandidates.Add(string.Join("", p));
                                cgiCandidates.Add(string.Join("-", p));
                            }

                            // Also try Last + FirstMiddle (common CGI pattern)
                            if (cleanedParts.Length >= 2)
                            {
                                cgiCandidates.Add(cleanedParts[cleanedParts.Length - 1] + string.Join("", cleanedParts.Take(cleanedParts.Length - 1)));
                            }

                            // Also add single last-name fallback
                            if (cleanedParts.Length > 0) cgiCandidates.Add(cleanedParts.Last());

                            bool cgiFound = false;
                            foreach (var cand in cgiCandidates.Distinct())
                            {
                                var cgiUrl = $"https://www.tennisabstract.com/cgi-bin/player-classic.cgi?p={Uri.EscapeDataString(cand)}";
                                try
                                {
                                    Console.WriteLine($"[DEBUG] Trying direct player-classic URL: {cgiUrl}");
                                    var resp = await client.GetAsync(cgiUrl);
                                    Console.WriteLine($"[DEBUG] CGI HTTP Status: {(int)resp.StatusCode} {resp.StatusCode} for {cgiUrl}");
                                    if (resp.IsSuccessStatusCode)
                                    {
                                        var body = await resp.Content.ReadAsStringAsync();
                                        if (!string.IsNullOrWhiteSpace(body) && cleanedParts.Length>0 && body.ToLowerInvariant().Contains(cleanedParts.Last().ToLowerInvariant()))
                                        {
                                            Console.WriteLine($"[DEBUG] Found player-classic page for {playerName} at {cgiUrl}");
                                            cgiFound = true;
                                            // If caller only needs existence, return minimal record immediately
                                            if (this.SearchForName)
                                            {
                                                Console.WriteLine($"[DEBUG] SearchForName enabled and CGI page matched — returning minimal record for {playerName}.");
                                                var headersShort = new List<string> { "Found", playerType };
                                                var dataShort = new List<string> { "1", cutOffTime };
                                                return Tuple.Create(headersShort, dataShort);
                                            }
                                            // Otherwise allow downstream parsing by setting html to the CGI page
                                            html = body;
                                            break;
                                        }
                                    }
                                }
                                catch (Exception exCgi)
                                {
                                    Console.WriteLine($"[DEBUG] CGI fetch failed for {cgiUrl}: {exCgi.Message}");
                                }
                            }

                            if (!cgiFound)
                            {
                                return null;
                            }
                        }
                        catch (Exception exFallback)
                        {
                            Console.WriteLine($"[DEBUG] CGI fallback failed for {playerName}: {exFallback.Message}");
                            return null;
                        }
                    }
                }

                // Load HTML fragment with HtmlAgilityPack (declare doc2 only once)
                HtmlDocument doc2 = new HtmlDocument();
                doc2.LoadHtml(html);

                // If caller only needs to verify the player page exists on TennisAbstract,
                // short-circuit here and return a minimal "found" tuple. This will cause
                // callers to treat the player as present without performing DOB/hand/split parsing.
                if (this.SearchForName)
                {
                    Console.WriteLine($"[DEBUG] SearchForName enabled — TA fragment found for {playerName}; returning minimal record.");
                    var headersShort = new List<string> { "Found", playerType };
                    var dataShort = new List<string> { "1", cutOffTime };
                    return Tuple.Create(headersShort, dataShort);
                }

                // Headers and nodes used in script
                var itfHeaderNode = doc2.DocumentNode.SelectSingleNode("//h1[@id='chall-years-h']");
                var tourHeaderNode = doc2.DocumentNode.SelectSingleNode("//h1[@id='tour-years-h']");
                var splitItfHeaderNode = doc2.DocumentNode.SelectSingleNode("//h1[@id='career-splits-chall-h']");
                var splitTourHeaderNode = doc2.DocumentNode.SelectSingleNode("//h1[@id='career-splits-h']");

                HtmlNode itfLevelSeason = doc2.GetElementbyId("chall-years");
                HtmlNode tourLevelSeason = doc2.GetElementbyId("tour-years");

                Console.WriteLine($"TOUR TEXT HEADER: {(tourHeaderNode != null ? tourHeaderNode.InnerText.Trim() : "None")}");
                Console.WriteLine($"TOUR YEARS TABLE FOUND: {(tourLevelSeason != null)}");
                Console.WriteLine($"ITF TEXT HEADER: {(itfHeaderNode != null ? itfHeaderNode.InnerText.Trim() : "None")}");
                Console.WriteLine($"ITF YEARS TABLE FOUND: {(itfLevelSeason != null)}");

                HtmlNode splitItfLevel = doc2.GetElementbyId("career-splits-chall");
                HtmlNode splitTourLevel = doc2.GetElementbyId("career-splits");

                // Prepare outputs
                var headers = new List<string> { "Type" };
                var playerData = new List<string>();

                bool seasonDataNotFound = false;
                var requiredElements = new List<string> { "Clay", "Hard", "Quarter finals", "vs Righties" };
                var splitDataMust = new List<string>
                {
                    "Hard", "Clay", "Finals", "Semi finals", "Quarter finals", "vs Righties", "vs Lefties"
                };

                var splitDataAvailable = new List<string>();
                bool iteratedSplitDataMust = false;

                // Fetch player details: prefer jsfrags then jsmatches for the matched candidate (or try candidates until one works)
                    (string dob, string hand, string detailsJs) = (null, null, null);
                    bool detailsFetched = false;

                    // If we pre-fetched a JS fragment via PlayerLookupService, parse dob/hand from it
                    if (preFetchedFound && !string.IsNullOrWhiteSpace(preFetchedJs))
                    {
                        try
                        {
                            (dob, hand, detailsJs) = DataScraperHelpers.ParsePlayerDetailsFromJs(preFetchedJs);
                            if (!string.IsNullOrWhiteSpace(dob) || !string.IsNullOrWhiteSpace(hand) || !string.IsNullOrWhiteSpace(detailsJs))
                            {
                                detailsFetched = true;
                                Console.WriteLine($"[DEBUG] Used prefetched JS fragment to extract details for {playerName}");
                            }
                        }
                        catch (Exception exParse)
                        {
                            Console.WriteLine($"[DEBUG] Parsing prefetched JS failed for {playerName}: {exParse.Message}");
                        }
                    }

                // Build a list of candidate detail URLs to try. If we previously matched a specific
                // permutation (matchedDisplayName -> matchedUrlName), prefer that; otherwise try all
                // sanitized candidate names we generated earlier.
                var detailCandidateNames = new List<string>();
                if (!string.IsNullOrWhiteSpace(matchedDisplayName))
                {
                    // find the urlName for this displayName in finalCandidates
                    var found = finalCandidates.FirstOrDefault(fc => string.Equals(fc.displayName, matchedDisplayName, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(found.urlName)) detailCandidateNames.Add(found.urlName);
                }
                // append all final candidates as fallback
                detailCandidateNames.AddRange(finalCandidates.Select(fc => fc.urlName));

                foreach (var candidateUrlName in detailCandidateNames.Distinct())
                {
                    var durlJs = $"https://www.tennisabstract.com/jsfrags/{candidateUrlName}.js";
                    var durlMatch = $"https://www.tennisabstract.com/jsmatches/{candidateUrlName}.js";
                    var detailUrls = new[] { durlJs, durlMatch };

                    foreach (var durl in detailUrls)
                    {
                        for (int attempt = 1; attempt <= 2 && !detailsFetched; attempt++)
                        {
                            try
                            {
                                Console.WriteLine($"[DEBUG] Attempting to fetch player details from {durl} (attempt {attempt})");
                                (dob, hand, detailsJs) = await DataScraperHelpers.GetPlayerDetailsAsync(durl);
                                Console.WriteLine($"[DEBUG] Player details fetched from {durl}");
                                detailsFetched = true;
                            }
                            catch (Exception dex)
                            {
                                Console.WriteLine($"[DEBUG] Failed to fetch details from {durl} (attempt {attempt}): {dex.Message}");
                                if (attempt < 2) await Task.Delay(700);
                            }
                        }
                        if (detailsFetched) break;
                    }
                    if (detailsFetched) break;
                }

                if (!detailsFetched)
                {
                    Console.WriteLine($"Main Thread: Could not fetch player details for {playerName} from any endpoint.");
                    return null;
                }

                // If dob/hand not found in the JS response, try to extract from the HTML fragment as a fallback.
                if (string.IsNullOrWhiteSpace(dob) || string.IsNullOrWhiteSpace(hand))
                {
                    try
                    {
                        var docText = doc2.DocumentNode.InnerText;

                        if (string.IsNullOrWhiteSpace(dob))
                        {
                            // Try common textual formats like "Born May 3, 1998" or "Born: May 3, 1998"
                            var m = Regex.Match(docText, @"Born[:\s]*([A-Za-z]{3,9}\s+\d{1,2},\s*\d{4})", RegexOptions.IgnoreCase);
                            if (m.Success)
                                dob = m.Groups[1].Value;
                            else
                            {
                                // Try ISO-like
                                m = Regex.Match(docText, @"(\d{4}-\d{2}-\d{2})");
                                if (m.Success)
                                    dob = m.Groups[1].Value;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(hand))
                        {
                            // Look for words 'right' or 'left' near 'hand' or 'plays'
                            var m = Regex.Match(docText, @"(Right[- ]?hand|Right-handed|Plays[:\s]*Right|Hand[:\s]*Right)", RegexOptions.IgnoreCase);
                            if (m.Success)
                                hand = "R";
                            else
                            {
                                m = Regex.Match(docText, @"(Left[- ]?hand|Left-handed|Plays[:\s]*Left|Hand[:\s]*Left)", RegexOptions.IgnoreCase);
                                if (m.Success)
                                    hand = "L";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DEBUG] Fallback parsing from HTML failed for {playerName}: {ex.Message}");
                    }
                }

                int age = DataScraperHelpers.CalculateAge(dob);
                Console.WriteLine($"Main Thread: Player Found. dob='{(dob ?? "(null)")}' hand='{(hand ?? "(null)")}' parsedAge={age}");

                // If after all attempts dob is still missing, save the JS fragment for offline inspection
                if (string.IsNullOrWhiteSpace(dob))
                {
                    try
                    {
                        var diagDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? Directory.GetCurrentDirectory(), "diagnostics", "ta_fragments");
                        Directory.CreateDirectory(diagDir);
                        string toWrite = !string.IsNullOrWhiteSpace(detailsJs) ? detailsJs : jsText;
                        if (!string.IsNullOrWhiteSpace(toWrite))
                        {
                            var safeName = Regex.Replace(playerName, "[^A-Za-z0-9_-]", "_");
                            var fileName = Path.Combine(diagDir, $"{safeName}_{DateTime.UtcNow:yyyyMMddHHmmss}.js");
                            File.WriteAllText(fileName, toWrite);
                            Console.WriteLine($"[DEBUG] Wrote diagnostics JS fragment to {fileName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DEBUG] Failed to write diagnostics for {playerName}: {ex.Message}");
                    }
                }

                headers.Add(playerType);
                playerData.Add(cutOffTime);
                // Use the matched display name (space-separated like "First Middle Last") when available
                var displayNameToStore = !string.IsNullOrWhiteSpace(matchedDisplayName)
                    ? CultureInfo.CurrentCulture.TextInfo.ToTitleCase(matchedDisplayName.ToLower())
                    : playerName;
                playerData.Add(displayNameToStore);

                // Add Age (use -1 when unknown)
                headers.Add("Age");
                playerData.Add(age >= 0 ? age.ToString() : "-1");

                // Parse hand safely
                string handType = "1"; // default (preserve original mapping)
                if (!string.IsNullOrWhiteSpace(hand))
                {
                    if (hand.IndexOf("R", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        handType = "0";
                    }
                    else if (hand.IndexOf("L", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        handType = "1";
                    }
                }

                headers.Add("Hand");
                playerData.Add(handType);

                // ITF / Tour selection
                if (itfHeaderNode != null && itfHeaderNode.InnerText.Contains("ITF") && itfLevelSeason != null)
                {
                    Console.WriteLine($"Main Thread: ITF Data Found for {playerName}");
                    playerData.Insert(0, "ITF");
                    DataScraperHelpers.GetTournamentRecord(itfLevelSeason, headers, playerData);
                }
                else
                {
                    bool pickTour = DataScraperHelpers.PickTourLevel(tourLevelSeason, itfLevelSeason);

                    if (pickTour && tourHeaderNode != null && tourHeaderNode.InnerText.Contains("Tour-Level") && tourLevelSeason != null)
                    {
                        Console.WriteLine($"Main Thread: Tour-Level Data Picked for {playerName}");
                        playerData.Insert(0, "Tour-Level Season");
                        DataScraperHelpers.GetTournamentRecord(tourLevelSeason, headers, playerData);
                    }
                    else if (itfHeaderNode != null && itfHeaderNode.InnerText.Contains("Challenger") && itfLevelSeason != null)
                    {
                        Console.WriteLine($"Main Thread: Challenger-Level Data Picked for {playerName}");
                        playerData.Insert(0, "Challenger");
                        DataScraperHelpers.GetTournamentRecord(itfLevelSeason, headers, playerData);
                    }
                    else
                    {
                        Console.WriteLine($"Main Thread: {playerName} skipped due to Season data not found");
                        headers.Clear();
                        playerData.Clear();
                    }
                }

                // Split data
                if (!seasonDataNotFound && (itfLevelSeason != null || tourLevelSeason != null))
                {
                    if (splitItfLevel != null && splitItfHeaderNode != null && splitItfHeaderNode.InnerText.Contains("ITF"))
                    {
                        Console.WriteLine($"Main Thread: ITF Split data found for {playerName}");
                        var hd = splitItfLevel.SelectNodes(".//th")?.Cast<HtmlNode>().ToList() ?? new List<HtmlNode>();
                        var rows = splitItfLevel.SelectNodes(".//tr")?.Cast<HtmlNode>().ToList() ?? new List<HtmlNode>();
                        if (hd.Count > 0) hd[0].InnerHtml = "ITF-Level Split";
                        iteratedSplitDataMust = DataScraperHelpers.GetSplitData(rows, hd, splitDataMust, splitDataAvailable, headers, playerData);
                    }
                    else if (splitItfLevel != null && splitItfHeaderNode != null && splitItfHeaderNode.InnerText.Contains("Challenger-Level"))
                    {
                        Console.WriteLine($"Main Thread: Challenger-Level Split data found for {playerName}");
                        var hd = splitItfLevel.SelectNodes(".//th")?.Cast<HtmlNode>().ToList() ?? new List<HtmlNode>();
                        var rows = splitItfLevel.SelectNodes(".//tr")?.Cast<HtmlNode>().ToList() ?? new List<HtmlNode>();
                        if (hd.Count > 0) hd[0].InnerHtml = "Challenger-Level Split";
                        iteratedSplitDataMust = DataScraperHelpers.GetSplitData(rows, hd, splitDataMust, splitDataAvailable, headers, playerData);
                    }
                    else if (splitTourLevel != null && splitTourHeaderNode != null && splitTourHeaderNode.InnerText.Contains("Tour-Level"))
                    {
                        Console.WriteLine($"Main Thread: Tour-Level Split data found for {playerName}");
                        var hd = splitTourLevel.SelectNodes(".//th")?.Cast<HtmlNode>().ToList() ?? new List<HtmlNode>();
                        var rows = splitTourLevel.SelectNodes(".//tr")?.Cast<HtmlNode>().ToList() ?? new List<HtmlNode>();
                        if (hd.Count > 0) hd[0].InnerHtml = "Tour-Level Split";
                        iteratedSplitDataMust = DataScraperHelpers.GetSplitData(rows, hd, splitDataMust, splitDataAvailable, headers, playerData);
                    }
                    else
                    {
                        Console.WriteLine($"Main Thread: {playerName} skipped due to season data found but split data not found");
                        playerData.Clear();
                        headers.Clear();
                    }
                }

                // Validations
                if (iteratedSplitDataMust && splitDataAvailable.Count < requiredElements.Count)
                {
                    Console.WriteLine($"Main Thread: {playerName} skipped because we need {string.Join(", ", requiredElements)} and he/she have {string.Join(", ", splitDataAvailable)}");
                    headers.Clear();
                    playerData.Clear();
                }

                if (!iteratedSplitDataMust)
                {
                    Console.WriteLine($"Main Thread: The Split data does not meet our requirements {playerName}");
                    headers.Clear();
                    playerData.Clear();
                }

                if (headers.Count != 0 && playerData.Count != 0)
                {
                    // Mark record as incomplete if core TA fields are missing
                    headers.Add("IncompleteTA");
                    bool incomplete = (age < 0) || string.IsNullOrWhiteSpace(hand);
                    playerData.Add(incomplete ? "1" : "0");

                    recordDic = Tuple.Create(headers, playerData);
                }

                return recordDic;
            }
            catch (Exception ex)
            {
                // Provide full exception details to help identify parsing issues
                Console.WriteLine($"Main Thread: {playerName} not found due to error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);

                string correctedName = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(playerName);
                if (playerName.Contains("-"))
                    correctedName = playerName.Replace("-", " ");

                if (playerName == correctedName)
                {
                    Console.WriteLine($"Main Thread: {playerName} is skipped. No data was found");
                }
                else
                {
                    Console.WriteLine($"Main Thread: Try again for {playerName} with correct name ({correctedName}).");
                    return await GetDataAsync(cutOffTime, correctedName, playerType, retries);
                }

                return recordDic;
            }
        }
    }
}
