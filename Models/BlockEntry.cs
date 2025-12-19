namespace TennisScraper.Models
{
    public class BlockEntry
    {
        public string Status { get; set; } = "UNKNOWN";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public int Count { get; set; } = 1;
    }
}
