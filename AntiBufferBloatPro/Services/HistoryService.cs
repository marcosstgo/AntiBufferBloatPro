using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AntiBufferBloatPro.Models;

namespace AntiBufferBloatPro.Services
{
    public sealed class HistoryService
    {
        private static readonly string HistoryDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AntiBufferBloatPro", "history");

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public void Save(BufferbloatTestResult result, string recommendation)
        {
            try
            {
                Directory.CreateDirectory(HistoryDir);
                var entry = new HistoryEntry
                {
                    Timestamp = DateTime.Now,
                    Result = result,
                    Recommendation = recommendation
                };
                var fileName = $"{entry.Timestamp:yyyyMMdd_HHmmss}_{result.Grade}.json";
                var path = Path.Combine(HistoryDir, fileName);
                File.WriteAllText(path, JsonSerializer.Serialize(entry, JsonOpts));
            }
            catch { }
        }

        public List<HistoryEntry> LoadAll()
        {
            var entries = new List<HistoryEntry>();
            if (!Directory.Exists(HistoryDir)) return entries;

            foreach (var file in Directory.GetFiles(HistoryDir, "*.json").OrderByDescending(f => f))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var entry = JsonSerializer.Deserialize<HistoryEntry>(json);
                    if (entry != null)
                    {
                        entry.FilePath = file;
                        entries.Add(entry);
                    }
                }
                catch { }
            }

            return entries;
        }

        public void Delete(string filePath)
        {
            try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
        }

        public void ClearAll()
        {
            try
            {
                if (Directory.Exists(HistoryDir))
                    foreach (var f in Directory.GetFiles(HistoryDir, "*.json"))
                        File.Delete(f);
            }
            catch { }
        }
    }
}
