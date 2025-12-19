using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using Serilog;
using TennisScraper.Models;

namespace TennisScraper
{
    /// <summary>
    /// Selenium-based scraper for Dexsport.io
    /// Uses Chrome browser to bypass Cloudflare protection
    /// </summary>
    public class DexsportSeleniumScraper : IDisposable
    {
        public enum BetStatus
        {
            PLACED,
            EVENT_NOT_FOUND,
            PLACEMENT_ERROR
        }

        private IWebElement? FindElementSafe(By by)
        {
            try { return _driver.FindElement(by); } catch { return null; }
        }

        private void SaveDiagnostics(int matchId, string status)
        {
            // Stub: implement as needed
        }

        // Loader wait logic for polling
        private bool WaitForLoaderToDisappear(int timeoutSeconds = 30, int maxRetries = 3)
        {
            if (_driver == null) return true;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(timeoutSeconds));
                    bool loaderGone = wait.Until(drv =>
                    {
                        var loaderDivs = drv.FindElements(By.CssSelector("div[class*='z-[50]']"));
                        // Loader is gone if not found or has 'hidden' class
                        return loaderDivs.Count == 0 || loaderDivs.All(div => div.GetAttribute("class").Contains("hidden"));
                    });
                    if (loaderGone) return true;
                }
                catch (WebDriverTimeoutException)
                {
                    // Loader stuck, refresh and retry
                    Log.Warning($"[Loader] Loader stuck after {timeoutSeconds}s (attempt {attempt}/{maxRetries}). Refreshing page...");
                    _driver.Navigate().Refresh();
                    System.Threading.Thread.Sleep(3000); // Wait for refresh
                }
                catch (Exception ex)
                {
                    Log.Warning($"[Loader] Exception while waiting for loader: {ex.Message}");
                }
            }
            Log.Error($"[Loader] Loader did not disappear after {maxRetries} retries. Giving up.");
            return false;
        }

        private IWebDriver? _driver = null;
        private WebDriverWait? _wait = null;
        private const string DexsportTennisUrl = "https://dexsport.io/sports/tennis";
        private static readonly string JwtToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWJqZWN0Ijp7ImlkIjo0MDEwMH0sInR5cGUiOiJhY2Nlc3MiLCJleHAiOjE3NjQzMjc1NTgsImlhdCI6MTc2NDI0MTE1OCwianRpIjoiZGE3ZTQ3YzctZGJkMi00Mzg3LWFlMGItMDdkYWRhN2ZiNzM4In0.lMbiesJ96PqNKR73WSpJLq__1pZUmFvt5-ZfuGJe3m8";

        private bool _isInitialized = false;
        private readonly bool _headless;
        // When an external driver is provided we must not quit/dispose it
        private bool _ownsDriver = true;

        public DexsportSeleniumScraper(bool headless = false)
        {
            // Constructor - browser initialized on first use
            // Default: headless = false (Cloudflare detects headless browsers)
            // For production with Xvfb, keep headless = false (Chrome will render to virtual display)
            _headless = headless;
            _driver = null;
            _wait = null;
        }

        /// <summary>
        /// Construct the scraper with an existing IWebDriver instance.
        /// When provided, the scraper will reuse the driver and will NOT dispose it on Dispose().
        /// This allows sharing a single ChromeDriver across multiple scraper instances.
        /// </summary>
        public DexsportSeleniumScraper(IWebDriver driver, bool headless = false)
        {
            _headless = headless;
            _driver = driver;
            _ownsDriver = false; // do not quit/dispose external driver
            _isInitialized = true;
            try
            {
                if (_driver != null)
                    _wait = new WebDriverWait(_driver, TimeSpan.FromMinutes(5));
                // Do NOT set authentication when using shared driver (manual login)
                Log.Information("Using shared ChromeDriver: skipping SetAuthentication to preserve manual login session.");
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to setup provided driver: {ex.Message}");
            }
        }

        /// <summary>
        /// Convert Dexsport name format to TennisAbstract format
        /// Dexsport: "Alcaraz Garfia Carlos" or "Van Assche Luca" or "Sabalenka Aryna"
        /// TennisAbstract: "Carlos Alcaraz" or "Luca Van Assche" or "Aryna Sabalenka"
        /// 
        /// Rules:
        /// - Last name comes FIRST in Dexsport format
        /// - First name comes LAST in Dexsport format
        /// - Middle names/compound last names are in between
        /// 
        /// Conversion:
        /// - 2 parts: "Last First" → "First Last"
        /// - 3+ parts: "LastPart1 LastPart2... First" → "First LastPart1 LastPart2..."
        ///   (LAST word is first name, ALL OTHER words are the last name)
        /// </summary>
        private static string ConvertNameFormat(string dexsportName)
        {
            if (string.IsNullOrWhiteSpace(dexsportName))
                return dexsportName;

            // Normalize hyphens to spaces (e.g. Jan-Lennard -> Jan Lennard)
            var cleaned = dexsportName.Replace('-', ' ').Trim();
            // Remove any duplicate whitespace and split
            var parts = System.Text.RegularExpressions.Regex.Split(cleaned, "\\s+")
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

            if (parts.Length == 1)
            {
                // Single name - return as is (rare case)
                return parts[0];
            }
            else if (parts.Length == 2)
            {
                // "Last First" → "First Last"
                // Example: "Sabalenka Aryna" → "Aryna Sabalenka"
                return $"{parts[1]} {parts[0]}";
            }
            else // 3 or more parts
            {
                // "LastPart1 LastPart2... First" → "First LastPart1 LastPart2..."
                // Examples:
                //   "Van Assche Luca" → "Luca Van Assche"
                //   "Alcaraz Garfia Carlos" → "Carlos Alcaraz Garfia"
                //   "Silva Frederico Ferreira" → "Ferreira Silva Frederico"
                //   "Auger Aliassime Felix" → "Felix Auger Aliassime"

                // First name is the LAST part
                string firstName = parts[parts.Length - 1];

                // Last name is ALL parts EXCEPT the last one
                string lastName = string.Join(" ", parts.Take(parts.Length - 1));

                return $"{firstName} {lastName}";
            }
        }

        /// <summary>
        /// Initialize Chrome browser with Tor proxy
        /// </summary>
        private void InitializeBrowser()
        {
            if (_isInitialized) return;

            try
            {
                Log.Information("Initializing Chrome browser for Dexsport scraping...");

                var options = new ChromeOptions();

                // Headless mode (only if explicitly requested - Cloudflare can detect it)
                if (_headless)
                {
                    options.AddArgument("--headless=new");
                    Log.Warning("Running in HEADLESS mode - Cloudflare may show CAPTCHA");
                }
                else
                {
                    Log.Information("Running in VISIBLE mode (recommended for Cloudflare bypass)");
                }

                // Basic Chrome arguments (minimal to avoid detection)
                options.AddArgument("--no-sandbox");
                options.AddArgument("--disable-dev-shm-usage");
                options.AddArgument("--window-size=1920,1080");

                // Stealth options
                options.AddArgument("--disable-blink-features=AutomationControlled");
                options.AddExcludedArgument("enable-automation");
                options.AddUserProfilePreference("credentials_enable_service", false);
                options.AddUserProfilePreference("profile.password_manager_enabled", false);

                // Persistent user profile support (useful for manual Cloudflare solves)
                // Set CHROME_USER_DATA_DIR env var to a folder path to persist the Chrome profile.
                try
                {
                    var userDataDir = Environment.GetEnvironmentVariable("CHROME_USER_DATA_DIR");
                    if (!string.IsNullOrWhiteSpace(userDataDir))
                    {
                        try
                        {
                            // Ensure directory exists
                            if (!Directory.Exists(userDataDir))
                                Directory.CreateDirectory(userDataDir);

                            options.AddArgument($"--user-data-dir={userDataDir}");
                            Log.Information($"Using persistent Chrome user data dir: {userDataDir}");
                        }
                        catch (Exception dirEx)
                        {
                            Log.Warning($"Failed to prepare CHROME_USER_DATA_DIR '{userDataDir}': {dirEx.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Error while reading CHROME_USER_DATA_DIR: {ex.Message}");
                }

                // Create driver (no persistent profile - keeps it clean)
                // Use a ChromeDriverService and increase the command timeout (default ~60s)
                var service = ChromeDriverService.CreateDefaultService();
                service.SuppressInitialDiagnosticInformation = true;
                service.HideCommandPromptWindow = true;

                // Increase the command timeout to 3 minutes to avoid execute/sync timeouts
                _driver = new ChromeDriver(service, options, TimeSpan.FromSeconds(180));

                // Increase timeouts to prevent auto-close during Cloudflare challenges
                _driver.Manage().Timeouts().PageLoad = TimeSpan.FromMinutes(5);  // 5 minutes for page load
                _driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(10);
                // Longer JS execution timeout for heavy scripts / Cloudflare delays
                _driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromMinutes(5);  // 5 minutes for JS execution

                // Increase explicit wait horizon to match JS/script timeouts
                _wait = new WebDriverWait(_driver, TimeSpan.FromMinutes(5));  // 5 minute wait for elements

                // Set JWT token in localStorage (non-critical, continue if it fails)
                try
                {
                    SetAuthentication();
                }
                catch (Exception authEx)
                {
                    Log.Warning($"Authentication setup failed but continuing anyway: {authEx.Message}");
                }

                _isInitialized = true;
                Log.Information("Chrome browser initialized successfully");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to initialize browser: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Ensures the page is fully loaded with JavaScript executed
        /// </summary>
        private async Task EnsurePageFullyLoaded(int maxWaitSeconds = 30)
        {
            if (_driver == null) return;

            var js = (IJavaScriptExecutor)_driver;
            var startTime = DateTime.UtcNow;

            while ((DateTime.UtcNow - startTime).TotalSeconds < maxWaitSeconds)
            {
                try
                {
                    // Check document ready state
                    var readyState = js.ExecuteScript("return document.readyState")?.ToString();

                    // Check if key elements exist
                    var hasIframe = (bool)js.ExecuteScript("return document.querySelectorAll('iframe').length > 0;");
                    var hasNavigation = (bool)js.ExecuteScript("return document.querySelectorAll('.games-nav-pro').length > 0;");

                    if (readyState == "complete" && (hasIframe || hasNavigation))
                    {
                        Log.Information("✅ Page fully loaded with all elements");
                        return;
                    }

                    await Task.Delay(1000);
                }
                catch
                {
                    await Task.Delay(1000);
                }
            }

            Log.Warning("⚠️ Page load timeout reached, proceeding anyway...");
        }

        /// <summary>
        /// Set JWT authentication token in localStorage
        /// </summary>
        private void SetAuthentication()
        {
            try
            {
                // Navigate to Dexsport first to set domain
                Log.Information("Setting up authentication...");
                if (_driver != null)
                {
                    _driver.Navigate().GoToUrl("https://dexsport.io");
                }

                // Wait for page to load
                System.Threading.Thread.Sleep(3000);

                // Check JWT token
                if (string.IsNullOrEmpty(JwtToken))
                {
                    Log.Error("DEXSPORT_JWT environment variable is not set. Cannot authenticate.");
                    throw new InvalidOperationException("DEXSPORT_JWT environment variable is not set.");
                }

                // Set JWT token in localStorage
                var js = (IJavaScriptExecutor)_driver;
                js.ExecuteScript($"localStorage.setItem('persist:user', JSON.stringify({{sign: '\"{JwtToken}\"'}}));");
                js.ExecuteScript($"localStorage.setItem('user_id', '40100');");

                Log.Information("Authentication tokens set in localStorage");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to set authentication: {ex.Message}");
            }
        }

        /// <summary>
        /// Fetch tennis matches from Dexsport
        /// </summary>
        public async Task<List<MatchModel>> GetMatchesAsync()
        {
            var matches = new List<MatchModel>();

            try
            {
                // Initialize browser if not already done
                if (_driver == null && !_isInitialized)
                {
                    InitializeBrowser();
                }

                if (_driver == null)
                {
                    throw new InvalidOperationException("No browser instance available. Please provide a shared ChromeDriver.");
                }

                // Only navigate if we're not already on the tennis page (avoid unnecessary reload)
                string currentUrl = _driver.Url ?? "";
                bool alreadyOnTennisPage = currentUrl.Contains("dexsport.io/sports/tennis");

                if (!alreadyOnTennisPage)
                {
                    Log.Information("Navigating to Dexsport tennis page...");

                    // ✅ IMPROVED: Navigate with retry logic
                    int maxRetries = 2;
                    for (int attempt = 1; attempt <= maxRetries; attempt++)
                    {
                        try
                        {
                            _driver.Navigate().GoToUrl(DexsportTennisUrl);

                            // Wait for document ready
                            var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));
                            wait.Until(d => ((IJavaScriptExecutor)d).ExecuteScript("return document.readyState").Equals("complete"));

                            Log.Information($"✓ Page loaded (attempt {attempt})");
                            break;
                        }
                        catch (Exception ex)
                        {
                            if (attempt == maxRetries)
                                throw;

                            Log.Warning($"Page load failed (attempt {attempt}), refreshing...");
                            await Task.Delay(2000);
                            _driver.Navigate().Refresh();
                        }
                    }

                    // ✅ ADD: Check if we need to refresh (detect incomplete load)
                    var js = (IJavaScriptExecutor)_driver;
                    var hasIframe = (bool)js.ExecuteScript("return document.querySelectorAll('iframe').length > 0;");

                    if (!hasIframe)
                    {
                        Log.Warning("⚠️ No iframes detected, page may not be fully loaded. Refreshing...");
                        _driver.Navigate().Refresh();
                        await Task.Delay(5000);
                    }

                    Log.Information("✓ Waiting 30 seconds for tennis page to fully load...");
                    await Task.Delay(30000); // Wait 30 seconds for page and iframe to load
                    Log.Information("✓ Tennis page loaded, proceeding to scrape matches...");
                }
                else
                {
                    Log.Information("✓ Already on tennis page, skipping navigation (keeping session)");
                    await Task.Delay(2000); // Short wait to ensure iframe is ready
                }
                // Continue with scraping logic
                // ROBUST APPROACH: Wait directly for iframe to appear (this is where match data lives)
                Log.Information("=== WAITING FOR IFRAME WITH MATCH DATA ===");
                var iframeWait = new WebDriverWait(_driver, TimeSpan.FromMinutes(2));
                IWebElement iframe = null;
                int checkCount = 0;
                try
                {
                    iframe = iframeWait.Until(driver =>
                    {
                        try
                        {
                            checkCount++;
                            var iframes = driver.FindElements(By.TagName("iframe"));
                            if (iframes.Count > 0)
                            {
                                Log.Information($"✓ Found {iframes.Count} iframe(s) on page!");
                                return iframes[0];
                            }
                            if (checkCount % 5 == 0)
                            {
                                Log.Information($"Still waiting for iframe... (checked {checkCount} times)");
                                var title = driver.Title;
                                if (string.IsNullOrEmpty(title))
                                {
                                    Log.Warning("⚠ Browser may have closed or crashed!");
                                    throw new WebDriverException("Browser session lost");
                                }
                            }
                            return null;
                        }
                        catch (Exception ex) when (ex is not WebDriverException)
                        {
                            Log.Warning($"Error checking for iframe: {ex.Message}");
                            return null;
                        }
                    });
                }
                catch (WebDriverTimeoutException)
                {
                    Log.Error("✗ TIMEOUT: Iframe did not appear within 2 minutes!");
                    Log.Error("Possible causes:");
                    Log.Error("  1. Manual login not completed");
                    Log.Error("  2. Page structure changed");
                    Log.Error("  3. Network issues");
                    try
                    {
                        var debugSource = _driver.PageSource;
                        var debugPath = Path.Combine(Directory.GetCurrentDirectory(), "dexsport_page_debug.html");
                        System.IO.File.WriteAllText(debugPath, debugSource);
                        Log.Information($">>> Saved page HTML to: {debugPath}");
                    }
                    catch { }
                    return matches;
                }
                catch (WebDriverException ex)
                {
                    Log.Error($"✗ Browser session lost: {ex.Message}");
                    Log.Error("The browser may have closed or crashed.");
                    return matches;
                }
                Log.Information("✓ Iframe found! Switching to iframe context...");
                _driver.SwitchTo().Frame(iframe);
                await Task.Delay(3000); // Wait for iframe content to initialize
                // NOW WAIT FOR ACTUAL MATCH ELEMENTS INSIDE THE IFRAME
                Log.Information("=== WAITING FOR MATCH ELEMENTS INSIDE IFRAME ===");
                var matchElementWait = new WebDriverWait(_driver, TimeSpan.FromMinutes(3));
                try
                {
                    matchElementWait.Until(driver =>
                    {
                        var competitorNames = driver.FindElements(By.ClassName("grid-event__competitor-name"));
                        if (competitorNames.Count > 0)
                        {
                            Log.Information($"✓ Found {competitorNames.Count} competitor name elements!");
                            return true;
                        }
                        var gridEvents = driver.FindElements(By.CssSelector(".grid-event__content"));
                        if (gridEvents.Count > 0)
                        {
                            Log.Information($"✓ Found {gridEvents.Count} grid-event__content elements!");
                            return true;
                        }
                        var outcomes = driver.FindElements(By.ClassName("outcome"));
                        if (outcomes.Count > 0)
                        {
                            Log.Information($"✓ Found {outcomes.Count} outcome elements!");
                            return true;
                        }
                        Log.Information("Still waiting for match elements to render... (checking every 2s)");
                        return false;
                    });
                    Log.Information("✓✓✓ Match elements successfully loaded!");
                    Log.Information("Waiting for all elements to stabilize...");
                    await Task.Delay(5000);
                }
                catch (WebDriverTimeoutException)
                {
                    Log.Error("✗ TIMEOUT: Match elements did not appear within 3 minutes!");
                    Log.Error("Possible causes:");
                    Log.Error("  1. Manual login not completed");
                    Log.Error("  2. Site structure changed (CSS selectors may need updating)");
                    Log.Error("  3. No live matches available right now");
                    try
                    {
                        var iframeSource = _driver.PageSource;
                        var debugPath = Path.Combine(Directory.GetCurrentDirectory(), "dexsport_iframe_debug.html");
                        System.IO.File.WriteAllText(debugPath, iframeSource);
                        Log.Information($">>> Saved iframe HTML to: {debugPath}");
                    }
                    catch { }
                    _driver.SwitchTo().DefaultContent();
                    return matches;
                }
                Log.Information("=".PadRight(80, '='));
                Log.Information(">>> ALL MATCH ELEMENTS LOADED - READY TO SCRAPE <<<");
                Log.Information($">>> Iframe URL: {_driver.Url}");
                Log.Information("=".PadRight(80, '='));
                matches = await ParseMatches();
                _driver.SwitchTo().DefaultContent();
                Log.Information($"✓✓✓ Successfully scraped {matches.Count} matches from Dexsport");
            }
            catch (Exception ex)
            {
                Log.Error($"Error fetching matches from Dexsport: {ex.Message}");
                Log.Error($"Stack trace: {ex.StackTrace}");
            }
            return matches;
        }

        /// <summary>
        /// Parse tennis matches from the loaded iframe (assumes we're already in iframe context)
        /// </summary>
        private async Task<List<MatchModel>> ParseMatches()
        {
            var matches = new List<MatchModel>();

            try
            {
                // Save iframe HTML for debugging
                var iframeSource = _driver != null ? _driver.PageSource : string.Empty;
                Log.Information($"Iframe source length: {iframeSource.Length} characters");

                var iframeFilePath = Path.Combine(Directory.GetCurrentDirectory(), "dexsport_iframe.html");
                System.IO.File.WriteAllText(iframeFilePath, iframeSource);
                Log.Information($">>> Iframe HTML saved to: {iframeFilePath}");

                // We're already in the iframe context from GetMatchesAsync()

                // TEST: Find all match events with the correct class
                Log.Information("=== EXTRACTING MATCH DATA (FROM IFRAME) ===");

                // FIRST: Try JavaScript-based extraction (bypasses Shadow DOM issues)
                var jsExecutor = (IJavaScriptExecutor)_driver;

                Log.Information("Using JavaScript to query elements directly...");
                var jsScript = @"
                    // Try multiple selectors based on the screenshot
                    const competitorNames = Array.from(document.querySelectorAll('.grid-event__competitor-name')).map(e => e.textContent.trim());
                    const gridEventContent = document.querySelectorAll('.grid-event__content').length;
                    const gridEvents = document.querySelectorAll('.grid-event').length;
                    const gridEventPro = document.querySelectorAll('.grid-event-pro').length;
                    const lazyEventWrapper = document.querySelectorAll('.lazy-event-wrapper').length;

                    // Try to get match data from grid-event__content elements
                    const matches = [];
                    document.querySelectorAll('.grid-event__content').forEach(content => {
                        const link = content.querySelector('a[href*=""bouquier""], a[href*=""tennis""]');
                        const competitors = Array.from(content.querySelectorAll('.grid-event__competitor-name')).map(e => e.textContent.trim());
                        const odds = Array.from(content.querySelectorAll('.outcome__number')).map(e => e.textContent.trim());

                        // Extract match start time and status - look in parent grid-event element
                        let startTime = null;
                        let matchStatus = 'upcoming';
                        const gridEvent = content.closest('.grid-event, .grid-event-pro');
                        if (gridEvent) {
                            // ✅ PRIORITY CHECK: Look for live badge first (most reliable indicator)
                            const liveBadge = gridEvent.querySelector('._badge--live, [class*=\""badge\""][class*=\""live\""]');
                            if (liveBadge) {
                                matchStatus = 'live';
                                startTime = 'Live';
                            } else {
                                // Only check text-based indicators if no live badge found
                                const timeElement = gridEvent.querySelector('.grid-event__time span, .grid-event__time .time');
                                if (timeElement) {
                                    startTime = timeElement.textContent.trim();
                                    // Determine status based on time format
                                    if (startTime.includes('Set') || startTime.includes('Game') || startTime.includes(':') && startTime.split(':').length === 3) {
                                        matchStatus = 'live';
                                    } else if (startTime.includes('Live in')) {
                                        matchStatus = 'upcoming';
                                    }
                                } else {
                                    // Fallback: get all text from grid-event__time
                                    const timeContainer = gridEvent.querySelector('.grid-event__time');
                                    if (timeContainer) {
                                        startTime = timeContainer.textContent.trim();
                                        if (startTime.includes('Set') || startTime.includes('Game')) {
                                            matchStatus = 'live';
                                        }
                                    }
                                }
                            }
                        }

                        if (competitors.length > 0 || link) {
                            matches.push({
                                competitors: competitors,
                                odds: odds,
                                link: link ? link.href : null,
                                startTime: startTime,
                                status: matchStatus
                            });
                        }
                    });

                    return JSON.stringify({
                        competitorNames: competitorNames,
                        competitorCount: competitorNames.length,
                        gridEventContent: gridEventContent,
                        gridEvents: gridEvents,
                        gridEventPro: gridEventPro,
                        lazyEventWrapper: lazyEventWrapper,
                        matches: matches,
                        matchCount: matches.length
                    });
                ";

                try
                {
                    var jsResult = jsExecutor.ExecuteScript(jsScript) as string;
                    Log.Information($"JavaScript result: {jsResult}");

                    // Parse the result
                    dynamic data = Newtonsoft.Json.JsonConvert.DeserializeObject(jsResult);
                    Log.Information($"✓ JavaScript Element Counts:");
                    Log.Information($"  - grid-event__content: {data.gridEventContent}");
                    Log.Information($"  - grid-event: {data.gridEvents}");
                    Log.Information($"  - grid-event-pro: {data.gridEventPro}");
                    Log.Information($"  - lazy-event-wrapper: {data.lazyEventWrapper}");
                    Log.Information($"  - Competitor names: {data.competitorCount}");
                    Log.Information($"  - Matches found: {data.matchCount}");

                    if (data.matchCount > 0)
                    {
                        Log.Information("=== MATCHES FROM JAVASCRIPT ===");
                        var jsMatches = (Newtonsoft.Json.Linq.JArray)data.matches;

                        // Convert JavaScript matches to MatchModel objects
                        for (int i = 0; i < jsMatches.Count; i++)
                        {
                            try
                            {
                                var match = jsMatches[i];
                                var competitors = (Newtonsoft.Json.Linq.JArray)match["competitors"];
                                var odds = (Newtonsoft.Json.Linq.JArray)match["odds"];

                                // Extract match ID from link first (needed for display)
                                long matchId = i + 1;
                                string link = match["link"]?.ToString();
                                if (!string.IsNullOrEmpty(link))
                                {
                                    var linkParts = link.Split('-');
                                    if (linkParts.Length > 0)
                                    {
                                        var lastPart = linkParts[linkParts.Length - 1].Replace("/bets", "");
                                        if (long.TryParse(lastPart, out long parsedId))
                                            matchId = parsedId;
                                    }
                                }

                                if (i < 10)
                                {
                                    Log.Information($"Match {i + 1}:");
                                    if (competitors.Count > 0)
                                    {
                                        Log.Information($"  Players: {string.Join(" vs ", competitors)}");
                                    }
                                    if (odds.Count > 0)
                                    {
                                        Log.Information($"  Odds: {string.Join(" / ", odds)}");
                                    }
                                    if (match["startTime"] != null)
                                    {
                                        Log.Information($"  Start Time: {match["startTime"]}");
                                    }
                                    if (match["status"] != null)
                                    {
                                        Log.Information($"  Match Status: {match["status"]}");
                                    }
                                    Log.Information($"  Match ID: {matchId}");
                                    if (match["link"] != null)
                                    {
                                        Log.Information($"  Link: {match["link"]}");
                                    }
                                    Log.Information(""); // Empty line after each match
                                }

                                // Create MatchModel if we have at least 2 competitors
                                if (competitors != null && competitors.Count >= 2)
                                {
                                    // Convert names from Dexsport format to TennisAbstract format
                                    string dexsportHome = competitors[0]?.ToString();
                                    string dexsportAway = competitors[1]?.ToString();
                                    string homePlayer = ConvertNameFormat(dexsportHome);
                                    string awayPlayer = ConvertNameFormat(dexsportAway);

                                    // Log name conversion for first few matches (debugging)
                                    if (i < 3)
                                    {
                                        Log.Information($"  📝 Name Conversion:");
                                        Log.Information($"     Dexsport: '{dexsportHome}' → TennisAbstract: '{homePlayer}'");
                                        Log.Information($"     Dexsport: '{dexsportAway}' → TennisAbstract: '{awayPlayer}'");
                                    }

                                    double? homeOdds = null;
                                    double? awayOdds = null;

                                    if (odds != null && odds.Count > 0)
                                    {
                                        if (double.TryParse(odds[0]?.ToString(), out double home))
                                            homeOdds = home;
                                        if (odds.Count > 1 && double.TryParse(odds[1]?.ToString(), out double away))
                                            awayOdds = away;
                                    }

                                    // Parse start time from extracted data or use default
                                    string startTime = match["startTime"]?.ToString();
                                    string cutOffTime;

                                    if (!string.IsNullOrEmpty(startTime))
                                    {
                                        try
                                        {
                                            // Handle "Live in: 1h 26m" format (most common for upcoming matches)
                                            if (startTime.Contains("Live in:") || startTime.Contains("Live in"))
                                            {
                                                // Extract hours and minutes from "Live in: 1h 26m" or "Live in:1h 26m"
                                                var timeStr = startTime.Replace("Live in:", "").Replace("Live in", "").Trim();

                                                int hours = 0;
                                                int minutes = 0;

                                                // Parse hours
                                                var hourMatch = System.Text.RegularExpressions.Regex.Match(timeStr, @"(\d+)\s*h");
                                                if (hourMatch.Success)
                                                    int.TryParse(hourMatch.Groups[1].Value, out hours);

                                                // Parse minutes
                                                var minuteMatch = System.Text.RegularExpressions.Regex.Match(timeStr, @"(\d+)\s*m");
                                                if (minuteMatch.Success)
                                                    int.TryParse(minuteMatch.Groups[1].Value, out minutes);

                                                // Calculate cutoff time (current time + hours + minutes)
                                                var matchDateTime = DateTime.UtcNow.AddHours(hours).AddMinutes(minutes);
                                                cutOffTime = matchDateTime.ToString("o"); // ISO 8601 format

                                                Log.Debug($"  Parsed 'Live in' time: '{startTime}' → {hours}h {minutes}m → {matchDateTime:yyyy-MM-dd HH:mm} UTC");
                                            }
                                            // Handle "HH:mm" format (just time, assume today in local time)
                                            else if (startTime.Contains(":") && !startTime.Contains(" "))
                                            {
                                                var timeParts = startTime.Split(':');
                                                if (timeParts.Length >= 2 &&
                                                    int.TryParse(timeParts[0], out int hour) &&
                                                    int.TryParse(timeParts[1], out int minute))
                                                {
                                                    var now = DateTime.Now;  // Use local time, not UTC
                                                    var matchDateTime = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0, DateTimeKind.Local);

                                                    // If time already passed today, assume it's tomorrow
                                                    if (matchDateTime < now)
                                                        matchDateTime = matchDateTime.AddDays(1);

                                                    cutOffTime = matchDateTime.ToString("o");
                                                    Log.Debug($"  Parsed time: '{startTime}' → {matchDateTime:yyyy-MM-dd HH:mm} Local");
                                                }
                                                else
                                                {
                                                    cutOffTime = DateTime.UtcNow.AddHours(1).ToString("o");
                                                    Log.Warning($"  Could not parse time '{startTime}', using default +1 hour");
                                                }
                                            }
                                            // Handle live matches (Set 1, Set 2, etc.)
                                            else if (startTime.Contains("Set") || startTime.Contains("Game"))
                                            {
                                                // Match is already live, set cutoff to now (will be filtered out as live match)
                                                cutOffTime = DateTime.UtcNow.ToString("o");
                                                Log.Debug($"  Match is live: '{startTime}' → cutoff set to now");
                                            }
                                            // Handle full date/time strings (e.g., "Dec 15 • 20:00")
                                            else if (DateTime.TryParse(startTime, out DateTime parsedTime))
                                            {
                                                cutOffTime = parsedTime.ToUniversalTime().ToString("o");
                                                Log.Debug($"  Parsed full datetime: '{startTime}' → {parsedTime:yyyy-MM-dd HH:mm} UTC");
                                            }
                                            // Fallback: default to 1 hour from now
                                            else
                                            {
                                                cutOffTime = DateTime.UtcNow.AddHours(1).ToString("o");
                                                Log.Warning($"  Unknown time format '{startTime}', using default +1 hour");
                                            }
                                        }
                                        catch (Exception parseEx)
                                        {
                                            cutOffTime = DateTime.UtcNow.AddHours(1).ToString("o");
                                            Log.Warning($"  Error parsing start time '{startTime}': {parseEx.Message}, using default +1 hour");
                                        }
                                    }
                                    else
                                    {
                                        cutOffTime = DateTime.UtcNow.AddHours(1).ToString("o"); // Default
                                        Log.Warning($"  No start time found, using default +1 hour");
                                    }

                                    var matchModel = new MatchModel(
                                        matchId,
                                        homePlayer,
                                        awayPlayer,
                                        homeOdds,
                                        awayOdds,
                                        cutOffTime,
                                        link ?? string.Empty
                                    );

                                    // Set match status (live/upcoming) from JavaScript extraction
                                    if (match["status"] != null)
                                    {
                                        matchModel.Status = match["status"].ToString();
                                    }

                                    matches.Add(matchModel);
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warning($"Error converting match {i + 1}: {ex.Message}");
                            }
                        }

                        Log.Information($"\n✓✓✓ Successfully created {matches.Count} MatchModel objects!");

                        // Summary: Show all converted player names for TennisAbstract verification
                        if (matches.Count > 0)
                        {
                            Log.Information("\n" + "=".PadRight(80, '='));
                            Log.Information("📋 PLAYER NAMES CONVERTED FOR TENNISABSTRACT");
                            Log.Information("=".PadRight(80, '='));
                            Log.Information("These names will be checked against TennisAbstract API:");
                            Log.Information("URL format: https://www.tennisabstract.com/jsmatches/{FirstnameLastname}.js\n");

                            var uniquePlayers = new HashSet<string>();
                            foreach (var match in matches)
                            {
                                uniquePlayers.Add(match.HomePlayerName);
                                uniquePlayers.Add(match.AwayPlayerName);
                            }

                            int playerNum = 1;
                            foreach (var player in uniquePlayers.OrderBy(p => p))
                            {
                                string urlFormat = player.Replace(" ", "");
                                Log.Information($"{playerNum,3}. {player,-30} → URL: {urlFormat}.js", playerNum, player, urlFormat);
                                playerNum++;
                            }

                            Log.Information("\n" + "=".PadRight(80, '='));
                            Log.Information($"Total unique players: {uniquePlayers.Count}");
                            Log.Information($"Total matches: {matches.Count}");
                            Log.Information("=".PadRight(80, '=') + "\n");
                        }
                    }
                    else
                    {
                        Log.Warning("⚠ JavaScript found elements but no match data extracted");
                    }
                }
                catch (Exception jsEx)
                {
                    Log.Error($"JavaScript extraction failed: {jsEx.Message}");
                    Log.Error($"Stack trace: {jsEx.StackTrace}");
                }

                // THEN: Try Selenium's normal way for comparison
                var matchContainers = new List<IWebElement>();

                // Try CSS selector for grid-event
                var gridEvents = _driver.FindElements(By.CssSelector("[class*='grid-event']"));
                Log.Information($"Selenium found {gridEvents.Count} elements with 'grid-event' class");

                // Try finding competitor names directly
                var playerNameElements = _driver.FindElements(By.ClassName("grid-event__competitor-name"));
                Log.Information($"Found {playerNameElements.Count} player name elements");

                if (playerNameElements.Count == 0)
                {
                    // Try alternative selectors
                    Log.Information("Trying alternative selectors...");

                    // Try data attributes
                    var eventRows = _driver.FindElements(By.CssSelector("[data-event-id]"));
                    Log.Information($"Found {eventRows.Count} elements with data-event-id");

                    // Try looking for any element with "competitor" in class
                    var competitors = _driver.FindElements(By.CssSelector("[class*='competitor']"));
                    Log.Information($"Found {competitors.Count} elements with 'competitor' in class");

                    // Try to find by text content
                    var js = (IJavaScriptExecutor)_driver;
                    var elementsWithText = (long)js.ExecuteScript(
                        "return document.querySelectorAll('*').length;"
                    );
                    Log.Information($"Total elements on page: {elementsWithText}");
                }

                // Extract player names if found
                if (playerNameElements.Count > 0)
                {
                    Log.Information($"=== SUCCESSFULLY FOUND {playerNameElements.Count} PLAYER NAMES ===");

                    for (int i = 0; i < Math.Min(20, playerNameElements.Count); i++)
                    {
                        try
                        {
                            var element = playerNameElements[i];
                            var playerName = element.Text;

                            if (!string.IsNullOrWhiteSpace(playerName))
                            {
                                Log.Information($"  Player {i + 1}: {playerName}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"Could not extract player #{i + 1}: {ex.Message}");
                        }
                    }

                    // TODO: Group players into matches (pairs of players)
                    // TODO: Extract odds for each player
                    // TODO: Create MatchModel objects

                    Log.Information($"=== Found {playerNameElements.Count / 2} potential matches ===");
                }
                else
                {
                    Log.Error("✗ NO PLAYER NAME ELEMENTS FOUND!");
                    Log.Error("This means:");
                    Log.Error("  1. The CSS selectors are incorrect for this version of Dexsport");
                    Log.Error("  2. The page structure has changed");
                    Log.Error("  3. JavaScript is still loading content");
                    Log.Error("  4. Content is in a shadow DOM or iframe we haven't accessed");
                }

                Log.Information("=== END EXTRACTION ===");

                // Note: We DON'T switch back here - that's done in GetMatchesAsync() after this method returns
            }
            catch (Exception ex)
            {
                Log.Error($"Error parsing matches: {ex.Message}");
            }

            return matches;
        }

        /// <summary>
        /// Place a bet on Dexsport
        /// </summary>
        public async Task<string> PlaceBetAsync(string matchId, string selection, decimal odds, decimal stake, string homePlayer, string awayPlayer)
        {
            // homePlayer and awayPlayer come from Dexsport API in format already matching page display (e.g., "Udvardy Panna")
            Log.Information($"Dexsport PlaceBet: Attempting to place bet for match {matchId}");
            Log.Information($"  Player names from API: '{homePlayer}' vs '{awayPlayer}'");
            Log.Information($"  Selection: {selection}, Stake: {stake}, Odds: {odds}");

            try
            {
                if (_driver == null)
                    throw new InvalidOperationException("No browser instance available. Please provide a shared ChromeDriver.");

                // Use names directly as they come from API - they already match the page format
                // Only navigate if we're not already on this match page
                var homeSlug = homePlayer.ToLowerInvariant().Replace(" ", "-");
                var awaySlug = awayPlayer.ToLowerInvariant().Replace(" ", "-");
                var matchUrl = $"https://dexsport.io/sports/tennis/{homeSlug}-vs-{awaySlug}-{matchId}/bets/";

                string currentUrl = _driver.Url ?? "";
                bool alreadyOnMatchPage = currentUrl.Contains($"/{matchId}/");

                if (!alreadyOnMatchPage)
                {
                    Log.Information($"  Navigating to match URL: {matchUrl}");
                    _driver.Navigate().GoToUrl(matchUrl);

                    // Wait for page to load - Dexsport uses React/Next.js so content loads dynamically
                    Log.Information("  Waiting for page content to load (JavaScript rendering)...");
                    await Task.Delay(60000); // Wait 60 seconds for JS to execute
                }
                else
                {
                    Log.Information($"  ✓ Already on match page {matchId}, skipping navigation");
                    await Task.Delay(2000); // Short stabilization wait
                }

                // Check if betting content loads in an iframe
                try
                {
                    Log.Information("  Checking for iframe containing betting content...");
                    var iframes = _driver.FindElements(By.TagName("iframe"));
                    Log.Information($"  Found {iframes.Count} iframes on page");

                    if (iframes.Count > 0)
                    {
                        // Try switching to each iframe to find betting buttons
                        for (int i = 0; i < iframes.Count; i++)
                        {
                            try
                            {
                                _driver.SwitchTo().Frame(i);
                                await Task.Delay(2000); // Wait for iframe content to load

                                var buttonsInFrame = _driver.FindElements(By.TagName("button"));
                                Log.Information($"  Iframe {i}: Found {buttonsInFrame.Count} buttons");

                                if (buttonsInFrame.Count > 10) // Likely the betting frame
                                {
                                    Log.Information($"  ✓ Switched to betting iframe #{i}");
                                    break;
                                }

                                _driver.SwitchTo().DefaultContent();
                            }
                            catch (Exception ex)
                            {
                                Log.Warning($"  Error checking iframe {i}: {ex.Message}");
                                _driver.SwitchTo().DefaultContent();
                            }
                        }
                    }

                    // Wait for betting buttons to appear
                    var wait = new OpenQA.Selenium.Support.UI.WebDriverWait(_driver, TimeSpan.FromSeconds(15));
                    wait.Until(d => d.FindElements(By.XPath("//button[contains(@class,'outcome') or contains(@class,'equal')]")).Count > 0);
                    Log.Information("  ✓ Betting buttons loaded");
                }
                catch (Exception ex)
                {
                    Log.Warning($"  Timeout waiting for betting buttons to load: {ex.Message}");
                }

                // We're on the direct match page, so we don't need to find the event element
                // Instead, find the odds buttons directly and click the appropriate one
                Log.Information("  Looking for odds buttons on match page...");

                // First, let's see what's actually on the page
                try
                {
                    var allButtons = _driver.FindElements(By.TagName("button"));
                    Log.Information($"  DEBUG: Found {allButtons.Count} total buttons on page");

                    var allDivs = _driver.FindElements(By.TagName("div"));
                    Log.Information($"  DEBUG: Found {allDivs.Count} total divs on page");

                    // Log first 10 buttons to see what they contain
                    for (int i = 0; i < Math.Min(10, allButtons.Count); i++)
                    {
                        try
                        {
                            var btn = allButtons[i];
                            var btnText = btn.Text;
                            var btnClass = btn.GetAttribute("class");
                            if (!string.IsNullOrWhiteSpace(btnText) || !string.IsNullOrWhiteSpace(btnClass))
                            {
                                Log.Information($"  DEBUG Button {i}: text='{btnText}', class='{btnClass}'");
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"  DEBUG: Failed to enumerate page elements: {ex.Message}");
                }

                IWebElement? oddsButton = null;
                var selectedPlayer = selection?.ToLowerInvariant() == "home" ? homePlayer : awayPlayer;
                var selectedOdds = selection?.ToLowerInvariant() == "home" ? odds : (selection?.ToLowerInvariant() == "away" ? odds : 0);

                // Convert odds from format like 6200 to 6.20
                var displayOdds = ((double)selectedOdds / 1000.0).ToString("F2");
                Log.Information($"  Looking for odds button with player='{selectedPlayer}' or odds='{displayOdds}'");

                // Strategy 1: Try to find button by player name in span elements
                // Based on actual HTML: <button class="outcome equal__101"><span class="outcome__status">Player Name</span></button>
                var buttonSelectors = new[]
                {
                    // Most specific: button with span containing player name
                    $"//button[.//span[contains(@class,'outcome__status') and contains(text(),'{selectedPlayer}')]]",
                    $"//button[.//span[contains(text(),'{selectedPlayer}')]]",
                    $"//button[contains(@class,'outcome')][.//span[contains(text(),'{selectedPlayer}')]]",
                    // Try by odds value
                    $"//button[.//span[contains(@class,'outcome__number') and contains(text(),'{displayOdds}')]]",
                    $"//button[.//span[contains(text(),'{displayOdds}')]]",
                    // Fallback: any button with player name anywhere
                    $"//button[contains(.,'{selectedPlayer}')]",
                    // Generic outcome buttons
                    "//button[contains(@class,'outcome')]",
                    "//button[contains(@class,'equal')]"
                };

                foreach (var selector in buttonSelectors)
                {
                    try
                    {
                        var buttons = _driver.FindElements(By.XPath(selector));
                        Log.Information($"  Selector '{selector}' found {buttons.Count} buttons");
                        if (buttons.Count > 0)
                        {
                            // Check if this matches our selection
                            foreach (var btn in buttons)
                            {
                                try
                                {
                                    var btnText = btn.Text;
                                    var btnClass = btn.GetAttribute("class");
                                    Log.Information($"    Button: text='{btnText}', class='{btnClass}'");

                                    if (btnText.Contains(selectedPlayer) || btnText.Contains(displayOdds) || btnText.Contains(selectedOdds.ToString()))
                                    {
                                        oddsButton = btn;
                                        Log.Information($"  ✓ Found matching odds button for {selectedPlayer}");
                                        break;
                                    }
                                }
                                catch { }
                            }
                            if (oddsButton != null) break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"  Selector failed: {ex.Message}");
                    }
                }

                if (oddsButton == null)
                {
                    // Save diagnostics for debugging
                    var sessionDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "session");
                    if (!System.IO.Directory.Exists(sessionDir)) System.IO.Directory.CreateDirectory(sessionDir);
                    var ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
                    var screenshotPath = System.IO.Path.Combine(sessionDir, $"odds_button_not_found_{matchId}_{ts}.png");
                    var htmlPath = System.IO.Path.Combine(sessionDir, $"odds_button_not_found_{matchId}_{ts}.html");

                    try
                    {
                        ((ITakesScreenshot)_driver).GetScreenshot().SaveAsFile(screenshotPath, ScreenshotImageFormat.Png);
                        System.IO.File.WriteAllText(htmlPath, _driver.PageSource);
                        Log.Information($"  Diagnostics saved: {screenshotPath}, {htmlPath}");
                    }
                    catch (Exception diagEx)
                    {
                        Log.Error($"  Failed to save diagnostics: {diagEx.Message}");
                    }

                    SaveDiagnostics(int.TryParse(matchId, out var mid) ? mid : 0, "odds_button_not_found");
                    Log.Warning($"Dexsport PlaceBet: could not locate odds button for matchId {matchId}. Selected player: '{selectedPlayer}'");
                    return "ODDS_BUTTON_NOT_FOUND";
                }

                // Scroll button into view and click it
                try
                {
                    // First, scroll the element into view
                    ((IJavaScriptExecutor)_driver).ExecuteScript("arguments[0].scrollIntoView({behavior: 'smooth', block: 'center'});", oddsButton);
                    await Task.Delay(1000); // Wait for smooth scroll to complete

                    // Try native click first
                    try
                    {
                        oddsButton.Click();
                        Log.Information($"  ✓ Clicked odds button for {selectedPlayer} (native click)");
                    }
                    catch (ElementClickInterceptedException)
                    {
                        // If native click fails due to overlay, use JavaScript click
                        Log.Information($"  Native click intercepted, trying JavaScript click...");
                        ((IJavaScriptExecutor)_driver).ExecuteScript("arguments[0].click();", oddsButton);
                        Log.Information($"  ✓ Clicked odds button for {selectedPlayer} (JavaScript click)");
                    }
                }
                catch (Exception clickEx)
                {
                    Log.Error($"Failed to click odds button: {clickEx.Message}");
                    return "ODDS_BUTTON_CLICK_FAILED";
                }

                // Wait for betslip to appear and load
                Log.Information($"  Waiting for betslip to load...");
                await Task.Delay(5000); // Wait 5 seconds for betslip to appear and fully render

                // Try to wait for betslip to be visible
                try
                {
                    var betslipWait = new WebDriverWait(_driver, TimeSpan.FromSeconds(10));
                    betslipWait.Until(d =>
                    {
                        var betslips = d.FindElements(By.XPath("//div[contains(@class,'bet-slip') or contains(@class,'betslip')]"));
                        return betslips.Any(b => b.Displayed);
                    });
                    Log.Information($"  ✓ Betslip loaded and visible");
                }
                catch (Exception ex)
                {
                    Log.Warning($"  Betslip visibility wait timed out: {ex.Message}");
                }

                // Debug: Log all inputs on page to understand structure
                try
                {
                    var allInputs = _driver.FindElements(By.TagName("input"));
                    Log.Information($"  DEBUG: Found {allInputs.Count} total input elements on page");
                    foreach (var input in allInputs.Take(20))
                    {
                        try
                        {
                            var inputType = input.GetAttribute("type") ?? "unknown";
                            var inputClass = input.GetAttribute("class") ?? "";
                            var inputPlaceholder = input.GetAttribute("placeholder") ?? "";
                            var isDisplayed = input.Displayed;
                            var isEnabled = input.Enabled;
                            Log.Information($"  DEBUG Input: type='{inputType}', class='{inputClass}', placeholder='{inputPlaceholder}', displayed={isDisplayed}, enabled={isEnabled}");
                        }
                        catch { }
                    }
                }
                catch { }

                // Enter stake - try multiple selectors
                IWebElement? stakeInput = null;
                var stakeSelectors = new[]
                {
                    "//input[@type='number']",
                    "//input[@type='text']",
                    "//input[contains(@class,'stake')]",
                    "//input[contains(@placeholder,'stake') or contains(@placeholder,'Stake')]",
                    "//input[contains(@class,'amount')]",
                    "//div[contains(@class,'bet-slip')]//input[@type='number']",
                    "//div[contains(@class,'betslip')]//input[@type='number']",
                    "//div[contains(@class,'coupon')]//input[@type='number']",
                    "//input[contains(@name,'stake')]",
                    "//input"  // Last resort: any input element
                };

                foreach (var selector in stakeSelectors)
                {
                    try
                    {
                        var inputs = _driver.FindElements(By.XPath(selector));
                        Log.Information($"  Stake selector '{selector}' found {inputs.Count} inputs");
                        if (inputs.Count > 0)
                        {
                            // Try to find a visible and enabled input
                            stakeInput = inputs.FirstOrDefault(i => i.Displayed && i.Enabled);
                            if (stakeInput != null)
                            {
                                Log.Information($"  ✓ Found stake input using selector: '{selector}'");
                                break;
                            }
                            else
                            {
                                // Log why inputs were rejected
                                foreach (var inp in inputs.Take(5))
                                {
                                    try
                                    {
                                        Log.Debug($"  Input rejected: displayed={inp.Displayed}, enabled={inp.Enabled}");
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"  Stake selector failed: {ex.Message}");
                    }
                }

                if (stakeInput == null)
                {
                    SaveDiagnostics(int.TryParse(matchId, out var midStake) ? midStake : 0, "stake_input_not_found");
                    Log.Warning($"Could not find stake input field for matchId {matchId}");
                    return "STAKE_INPUT_NOT_FOUND";
                }

                // Clear and enter stake (just the number, no currency)
                var jsExecutor = (IJavaScriptExecutor)_driver;
                var stakeValue = stake.ToString("0.##"); // Format as plain number (e.g., "10" not "10.00")

                // Method 1: Clear using JavaScript
                jsExecutor.ExecuteScript("arguments[0].value = '';", stakeInput);
                await Task.Delay(300);

                // Method 2: Enter stake using SendKeys
                stakeInput.SendKeys(stakeValue);
                await Task.Delay(300);

                // Method 3: Verify and force set if needed
                var enteredValue = stakeInput.GetAttribute("value");
                if (enteredValue != stakeValue)
                {
                    Log.Warning($"  ⚠️ Stake mismatch! Expected '{stakeValue}', got '{enteredValue}'. Using JavaScript...");
                    jsExecutor.ExecuteScript($"arguments[0].value = '{stakeValue}';", stakeInput);
                    // Trigger input/change events so the UI updates
                    jsExecutor.ExecuteScript("arguments[0].dispatchEvent(new Event('input', { bubbles: true }));", stakeInput);
                    jsExecutor.ExecuteScript("arguments[0].dispatchEvent(new Event('change', { bubbles: true }));", stakeInput);
                    await Task.Delay(300);
                }
                
                Log.Information($"  ✓ Entered stake: {stakeValue}");
                await Task.Delay(500);
                
                // Debug: Show all buttons after entering stake
                try
                {
                    var allButtonsAfterStake = _driver.FindElements(By.TagName("button"));
                    Log.Information($"  DEBUG: Found {allButtonsAfterStake.Count} total buttons after stake entry");
                    for (int i = 0; i < Math.Min(15, allButtonsAfterStake.Count); i++)
                    {
                        try
                        {
                            var btnText = allButtonsAfterStake[i].Text?.Replace("\n", " ").Replace("\r", "");
                            var btnClass = allButtonsAfterStake[i].GetAttribute("class");
                            var btnDisplayed = allButtonsAfterStake[i].Displayed;
                            var btnEnabled = allButtonsAfterStake[i].Enabled;
                            Log.Information($"  DEBUG Button {i}: text='{btnText}', class='{btnClass}', displayed={btnDisplayed}, enabled={btnEnabled}");
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"Could not debug buttons: {ex.Message}");
                }
                
                // Click confirm/place bet button - try multiple selectors
                IWebElement? confirmButton = null;
                var confirmSelectors = new[]
                {
                    "//button[contains(@class,'coupon__placebet-btn')]",  // Primary selector for Dexsport
                    "//button[contains(text(),'Place bet')]",              // Case-sensitive match
                    "//button[contains(text(),'Place Bet')]",
                    "//button[contains(text(),'Confirm')]",
                    "//button[contains(@class,'place-bet')]",
                    "//button[contains(@class,'placebet')]",
                    "//button[contains(@class,'confirm')]",
                    "//div[contains(@class,'bet-slip')]//button[contains(@class,'primary')]",
                    "//button[contains(@class,'submit')]"
                };
                
                foreach (var selector in confirmSelectors)
                {
                    try
                    {
                        var buttons = _driver.FindElements(By.XPath(selector));
                        Log.Information($"  Confirm selector '{selector}' found {buttons.Count} buttons");
                        if (buttons.Count > 0)
                        {
                            confirmButton = buttons.FirstOrDefault(b => b.Displayed && b.Enabled);
                            if (confirmButton != null)
                            {
                                Log.Information($"  ✓ Found confirm button");
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"  Confirm selector failed: {ex.Message}");
                    }
                }
                
                if (confirmButton == null)
                {
                    SaveDiagnostics(int.TryParse(matchId, out var midConfirm) ? midConfirm : 0, "confirm_button_not_found");
                    Log.Warning($"Could not find confirm button for matchId {matchId}");
                    return "CONFIRM_BUTTON_NOT_FOUND";
                }
                
                // Click confirm button
                confirmButton.Click();
                Log.Information($"  ✓ Clicked confirm/place bet button");
                
                // Wait longer for bet processing and potential error messages (increased from 3s to 10s)
                Log.Information($"  ⏳ Waiting 10 seconds for bet processing...");
                await Task.Delay(10000);
                
                // Check for error messages (insufficient funds, bet rejected, etc.)
                Log.Information($"  🔍 Scanning page for error messages...");
                try
                {
                    // First, check if betslip is still visible (indicates error)
                    var betslipStillVisible = false;
                    try
                    {
                        var betslipElement = _driver.FindElement(By.XPath("//div[contains(@class,'coupon') or contains(@class,'betslip')]"));
                        betslipStillVisible = betslipElement.Displayed;
                        Log.Information($"  DEBUG: Betslip still visible = {betslipStillVisible}");
                    }
                    catch { }
                    
                    // Check all text elements on page for error keywords
                    var allTextElements = _driver.FindElements(By.XPath("//*[self::div or self::span or self::p][string-length(text()) > 0]"));
                    Log.Information($"  DEBUG: Found {allTextElements.Count} text elements on page");
                    
                    int checkedCount = 0;
                    foreach (var element in allTextElements.Where(e => e.Displayed).Take(100))
                    {
                        try
                        {
                            var elementText = element.Text?.Trim() ?? "";
                            if (string.IsNullOrEmpty(elementText)) continue;
                            
                            checkedCount++;
                            var lowerText = elementText.ToLowerInvariant();
                            
                            // Log any message that might be relevant
                            if (lowerText.Contains("balance") || 
                                lowerText.Contains("insufficient") || 
                                lowerText.Contains("not enough") ||
                                lowerText.Contains("error") ||
                                lowerText.Contains("fail") ||
                                lowerText.Contains("недостаточно") ||
                                lowerText.Contains("insuficiente"))
                            {
                                Log.Warning($"  ⚠️ FOUND RELEVANT MESSAGE: '{elementText}' (class='{element.GetAttribute("class")}')");
                                
                                // Check for insufficient funds
                                if (lowerText.Contains("insufficient") || 
                                    lowerText.Contains("not enough") || 
                                    (lowerText.Contains("balance") && lowerText.Contains("low")) ||
                                    lowerText.Contains("недостаточно") || 
                                    lowerText.Contains("insuficiente"))
                                {
                                    SaveDiagnostics(int.TryParse(matchId, out var midError) ? midError : 0, "insufficient_funds");
                                    Log.Error($"  ❌ INSUFFICIENT FUNDS DETECTED!");
                                    return "INSUFFICIENT_FUNDS";
                                }
                                
                                // Check for other rejection reasons
                                if (lowerText.Contains("rejected") || 
                                    lowerText.Contains("declined") || 
                                    lowerText.Contains("failed"))
                                {
                                    SaveDiagnostics(int.TryParse(matchId, out var midReject) ? midReject : 0, "bet_rejected");
                                    Log.Error($"  ❌ BET REJECTED: {elementText}");
                                    return "REJECTED";
                                }
                            }
                        }
                        catch { }
                    }
                    
                    Log.Information($"  DEBUG: Checked {checkedCount} text elements for error messages");
                }
                catch (Exception checkEx)
                {
                    Log.Debug($"Error checking for error messages: {checkEx.Message}");
                }
                
                // Save diagnostics after placement
                SaveDiagnostics(int.TryParse(matchId, out var mid2) ? mid2 : 0, "placed");
                
                // Return the matchId (EventId) so it can be used to identify this bet later
                Log.Information($"  ✅ Bet placed successfully. EventId: {matchId}");
                return matchId;
            }
            catch (Exception ex)
            {
                SaveDiagnostics(int.TryParse(matchId, out var mid3) ? mid3 : 0, "placement_error");
                Log.Error(ex, $"Dexsport PlaceBet: Exception during bet placement for matchId {matchId}");
                return "PLACEMENT_ERROR";
            }
        }

        /// <summary>
        /// Get account balance from Dexsport
        /// </summary>
        public async Task<decimal> GetBalanceAsync(string currency = "USD")
        {
            try
            {
                // Only navigate if we're not already on the profile page (avoid unnecessary reload)
                if (_driver != null)
                {
                    string currentUrl = _driver.Url ?? "";
                    bool alreadyOnProfilePage = currentUrl.Contains("dexsport.io/profile");
                    
                    if (!alreadyOnProfilePage)
                    {
                        Log.Information("Navigating to profile page to fetch balance...");
                        _driver.Navigate().GoToUrl("https://dexsport.io/profile/");
                        await Task.Delay(30000); // Wait 30 seconds for profile page to fully load
                        Log.Information("✓ Profile page loaded (waited 30s)");
                    }
                    else
                    {
                        Log.Information("✓ Already on profile page, skipping navigation (keeping session)");
                        await Task.Delay(2000); // Short wait to ensure balance is visible
                    }
                }
                
                // Try multiple selectors to find the balance
                var selectors = new[]
                {
                    // Primary: ProfileBalance value div (most specific from screenshot)
                    "div[class*='ProfileBalance_profile-balance__val']",
                    "div.ProfileBalance_profile-balance__val__y1Iw8",
                    
                    // Backup: parent structure
                    "div[class*='ProfileBalance_profile-balance__col'] div[class*='val']",
                    
                    // Alternative: look for balance in profile area
                    "div[class*='profile-balance'] div[class*='val']",
                    
                    // Fallback: generic balance patterns
                    "span[class*='balance']",
                    ".balance-value",
                    "[class*='balance-val']",
                };
                
                foreach (var selector in selectors)
                {
                    try
                    {
                        if (_driver == null) continue;
                        
                        var elements = _driver.FindElements(By.CssSelector(selector));
                        
                        foreach (var element in elements)
                        {
                            string balanceText = element.Text.Trim();
                            
                            // Skip empty or very short texts
                            if (string.IsNullOrWhiteSpace(balanceText) || balanceText.Length < 1)
                                continue;
                            
                            // Clean the balance text (remove currency symbols, commas, etc.)
                            balanceText = balanceText
                                .Replace("$", "")
                                .Replace("€", "")
                                .Replace("£", "")
                                .Replace(",", "")
                                .Replace(" ", "")
                                .Trim();
                            
                            // Try to parse as decimal
                            if (decimal.TryParse(balanceText, System.Globalization.NumberStyles.Any, 
                                System.Globalization.CultureInfo.InvariantCulture, out decimal balance))
                            {
                                // Sanity check: balance should be reasonable (between 0 and 1,000,000)
                                if (balance >= 0 && balance <= 1000000)
                                {
                                    Log.Information($"✅ Dexsport balance retrieved: ${balance:F2} (selector: {selector})");
                                    return balance;
                                }
                            }
                        }
                    }
                    catch (NoSuchElementException)
                    {
                        // Try next selector
                        continue;
                    }
                    catch (InvalidSelectorException ex)
                    {
                        Log.Warning($"⚠️ Invalid selector '{selector}': {ex.Message}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"⚠️ Error with selector '{selector}': {ex.Message}");
                        continue;
                    }
                }
                
                Log.Warning("⚠️ Could not find balance element on Dexsport page with any selector");
                Log.Information("💡 Trying JavaScript approach to find balance...");
                
                // Try JavaScript as last resort
                try
                {
                    if (_driver != null)
                    {
                        var jsExecutor = (IJavaScriptExecutor)_driver;
                        var jsResult = jsExecutor.ExecuteScript(@"
                            // Try to find balance using multiple approaches
                            const selectors = [
                                'div[class*=""ProfileBalance_profile-balance__val""]',
                                'div[class*=""profile-balance""][class*=""val""]',
                                '[class*=""balance""]'
                            ];
                            
                            for (const selector of selectors) {
                                const elements = document.querySelectorAll(selector);
                                for (const el of elements) {
                                    const text = el.textContent.trim();
                                    if (text && text.length > 0 && text.match(/[\d.,]+/)) {
                                        return text;
                                    }
                                }
                            }
                            return null;
                        ");
                        
                        if (jsResult != null)
                        {
                            string balanceText = jsResult.ToString() ?? "";
                            balanceText = balanceText
                                .Replace("$", "")
                                .Replace("€", "")
                                .Replace("£", "")
                                .Replace(",", "")
                                .Replace(" ", "")
                                .Trim();
                            
                            if (decimal.TryParse(balanceText, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out decimal balance))
                            {
                                if (balance >= 0 && balance <= 1000000)
                                {
                                    Log.Information($"✅ Balance retrieved via JavaScript: ${balance:F2}");
                                    return balance;
                                }
                            }
                        }
                    }
                }
                catch (Exception jsEx)
                {
                    Log.Warning($"⚠️ JavaScript approach also failed: {jsEx.Message}");
                }
                
                Log.Warning("⚠️ All balance retrieval methods failed");
                return 0m;
            }
            catch (Exception ex)
            {
                Log.Error($"❌ Error fetching Dexsport balance: {ex.Message}");
                return 0m;
            }
        }

        /// <summary>
        /// Best-effort: look up a placed ticket/reference on Dexsport and return a short status string.
        /// This is heuristic — Dexsport's DOM may change; we attempt several selectors and fallbacks.
        /// Returns null when the ticket could not be found or status is unknown.
        /// </summary>
        /// <param name="referenceId">Internal reference ID (not used for matching)</param>
        /// <param name="eventId">Dexsport event ID (e.g., "31781004") - used to match bet in modal</param>
        public async Task<string?> LookupTicketStatusAsync(string referenceId, string eventId = "")
        {
            try
            {
                if (_driver == null)
                    throw new InvalidOperationException("No browser instance available. Please provide a shared ChromeDriver.");

                // Wait for loader to disappear before polling for bet status
                if (!WaitForLoaderToDisappear(30, 3))
                {
                    Log.Error("[BET STATUS] Loader did not disappear before polling. Aborting status check.");
                    return null;
                }
                var js = (IJavaScriptExecutor)_driver;
                
                // Navigate to tennis page where "My Bets" button is always present
                Log.Information($"[BET STATUS] Current URL: {_driver.Url}");
                Log.Information("[BET STATUS] Waiting 30 seconds for bet to be processed...");
                await Task.Delay(30000);
                
                // Switch to main frame (exit betting iframe)
                try
                {
                    _driver.SwitchTo().DefaultContent();
                    Log.Information("[BET STATUS] ✓ Switched to default content (exited iframe)");
                }
                catch (Exception ex)
                {
                    Log.Warning($"[BET STATUS] Could not switch to default content: {ex.Message}");
                }
                
                // Navigate to tennis page to access "My Bets" button
                Log.Information("[BET STATUS] Navigating to tennis page to access 'My Bets' button...");
                _driver.Navigate().GoToUrl(DexsportTennisUrl);
                
                // Wait 30 seconds for full page reload
                Log.Information("[BET STATUS] Waiting 30 seconds for tennis page to fully load...");
                await Task.Delay(30000);
                
                // ✅ FIX: Find and switch to the iframe containing navigation
                Log.Information("[BET STATUS] Looking for iframe containing navigation header...");
                try
                {
                    // First, make sure we're in default content
                    _driver.SwitchTo().DefaultContent();
                    
                    // Find all iframes
                    var iframes = _driver.FindElements(By.TagName("iframe"));
                    Log.Information($"[BET STATUS] Found {iframes.Count} iframes on page");
                    
                    bool foundNavigation = false;
                    
                    // Try each iframe to find the one with navigation
                    for (int i = 0; i < iframes.Count; i++)
                    {
                        try
                        {
                            _driver.SwitchTo().DefaultContent(); // Reset first
                            _driver.SwitchTo().Frame(i);
                            await Task.Delay(1000); // Wait for iframe to load
                            
                            // Check if this iframe has navigation items
                            var navItemCount = (long)js.ExecuteScript(@"
                                return document.querySelectorAll('.games-nav-pro__item, [id=""mybets""], [class*=""nav""]').length;
                            ");
                            
                            if (navItemCount > 0)
                            {
                                Log.Information($"[BET STATUS] ✓ Found navigation in iframe #{i} ({navItemCount} nav items)");
                                foundNavigation = true;
                                break;
                            }
                            else
                            {
                                Log.Debug($"[BET STATUS] Iframe #{i} has no navigation ({navItemCount} items)");
                            }
                        }
                        catch (Exception iframeEx)
                        {
                            Log.Debug($"[BET STATUS] Error checking iframe #{i}: {iframeEx.Message}");
                            _driver.SwitchTo().DefaultContent();
                        }
                    }
                    
                    if (!foundNavigation)
                    {
                        Log.Warning("[BET STATUS] Could not find iframe with navigation header!");
                        _driver.SwitchTo().DefaultContent();
                    }
                    else
                    {
                        Log.Information("[BET STATUS] ✓ Switched into navigation iframe successfully");
                    }
                }
                catch (Exception navEx)
                {
                    Log.Warning($"[BET STATUS] Error finding navigation iframe: {navEx.Message}");
                    _driver.SwitchTo().DefaultContent();
                }
                
                // Now try to click "My Bets" button (should work now that we're in correct iframe)
                try
                {
                    Log.Information("[BET STATUS] Attempting to open 'My Bets' modal...");
                    
                    // Click "My bets" button
                    var clickResult = js.ExecuteScript(@"
                        try {
                            // Debug: Check what's on the page
                            let debugInfo = {
                                hasMyBetsId: !!document.getElementById('mybets'),
                                hasMyBetsClass: !!document.querySelector('.games-nav-pro__item._mybets'),
                                navItems: document.querySelectorAll('.games-nav-pro__item').length,
                                iframeCount: document.getElementsByTagName('iframe').length,
                                currentUrl: window.location.href
                            };
                            
                            // Try ID first (most specific)
                            let btn = document.getElementById('mybets');
                            if (btn) { 
                                btn.click();
                                return 'Clicked My Bets button (by ID)';
                            }
                            
                            // Fallback to class selectors
                            btn = document.querySelector('.games-nav-pro__item._mybets');
                            if (btn) { 
                                btn.click();
                                return 'Clicked My Bets button (by class)';
                            }
                            
                            // Mobile menu fallback
                            btn = document.querySelector('.mobile-menu__item._mybets');
                            if (btn) { 
                                btn.click();
                                return 'Clicked My Bets button (mobile)';
                            }
                            
                            return 'No mybets button found. Debug: ' + JSON.stringify(debugInfo);
                        } catch(e){
                            return 'Error: ' + e.message;
                        }
                    ");
                    Log.Information($"[BET STATUS] My Bets click result: {clickResult}");
                    
                    // If button not found, save screenshot for debugging
                    if (clickResult != null && clickResult.ToString().Contains("No mybets button found"))
                    {
                        try
                        {
                            var sessionDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "session");
                            if (!System.IO.Directory.Exists(sessionDir)) System.IO.Directory.CreateDirectory(sessionDir);
                            
                            var ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
                            var screenshotPath = System.IO.Path.Combine(sessionDir, $"mybets_button_not_found_{ts}.png");
                            var htmlPath = System.IO.Path.Combine(sessionDir, $"mybets_button_not_found_{ts}.html");
                            
                            ((ITakesScreenshot)_driver).GetScreenshot().SaveAsFile(screenshotPath, ScreenshotImageFormat.Png);
                            System.IO.File.WriteAllText(htmlPath, _driver.PageSource);
                            
                            Log.Warning($"[BET STATUS] 'My Bets' button not found! Diagnostics saved: {screenshotPath}");
                        }
                        catch { }
                    }
                    
                    // ✅ Wait longer for modal to fully load with tabs (increased to 20s)
                    Log.Information("[BET STATUS] Waiting 20 seconds for modal to fully load with tabs...");
                    await Task.Delay(20000);
                    
                    // ✅ Check if modal opened in CURRENT iframe
                    Log.Information("[BET STATUS] Checking if modal opened inside current iframe...");
                    var iframeModalCheck = js.ExecuteScript(@"
                        try {
                            const tabs = document.querySelectorAll('.games-tab, [class*=""games-tab""]');
                            const tabTexts = Array.from(tabs).map(t => t.textContent.trim());
                            const modals = document.querySelectorAll('[class*=""modal""], [class*=""mybets""]');
                            
                            console.log('[IFRAME CHECK] Found', tabs.length, 'tabs');
                            console.log('[IFRAME CHECK] Tab texts:', tabTexts);
                            
                            return JSON.stringify({
                                inIframe: true,
                                tabCount: tabs.length,
                                tabTexts: tabTexts,
                                modalCount: modals.length,
                                hasUnsettledTab: tabTexts.some(t => t.toLowerCase().includes('unsettled')),
                                hasSettledTab: tabTexts.some(t => t.toLowerCase().includes('settled'))
                            });
                        } catch(e) {
                            console.error('[IFRAME CHECK ERROR]:', e);
                            return 'Error: ' + e.message;
                        }
                    ");
                    Log.Information($"[BET STATUS] Iframe modal check: {iframeModalCheck}");
                    
                    // ✅ CRITICAL FIX: Check if modal opened inside the iframe
                    bool modalInIframe = false;
                    try
                    {
                        var iframeData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(iframeModalCheck?.ToString() ?? "{}");
                        if (iframeData != null && iframeData.ContainsKey("tabCount"))
                        {
                            int tabCount = iframeData["tabCount"].GetInt32();
                            if (tabCount > 0)
                            {
                                Log.Information($"[BET STATUS] ✅ Modal IS inside iframe! Found {tabCount} tabs - STAYING IN IFRAME");
                                modalInIframe = true;
                                // DON'T switch to main document - stay in iframe!
                            }
                            else
                            {
                                Log.Warning($"[BET STATUS] ⚠️ Modal in iframe but no tabs found yet (tabCount={tabCount})");
                                
                                // ✅ RETRY: Wait another 10 seconds and check again
                                Log.Information("[BET STATUS] 🔄 Waiting another 10 seconds for tabs to appear...");
                                await Task.Delay(10000);
                                
                                // Re-check for tabs
                                var retryCheck = js.ExecuteScript(@"
                                    try {
                                        const tabs = document.querySelectorAll('.games-tab, [class*=""games-tab""]');
                                        const tabTexts = Array.from(tabs).map(t => t.textContent.trim());
                                        console.log('[RETRY CHECK] Found', tabs.length, 'tabs:', tabTexts);
                                        return tabs.length;
                                    } catch(e) {
                                        console.error('[RETRY CHECK ERROR]:', e);
                                        return 0;
                                    }
                                ");
                                
                                int retryTabCount = retryCheck != null ? Convert.ToInt32(retryCheck) : 0;
                                Log.Information($"[BET STATUS] 🔄 Retry check found {retryTabCount} tabs");
                                
                                if (retryTabCount > 0)
                                {
                                    Log.Information($"[BET STATUS] ✅ Tabs appeared after retry! Staying in iframe.");
                                    modalInIframe = true;
                                }
                            }
                        }
                    }
                    catch (Exception parseEx)
                    {
                        Log.Warning($"[BET STATUS] Failed to parse iframe modal check: {parseEx.Message}");
                    }
                    
                    // ✅ Only switch to main document if modal is NOT in iframe
                    if (!modalInIframe)
                    {
                        Log.Information("[BET STATUS] Modal not in iframe, switching to main document...");
                        _driver.SwitchTo().DefaultContent();
                        
                        // Wait for modal in main document (increased to 20s)
                        Log.Information("[BET STATUS] Waiting 20 seconds for modal to render in main document...");
                        await Task.Delay(20000);
                        
                        var mainDocModalCheck = js.ExecuteScript(@"
                            try {
                                const tabs = document.querySelectorAll('.games-tab, [class*=""games-tab""]');
                                const tabTexts = Array.from(tabs).map(t => t.textContent.trim());
                                const modals = document.querySelectorAll('[class*=""modal""], [class*=""mybets""]');
                                const visibleModals = Array.from(modals).filter(m => m.offsetParent !== null);
                                
                                console.log('[MAIN DOC CHECK] Found', tabs.length, 'tabs');
                                
                                return JSON.stringify({
                                    inMainDoc: true,
                                    tabCount: tabs.length,
                                    tabTexts: tabTexts,
                                    modalCount: modals.length,
                                    visibleModalCount: visibleModals.length,
                                    hasUnsettledTab: tabTexts.some(t => t.toLowerCase().includes('unsettled')),
                                    hasSettledTab: tabTexts.some(t => t.toLowerCase().includes('settled'))
                                });
                            } catch(e) {
                                return 'Error: ' + e.message;
                            }
                        ");
                        Log.Information($"[BET STATUS] Main document modal check: {mainDocModalCheck}");
                    }
                    else
                    {
                        Log.Information("[BET STATUS] ✓ Staying in iframe context (modal is here)");
                    }
                    
                    // Wait for modal content to fully populate (increased to 20s)
                    Log.Information("[BET STATUS] Waiting 20 seconds for modal content to load...");
                    await Task.Delay(20000);
                    
                    // ✅ Debug: Check what's actually on the page now
                    var modalDebug = js.ExecuteScript(@"
                        try {
                            // Check for MY BETS modal specifically (has games-tab elements)
                            const allTabs = document.querySelectorAll('.games-tab, [class*=""games-tab""]');
                            const tabTexts = Array.from(allTabs).map(t => ({
                                text: t.textContent.trim(),
                                isActive: t.classList.contains('_active'),
                                classes: t.className
                            }));
                            
                            // Check for bet items
                            const betItems = document.querySelectorAll('.mybets-list__item, [class*=""mybets-list""]');
                            
                            // Find the mybets modal container
                            const mybetsModal = document.querySelector('[class*=""modal-box-mybets""], [class*=""mybets""][class*=""modal""]');
                            
                            return JSON.stringify({
                                tabCount: allTabs.length,
                                tabs: tabTexts,
                                betItemCount: betItems.length,
                                mybetsModalFound: !!mybetsModal,
                                mybetsModalVisible: mybetsModal ? (mybetsModal.offsetParent !== null) : false
                            });
                        } catch(e) {
                            return 'Error: ' + e.message;
                        }
                    ");
                    Log.Information($"[BET STATUS] Modal debug: {modalDebug}");
                    
                    // Click 'Unsettled' tab first (active bets)
                    Log.Information("[BET STATUS] Attempting to click 'Unsettled' tab...");
                    var tabClickResult = js.ExecuteScript(@"
                        try {
                            // Try multiple selectors for tabs - look inside visible modal
                            const tabSelectors = [
                                '.games-tab',
                                '.games-tabs .games-tab',
                                '[class*=""games-tab""]',
                                'div[class*=""tab""]',
                                'button[class*=""tab""]'
                            ];
                            
                            console.log('[TAB SEARCH] Looking for Unsettled tab...');
                            
                            for (let selector of tabSelectors) {
                                const tabs = document.querySelectorAll(selector);
                                console.log('[TAB SEARCH] Selector:', selector, 'Found:', tabs.length, 'tabs');
                                
                                for (let tab of tabs) {
                                    const tabText = tab.textContent.trim();
                                    console.log('[TAB SEARCH] Tab text:', tabText);
                                    
                                    if (tabText === 'Unsettled' || tabText.toLowerCase().includes('unsettled')) {
                                        console.log('[TAB SEARCH] ✓ Found Unsettled tab, clicking...');
                                        tab.click();
                                        return 'Clicked Unsettled tab: ' + tabText;
                                    }
                                }
                            }
                            
                            return 'No Unsettled tab found. Searched selectors: ' + tabSelectors.join(', ');
                        } catch(e) {
                            return 'Error: ' + e.message;
                        }
                    ");
                    Log.Information($"[BET STATUS] Tab click result: {tabClickResult}");
                    await Task.Delay(2000);
                }
                catch (Exception modalEx)
                {
                    Log.Warning($"[BET STATUS] Failed to open My Bets modal: {modalEx.Message}");
                }

                // ✅ ALWAYS check BOTH tabs (Unsettled first, then Settled)
                // Search by EventId (most reliable - matches link href like "/tennis/match-31812665/result")
                string script = $@"
                    (function() {{
                        try {{
                            window._betSearchLogs = window._betSearchLogs || [];
                            function log(...args) {{
                                const msg = args.join(' ');
                                console.log(...args);
                                window._betSearchLogs.push(msg);
                            }}
                            
                            const targetEventId = '{eventId}';
                            log('[BET SEARCH] Looking for EventId:', targetEventId);
                            
                            // Find all bet items
                            const betItems = document.querySelectorAll('.mybets-list__item, [class*=""mybets-list""][class*=""item""]');
                            log('[BET SEARCH] Found', betItems.length, 'bet items');
                            
                            if (betItems.length === 0) {{
                                log('[BET SEARCH] No bet items found');
                                return null;
                            }}
                            
                            // Search through each bet item
                            for (let i = 0; i < betItems.length; i++) {{
                                const bet = betItems[i];
                                
                                // Get all links in this bet
                                const links = bet.querySelectorAll('a');
                                log('[BET SEARCH] Bet #' + i + ' has', links.length, 'links');
                                
                                for (let j = 0; j < links.length; j++) {{
                                    const link = links[j];
                                    const href = link.href || '';
                                    
                                    if (i === 0 && j === 0) {{
                                        log('[BET SEARCH] First link href:', href);
                                        log('[BET SEARCH] Checking if href includes:', targetEventId);
                                        log('[BET SEARCH] Result:', href.includes(targetEventId));
                                    }}
                                    
                                    // ✅ Check if this link contains our EventId anywhere in the URL
                                    // Handles: /tennis/...-31812665/result OR ?setIframePath=/tennis/.../31812665/result
                                    if (href.includes(targetEventId)) {{
                                        log('[BET SEARCH] ✓ FOUND! EventId', targetEventId, 'at bet index', i, 'link', j);
                                        log('[BET SEARCH] Link:', href);
                                        
                                        // ✅ Extract status from bet-status div (as shown in screenshot)
                                        let status = 'Unknown';
                                        const statusDiv = bet.querySelector('.bet-status, [class*=""bet-status""]');
                                        if (statusDiv) {{
                                            status = statusDiv.textContent.trim();
                                            log('[BET SEARCH] Found status in .bet-status:', status);
                                        }}
                                        
                                        // Fallback: check for other status indicators
                                        if (status === 'Unknown') {{
                                            const allDivs = bet.querySelectorAll('div');
                                            for (let div of allDivs) {{
                                                const text = div.textContent.trim();
                                                if (text === 'Win' || text === 'Lost' || text === 'Void') {{
                                                    status = text;
                                                    log('[BET SEARCH] Found status in div:', status);
                                                    break;
                                                }}
                                            }}
                                        }}
                                        
                                        // Get full bet text for additional info
                                        const betText = bet.textContent || bet.innerText || '';
                                        
                                        // Get active tab name
                                        const activeTab = document.querySelector('.games-tab._active');
                                        const tabName = activeTab ? activeTab.textContent.trim() : 'Unknown';
                                        
                                        log('[BET SEARCH] Final status:', status, 'Tab:', tabName);
                                        
                                        return JSON.stringify({{
                                            found: true,
                                            eventId: targetEventId,
                                            status: status,
                                            tab: tabName,
                                            betIndex: i,
                                            fullText: betText.substring(0, 300),
                                            link: href
                                        }});
                                    }}
                                }}
                            }}
                            
                            log('[BET SEARCH] EventId', targetEventId, 'not found in', betItems.length, 'bets');
                            return null;
                            
                        }} catch(e) {{
                            log('[BET SEARCH ERROR]:', e.message);
                            return null;
                        }}
                    }})();
                ";

                // Try Unsettled tab first
                var result = js.ExecuteScript(script);
                
                // If not found in Unsettled tab, try Settled tab (this is where final results appear)
                if (result == null)
                {
                    try
                    {
                        Log.Information("[BET STATUS] Bet not found in Unsettled tab, checking Settled tab...");
                        
                        // ✅ CLICK SETTLED TAB BEFORE SEARCHING
                        var settledClickResult = js.ExecuteScript(@"
                            try {
                                console.log('[SETTLED TAB] Looking for Settled tab to click...');
                                
                                // Try multiple selectors for Settled tab
                                const tabSelectors = [
                                    '.games-tab',
                                    '.games-tabs .games-tab',
                                    '[class*=""games-tab""]',
                                    'div[class*=""tab""]',
                                    'button[class*=""tab""]'
                                ];
                                
                                for (let selector of tabSelectors) {
                                    const tabs = document.querySelectorAll(selector);
                                    console.log('[SETTLED TAB] Selector:', selector, 'Found:', tabs.length, 'tabs');
                                    
                                    for (let tab of tabs) {
                                        const tabText = tab.textContent.trim();
                                        console.log('[SETTLED TAB] Checking tab:', tabText);
                                        
                                        // EXACT MATCH for 'Settled'
                                        if (tabText === 'Settled' || tabText.toLowerCase() === 'settled') {
                                            console.log('[SETTLED TAB] ✓ Found Settled tab, clicking...');
                                            
                                            // Click using both methods to ensure it works
                                            tab.click();
                                            tab.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                                            
                                            // Add _active class if needed (some sites use this for styling)
                                            tab.classList.add('_active');
                                            
                                            // Remove _active from other tabs
                                            const allTabs = document.querySelectorAll(selector);
                                            allTabs.forEach(t => {
                                                if (t !== tab) {
                                                    t.classList.remove('_active');
                                                }
                                            });
                                            
                                            return 'Clicked Settled tab: ' + tabText + ' (class updated: ' + tab.className + ')';
                                        }
                                    }
                                }
                                
                                return 'ERROR: Settled tab not found! Searched selectors: ' + tabSelectors.join(', ');
                            } catch(e){
                                console.error('[SETTLED TAB ERROR]:', e);
                                return 'Error: ' + e.message;
                            }
                        ");
                        Log.Information($"[BET STATUS] Settled tab click result: {settledClickResult}");
                        
                        // ✅ CHECK IF CLICK WAS SUCCESSFUL
                        if (settledClickResult != null && settledClickResult.ToString()?.Contains("ERROR") == true)
                        {
                            Log.Error("[BET STATUS] ❌ Failed to click Settled tab!");
                            
                            // Save screenshot for debugging
                            try
                            {
                                var sessionDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "session");
                                if (!System.IO.Directory.Exists(sessionDir)) System.IO.Directory.CreateDirectory(sessionDir);
                                var ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
                                var screenshotPath = System.IO.Path.Combine(sessionDir, $"settled_tab_not_found_{ts}.png");
                                ((ITakesScreenshot)_driver).GetScreenshot().SaveAsFile(screenshotPath, ScreenshotImageFormat.Png);
                                Log.Warning($"[BET STATUS] Screenshot saved: {screenshotPath}");
                            }
                            catch { }
                            
                            return null;
                        }
                        
                        // ✅ WAIT FOR SETTLED TAB CONTENT TO LOAD (LONGER WAIT)
                        Log.Information("[BET STATUS] ⏳ Waiting 10 seconds for Settled tab to load and populate bets...");
                        await Task.Delay(10000);
                        
                        // ✅ VERIFY TAB IS NOW ACTIVE
                        var tabVerification = js.ExecuteScript(@"
                            try {
                                const activeTabs = document.querySelectorAll('.games-tab._active, [class*=""games-tab""][class*=""active""]');
                                const activeTabTexts = Array.from(activeTabs).map(t => t.textContent.trim());
                                
                                const allTabs = document.querySelectorAll('.games-tab, [class*=""games-tab""]');
                                const allTabTexts = Array.from(allTabs).map(t => ({
                                    text: t.textContent.trim(),
                                    isActive: t.classList.contains('_active') || t.classList.contains('active'),
                                    classes: t.className
                                }));
                                
                                return JSON.stringify({
                                    activeTabCount: activeTabs.length,
                                    activeTabTexts: activeTabTexts,
                                    allTabs: allTabTexts,
                                    settledIsActive: activeTabTexts.some(t => t.toLowerCase().includes('settled'))
                                });
                            } catch(e) {
                                return 'Error: ' + e.message;
                            }
                        ");
                        Log.Information($"[BET STATUS] Tab verification: {tabVerification}");
                        
                        // ✅ GET DEBUG INFO ABOUT SETTLED TAB CONTENTS
                        var settledDebug = js.ExecuteScript(@"
                            try {
                                // Count bet items
                                const betSelectors = [
                                    '.mybets-list__item',
                                    '[class*=""mybets-list""][class*=""item""]',
                                    '[class*=""infinite-list__item""]',
                                    '[data-id]'
                                ];
                                
                                let maxBetCount = 0;
                                let usedSelector = '';
                                
                                for (let selector of betSelectors) {
                                    const items = document.querySelectorAll(selector);
                                    if (items.length > maxBetCount) {
                                        maxBetCount = items.length;
                                        usedSelector = selector;
                                    }
                                }
                                
                                // Get links from bet items
                                const allBets = document.querySelectorAll(usedSelector || '.mybets-list__item');
                                const links = Array.from(allBets).slice(0, 10).map(bet => {
                                    const link = bet.querySelector('a');
                                    return link ? {
                                        href: link.href,
                                        text: bet.textContent.substring(0, 100)
                                    } : { href: 'no-link', text: bet.textContent.substring(0, 100) };
                                });
                                
                                // Check if there's a message about no bets
                                const emptyMessages = Array.from(document.querySelectorAll('*')).filter(el => {
                                    const text = el.textContent.toLowerCase();
                                    return text.includes('no bets') || 
                                           text.includes('no settled') || 
                                           text.includes('there will be information');
                                }).map(el => el.textContent.trim());
                                
                                return JSON.stringify({
                                    betCount: maxBetCount,
                                    usedSelector: usedSelector,
                                    first10Bets: links,
                                    emptyMessages: emptyMessages,
                                    pageTextSnippet: document.body.textContent.substring(0, 500)
                                });
                            } catch(e) {
                                return 'Error: ' + e.message;
                            }
                        ");
                        Log.Information($"[BET STATUS] 📊 Settled tab contents: {settledDebug}");
                        
                        // ✅ NOW SEARCH FOR THE BET IN SETTLED TAB
                        Log.Information($"[BET STATUS] 🔍 Searching for bet in Settled tab (EventId: {eventId})...");
                        
                        // Execute the search script and explicitly store result in window variable
                        var scriptWithStorage = $@"
                            window._betSearchResult = {script}
                            return window._betSearchResult;
                        ";
                        
                        result = js.ExecuteScript(scriptWithStorage);
                        
                        // Log what we got back
                        Log.Information($"[BET STATUS] Script execution complete. Result type: {result?.GetType().Name ?? "NULL"}");
                        
                        if (result == null)
                        {
                            Log.Warning($"[BET STATUS] ⚠️ Script returned NULL");
                            
                            // Try to retrieve from window variable as backup
                            var storedResult = js.ExecuteScript("return window._betSearchResult;");
                            Log.Information($"[BET STATUS] Checking stored result: {storedResult?.GetType().Name ?? "NULL"}");
                            
                            if (storedResult != null)
                            {
                                result = storedResult;
                                Log.Information($"[BET STATUS] ✅ Retrieved result from window variable!");
                            }
                            
                            // Try to get browser console logs
                            var consoleLogs = js.ExecuteScript(@"
                                const logs = window._betSearchLogs || [];
                                return JSON.stringify(logs);
                            ");
                            Log.Information($"[BET STATUS] Console logs: {consoleLogs}");
                        }
                        else
                        {
                            var resultStr = result.ToString();
                            Log.Information($"[BET STATUS] ✅ Script returned: {resultStr?.Substring(0, Math.Min(200, resultStr?.Length ?? 0))}");
                        }
                    }
                    catch (Exception settledEx)
                    {
                        Log.Warning($"[BET STATUS] Error switching to Settled tab: {settledEx.Message}");
                    }
                }
                
                if (result == null)
                {
                    Log.Warning($"[BET STATUS] Bet reference '{referenceId}' not found in either tab");
                    
                    // Save diagnostics
                    try
                    {
                        var sessionDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "session");
                        if (!System.IO.Directory.Exists(sessionDir)) System.IO.Directory.CreateDirectory(sessionDir);
                        
                        var ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
                        var screenshotPath = System.IO.Path.Combine(sessionDir, $"bet_not_found_{referenceId}_{ts}.png");
                        var htmlPath = System.IO.Path.Combine(sessionDir, $"bet_not_found_{referenceId}_{ts}.html");
                        
                        ((ITakesScreenshot)_driver).GetScreenshot().SaveAsFile(screenshotPath, ScreenshotImageFormat.Png);
                        System.IO.File.WriteAllText(htmlPath, _driver.PageSource);
                        
                        Log.Information($"[BET STATUS] Diagnostics saved: {screenshotPath}");
                    }
                    catch (Exception diagEx)
                    {
                        Log.Warning($"[BET STATUS] Could not save diagnostics: {diagEx.Message}");
                    }
                    
                    return null;
                }

                var foundText = result.ToString();
                if (string.IsNullOrWhiteSpace(foundText))
                {
                    Log.Warning($"[BET STATUS] Empty result for bet reference '{referenceId}'");
                    return null;
                }

                // Parse JSON response if available
                string statusText = "";
                string fullText = foundText;
                string tabName = "";
                
                try
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(foundText);
                    if (parsed != null)
                    {
                        statusText = parsed.ContainsKey("status") ? parsed["status"]?.ToString() ?? "" : "";
                        fullText = parsed.ContainsKey("fullText") ? parsed["fullText"]?.ToString() ?? "" : "";
                        tabName = parsed.ContainsKey("tab") ? parsed["tab"]?.ToString() ?? "" : "";
                    }
                }
                catch
                {
                    // If JSON parsing fails, treat as plain text
                    fullText = foundText;
                }

                // Log what we found (with more details)
                Log.Information($"[BET STATUS CHECK] Ref: {referenceId}");
                Log.Information($"[BET STATUS CHECK] Tab: '{tabName}'");
                Log.Information($"[BET STATUS CHECK] Status Text: '{statusText}'");
                Log.Information($"[BET STATUS CHECK] Full Text (first 200 chars): '{fullText.Substring(0, Math.Min(200, fullText.Length))}'");
                
                // Combine status text and full text for matching
                var combinedText = (statusText + " " + fullText).ToLowerInvariant();
                // Normalize tab name for case-insensitive checks
                var tabNameLower = (tabName ?? string.Empty).ToLowerInvariant();

                // ✅ PRIMARY CHECK: If bet found in "Unsettled" tab, it means bet is ACCEPTED
                if (tabNameLower.Contains("unsettled"))
                {
                    Log.Information($"✅ Bet #{referenceId} found in Unsettled tab → Status: ACCEPTED");
                    return "ACCEPTED";
                }
                
                // ✅ SECONDARY CHECK: If bet is in "Settled" tab, check the bet-status div for final result
                if (tabNameLower.Contains("settled"))
                {
                    Log.Information($"🔍 Bet #{referenceId} found in Settled tab");
                    Log.Information($"   - Status div text: '{statusText}'");
                    Log.Information($"   - Combined text: '{combinedText.Substring(0, Math.Min(150, combinedText.Length))}'");
                    
                    // Check status div first (most reliable)
                    if (!string.IsNullOrWhiteSpace(statusText))
                    {
                        var statusLower = statusText.ToLowerInvariant().Trim();
                        
                        Log.Information($"[STATUS PARSE] Checking status: '{statusText}' -> lowercase: '{statusLower}'");
                        
                        // ✅ Check for exact matches first, then partial matches
                        if (statusLower == "lost" || statusLower == "loss" || statusLower == "lose")
                        {
                            Log.Warning($"💔 Bet #{referenceId} LOST (exact match from bet-status div: '{statusText}')");
                            return "LOST";
                        }
                        if (statusLower == "win" || statusLower == "won")
                        {
                            Log.Information($"🎉 Bet #{referenceId} WON (exact match from bet-status div: '{statusText}')");
                            return "WON";
                        }
                        if (statusLower == "void" || statusLower == "voided")
                        {
                            Log.Warning($"⚠️ Bet #{referenceId} VOIDED (exact match from bet-status div: '{statusText}')");
                            return "VOID";
                        }
                        
                        // Partial matches as fallback
                        if (statusLower.Contains("lost") || statusLower.Contains("loss") || statusLower.Contains("lose"))
                        {
                            Log.Warning($"💔 Bet #{referenceId} LOST (partial match from bet-status div: '{statusText}')");
                            return "LOST";
                        }
                        if (statusLower.Contains("win") || statusLower.Contains("won"))
                        {
                            Log.Information($"🎉 Bet #{referenceId} WON (partial match from bet-status div: '{statusText}')");
                            return "WON";
                        }
                        if (statusLower.Contains("void") || statusLower.Contains("cancelled") || statusLower.Contains("canceled"))
                        {
                            Log.Warning($"⚠️ Bet #{referenceId} VOIDED (partial match from bet-status div: '{statusText}')");
                            return "VOID";
                        }
                    }
                    
                    // Fallback: check combined text
                    if (combinedText.Contains("win") || combinedText.Contains("won"))
                    {
                        Log.Information($"🎉 Bet #{referenceId} WON (from combined text)");
                        return "WON";
                    }
                    if (combinedText.Contains("loss") || combinedText.Contains("lost") || combinedText.Contains("lose"))
                    {
                        Log.Warning($"💔 Bet #{referenceId} LOST (from combined text)");
                        return "LOST";
                    }
                    if (combinedText.Contains("void") || combinedText.Contains("cancelled") || combinedText.Contains("canceled"))
                    {
                        Log.Warning($"⚠️ Bet #{referenceId} VOIDED (from combined text)");
                        return "VOID";
                    }
                }
                
                // Check explicit status keywords (fallback for edge cases)
                if (combinedText.Contains("rejected") || combinedText.Contains("declined") || combinedText.Contains("refused"))
                {
                    Log.Warning($"❌ Bet #{referenceId} REJECTED (contains rejection keyword)");
                    return "REJECTED";
                }
                // Extra fallback: if settled keywords appear anywhere in the combined text, classify accordingly.
                if (combinedText.Contains("win") || combinedText.Contains("won"))
                {
                    Log.Information($"🎉 Bet #{referenceId} WON (fallback from combined text)");
                    return "WON";
                }
                if (combinedText.Contains("loss") || combinedText.Contains("lost") || combinedText.Contains("lose"))
                {
                    Log.Warning($"💔 Bet #{referenceId} LOST (fallback from combined text)");
                    return "LOST";
                }
                if (combinedText.Contains("accepted") || combinedText.Contains("confirmed") || combinedText.Contains("paid") || combinedText.Contains("success"))
                {
                    Log.Information($"✅ Bet #{referenceId} ACCEPTED (contains acceptance keyword)");
                    return "ACCEPTED";
                }
                if (combinedText.Contains("pending") || combinedText.Contains("waiting") || combinedText.Contains("processing") || combinedText.Contains("accepting") || combinedText.Contains("in progress"))
                {
                    Log.Information($"⏳ Bet #{referenceId} PENDING (contains pending keyword)");
                    return "PENDING_ACCEPTANCE";
                }

                // Extra attempt: try the centralized parser helper (covers more normalization and edge cases)
                try
                {
                    var helperResult = TennisScraper.BetStatusParser.ParseStatus(statusText, fullText, tabName ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(helperResult))
                    {
                        Log.Information($"[BET STATUS] Parser helper resolved status: {helperResult}");
                        return helperResult;
                    }
                }
                catch (Exception parseHelperEx)
                {
                    Log.Warning($"[BET STATUS] Parser helper threw: {parseHelperEx.Message}");
                }

                // Fallback: return snippet so caller can inspect
                Log.Warning($"⚠️ Could not determine status from text, returning raw text");
                return fullText.Length > 200 ? fullText.Substring(0, 200) : fullText;
            }
            catch (Exception ex)
            {
                Log.Warning($"LookupTicketStatusAsync exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Test if the JWT token is valid by checking profile page for username
        /// </summary>
        public bool IsJwtTokenValid(string expectedUsername)
        {
            try
            {
                if (_driver == null)
                    throw new InvalidOperationException("No browser instance available. Please provide a shared ChromeDriver.");
                SetAuthentication();
                _driver.Navigate().GoToUrl("https://dexsport.io/profile");
                System.Threading.Thread.Sleep(3000); // Wait for page to load
                var pageSource = _driver.PageSource;
                if (pageSource.Contains(expectedUsername))
                {
                    Log.Information($"JWT token is valid. Username '{expectedUsername}' found on profile page.");
                    return true;
                }
                else
                {
                    Log.Warning($"JWT token may be invalid. Username '{expectedUsername}' not found on profile page.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error while testing JWT token validity: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Test if auto-login works by checking for the username on the profile page after authentication
        /// </summary>
        public bool TestAutoLogin(string expectedUsername)
        {
            try
            {
                // Initialize browser if not already done
                if (_driver == null && !_isInitialized)
                {
                    InitializeBrowser();
                }
                
                if (_driver == null)
                    throw new InvalidOperationException("No browser instance available. Please provide a shared ChromeDriver.");
                
                SetAuthentication();
                _driver.Navigate().GoToUrl("https://dexsport.io/profile");
                System.Threading.Thread.Sleep(3000); // Wait for page to load
                var pageSource = _driver.PageSource;
                if (pageSource.Contains(expectedUsername))
                {
                    Log.Information($"Auto-login successful. Username '{expectedUsername}' found on profile page.");
                    return true;
                }
                else
                {
                    Log.Warning($"Auto-login failed. Username '{expectedUsername}' not found on profile page.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error while testing auto-login: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Dispose browser resources
        /// </summary>
        /// <summary>
        /// Convert player name from "First Last" to "Last First" format for button clicking
        /// Example: "Shuai Zhang" -> "Zhang Shuai"
        /// </summary>
        /// <summary>
        /// Convert player name from "Last First" to "First Last" format
        /// Dexsport API gives names in "Last First" format: "Zhang Shuai"
        /// TennisAbstract needs "First Last" format: "Shuai Zhang"
        /// Example: "Zhang Shuai" -> "Shuai Zhang"
        /// </summary>
        public static string ConvertToFirstLastFormat(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return name;
            
            var parts = name.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return name; // Single name, return as-is
            
            // First part is last name, second part is first name
            var lastName = parts[0];
            var firstName = string.Join(" ", parts.Skip(1));
            
            return $"{firstName} {lastName}";
        }

        public void Dispose()
        {
            try
            {
                if (_driver != null && _ownsDriver)
                {
                    _driver.Quit();
                    _driver.Dispose();
                    Log.Information("Browser closed successfully");
                }
                else if (_driver != null && !_ownsDriver)
                {
                    Log.Information("Shared external browser instance provided - not closing it.");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error disposing browser: {ex.Message}");
            }
        }

        // Example usage for auto-login test
        public static void RunAutoLoginTest()
        {
            var scraper = new DexsportSeleniumScraper();
            string expectedUsername = "ChampagneHanks";
            bool loginSuccess = scraper.TestAutoLogin(expectedUsername);
            Console.WriteLine($"Auto-login test result: {loginSuccess}");
            scraper.Dispose();
        }
    }



}
