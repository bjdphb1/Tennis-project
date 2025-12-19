using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SocksClient;

public class CloudbetBetPlacer
{
    private static readonly HttpClient Client = ProxyHttpClientFactory.CreateHttpClientUsingSocks();
    private static readonly HttpClient httpClient = new HttpClient();

    private const string CLOUDBET_TRADING_API_URL = "https://sports-api.cloudbet.com/pub/v3/"; // Replace with actual URL
    private const string API_KEY = "eyJhbGciOiJSUzI1NiIsImtpZCI6IkhKcDkyNnF3ZXBjNnF3LU9rMk4zV05pXzBrRFd6cEdwTzAxNlRJUjdRWDAiLCJ0eXAiOiJKV1QifQ.eyJhY2Nlc3NfdGllciI6InRyYWRpbmciLCJleHAiOjIwNzU2NDU1NzUsImlhdCI6MTc2MDI4NTU3NSwianRpIjoiMDFmZjgxYzEtYjE2Zi00NDFmLWIzM2YtNzViNzAwNWUxZjFhIiwic3ViIjoiMGZlODJiNzItYTIzMC00OTlmLTkzYTYtZGVjMThiZjQyMGJmIiwidGVuYW50IjoiY2xvdWRiZXQiLCJ1dWlkIjoiMGZlODJiNzItYTIzMC00OTlmLTkzYTYtZGVjMThiZjQyMGJmIn0.dVaenytfWb56wlE_Isaww4Vp_YN536pDN_UBocljRR7XEsfDh4mozMe957aDSzK-yUntSVGUuALySdPFZXr0VUWgdMHI3kY1IXe59vZEbpquHe79g7LlxK8BtgRthg5FTUB7Mhjv_IppLhW4elDIkMX_gLLe4dvReStdbFO6glh0bToSr_7_68LeHM9YOlBypf0MFa96L9PV6hUrdGXgQ_CTp8si0Gyy6psjF0CcU4ns0UHmrW9e6R38AjIAqPSShxEAJy--VBjNBvpV9kJAM12kxYk3iMzQv8SN6-WMznpEgcrZQfFaDUXhlRF0LcfaGBxFBeShV-RHsadW6N4JrQ"; // Replace with your Cloudbet API key

    public class BetPayload
    {
        public string acceptPriceChange { get; set; } = "BETTER";
        public string currency { get; set; } = "PLAY_EUR";
        public string eventId { get; set; } = "31327690";
        public string marketUrl { get; set; } = "tennis.winner/home";
        public string price { get; set; } = "1.442";
        public string referenceId { get; set; }
        public string side { get; set; } = "BACK";
        public string stake { get; set; } = "1";
    }

    public static async Task<bool> PlaceBet(string referenceId = null)
    {
        referenceId ??= Guid.NewGuid().ToString();

        var payload = new BetPayload
        {
            referenceId = referenceId
        };

        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Add auth header
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("x-api-key", API_KEY);

        try
        {
            var response = await Client.PostAsync($"{CLOUDBET_TRADING_API_URL}/bets/place", content);
            string responseBody = await response.Content.ReadAsStringAsync();

            Console.WriteLine($"📥 API Response ({response.StatusCode}): {responseBody}");

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error placing bet: {ex.Message}");
            return false;
        }
    }
}
