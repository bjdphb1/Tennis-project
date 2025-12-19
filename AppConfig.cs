using System;

namespace TennisScraper
{
    public static class AppConfig
    {
        // Default connect timeout for WebDriver operations in seconds (default 6 seconds)
        public static int WebDriverConnectTimeoutSeconds
        {
            get
            {
                if (int.TryParse(Environment.GetEnvironmentVariable("WEBDRIVER_CONNECT_TIMEOUT_SEC"), out var v) && v > 0)
                    return v;
                return 30;
            }
        }

        // Default number of attempts to try creating a WebDriver (default 3)
        public static int WebDriverMaxAttempts
        {
            get
            {
                if (int.TryParse(Environment.GetEnvironmentVariable("WEBDRIVER_MAX_ATTEMPTS"), out var v) && v > 0)
                    return v;
                return 3;
            }
        }

        // Milliseconds delay between retry attempts (default 2000 ms)
        public static int WebDriverRetryDelayMs
        {
            get
            {
                if (int.TryParse(Environment.GetEnvironmentVariable("WEBDRIVER_RETRY_DELAY_MS"), out var v) && v > 0)
                    return v;
                return 2000;
            }
        }
    }
}
