using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TennisScraper
{
    /// <summary>
    /// Simple cycle scheduler helper for grouping matches into cycles and preparing predictor inputs.
    /// This is a lightweight skeleton intended to be extended later.
    /// </summary>
    public static class CycleScheduler
    {
        public class Cycle
        {
            public string Id { get; set; } = string.Empty;
            public DateTime EarliestStartUtc { get; set; }
            public List<string> MatchLines { get; set; } = new List<string>();
        }

        /// <summary>
        /// Build a single cycle from an input file used by the predictor (first line budget, remaining match lines).
        /// This helper reads the sample input format and returns a Cycle with up to cycleSize matches.
        /// </summary>
        public static Cycle BuildCycleFromPredictorSample(string predictorSampleInputPath, int cycleSize = 10)
        {
            if (string.IsNullOrWhiteSpace(predictorSampleInputPath)) throw new ArgumentNullException(nameof(predictorSampleInputPath));
            if (!File.Exists(predictorSampleInputPath)) throw new FileNotFoundException("sample input not found", predictorSampleInputPath);

            var lines = File.ReadAllLines(predictorSampleInputPath).Select(l => l?.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (lines.Count <= 1) throw new InvalidDataException("sample input must contain budget line + at least one match line");

            var matches = lines.Skip(1).Take(cycleSize).Select(s => s!).ToList();
            var cycle = new Cycle
            {
                Id = Guid.NewGuid().ToString("N"),
                EarliestStartUtc = DateTime.UtcNow.AddHours(3), // placeholder: real implementation should parse actual match times
                MatchLines = matches
            };
            return cycle;
        }

        /// <summary>
        /// Write predictor input for a cycle to the specified file path. budgetMbtc is an integer representing mBTC.
        /// </summary>
        public static string PreparePredictorInput(Cycle cycle, int budgetMbtc, string outPath)
        {
            if (cycle == null) throw new ArgumentNullException(nameof(cycle));
            if (string.IsNullOrWhiteSpace(outPath)) throw new ArgumentNullException(nameof(outPath));

            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // Write UTF-8 WITHOUT BOM to keep the predictor's simple float parsing happy
            using (var w = new StreamWriter(outPath, false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                w.WriteLine(budgetMbtc.ToString());
                foreach (var m in cycle.MatchLines)
                {
                    w.WriteLine(m);
                }
            }
            return outPath;
        }
    }
}
