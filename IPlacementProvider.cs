using System.Threading.Tasks;
using TennisScraper.Models;

namespace TennisScraper
{
    public interface IPlacementProvider
    {
        Task<BetStatusResult?> PlaceBetAsync(long eventId, string winner, double winnerOdds, string uuid, double stake, string currency, string homePlayerName, string awayPlayerName);
        Task<BetStatusResult?> GetBetStatusAsync(string referenceId);
        Task<decimal> GetBalanceAsync(string currency = "USD");
    }
}
