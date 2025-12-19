using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

public static class CsvUtils
{
    public static void SplitCsv(string inputCsv, string thread1Csv, string thread2Csv)
    {
        var rows = File.ReadAllLines(inputCsv).ToList();
        if (rows.Count == 0)
        {
            Console.WriteLine("CSV has no rows.");
            return;
        }

        int half = rows.Count / 2;
        var firstHalf = rows.Take(half).ToList();
        var secondHalf = rows.Skip(half).ToList();

        File.WriteAllLines(thread1Csv, firstHalf);
        File.WriteAllLines(thread2Csv, secondHalf);

        Console.WriteLine($"Split CSV into {thread1Csv} ({firstHalf.Count} rows) and {thread2Csv} ({secondHalf.Count} rows).");
    }
    public static void CleanCsv(string inputCsv, string outputCsv)
    {
        var lines = File.ReadAllLines(inputCsv);
        if (lines.Length <= 1)
        {
            Console.WriteLine("CSV has no data rows.");
            return;
        }

        string header = lines[0];
        var now = DateTime.UtcNow;
        var validRows = new List<string>();

        foreach (var line in lines.Skip(1))
        {
            var cols = line.Split(',');
            if (cols.Length < 6)
                continue;

            string cutoffStr = cols[1]; // CutOffTime column
            if (DateTimeOffset.TryParse(cutoffStr, null, DateTimeStyles.AssumeUniversal, out var cutoff))
            {
                if (cutoff.UtcDateTime > now)
                {
                    validRows.Add(line);
                }
            }
        }

        File.WriteAllLines(outputCsv, new[] { header }.Concat(validRows));

        Console.WriteLine($"Cleaned CSV. Remaining rows: {validRows.Count}. Saved to {outputCsv}.");
    }
}
