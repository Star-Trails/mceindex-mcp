# MCEIndex MCP

面向 [有意义中国经济指数（mceindex.com）](https://mceindex.com/) 的本地索引 MCP（Model Context Protocol）服务。

---

## 核心特性

1. **单二进制静态交付（Zero CGO 与 Zero External Dependencies）**：
   - 采用嵌入式 SQLite 驱动（内置 FTS5 Trigram 中文三元分词），无需配置外部 CGO 编译环境，全平台静态编译。
   - 下载即可直接运行。
2. **系统浏览器智能复用与反反爬**：
   - 自动检测并复用操作系统内置的 Microsoft Edge（`msedge.exe`）或 Google Chrome / Chromium。
   - 注入 Stealth 隐身补丁，绕过 Cloudflare Turnstile 质询验证。
3. **Plotly.js 内存对象高精度直取**：
   - 注入 JavaScript 直接读取挂载在 DOM 上的 Plotly 原始数据对象（`_fullData`），支持 TypedArray 及 Base64 二进制解码，完整还原时间序列浮点精度。
4. **全自动分段视图遍历与行业下钻**：
   - 自动遍历“五大新产业续命指数”的 5 大子视图与 5 大行业（集成电路、新能源汽车等），采集 23 张全量图表与 1,470+ 历史数据点。
5. **严密宏观经济趋势与防偏评估引擎**：
   - 精确计算环比（MoM）、同比（YoY）、近 3 个月动量（Momentum）与走势方向。
   - **关键防偏机制**：`MCPI`（通胀）与 `MSF`（社融）强制判定为 `indeterminate`，避免将升降机械解释为经济好坏。
6. **权威审计核验与数据溯源**：
   - 内置包含国家统计局、教育部、人社部、财政部、中国人民银行等 18 个权威数据源的核验矩阵。
7. **极速响应与本地缓存熔断**：
   - 所有 MCP 客户端查询直接从本地 SQLite 毫秒级读取；浏览器仅在 24 小时刷新时临时运行数秒，完成后立即释放内存。

---

## 快速开始

### 1. 编译构建

需预先安装 [Go 1.27+](https://golang.org/dl/)：

```bash
# 克隆仓库
git clone https://github.com/Star-Trails/mceindex-mcp.git
cd mceindex-mcp

# 编译当前平台可执行文件
go build -o mceindex-mcp ./cmd/mceindex-mcp
```

### 2. 配置 MCP 客户端

#### Claude Desktop

在 `claude_desktop_config.json` 中配置：

```json
{
  "mcpServers": {
    "mceindex": {
      "command": "/绝对路径/mceindex-mcp",
      "args": []
    }
  }
}
```

*Windows 用户示例：`"command": "C:\\path\\to\\mceindex-mcp.exe"`*

#### Cursor / Codex

在 Cursor 或 Codex 的 MCP 配置文件中添加：

```toml
[mcp_servers.mceindex]
command = "/绝对路径/mceindex-mcp"
startup_timeout_sec = 60
tool_timeout_sec = 180
```

---

## 提供的 MCP Tools

| 工具名称 | 功能说明 | 关键参数 |
|---|---|---|
| `discover_data` | 数据发现入口。汇总六个主题、当前读数、历史趋势、指标意义、典型问题与建议工具。 | 无 |
| `get_latest` | 返回月度总览的六组结构化读数、近 13 个月趋势及权威审计核验信息。 | 无 |
| `get_indicator` | 按代码或中文名读取单项指标可调历史序列（2 到 120 个月）。代码支持：`LEI-GDP`、`LEI-EMP`、`LEI-FIS`、`MRS`、`MCPI`、`MSF`。 | `indicator`（string，必填）、`months`（int，默认 24） |
| `list_pages` | 列出本地已索引栏目和刷新状态。 | 无 |
| `get_page` | 按栏目读取结构化页面（`summary`、`content`、`tables`、`charts`）。 | `page`（string，必填）、`view`、`offset`、`limit` |
| `search_index` | 使用 SQLite FTS5 Trigram 全文检索中文内容与指标代码。 | `query`（string，必填）、`page`、`kind`、`mode`、`offset`、`limit` |
| `refresh_index` | 触发全量抓取更新本地 SQLite 缓存（受 24 小时刷新间隔与 60 秒硬冷却保护）。 | `force`（bool，默认 false） |

---

## 环境变量配置

所有配置项均支持环境变量覆盖，具备开箱即用的默认值：

| 环境变量 | 默认值 | 用途说明 |
|---|---|---|
| `MCEINDEX_BASE_URL` | `https://mceindex.com/` | 数据源地址 |
| `MCEINDEX_DB_PATH` | 用户缓存目录下的 `mceindex_mcp/mceindex.db` | SQLite 数据库存储路径 |
| `MCEINDEX_BROWSER_EXECUTABLE` | 自动探测系统 Edge 或 Chrome | 自定义指定无头浏览器可执行文件路径 |
| `MCEINDEX_TIMEOUT_MS` | `45000`（45 秒） | 单页面加载与就绪超时时间（毫秒） |
| `MCEINDEX_SETTLE_MS` | `1200`（1.2 秒） | DOM 稳定静默等待时间（毫秒） |
| `MCEINDEX_REFRESH_INTERVAL_MS` | `86400000`（24 小时） | 自动刷新周期（毫秒） |
| `MCEINDEX_CRAWL_DELAY_MS` | `3000`（3 秒） | 页面请求间隔防封保护（毫秒） |
| `MCEINDEX_CRAWL_CONCURRENCY` | `1` | 抓取并发数（范围 1 到 4） |
| `MCEINDEX_MAX_PAGES` | `20` | 单次刷新抓取页面上限（范围 5 到 100） |

---

## 自动化测试

```bash
# 运行全部单元测试
go test -v ./...
```

---

## 开源许可证

本项目基于 [MIT License](LICENSE) 开源。
