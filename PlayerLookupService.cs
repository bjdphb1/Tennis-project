using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TennisScraper
{
    public class PlayerLookupResult
    {
        public string? NormalizedName { get; set; }
        public string? DisplayName { get; set; }
        public string? RawHtmlFragment { get; set; }
        public bool Found { get; set; }
        public DateTime FetchedAt { get; set; }
    }

    /// <summary>
    /// Simple player lookup service with in-memory and optional on-disk JSON cache.
    /// Bounded concurrency, memoizes in-flight requests to avoid duplicate fetches.
    /// </summary>
    public class PlayerLookupService : IDisposable
    {
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _semaphore;
        private readonly ConcurrentDictionary<string, Task<PlayerLookupResult>> _inflight;
        private readonly ConcurrentDictionary<string, PlayerLookupResult> _cache;
        private readonly TimeSpan _cacheTtl = TimeSpan.FromHours(24);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _hostSemaphores;
        
        
    private readonly string _cacheFilePath;
    private readonly string _overridesFilePath;
    private readonly System.Collections.Generic.Dictionary<string, string> _overrides;

    public PlayerLookupService(int maxConcurrency = 6, TimeSpan? httpTimeout = null, string? cacheFile = null)
        {
            _cacheFilePath = cacheFile ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? Directory.GetCurrentDirectory(), "cache", "player_lookup_cache.json");
            try { Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath) ?? "cache"); } catch { }

            _overridesFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? Directory.GetCurrentDirectory(), "config", "ta_overrides.json");
            try { Directory.CreateDirectory(Path.GetDirectoryName(_overridesFilePath) ?? "config"); } catch { }
            _overrides = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(_overridesFilePath))
                {
                    var txt = File.ReadAllText(_overridesFilePath);
                    var map = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(txt);
                    if (map != null)
                    {
                        foreach (var kv in map)
                            if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                                _overrides[kv.Key.Trim()] = kv.Value.Trim();
                    }
                }
            }
            catch { /* don't fail startup for bad overrides file */ }

            _http = new HttpClient();
            // shorten default timeout to avoid 60s blocking waits during batch prefetches
            _http.Timeout = httpTimeout ?? TimeSpan.FromSeconds(10);
            // per-host concurrency control (limit concurrent requests to the same host)
            _hostSemaphores = new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
            _semaphore = new SemaphoreSlim(maxConcurrency);
            _inflight = new ConcurrentDictionary<string, Task<PlayerLookupResult>>(StringComparer.OrdinalIgnoreCase);
            _cache = new ConcurrentDictionary<string, PlayerLookupResult>(StringComparer.OrdinalIgnoreCase);

            // load disk cache if present
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    var text = File.ReadAllText(_cacheFilePath);
                    var entries = JsonSerializer.Deserialize<PlayerLookupResult[]>(text) ?? Array.Empty<PlayerLookupResult>();
                    foreach (var e in entries)
                    {
                        // validate cached entry minimally to avoid using poisoned or corrupted cache
                        if (e == null) continue;
                        var key = e.DisplayName ?? e.NormalizedName ?? "";
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        // mark Found=false if RawHtmlFragment looks empty or clearly invalid
                        if (string.IsNullOrWhiteSpace(e.RawHtmlFragment) || (!e.RawHtmlFragment.Contains("<") && !e.RawHtmlFragment.Contains("var player_frag")))
                        {
                            e.Found = false;
                            e.RawHtmlFragment = null;
                        }
                        _cache[key] = e;
                    }
                }
            }
            catch { }
        }

        // Normalize a display name into a safe URL token: remove diacritics, punctuation and whitespace.
        public static string NormalizeForUrl(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return string.Empty;
            // remove diacritics (accents)
            var normalized = displayName.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();
            foreach (var ch in normalized)
            {
                var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            var withoutDiacritics = sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
            // strip any non-alphanumeric characters
            var alpha = Regex.Replace(withoutDiacritics ?? string.Empty, "[^A-Za-z0-9 ]", "", RegexOptions.Compiled).Trim();
            // collapse spaces and return a concatenation (TennisAbstract uses FirstnameLastname without spaces)
            return Regex.Replace(alpha, "\\s+", "", RegexOptions.Compiled);
        }

        // Generate a small set of reasonable candidate url tokens from a display name.
        // Tries a few permutations (firstname+lastname, lastname+firstname, first+last only, etc.)
        private static string[] GenerateUrlCandidates(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return Array.Empty<string>();
            // remove diacritics and keep whitespace-separated tokens
            var normalized = displayName.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();
            foreach (var ch in normalized)
            {
                var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            var cleaned = Regex.Replace(sb.ToString(), "[^A-Za-z0-9 ]", " ", RegexOptions.Compiled).Trim();
            var tokens = Regex.Split(cleaned, "\\s+").Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();

            // remove common particles that may appear in names but not in TennisAbstract tokens
            var particles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "de", "da", "dos", "das", "la", "van", "von", "le", "du" };
            var filtered = tokens.Where(t => !particles.Contains(t)).ToArray();

            var candidates = new System.Collections.Generic.List<string>();
            // simple concatenation of cleaned tokens in original order
            if (filtered.Length > 0) candidates.Add(string.Concat(filtered));
            // reversed order
            if (filtered.Length > 1) candidates.Add(string.Concat(filtered.Reverse()));
            // first + last
            if (filtered.Length >= 2) candidates.Add(filtered[0] + filtered[filtered.Length - 1]);
            // last + first
            if (filtered.Length >= 2) candidates.Add(filtered[filtered.Length - 1] + filtered[0]);
            // try dropping middle names (first + last) already added, also try first + second
            if (filtered.Length >= 2) candidates.Add(filtered[0] + filtered[1]);
            // try joining only last two tokens (common for double-barrel names)
            if (filtered.Length >= 2) candidates.Add(filtered[filtered.Length - 2] + filtered[filtered.Length - 1]);

            // fallback: raw normalized (no particle removal)
            var rawConcat = Regex.Replace(displayName, "[^A-Za-z0-9]", "", RegexOptions.Compiled);
            if (!string.IsNullOrWhiteSpace(rawConcat)) candidates.Add(rawConcat);

            // ensure uniqueness and reasonable limit
            return candidates.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        }

        public Task<PlayerLookupResult> GetPlayerAsync(string displayName, CancellationToken cancel = default)
        {
            var key = (displayName ?? "").Trim();
            if (string.IsNullOrEmpty(key))
                return Task.FromResult(new PlayerLookupResult { NormalizedName = key, Found = false, FetchedAt = DateTime.UtcNow });

            if (_cache.TryGetValue(key, out var cached) && (DateTime.UtcNow - cached.FetchedAt) < _cacheTtl)
                return Task.FromResult(cached);

            return _inflight.GetOrAdd(key, _ => LookupInternalAsync(key, cancel));
        }

        private async Task<PlayerLookupResult> LookupInternalAsync(string displayName, CancellationToken cancel)
        {
            try
            {
                await _semaphore.WaitAsync(cancel).ConfigureAwait(false);

                if (_cache.TryGetValue(displayName, out var cached) && (DateTime.UtcNow - cached.FetchedAt) < _cacheTtl)
                    return cached;

                var urlName = NormalizeForUrl(displayName);
                var nameCandidates = GenerateUrlCandidates(displayName);

                var candidatesList = new System.Collections.Generic.List<string>();

                // Check overrides map first (quick win for known-bad names)
                try
                {
                    if (_overrides != null)
                    {
                        if (_overrides.TryGetValue(displayName.Trim(), out var ov) && !string.IsNullOrWhiteSpace(ov))
                        {
                            candidatesList.Add($"https://www.tennisabstract.com/cgi-bin/player-classic.cgi?p={ov}");
                            candidatesList.Add($"https://www.tennisabstract.com/jsfrags/{ov}.js");
                            candidatesList.Add($"https://www.tennisabstract.com/jsmatches/{ov}.js");
                        }
                        else if (!string.IsNullOrWhiteSpace(urlName) && _overrides.TryGetValue(urlName, out var ov2) && !string.IsNullOrWhiteSpace(ov2))
                        {
                            candidatesList.Add($"https://www.tennisabstract.com/cgi-bin/player-classic.cgi?p={ov2}");
                            candidatesList.Add($"https://www.tennisabstract.com/jsfrags/{ov2}.js");
                            candidatesList.Add($"https://www.tennisabstract.com/jsmatches/{ov2}.js");
                        }
                    }
                }
                catch { }

                foreach (var n in nameCandidates)
                {
                    candidatesList.Add($"https://www.tennisabstract.com/cgi-bin/player-classic.cgi?p={n}");
                    candidatesList.Add($"https://www.tennisabstract.com/jsfrags/{n}.js");
                    candidatesList.Add($"https://www.tennisabstract.com/jsmatches/{n}.js");
                }
                // ensure the simple normalized token is included as a baseline
                candidatesList.Add($"https://www.tennisabstract.com/cgi-bin/player-classic.cgi?p={urlName}");
                candidatesList.Add($"https://www.tennisabstract.com/jsfrags/{urlName}.js");
                candidatesList.Add($"https://www.tennisabstract.com/jsmatches/{urlName}.js");

                var candidates = candidatesList.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

                string? html = null;
                foreach (var candidate in candidates)
                {
                    var attempt = 0;
                    var maxAttempts = 3; // allow a couple retries for transient errors
                    var baseDelay = 250;

                    while (attempt < maxAttempts && html == null)
                    {
                        attempt++;
                        try
                        {
                            var host = new Uri(candidate).Host;
                            var hostSem = _hostSemaphores.GetOrAdd(host, h => new SemaphoreSlim(2));
                            await hostSem.WaitAsync(cancel).ConfigureAwait(false);
                            try
                            {
                                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                                // shorter per-request timeout to avoid long blocking waits
                                cts.CancelAfter(TimeSpan.FromSeconds(8));
                                var resp = await _http.GetAsync(candidate, cts.Token).ConfigureAwait(false);

                                // Distinguish between 404 (not found) and transient server errors
                                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                                {
                                    // this candidate is not present; stop retrying this candidate
                                    break;
                                }

                                if ((int)resp.StatusCode >= 500 || resp.StatusCode == (System.Net.HttpStatusCode)429)
                                {
                                    // transient server issue or rate-limit: backoff and retry
                                    var jitter = new Random().Next(100, 800);
                                    await Task.Delay(baseDelay * attempt + jitter, cancel).ConfigureAwait(false);
                                    continue;
                                }

                                if (resp.IsSuccessStatusCode)
                                {
                                    var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                                    // If this is a player-classic page, try to extract jsfrags/jsmatches links and fetch them
                                    if (candidate.Contains("player-classic.cgi") && !string.IsNullOrWhiteSpace(content))
                                    {
                                        // look for /jsfrags/Name.js or /jsmatches/Name.js in href or script tags
                                        var m = Regex.Match(content, "(jsfrags|jsmatches)/([A-Za-z0-9]+)\\.js", RegexOptions.IgnoreCase);
                                        if (m.Success)
                                        {
                                            var frag = m.Groups[1].Value + "/" + m.Groups[2].Value + ".js";
                                            var fragUrl = "https://www.tennisabstract.com/" + frag;
                                            try
                                            {
                                                using var fragCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                                                fragCts.CancelAfter(TimeSpan.FromSeconds(6));
                                                var fragResp = await _http.GetAsync(fragUrl, fragCts.Token).ConfigureAwait(false);
                                                if (fragResp.IsSuccessStatusCode)
                                                {
                                                    var fragContent = await fragResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                                    if (!string.IsNullOrWhiteSpace(fragContent))
                                                    {
                                                        html = fragContent;
                                                        break;
                                                    }
                                                }
                                            }
                                            catch { }
                                        }
                                    }

                                    // otherwise accept the content (jsfrags/jsmatches will be raw JS fragment strings)
                                    if (!string.IsNullOrWhiteSpace(content))
                                    {
                                        html = content;
                                        break;
                                    }
                                }
                            }
                            finally
                            {
                                try { hostSem.Release(); } catch { }
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch { /* ignore and retry where appropriate */ }

                        var jitter2 = new Random().Next(0, 300);
                        await Task.Delay(baseDelay * attempt + jitter2, cancel).ConfigureAwait(false);
                    }

                    if (!string.IsNullOrWhiteSpace(html)) break;
                }

                var result = new PlayerLookupResult
                {
                    NormalizedName = urlName,
                    DisplayName = displayName,
                    RawHtmlFragment = html,
                    Found = !string.IsNullOrWhiteSpace(html),
                    FetchedAt = DateTime.UtcNow
                };

                _cache[displayName] = result;
                _ = Task.Run(() => SaveCacheSafely());
                return result;
            }
            catch (OperationCanceledException)
            {
                return new PlayerLookupResult { NormalizedName = displayName, DisplayName = displayName, Found = false, FetchedAt = DateTime.UtcNow };
            }
            finally
            {
                _inflight.TryRemove(displayName, out _);
                _semaphore.Release();
            }
        }

        private void SaveCacheSafely()
        {
            try
            {
                var arr = _cache.Values;
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(_cacheFilePath, JsonSerializer.Serialize(arr, opts));
            }
            catch { }
        }

        public void Dispose()
        {
            SaveCacheSafely();
            _http?.Dispose();
            _semaphore?.Dispose();
        }
    }
}
