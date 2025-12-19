using System;
using System.Collections.Generic;
using System.Linq;
using HtmlAgilityPack;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TennisScraper
{
    public static class DataScraperHelpers
    {
        private static readonly HttpClient _httpClient = ProxyHttpClientFactoryBasic.CreateHttpClientWithProxy();
        // ------------------ ModifyList ------------------
        public static List<string> ModifyList(List<string> inputList)
        {
            List<string> outputList = new List<string>();

            foreach (var item in inputList)
            {
                if (item.Contains('%'))
                {
                    string itemWithoutPercent = item.Replace("%", "");
                    if (double.TryParse(itemWithoutPercent, out double value))
                    {
                        outputList.Add((value * 10).ToString());
                    }
                    else
                    {
                        outputList.Add(item);
                    }
                }
                else if (!item.ToLower().Contains("finals"))
                {
                    List<string> parts = new List<string>();

                    if (item == "-")
                    {
                        Console.WriteLine("Main Thread: Missing data found!");
                        Console.WriteLine($"Main Thread: Input list - {string.Join(", ", inputList)}");
                        parts.Add("0");
                    }
                    else if (item.Contains("-"))
                    {
                        parts.AddRange(item.Split('-'));
                    }

                    if (parts.Count == 0)
                        outputList.Add(item);
                    else
                        outputList.AddRange(parts);
                }
                else
                {
                    outputList.Add(item);
                }
            }

            return outputList;
        }

        // ------------------ PickTourLevel ------------------
        public static bool PickTourLevel(HtmlNode tourLevelSeason, HtmlNode itfLevelSeason)
        {
            string GetLastPlayedText(HtmlNode table)
            {
                if (table == null) return null;

                var rows = table.SelectNodes(".//tr")?.Cast<HtmlNode>().ToList();
                if (rows == null || rows.Count == 0) return null;

                // Get last row with at least 2 <td> elements
                for (int i = rows.Count - 1; i >= 0; i--)
                {
                    var cells = rows[i].SelectNodes(".//td")?.Cast<HtmlNode>().ToList();
                    if (cells != null && cells.Count > 1)
                    {
                        var text = cells[1].InnerText.Trim();
                        if (!string.IsNullOrEmpty(text))
                            return text; // Return the raw text, like Python
                    }
                }

                return null;
            }

            string tourText = GetLastPlayedText(tourLevelSeason);
            string itfText = GetLastPlayedText(itfLevelSeason);

            bool pickTour = true;

            if (itfText != null && tourText == null)
            {
                pickTour = false; // Only ITF exists
            }
            else if (itfText == null && tourText != null)
            {
                pickTour = true; // Only Tour exists
            }
            else if (itfText != null && tourText != null)
            {
                // Compare lexicographically like Python
                if (string.Compare(itfText, tourText, StringComparison.Ordinal) > 0)
                    pickTour = false;
            }

            return pickTour;
        }



        // ------------------ GetTournamentRecord ------------------
        public static void GetTournamentRecord(HtmlNode tournamentType, List<string> headers, List<string> playerData)
        {
            if (tournamentType == null) return;

            var hd = tournamentType.SelectNodes(".//th")?.Cast<HtmlNode>().ToList() ?? new List<HtmlNode>();
            var rows = tournamentType.SelectNodes(".//tr")?.Cast<HtmlNode>().ToList() ?? new List<HtmlNode>();

            List<string> newHList = hd.Take(10).Select(h => h.InnerText.Trim()).ToList();
            headers.AddRange(newHList);
            if (headers.Count > 0) headers.RemoveAt(headers.Count - 1);
            headers.Add("TB W");
            headers.Add("TB L");

            if (rows.Count > 0)
            {
                var lastRow = rows.Last();
                var cel = lastRow.SelectNodes(".//td")?.Take(10).Select(td => td.InnerText.Trim()).ToList() ?? new List<string>();

                if (cel.Count > 0)
                {
                    var splitElements = cel.Last().Split('-');
                    var newR = cel.Take(cel.Count - 1).Concat(splitElements).ToList();
                    var correctList = ModifyList(newR);
                    playerData.AddRange(correctList);
                }
            }
        }

        public static string ExtractHtmlFromJs(string jsText)
        {
            // Match: var player_frag = `...`;
            var match = Regex.Match(jsText, @"var\s+player_frag\s*=\s*`([\s\S]*?)`;");
            if (!match.Success)
                return "";

            return match.Groups[1].Value;
        }

    public static async Task<(string dob, string hand, string jsContent)> GetPlayerDetailsAsync(string url)
        {
            // Increase timeout to handle slow responses
            try
            {
                _httpClient.Timeout = TimeSpan.FromSeconds(60);
            }
            catch { /* ignore if static client doesn't allow set */ }

            int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var resp = await _httpClient.GetAsync(url);
                    if (!resp.IsSuccessStatusCode)
                    {
                        if ((int)resp.StatusCode >= 500 && attempt < maxAttempts)
                        {
                            Console.WriteLine($"[DEBUG] Server response {(int)resp.StatusCode} {resp.StatusCode} for {url}, retry {attempt}");
                            await Task.Delay(800);
                            continue;
                        }
                        resp.EnsureSuccessStatusCode();
                    }

                    var jsContent = await resp.Content.ReadAsStringAsync();

                    // --- Extract var dob (handles numbers and quoted strings) ---
                    var dobRegex = new Regex("var\\s+dob\\s*=\\s*(['\\\"]?)([^'\\\"]+?)\\1\\s*;", RegexOptions.IgnoreCase);
                    var dobMatch = dobRegex.Match(jsContent);
                    string dob = dobMatch.Success ? dobMatch.Groups[2].Value : null;

                    // --- Extract var hand (always quoted) ---
                    var handRegex = new Regex("var\\s+hand\\s*=\\s*['\\\"]([^'\\\"]+)['\\\"]\\s*;", RegexOptions.IgnoreCase);
                    var handMatch = handRegex.Match(jsContent);
                    string hand = handMatch.Success ? handMatch.Groups[1].Value : null;

                    return (dob, hand, jsContent);
                }
                catch (TaskCanceledException tce) when (attempt < maxAttempts)
                {
                    Console.WriteLine($"[DEBUG] Timeout fetching {url}, attempt {attempt}: {tce.Message}");
                    await Task.Delay(800);
                    continue;
                }
                catch (HttpRequestException hre) when (attempt < maxAttempts)
                {
                    Console.WriteLine($"[DEBUG] HTTP error fetching {url}, attempt {attempt}: {hre.Message}");
                    await Task.Delay(700);
                    continue;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    Console.WriteLine($"[DEBUG] Error fetching {url}, attempt {attempt}: {ex.Message}");
                    await Task.Delay(700);
                    continue;
                }
            }

            throw new Exception($"Failed to fetch JS for {url} after {maxAttempts} attempts");
        }

        /// <summary>
        /// Parse dob/hand/jsContent from an already-fetched JS fragment string.
        /// This allows callers to avoid an extra network request when they already
        /// have the JS content (for example from a cache).
        /// </summary>
        public static (string dob, string hand, string jsContent) ParsePlayerDetailsFromJs(string jsContent)
        {
            if (string.IsNullOrWhiteSpace(jsContent)) return (null, null, null);

            // --- Extract var dob (handles numbers and quoted strings) ---
            var dobRegex = new Regex("var\\s+dob\\s*=\\s*(['\\\"]?)([^'\\\"]+?)\\1\\s*;", RegexOptions.IgnoreCase);
            var dobMatch = dobRegex.Match(jsContent);
            string dob = dobMatch.Success ? dobMatch.Groups[2].Value : null;

            // --- Extract var hand (always quoted) ---
            var handRegex = new Regex("var\\s+hand\\s*=\\s*['\\\"]([^'\\\"]+)['\\\"]\\s*;", RegexOptions.IgnoreCase);
            var handMatch = handRegex.Match(jsContent);
            string hand = handMatch.Success ? handMatch.Groups[1].Value : null;

            return (dob, hand, jsContent);
        }


        public static int CalculateAge(string dobString)
        {
            // Return -1 if DOB missing or unparseable to avoid exceptions upstream
            if (string.IsNullOrWhiteSpace(dobString))
                return -1;

            // If it's in yyyymmdd numeric form, parse directly
            if (dobString.Length == 8 && long.TryParse(dobString, out _))
            {
                try
                {
                    int year = int.Parse(dobString.Substring(0, 4));
                    int month = int.Parse(dobString.Substring(4, 2));
                    int day = int.Parse(dobString.Substring(6, 2));
                    DateTime birthDate = new DateTime(year, month, day);
                    DateTime today = DateTime.Today;
                    int age = today.Year - birthDate.Year;
                    if (today.Month < birthDate.Month || (today.Month == birthDate.Month && today.Day < birthDate.Day))
                        age--;
                    return age;
                }
                catch
                {
                    return -1;
                }
            }

            // Try parsing common textual date formats
            DateTime parsed;
            if (DateTime.TryParse(dobString, out parsed))
            {
                var today = DateTime.Today;
                int age = today.Year - parsed.Year;
                if (today.Month < parsed.Month || (today.Month == parsed.Month && today.Day < parsed.Day))
                    age--;
                return age;
            }

            // Try a few explicit formats
            var formats = new[] { "MMMM d, yyyy", "MMM d, yyyy", "M/d/yyyy", "yyyy-MM-dd" };
            foreach (var f in formats)
            {
                if (DateTime.TryParseExact(dobString, f, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out parsed))
                {
                    var today = DateTime.Today;
                    int age = today.Year - parsed.Year;
                    if (today.Month < parsed.Month || (today.Month == parsed.Month && today.Day < parsed.Day))
                        age--;
                    return age;
                }
            }

            return -1;
        }

        // ------------------ GetSplitData ------------------
        public static bool GetSplitData(
            List<HtmlAgilityPack.HtmlNode> rows,
            List<HtmlAgilityPack.HtmlNode> hd,
            List<string> splitDataMust,
            List<string> splitDataAvailable,
            List<string> headers,
            List<string> playerData)
        {
            bool iteratedSplitDataMust = false;

            if (hd != null && hd.Count > 0)
                hd[0].InnerHtml = "ITF-Level Split";

            // Always iterate the required split elements and fill defaults for missing rows.
            // This makes the downstream logic tolerant to pages that don't include all rows
            // but still present a player fragment.
            iteratedSplitDataMust = true;

            foreach (var element in splitDataMust)
            {
                var foundRow = rows.FirstOrDefault(row =>
                {
                    var td = row.SelectSingleNode(".//td");
                    return td != null && td.InnerText.Trim().Replace("-", " ") == element;
                });

                if (foundRow != null)
                {
                    var tdElement = foundRow.SelectSingleNode(".//td");
                    string firstCellValue = tdElement?.InnerText.Trim().Replace("-", " ") ?? element;

                    var cells = foundRow.SelectNodes(".//td");
                    if (cells != null && cells.Count > 0)
                    {
                        splitDataAvailable.Add(firstCellValue);
                        headers.AddRange(hd.Take(10).Select(h => h.InnerText.Trim()));
                        var correctList = ModifyList(cells.Take(10).Select(c => c.InnerText.Trim()).ToList());
                        playerData.AddRange(correctList);
                    }
                    else
                    {
                        // no cells, add defaults below
                        List<string> newList = new List<string>();
                        for (int k = 0; k < 13; k++)
                        {
                            if (k == 0) newList.Add(element);
                            else if (k == 4 || k == 7 || k == 10) newList.Add("-1000");
                            else newList.Add("0");
                        }
                        splitDataAvailable.Add(element);
                        playerData.AddRange(newList);
                    }
                }
                else
                {
                    // Fill in defaults for missing split rows so the record remains usable
                    List<string> newList = new List<string>();
                    for (int k = 0; k < 13; k++)
                    {
                        if (k == 0) newList.Add(element);
                        else if (k == 4 || k == 7 || k == 10) newList.Add("-1000");
                        else newList.Add("0");
                    }
                    splitDataAvailable.Add(element);
                    playerData.AddRange(newList);
                }
            }

            return iteratedSplitDataMust;
        }
    }
}
