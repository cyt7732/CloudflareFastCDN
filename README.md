<div align="center">

# CloudflareFastCDN Pro

**自动优选 Cloudflare CDN IP，持续更新 DNS A 记录**

[![Release](https://img.shields.io/github/v/release/cyt7732/CloudflareFastCDN?display_name=tag)](https://github.com/cyt7732/CloudflareFastCDN/releases)
[![Docker](https://img.shields.io/badge/Docker-ghcr.io-2496ED?logo=docker&logoColor=white)](https://github.com/cyt7732/CloudflareFastCDN/pkgs/container/cloudflarefastcdn)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)
[![Fork](https://img.shields.io/badge/Fork%20of-aiqinxuancai%2FCloudflareFastCDN-blue)](https://github.com/aiqinxuancai/CloudflareFastCDN)

</div>

---

## 这是什么

一个运行在 Docker 里的 Cloudflare 优选 IP 工具：**持续测试 IP → 按稳定性评分排序 → 自动更新 DNS A 记录**。

支持 Cloudflare、腾讯云 DNSPod、阿里云解析。

本项目是 [aiqinxuancai/CloudflareFastCDN](https://github.com/aiqinxuancai/CloudflareFastCDN) 的**深度改造版**——保留了它的 Docker 部署、Web 管理页和 DNS 商适配，但**筛选流程、评分系统、决策逻辑全部重写**。

> 如果你只需要原版功能，请使用上游项目。

## 为什么改造

原版在实际使用中暴露了几个问题：

| 问题 | 现象 | 后果 |
|---|---|---|
| **只看平均延迟** | 延迟 247ms 但一半请求超时的 IP 照样入选 | 用户一半概率转圈 |
| **ICMP 筛选太严** | 4/4、5/5 全通才通过，CF 很多节点限 ICMP | 大量好 IP 被误杀 |
| **种子库分数冻结** | 100 个种子，每轮只有 10 个能进 HTTP 验证 | 33 轮才能刷一遍，等于摆设 |
| **当前 IP 脱管** | 被淘汰的 IP 不再参与测试 | 当前 IP 变差了也没人管 |
| **见好就换** | 差 5% 也换，频繁横跳 | DNS 抖动 |
| **故障恢复慢** | IP 挂了要跑 15 分钟全量优选 | 用户等 15 分钟 |

## 改造后的机制

### 筛选流程

```
每轮（RUN_MINUTES，默认 60 分钟；测试环境用 5 分钟）
  │
  ├─ 抽样 MAX_IPS 个
  │   ├─ 50% 网段加权（从历史高分 /24 网段抽）
  │   └─ 50% 全量随机（探索新网段）
  │
  ├─ TCP 快筛（443 端口，4 次允许丢 1 次）
  │
  ├─ 粗筛候选 = TCP 延迟最低 60 个
  │
  ├─ 并发粗筛（6 路，每候选 3 次探测、间隔 2 秒）
  │   └─ 只做淘汰，不参与最终排名
  │
  ├─ 精测候选 = 种子库前 4 + 粗筛前 8 + 当前 IP（如不在其中）
  │
  ├─ 串行精测（5 次探测，间隔 5~8 秒）
  │   ├─ 第 1 次 = 冷启动（新建 TCP + TLS），单独记录，不参与排名
  │   └─ 第 2 次起 = 稳态，取中位数 → 评分 = 稳态中位数 × (1 + 稳态cv) × (1 + 丢包率)
  │
  ├─ 前 10 名入库（种子库，容量 10，按评分淘汰）
  │
  ├─ 网段分数表记录（/24 网段的最高分与命中次数）
  │
  └─ 切换判定
      ├─ 当前 IP 本轮测试失败              → 换
      ├─ 当前 IP 排到候选池后 1/3          → 换
      ├─ 第 1 名比当前好 15% 以上          → 换
      └─ 其余                              → 不换
```

### 三种触发

| 触发 | 频率 | 行为 |
|---|---|---|
| **定时优选** | `RUN_MINUTES`（默认 60 分钟） | 跑一轮完整优选（约 10 分钟） |
| **心跳检查** | 5 分钟 | 检查当前 IP，连续 3 次失败触发灾备 |
| **手动** | 随时 | 「立即优选」/「立即复检」 |

### 灾备快速切换

当前 IP 连续 3 次心跳失败（约 15 分钟）→ 从种子库快速测一遍 TCP → **10 秒内换到可用 IP**，不跑全量优选。

### 稳定性评分：冷启动与稳态分开

同一个 `HttpClient` 连续探测同一个 IP 时，**第 1 次是冷启动**（新建 TCP 连接 + TLS 握手），
**第 2 次起是连接复用**（只发请求）。这是两个完全不同的分布，平均在一起会得到一个
既不代表首访、也不代表稳态的中间值。

```
冷启动 ColdMs = 第 1 次探测的延时        → 只展示「首次访问要多久」，不参与排名
稳态 WarmMs   = 第 2 次起成功探测的中位数  → 排名依据
评分 Score    = WarmMs × (1 + 稳态cv) × (1 + 丢包率)
```

为什么要拆开：

- 混在一起时，`cv` 会被抬到 0.7 以上——那不是网络抖动，是「一个冷启动值 + 四个热路径值」混合的必然结果
- 冷启动实测 400~470ms，稳态 60~120ms；平均后得到的 140~190ms 两边都不代表
- 分开之后，稳态的真实跨度（约 60%）远大于测量误差，排名才有意义

**选优、心跳、复检全部走同一个评分函数**，所以任何两个分数都是同一量纲，可以直接比较。

> 冷启动延时所有可用 CF IP 都挤在 400~470ms，排不出有意义的差别——它由链路物理决定，
> 没有备案就是这个成本，换 IP 改变不了。

## 快速开始

### Docker Compose（推荐）

```yaml
services:
  cloudflare-fast-cdn:
    image: ghcr.io/cyt7732/cloudflarefastcdn:latest
    container_name: cloudflare-fast-cdn
    restart: unless-stopped
    network_mode: host
    cap_add:
      - NET_RAW
    environment:
      TZ: Asia/Shanghai
      CLOUDFLARE_KEY: your_api_token
      CLOUDFLARE_DOMAINS: your.domain.com
      HTTP_PROBE_URL: https://your.domain.com/
      WEB_USERNAME: admin
      MAX_IPS: "1000"
      PING_THREADS: "16"
      RUN_MINUTES: "60"
      ENABLE_SUPPLEMENTAL_HTTP_CHECK: "true"
    volumes:
      - ./data:/data
```

```bash
docker compose up -d
```

启动后访问 `http://<主机IP>:19401/`。

> **`cap_add: NET_RAW` 必须保留**，TCP 快筛和 ICMP 探测需要原始套接字权限。

### 环境要求

- Docker（建议 `network_mode: host`，避免容器内 DNS 解析问题）
- 一个 Cloudflare API Token（DNS 编辑权限）
- 一个已接入 Cloudflare 的域名

## 配置

### 核心环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `CLOUDFLARE_KEY` | - | Cloudflare API Token（DNS 编辑权限） |
| `CLOUDFLARE_DOMAINS` | - | 第 1 名 IP 更新的域名（逗号分隔多个） |
| `CLOUDFLARE_DOMAINS2` | - | 第 2 名 IP 更新的域名 |
| `CLOUDFLARE_DOMAINS3` | - | 第 3 名 IP 更新的域名 |
| `HTTP_PROBE_URL` | `https://www.visa.cn/` | HTTP 验证地址，**建议用已开启 CF 代理的自有站点** |
| `MAX_IPS` | 400 | 每轮抽样 IP 数（建议 1000） |
| `PING_THREADS` | 8 | 并发线程数（建议 16） |
| `RUN_MINUTES` | 60 | 优选周期（分钟） |
| `ENABLE_SUPPLEMENTAL_HTTP_CHECK` | false | 开启每 5 分钟心跳检查 |
| `UPDATE_IP_LIST` | false | 启动时更新 Cloudflare 官方 IP 段 |

完整配置见 Web 管理页「设置」标签，或 `.env.example`。

### 数据文件

所有状态存在 `/data` 目录（建议绑定挂载到宿主机）：

| 文件 | 内容 |
|---|---|
| `seed_pool.json` | 种子 IP 库（10 个精品 IP，每轮全测） |
| `subnet_scores.json` | 网段分数表（`/24` 网段的最高分与命中次数） |
| `switch_state.json` | 当前 IP、评分、连续失败次数 |
| `subnet_cache.json` | 子网缓存（兼容保留，新流程不主动使用） |
| `web_history.json` | 优选历史与延迟采样（Web 页面图表数据） |
| `settings.json` | Web 页面保存的配置 |

**`seed_pool.json` 和 `subnet_scores.json` 跑得越久越准**，建议纳入备份。

## Web 管理页

访问 `http://<主机IP>:19401/`：

- **概览**：当前状态、下次检查倒计时、Top1 延迟、已完成轮数
- **已分配 IP**：当前 DNS 指向的 IP、基线延迟、最近检查
- **种子 IP 库**：10 个精品 IP 及其评分、延迟、最后命中时间
- **延迟曲线**：已分配 IP 的 HTTP 延迟趋势
- **最近一次优选**：本轮候选明细、评分、DNS 更新结果
- **日志**：实时日志，支持关键字筛选
- **设置**：全部配置项（部分需重启生效）

## 与上游的功能对比

| 功能 | 上游 | 本版 |
|---|---|---|
| 延迟测速协议 | ICMP | **TCP 443** |
| 丢包容忍 | 4/4、5/5 全通 | 3/4、4/5 |
| 评分依据 | 平均延迟 | **稳态中位数 × (1+cv) × (1+丢包率)**，冷启动单列 |
| 候选构成 | 两轮 ICMP 前 7 + 子网缓存 3 | **种子 10 + 新抽 10 + 当前 IP** |
| 种子库 | 100 个，分数冻结 | **10 个，每轮全测刷新** |
| 网段加权 | 无（10 个网段缓存） | **全量网段记录 + 加权抽样** |
| 切换策略 | 见好就换 | **15% 显著更优 + 后 1/3 兜底** |
| 当前 IP 保护 | 无 | **强制进候选池 + 对比** |
| 故障恢复 | 全量优选（15 分钟） | **灾备快速切换（10 秒）** |
| 心跳检查 | 可选 | **可选（建议开启）** |

## 已知限制

- **物理天花板**：不备案意味着流量必须绕境外节点，国内冷启动延迟约 **450~550ms**，这是「免费方案」的极限
- **上游同步**：改造幅度较大，上游更新无法直接 merge，需手动 cherry-pick
- **单次探测噪声**：心跳检查是单次探测，偶发波动可能被误判（连续 3 次才触发灾备，有缓冲）

## 致谢

- [aiqinxuancai/CloudflareFastCDN](https://github.com/aiqinxuancai/CloudflareFastCDN) — 本项目的基础
- [XIU2/CloudflareSpeedTest](https://github.com/XIU2/CloudflareSpeedTest) — TCP 测速与丢包率思路
- [Cloudflare](https://www.cloudflare.com/) — 免费 CDN

## License

MIT
