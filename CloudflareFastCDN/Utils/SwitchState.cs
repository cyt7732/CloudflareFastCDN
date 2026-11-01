using System.Text.Json;

namespace CloudflareFastCDN.Utils
{
    /// <summary>
    /// 当前在用 IP 的切换状态，用于实现「不轻易换、挂了立刻换」的策略。
    /// </summary>
    public class SwitchState
    {
        private const double SwitchThresholdRatio = 0.85;   // 新 IP 必须好 15% 才换
        private static readonly TimeSpan Cooldown = TimeSpan.FromHours(6);  // 切换后 6 小时冷却
        private const int FailoverThreshold = 3;            // 连续失败 3 次触发灾备

        private static readonly string StateFile =
            File.Exists("/.dockerenv") ? "/data/switch_state.json" : "switch_state.json";

        public string CurrentIp { get; set; } = string.Empty;
        public double CurrentScore { get; set; }
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
        /// 判定是否应该切换到新 IP。
        /// 返回 (是否切换, 原因)。
        /// </summary>
        public (bool ShouldSwitch, string Reason) Evaluate(string candidateIp, double candidateScore)
        {
            if (string.Equals(CurrentIp, candidateIp, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "与当前 IP 相同");
            }

            if (string.IsNullOrEmpty(CurrentIp))
            {
                return (true, "首次分配");
            }

            // 灾备优先：当前 IP 已经连续失败
            if (ConsecutiveFailures >= FailoverThreshold)
            {
                return (true, $"当前 IP 连续失败 {ConsecutiveFailures} 次，灾备切换");
            }

            // 冷却期内不换
            if (DateTime.TryParse(LastSwitchAt, out var lastSwitch))
            {
                var elapsed = DateTime.Now - lastSwitch;
                if (elapsed < Cooldown)
                {
                    return (false, $"冷却期内（距上次切换 {elapsed.TotalHours:0.0} 小时，需满 {Cooldown.TotalHours:0} 小时）");
                }
            }

            // 阈值：新 IP 必须显著更好
            if (CurrentScore > 0 && candidateScore >= CurrentScore * SwitchThresholdRatio)
            {
                return (false, $"提升不足（当前 {CurrentScore:0.00}，候选 {candidateScore:0.00}，需低于 {CurrentScore * SwitchThresholdRatio:0.00}）");
            }

            return (true, $"显著更优（当前 {CurrentScore:0.00} → 候选 {candidateScore:0.00}）");
        }

        public void MarkSwitched(string ip, double score)
        {
            CurrentIp = ip;
            CurrentScore = score;
            LastSwitchAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
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
