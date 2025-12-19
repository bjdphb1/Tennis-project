using System;
using System.IO;

namespace TennisScraper
{
    public static class Constants
    {
        public const string CLOUDBET_BASE_URL = "https://www.tennisabstract.com";
        public const string TENNISABSTRACT_BASE_URL = "https://www.tennisabstract.com";

        // Equivalent of Python ROOT_DIR and INTERNAL_DIR
        public static readonly string ROOT_DIR = AppContext.BaseDirectory;
        public static readonly string INTERNAL_DIR = Directory.GetCurrentDirectory();
    }
}
