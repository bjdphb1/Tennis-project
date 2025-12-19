using System;
using System.Collections.Generic;
using System.Globalization;


namespace TennisScraper
{
    public static class Utils
    {
        // Schema from Python
        public static readonly List<string> NewColumnNames = new List<string>
        {
            "Type", "CutOffTime", "Home Name", "Age", "Hand", "Year", 
            "M", "W", "L", "Win%", "Set W", "Set L", "Set%", "Game W", "Game L", "Game%", "TB W", "TB L", "Split", 
            "M1", "W1", "L1", "Win%1", "Set W1", "Set L1", "Set%1", "Game W1", "Game L1", "Game%1", "TB W1", "TB L1", "Split 1", 
            "M2", "W2", "L2", "Win%2", "Set W2", "Set L2", "Set%2", "Game W2", "Game L2", "Game%2", "TB W2", "TB L2", "Split 2", 
            "M3", "W3", "L3", "Win%3", "Set W3", "Set L3", "Set%3", "Game W3", "Game L3", "Game%3", "TB W3", "TB L3", "Split 3", 
            "M4", "W4", "L4", "Win%4", "Set W4", "Set L4", "Set%4", "Game W4", "Game L4", "Game%4", "TB W4", "TB L4", "Split 4", 
            "M5", "W5", "L5", "Win%5", "Set W5", "Set L5", "Set%5", "Game W5", "Game L5", "Game%5", "TB W5", "TB L5", "Split 5", 
            "M6", "W6", "L6", "Win%6", "Set W6", "Set L6", "Set%6", "Game W6", "Game L6", "Game%6", "TB W6", "TB L6", "Split 6",
            "M7", "W7", "L7", "Win%7", "Set W7", "Set L7", "Set%7", "Game W7", "Game L7", "Game%7", "TB W7", "TB L7",
            "IncompleteTA",
            "Type1", "CutOffTime1", "Away Name", "Age1", "Hand1", "Year1",
            "M8", "W8", "L8", "Win%8", "Set W8","Set L8", "Set%8", "Game W8", "Game L8", "Game%8", "TB W8", "TB L8", "Split 8", 
            "M9", "W9", "L9", "Win%9", "Set W9", "Set L9", "Set%9", "Game W9", "Game L9", "Game%9", "TB W9", "TB L9", "Split 9", 
            "M10", "W10", "L10", "Win%10", "Set W10", "Set L10", "Set%10", "Game W10", "Game L10", "Game%10", "TB W10", "TB L10", "Split 10", 
            "M11", "W11", "L11", "Win%11", "Set W11", "Set L11", "Set%11", "Game W11", "Game L11", "Game%11", "TB W11", "TB L11", "Split 11",
            "M12", "W12", "L12", "Win%12", "Set W12", "Set L12", "Set%12", "Game W12", "Game L12", "Game%12", "TB W12", "TB L12", "Split 12",
            "M13", "W13", "L13", "Win%13", "Set W13", "Set L13", "Set%13", "Game W13", "Game L13", "Game%13", "TB W13", "TB L13", "Split 13",
            "M14", "W14", "L14", "Win%14", "Set W14", "Set L14", "Set%14", "Game W14", "Game L14", "Game%14", "TB W14", "TB L14", "Split 14",
            "M15", "W15", "L15", "Win%15", "Set W15", "Set L15", "Set%15", "Game W15", "Game L15", "Game%15", "TB W15", "TB L15",
            "IncompleteTA",
            "Home Odds","Away Odds"
        };

        public static int CalculateAge(string dateOfBirth)
        {
            string cleaned = dateOfBirth.Replace("Date of birth: ", "");
            DateTime dob = DateTime.ParseExact(cleaned, "dd-MMM-yyyy", CultureInfo.InvariantCulture);
            DateTime now = DateTime.Now;
            int age = now.Year - dob.Year;
            if (now.Month < dob.Month || (now.Month == dob.Month && now.Day < dob.Day))
                age--;
            return age;
        }

        public static (long, long) GetTimeInterval(long startEpoch, int secondsFromNow = 0)
        {
            long futureEpoch = startEpoch + secondsFromNow;
            return (startEpoch, futureEpoch);
        }

        // ----------------------
        // API keys & headers
        // ----------------------
        public static readonly string ApiKey = Environment.GetEnvironmentVariable("CLOUDBET_API_KEY");

        private static ConfigReader config = new ConfigReader("Config.ini");

        public static readonly string SearchApiKey = "AIzaSyBMf6-3mYg1xqQKAQsU10L9qSO_NTbTBXY";
        public static readonly string Cx = "b55cc8c9874824409";

        public static readonly Dictionary<string, string> Headers = new Dictionary<string, string>
        {
            { "accept", "application/json" },
            { "X-API-Key", config.ApiKey }
        };

        public static readonly string UserAgents = "Mozilla/5.0 (X11; CrOS x86_64 12871.102.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/81.0.4044.141 Safari/537.36";
    }
}
