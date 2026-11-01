using System.Net;
using System.Text.Json;

namespace CloudflareFastCDN.Utils
{
    public class SeedRecord
    {
        public string Ip { get; set; } = string.Empty;
        public double Score { get; set; }        // 稳定性评分，越低越好
        public double DelayMs { get; set; }      // 平均延迟
        public int ConsecutiveFailures { get; set; } = 0;
        public string LastSeen { get; set; } = string.Empty;
    }

    /// <summary>
    /// 种子 IP 库：记录历史评分最优的具体 IP，下一轮优选时优先复用。
    /// 容量 100，超出按评分淘汰最差的；连续失败 2 次直接移除。
    /// </summary>
    public class SeedPool
    {
        private const int MaxSeeds = 10;
        private const int FailureEvictThreshold = 2;

        private static readonly string PoolFile =
            File.Exists("/.dockerenv") ? "/data/seed_pool.json" : "seed_pool.json";

        private List<SeedRecord> _seeds = new();

        public int Count => _seeds.Count;

        public void Load()
        {
            if (!File.Exists(PoolFile))
                return;

            try
            {
                var json = File.ReadAllText(PoolFile);
                _seeds = JsonSerializer.Deserialize<List<SeedRecord>>(json) ?? new();
                _seeds.Sort((a, b) => a.Score.CompareTo(b.Score));
                if (_seeds.Count > MaxSeeds)
                {
                    Console.WriteLine($"种子库裁剪：{_seeds.Count} 个 → {MaxSeeds} 个（保留评分最优）");
                    _seeds = _seeds.Take(MaxSeeds).ToList();
                }
            }
            catch
            {
                _seeds = new();
            }
        }

        public List<IPAddress> GetIPs()
        {
            var result = new List<IPAddress>();
            foreach (var record in _seeds)
            {
                if (IPAddress.TryParse(record.Ip, out var ip))
                    result.Add(ip);
            }
            return result;
        }

        public bool Contains(IPAddress ip)
        {
            var s = ip.ToString();
            return _seeds.Any(r => r.Ip == s);
        }

        public List<SeedRecord> GetEntries() => new(_seeds);

        /// <summary>
        /// 入库或更新：分数更低（更好）时更新，超过容量淘汰最差。
        /// </summary>
        public void AddOrUpdate(IPAddress ip, double score, double delayMs)
        {
            var key = ip.ToString();
            var existing = _seeds.FirstOrDefault(r => r.Ip == key);

            if (existing != null)
            {
                // 每轮全测，直接用本轮结果覆盖，反映当前真实状态
                existing.Score = score;
                existing.DelayMs = delayMs;
                existing.ConsecutiveFailures = 0;
                existing.LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
            else
            {
                _seeds.Add(new SeedRecord
                {
                    Ip = key,
                    Score = score,
                    DelayMs = delayMs,
                    ConsecutiveFailures = 0,
                    LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }

            _seeds.Sort((a, b) => a.Score.CompareTo(b.Score));

            while (_seeds.Count > MaxSeeds)
            {
                var evicted = _seeds[^1];
                _seeds.RemoveAt(_seeds.Count - 1);
                Console.WriteLine($"种子库淘汰：{evicted.Ip}（评分 {evicted.Score:0.00}，容量已满 {MaxSeeds}）");
            }
        }

        /// <summary>
        /// 记录测试失败：连续失败达到阈值则移除。
        /// </summary>
        public bool RecordFailure(IPAddress ip)
        {
            var key = ip.ToString();
            var record = _seeds.FirstOrDefault(r => r.Ip == key);
            if (record == null)
                return false;

            record.ConsecutiveFailures++;
            if (record.ConsecutiveFailures >= FailureEvictThreshold)
            {
                _seeds.Remove(record);
                Console.WriteLine($"种子库淘汰：{key} 连续{record.ConsecutiveFailures}次测试失败");
                return true;
            }
            return false;
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(PoolFile);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(_seeds, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(PoolFile, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存种子库失败: {ex.Message}");
            }
        }
    }
}
