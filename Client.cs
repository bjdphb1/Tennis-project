using System;
using System.Net;
using System.Net.Http;
using TennisScraper;
using System.Threading.Tasks;

namespace SocksClient
{
    public class ProxyHttpClientFactory
    {

        public static HttpClient CreateHttpClientUsingSocks()
        {
            
            // Define the SOCKS5 proxy settings for Tor
            var proxyUri = new Uri($"socks5://127.0.0.1:9050");

            var proxy = new WebProxy(proxyUri)
            {
                Credentials = new NetworkCredential("", ""),
                BypassProxyOnLocal = false
            };

            var handler = new HttpClientHandler
            {
                Proxy = proxy,
                UseProxy = true
            };

            // Create HttpClient with the custom handler
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };


            foreach (var header in Utils.Headers) { client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value); }
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/118.0.0.0 Safari/537.36");

            return client;
        }
    }
}