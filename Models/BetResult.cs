namespace TennisScraper.Models
{
    public class BetStatusResult
    {
        public string ReferenceId { get; set; } = "";
        public string Status { get; set; } = "";
        public string RawResponse { get; set; } = "";
        public string MarketUrl { get; set; } = "";
        public double Price { get; set; }
    }
}
