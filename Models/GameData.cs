namespace TennisScraper.Models
{
    public class GameData
    {
        public string Winner { get; set; }
        public string Stakes { get; set; }
        public GameData(string winner, string stakes)
        {
            Winner = winner;
            Stakes = stakes;
        }
    }
}
