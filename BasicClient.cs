using System;
using System.Net;
using System.Net.Http;
using TennisScraper;

public static class ProxyHttpClientFactoryBasic
{
    public static HttpClient CreateHttpClientWithProxy()
    {
        // Prefer environment variables if present (set by .env or process env)
        string proxyHost = Environment.GetEnvironmentVariable("IPROYAL_PROXY_HOST") ?? "geo.iproyal.com";
        string proxyPort = Environment.GetEnvironmentVariable("IPROYAL_PROXY_PORT") ?? "12321";
        string proxyUser = Environment.GetEnvironmentVariable("IPROYAL_PROXY_USER") ?? "zHpzPS8u5VBkwVwd";
        string proxyPass = Environment.GetEnvironmentVariable("IPROYAL_PROXY_PASS") ?? "2V1bg0bR4Ff5HEe8";

        if (string.IsNullOrEmpty(proxyHost) || string.IsNullOrEmpty(proxyPort))
        {
            Console.WriteLine("[DEBUG] Proxy host/port not set; creating direct HttpClient.");
            return CreateDirectHttpClient();
        }

        // Use HTTP proxy URI (HttpClientHandler supports HTTP/HTTPS proxies).
        var proxyUri = new Uri($"http://{proxyHost}:{proxyPort}");

        // Lightweight probe: DNS + TCP connect to proxy host:port with a short timeout
        bool proxyReachable = false;
        try
        {
            int portInt = int.TryParse(proxyPort, out var p) ? p : 0;
            if (portInt <= 0)
            {
                Console.WriteLine($"[DEBUG] Invalid proxy port '{proxyPort}'. Falling back to direct client.");
                return CreateDirectHttpClient();
            }

            System.Net.IPAddress[] addrs = null;
            try
            {
                addrs = System.Net.Dns.GetHostAddresses(proxyHost);
            }
            catch (Exception dnsEx)
            {
                Console.WriteLine($"[DEBUG] Proxy DNS lookup failed for {proxyHost}: {dnsEx.Message}");
            }

            if (addrs != null && addrs.Length > 0)
            {
                using (var tcp = new System.Net.Sockets.TcpClient())
                {
                    var connectTask = tcp.ConnectAsync(addrs[0], portInt);
                    // Wait up to 2500ms for the TCP connect
                    if (connectTask.Wait(2500))
                    {
                        if (tcp.Connected)
                        {
                            proxyReachable = true;
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine($"[DEBUG] No IP addresses resolved for proxy host {proxyHost}.");
            }
        }
        catch (Exception exProbe)
        {
            Console.WriteLine($"[DEBUG] Proxy probe failed: {exProbe.Message}");
        }

        if (!proxyReachable)
        {
            Console.WriteLine("[DEBUG] Proxy appears unreachable; falling back to direct HttpClient.");
            return CreateDirectHttpClient();
        }

        var proxy = new WebProxy(proxyUri)
        {
            Credentials = new NetworkCredential(proxyUser, proxyPass),
            BypassProxyOnLocal = false
        };

        var handler = new HttpClientHandler
        {
            Proxy = proxy,
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        // Increase default timeout to 60s to tolerate slower proxy/remote responses
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        foreach (var header in Utils.Headers)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }


        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/118.0.0.0 Safari/537.36"
        );

        return client;
    }

    private static HttpClient CreateDirectHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        var client = new HttpClient(handler)
        {
            // Direct requests should fail faster than proxied ones
            Timeout = TimeSpan.FromSeconds(30)
        };

        foreach (var header in Utils.Headers)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/118.0.0.0 Safari/537.36"
        );

        return client;
    }
}