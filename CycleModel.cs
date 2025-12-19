using System;
using System.Collections.Generic;

namespace TennisScraper
{
    public class CycleModel
    {
        public string Id { get; set; } = string.Empty;
        public long EarliestUnix { get; set; }
        public List<long> MatchIds { get; set; } = new List<long>();
        public int BudgetMbtc { get; set; }
        public string Status { get; set; } = "prepared";
    }
}
