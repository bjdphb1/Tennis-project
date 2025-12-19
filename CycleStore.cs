using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TennisScraper
{
    public static class CycleStore
    {
        private static readonly string CyclesPath = Path.Combine(Directory.GetCurrentDirectory(), "session", "cycles.json");

        public static List<CycleModel> LoadAll()
        {
            try
            {
                if (!File.Exists(CyclesPath)) return new List<CycleModel>();
                var txt = File.ReadAllText(CyclesPath);
                var list = JsonSerializer.Deserialize<List<CycleModel>>(txt);
                return list ?? new List<CycleModel>();
            }
            catch
            {
                return new List<CycleModel>();
            }
        }

        public static void SaveAll(IEnumerable<CycleModel> cycles)
        {
            var dir = Path.GetDirectoryName(CyclesPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var txt = JsonSerializer.Serialize(cycles, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(CyclesPath, txt);
        }

        public static void Add(CycleModel cycle)
        {
            var all = LoadAll();
            all.Add(cycle);
            SaveAll(all);
        }

        public static void Update(CycleModel cycle)
        {
            var all = LoadAll();
            var idx = all.FindIndex(c => c.Id == cycle.Id);
            if (idx >= 0) all[idx] = cycle; else all.Add(cycle);
            SaveAll(all);
        }
    }
}
