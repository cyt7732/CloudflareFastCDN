using System.Text.Json;

namespace CloudflareFastCDN.Utils
{
    /// <summary>
    /// 当前在用 IP 的状态与切换判定。
    ///
    /// 三个数值必须来自**同一个测量口径**（ProbeScore），否则比较没有意义：
    ///   CurrentScore   —— 评分，稳态延时 × (1+稳态cv) × (1+丢包率)，约 90~140
    ///   CurrentDelayMs —— 稳态延时（连接复用后的热路径），约 60~120ms
    ///   CurrentColdMs  —— 冷启动延时（新建连接的首次访问），约 400~470ms
    ///
    /// 判定规则：
    ///   1. 当前 IP 本轮测试失败           → 换
    ///   2. 候选与当前相同                 → 不换
    ///   3. 当前 IP 排到候选池后 1/3       → 换（确实明显掉队）
    ///   4. 第 1 名比当前好 15% 以上       → 换（显著更优）
    ///   5. 其余                           → 不换
    ///
    /// 不设冷却期：每小时一轮，候选池里都是好 IP。
    /// </summary>
    public class SwitchState
    {
        /// <summary>第 1 名需比当前好 15% 才换（0.85）。</summary>
        private const double SwitchThresholdRatio = 0.85;

        /// <summary>排到候选池后 1/3 视为明显掉队，直接换。</summary>
        private const double TailRankFraction = 2.0 / 3.0;

        /// <summary>连续失败 3 次触发灾备。</summary>
        private const int FailoverThreshold = 3;

        private static readonly string StateFile =
            File.Exists("/.dockerenv") ? "/data/switch_state.json" : "switch_state.json";

        public string CurrentIp { get; set; } = string.Empty;

        /// <summary>评分（ProbeScore 口径），切换判定用。</summary>
        public double CurrentScore { get; set; }

        /// <summary>稳态延时（热路径）。</summary>
        public double CurrentDelayMs { get; set; }

        /// <summary>冷启动延时（首次访问），仅用于展示。</summary>
        public double CurrentColdMs { get; set; }

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
        /// 任意检查点（补充心跳、手动复检、首次预检）测出当前 IP 的新数据时调用。
        /// 必须传 ProbeScore 口径的评分，不能传原始延时。
        /// </summary>
        public void UpdateBaseline(double score, double delayMs, double coldMs)
        {
            CurrentScore = score;
            CurrentDelayMs = delayMs;
            if (coldMs > 0)
                CurrentColdMs = coldMs;
            Save();
        }

        /// <summary>
        /// 判定是否切换到候选 IP（本轮第 1 名）。
        /// </summary>
        /// <param name="candidateIp">候选 IP</param>
        /// <param name="candidateScore">候选评分（ProbeScore 口径）</param>
        /// <param name="currentRank">当前 IP 在本轮评分中的排名（1-based；0 表示本轮测试失败/不在列表里）</param>
        /// <param name="candidateCount">本轮参与评分的候选总数</param>
        public (bool ShouldSwitch, string Reason) Evaluate(string candidateIp, double candidateScore, int currentRank, int candidateCount)
        {
            if (string.IsNullOrEmpty(CurrentIp))
            {
                return (true, "首次分配");
            }

            if (string.Equals(CurrentIp, candidateIp, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "候选与当前相同");
            }

            if (currentRank <= 0)
            {
                return (true, "当前 IP 本轮测试失败，切换到第 1 名");
            }

            // 末段兜底：排到候选池后 1/3，说明确实明显掉队
            var tailThreshold = Math.Max(3, (int)Math.Ceiling(candidateCount * TailRankFraction));
            if (candidateCount > 0 && currentRank > tailThreshold)
            {
                return (true, $"当前 IP 排名第 {currentRank}/{candidateCount}（后 1/3），切换到第 1 名");
            }

            if (CurrentScore > 0 && candidateScore > 0 && candidateScore < CurrentScore * SwitchThresholdRatio)
            {
                var improve = (1 - SwitchThresholdRatio) * 100;
                return (true, $"第 1 名显著更优（当前 {CurrentScore:0.00} → 候选 {candidateScore:0.00}，降幅超 {improve:0}%）");
            }

            return (false, $"当前 IP 排名第 {currentRank}/{candidateCount}，第 1 名提升不足 {(1 - SwitchThresholdRatio) * 100:0}%（当前 {CurrentScore:0.00}，候选 {candidateScore:0.00}）");
        }

        public void MarkSwitched(string ip, double score, double delayMs = 0, double coldMs = 0)
        {
            CurrentIp = ip;
            CurrentScore = score;
            CurrentDelayMs = delayMs;
            CurrentColdMs = coldMs;
            LastSwitchAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            ConsecutiveFailures = 0;
            Save();
        }

        /// <summary>
        /// 初始化基准（首次运行或状态文件丢失时，从当前 DNS 记录反推）。
        /// </summary>
        public void Initialize(string ip, double score, double delayMs = 0, double coldMs = 0)
        {
            CurrentIp = ip;
            CurrentScore = score;
            CurrentDelayMs = delayMs;
            CurrentColdMs = coldMs;
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
