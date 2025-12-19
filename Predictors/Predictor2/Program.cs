using System;
using System.IO;
using System.Linq;

// Minimal predictor stub: looks for thread_2.csv or thread_1.csv and writes output_2.csv / output_1.csv
class Program
{
    static int Main(string[] args)
    {
        try
        {
            var cwd = Directory.GetCurrentDirectory();
            var thread2 = Path.Combine(cwd, "thread_2.csv");
            var thread1 = Path.Combine(cwd, "thread_1.csv");

            if (File.Exists(thread2))
            {
                ProduceOutput(thread2, Path.Combine(cwd, "output_2.csv"));
            }
            else if (File.Exists(thread1))
            {
                ProduceOutput(thread1, Path.Combine(cwd, "output_1.csv"));
            }
            else
            {
                var any = Directory.GetFiles(cwd, "thread_*.csv").FirstOrDefault();
                if (any != null)
                {
                    var outName = Path.GetFileNameWithoutExtension(any).Replace("thread_", "output_") + ".csv";
                    ProduceOutput(any, Path.Combine(cwd, outName));
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Predictor2 error: {ex.Message}");
            return 2;
        }
    }

    static int ReadStakeFromConfig()
    {
        // Try to read DefaultStakeAmount from Config.ini in the main app directory
        try
        {
            // Use the working directory (where MainProgram runs) + Config.ini
            var configPath = Path.Combine(Directory.GetCurrentDirectory(), "Config.ini");
            if (File.Exists(configPath))
            {
                var lines = File.ReadAllLines(configPath);
                foreach (var line in lines)
                {
                    if (line.StartsWith("DefaultStakeAmount="))
                    {
                        var value = line.Substring("DefaultStakeAmount=".Length).Trim();
                        if (int.TryParse(value, out int stake) && stake > 0)
                        {
                            return stake;
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback to hardcoded value
        }
        
        // Default fallback: $10
        return 10;
    }

    static void ProduceOutput(string threadCsv, string outputCsv)
    {
        // Read stake amount from config
        int stakeAmount = ReadStakeFromConfig();
        
        var lines = File.ReadAllLines(threadCsv).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        int startIdx = 0;
        if (lines.Count > 0 && double.TryParse(lines[0].Trim(), out _)) startIdx = 1;

        using var sw = new StreamWriter(outputCsv, false);
        for (int i = startIdx; i < lines.Count; i++)
        {
            sw.WriteLine($"0,{stakeAmount}");
        }
        sw.Flush();
        Console.WriteLine($"Predictor2: wrote {outputCsv} with stake=${stakeAmount}");
    }
}
