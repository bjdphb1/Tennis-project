using System;
using System.IO;
using System.Linq;

// Minimal predictor stub: looks for thread_1.csv or thread_2.csv and writes output_1.csv / output_2.csv
class Program
{
    static int Main(string[] args)
    {
        try
        {
            var cwd = Directory.GetCurrentDirectory();
            var thread1 = Path.Combine(cwd, "thread_1.csv");
            var thread2 = Path.Combine(cwd, "thread_2.csv");

            if (File.Exists(thread1))
            {
                ProduceOutput(thread1, Path.Combine(cwd, "output_1.csv"));
            }
            else if (File.Exists(thread2))
            {
                ProduceOutput(thread2, Path.Combine(cwd, "output_2.csv"));
            }
            else
            {
                // Try any thread_*.csv
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
            Console.Error.WriteLine($"Predictor1 error: {ex.Message}");
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
        
        // Read rows after header; for each data row emit: winner,stake
        var lines = File.ReadAllLines(threadCsv).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        // If file contains only a single numeric header (firstRowValue) and then rows, skip header detection
        int startIdx = 0;
        if (lines.Count > 0 && double.TryParse(lines[0].Trim(), out _)) startIdx = 1;

        using var sw = new StreamWriter(outputCsv, false);
        for (int i = startIdx; i < lines.Count; i++)
        {
            // Default: pick home (0) with configured stake amount
            sw.WriteLine($"0,{stakeAmount}");
        }
        sw.Flush();
        Console.WriteLine($"Predictor1: wrote {outputCsv} with stake=${stakeAmount}");
    }
}
