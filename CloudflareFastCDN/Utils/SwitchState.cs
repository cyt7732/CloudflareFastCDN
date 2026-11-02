using System.Text.Json;

namespace CloudflareFastCDN.Utils
{
    /// <summary>
    /// 当前在用 IP 的切换状态。
    /// 判定规则：当前 IP 本轮排名 > 5 或测试失败 → 直接换；排名 ≤ 5 → 第 1 名需好 10% 以上才换。
    /// 不设冷却期：每小时一轮，候选池里都是好 IP，换了也不会差。
    /// </summary>
    public class SwitchState
    {
        private const double SwitchThresholdRatio = 0.90;   // 第 1 名需比当前好 10% 才换
        private const int TopRankProtection = 5;            // 前 5 名受保护，不轻易换
        private const int FailoverThreshold = 3;            // 连续失败 3 次触发灾备

        private static readonly string StateFile =
            File.Exists("/.dockerenv") ? "/data/switch_state.json" : "switch_state.json";

        public string CurrentIp { get; set; } = string.Empty;
        public double CurrentScore { get; set; }
        public double CurrentDelayMs { get; set; }
        public string LastSwitchAt { get; set; } = string.Empty;
        public int ConsecutiveFailures { get; set; }

        public static SwitchState Load()
        {
            if (!File.Exists(StateFile))
                return new SwitchState();

            try
            {
                var json = File.ReadAllText(StateFile);
                return JsonSerializer.Deserialize<SwitchState>(json) ?? new SwitchState();
            }
            catch
            {
                return new SwitchState();
            }
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(StateFile);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(StateFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存切换状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 任意检查点（补充检查、手动复检、首次预检）测出当前 IP 的新数据时调用，刷新基准。
        /// </summary>
        public void UpdateBaseline(double score, double delayMs)
        {
            CurrentScore = score;
            CurrentDelayMs = delayMs;
            Save();
        }

        /// <summary>
        /// 判定是否切换到候选 IP（种子库第 1 名）。
        /// </summary>
        /// <param name="candidateIp">候选 IP</param>
        /// <param name="candidateScore">候选评分</param>
        /// <param name="currentRank">当前 IP 在本轮评分中的排名（1-based；0 表示本轮测试失败/不在列表里）</param>
        public (bool ShouldSwitch, string Reason) Evaluate(string candidateIp, double candidateScore, int currentRank)
        {
            if (string.IsNullOrEmpty(CurrentIp))
            {
                return (true, "首次分配");
            }

            if (string.Equals(CurrentIp, candidateIp, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "候选与当前相同");
            }

            // 当前 IP 本轮测试失败，不在 scored 列表里
            if (currentRank <= 0)
            {
                return (true, "当前 IP 本轮测试失败，切换到种子库第 1 名");
            }

            // 排名在前 5 之外：第 1 名必然分数更低，直接换
            if (currentRank > TopRankProtection)
            {
                return (true, $"当前 IP 排名第 {currentRank}（前 5 之外），切换到第 1 名");
            }

            // 排名在前 5：第 1 名需好 10% 以上
            if (CurrentScore > 0 && candidateScore < CurrentScore * SwitchThresholdRatio)
            {
                return (true, $"第 1 名显著更优（当前 {CurrentScore:0.00} → 候选 {candidateScore:0.00}，降幅超 10%）");
            }

            return (false, $"当前 IP 排名第 {currentRank}，第 1 名提升不足 10%（当前 {CurrentScore:0.00}，候选 {candidateScore:0.00}）");
        }

        public void MarkSwitched(string ip, double score, double delayMs = 0)
        {
            CurrentIp = ip;
            CurrentScore = score;
            CurrentDelayMs = delayMs;
            LastSwitchAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            ConsecutiveFailures = 0;
            Save();
        }

        /// <summary>
        /// 初始化基准（首次运行或状态文件丢失时，从当前 DNS 记录反推）。
        /// </summary>
        public void Initialize(string ip, double score, double delayMs = 0)
        {
            CurrentIp = ip;
            CurrentScore = score;
            CurrentDelayMs = delayMs;
            LastSwitchAt = string.Empty;
            ConsecutiveFailures = 0;
            Save();
        }

        public void RecordFailure()
        {
            ConsecutiveFailures++;
            Save();
        }

        public void RecordSuccess()
        {
            if (ConsecutiveFailures != 0)
            {
                ConsecutiveFailures = 0;
                Save();
            }
        }
    }
}
