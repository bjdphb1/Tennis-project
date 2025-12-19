using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TennisScraper;
using TennisScraper.Models;
using TennisScraper.Wagering;
using Serilog;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace TennisScraper.Models
{
    public class MatchModel
    {
        public long MatchId { get; set; }
        public string HomePlayerName { get; set; }
        public string AwayPlayerName { get; set; }
        
        // Store original format from Dexsport API (e.g., "Udvardy Panna") - used for bet placement
        public string HomePlayerNameOriginal { get; set; }
        public string AwayPlayerNameOriginal { get; set; }
        
        public double? HomeOdds { get; set; }
        public double? AwayOdds { get; set; }
        public string CutOffTime { get; set; }
        public string ReferenceId { get; set; }
        public int Winner { get; set; }
        public decimal Stake { get; set; }
        public string Currency { get; set; }
        public int RetryCount { get; set; }
        public int RejectionCount { get; set; }
        public string Status { get; set; }
        public long InitCutOffTime { get; set; }
        public DataRow MatchRow { get; set; }
    // Market URL on Dexsport (optional) - helps wire Playwright placement
    public string MarketUrl { get; set; }

    public MatchModel(long matchId, string homePlayerName, string awayPlayerName,
              double? homeOdds, double? awayOdds, string cutOffTime, string marketUrl = "")
        {
            MatchId = matchId;
            HomePlayerName = homePlayerName;
            AwayPlayerName = awayPlayerName;
            // Store original names from API for bet placement on Dexsport
            HomePlayerNameOriginal = homePlayerName;
            AwayPlayerNameOriginal = awayPlayerName;
            HomeOdds = (int)((homeOdds ?? 0) * 1000);
            AwayOdds = (int)((awayOdds ?? 0) * 1000);
            
            DateTimeOffset dto = DateTimeOffset.Parse(cutOffTime);
            // Get Unix timestamp in seconds
            long unixTimestamp = dto.ToUnixTimeSeconds();

            InitCutOffTime = unixTimestamp;
            CutOffTime = cutOffTime;
            ReferenceId = Guid.NewGuid().ToString();
            Winner = 0;
            Stake = 0;
            Currency = "";
            RetryCount = 0;
            RejectionCount = 0;
            Status = "";

            MarketUrl = marketUrl ?? string.Empty;
            
            DataTable dt = new DataTable();
            dt.Columns.Add("Column1", typeof(string));
            dt.Columns.Add("Column2", typeof(string));
            dt.Columns.Add("Column3", typeof(string));
            MatchRow = dt.NewRow();
        }

        public override string ToString()
        {
            return $"{{'match_id': {MatchId}, 'home_player_name': '{HomePlayerName}', " +
                   $"'away_player_name': '{AwayPlayerName}', 'home_odds': {HomeOdds}, " +
                   $"'away_odds': {AwayOdds}, 'cut_off_time': {CutOffTime}}}";
        }
    }
}
