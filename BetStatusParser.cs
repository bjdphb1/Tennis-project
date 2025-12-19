using System;
using System.Text.RegularExpressions;

namespace TennisScraper
{
    /// <summary>
    /// Helper to parse the result returned by the injected JavaScript search.
    /// Extracts a canonical status (WON/LOST/VOID/ACCEPTED/REJECTED/PENDING_ACCEPTANCE) when possible.
    /// This class is deliberately static and side-effect-free so it can be unit-tested easily.
    /// </summary>
    public static class BetStatusParser
    {
        public static string? ParseStatus(string statusText, string fullText, string tabName)
        {
            statusText ??= string.Empty;
            fullText ??= string.Empty;
            tabName ??= string.Empty;

            // Normalize
            string statusLower = Normalize(statusText);
            string fullLower = Normalize(fullText);
            string tabLower = tabName.ToLowerInvariant();

            // If in Unsettled tab, treat as accepted
            if (tabLower.Contains("unsettled"))
                return "ACCEPTED";

            // If in Settled tab prefer explicit statusText
            if (tabLower.Contains("settled"))
            {
                if (!string.IsNullOrWhiteSpace(statusLower))
                {
                    if (ContainsWord(statusLower, "win") || ContainsWord(statusLower, "won"))
                        return "WON";
                    if (ContainsWord(statusLower, "lost") || ContainsWord(statusLower, "loss") || ContainsWord(statusLower, "lose"))
                        return "LOST";
                    if (ContainsWord(statusLower, "void") || ContainsWord(statusLower, "cancelled") || ContainsWord(statusLower, "canceled"))
                        return "VOID";
                }

                // Fallback to scanning full text when status div isn't helpful
                if (ContainsWord(fullLower, "win") || ContainsWord(fullLower, "won"))
                    return "WON";
                if (ContainsWord(fullLower, "lost") || ContainsWord(fullLower, "loss") || ContainsWord(fullLower, "lose"))
                    return "LOST";
                if (ContainsWord(fullLower, "void") || fullLower.Contains("cancelled") || fullLower.Contains("canceled"))
                    return "VOID";
            }

            // Generic checks anywhere in text
            if (ContainsWord(fullLower, "rejected") || ContainsWord(fullLower, "declined") || ContainsWord(fullLower, "refused"))
                return "REJECTED";
            if (ContainsWord(fullLower, "accepted") || ContainsWord(fullLower, "confirmed") || ContainsWord(fullLower, "paid") || ContainsWord(fullLower, "success"))
                return "ACCEPTED";
            if (ContainsWord(fullLower, "pending") || ContainsWord(fullLower, "waiting") || ContainsWord(fullLower, "processing") || fullLower.Contains("accepting") || fullLower.Contains("in progress"))
                return "PENDING_ACCEPTANCE";

            // Extra checks for messy HTML text from Dexsport (may have "win" repeated or with extra text)
            // The Dexsport status blob may contain both "WIN" (the status) and "WINNERODD" (odds label)
            // We need to check if "WIN" appears multiple times or standalone
            if (fullLower.Contains("win"))
            {
                // Count occurrences of "WIN"
                int winCount = 0;
                int index = 0;
                while ((index = fullLower.IndexOf("win", index)) != -1)
                {
                    winCount++;
                    index += 3;
                }
                
                // If "WIN" appears at least twice, one is likely the status and one is in "WINNERODD"
                if (winCount >= 2)
                    return "WON";
                
                // If "WIN" appears once and it's NOT part of "WINNERODD"
                if (winCount == 1 && !fullLower.Contains("winnerodd"))
                    return "WON";
            }
            
            if (fullLower.Contains("lost") || fullLower.Contains("loss") || fullLower.Contains("lose"))
                return "LOST";
            if (fullLower.Contains("void"))
                return "VOID";

            // Last resort: look for small status-like tokens in the statusText first
            if (!string.IsNullOrWhiteSpace(statusLower))
            {
                if (statusLower.Contains("win")) return "WON";
                if (statusLower.Contains("lost") || statusLower.Contains("loss") || statusLower.Contains("lose")) return "LOST";
                if (statusLower.Contains("void")) return "VOID";
            }

            // Unknown
            return null;
        }

        static string Normalize(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            // Lowercase, remove extra punctuation, collapse whitespace
            var lowered = input.ToLowerInvariant();
            lowered = Regex.Replace(lowered, "[\u2022\u25CF\u25CB\u2023]", " "); // common bullets
            lowered = Regex.Replace(lowered, "[\r\n\t]+", " ");
            lowered = Regex.Replace(lowered, "[^a-z0-9 ]+", " ");
            lowered = Regex.Replace(lowered, "\\s+", " ").Trim();
            return lowered;
        }

        static bool ContainsWord(string haystack, string word)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(word)) return false;
            return Regex.IsMatch(haystack, $"\\b{Regex.Escape(word)}\\b", RegexOptions.CultureInvariant);
        }
    }
}
