# MCEIndex MCP

面向 [有意义中国经济指数（mceindex.com）](https://mceindex.com/) 的本地索引 MCP 服务。它将公开页面中的指标卡、统计期、解释文本和 Plotly 图表写入 SQLite，并通过 stdio 提供结构化查询。

MCEIndex MCP 是独立项目，抓取范围限于公开页面并遵守站点访问控制。查询结果包含 `sourceUrl` 和 `fetchedAt`，便于核对来源与抓取时间。

## 功能

- 主动展示可查询主题、当前读数、指标意义和典型问题
- 读取月度总览和单项经济指标
- 按栏目读取正文、表格和图表
- 使用 SQLite FTS5 trigram 搜索中文内容与指标代码
- 将抓取结果保存在本地，支持离线查询
- 通过 MCP JSON Schema 返回结构化数据

## 快速开始

### 1. 准备运行环境

| 依赖 | 要求 |
|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | 运行 `dnx` 或安装 .NET Tool |
| [Node.js 24 LTS](https://nodejs.org/) | 安装和运行 Camofox |
| SQLite 3 | 提供系统 SQLite 动态库 |
| [Camofox](https://github.com/jo-inc/camofox-browser) | 渲染 Streamlit 页面并处理 Cloudflare |

Windows 10+ 提供 `winsqlite3.dll`，macOS 提供 `libsqlite3.dylib`。Debian 和 Ubuntu 先安装 SQLite 与 Camofox 所需原生库，再安装 Camofox：

```bash
sudo apt-get update
sudo apt-get install -y libsqlite3-0 libasound2
npm install --global @askjo/camofox-browser@1.13.1
```

检查运行环境：

```text
dotnet --version
node --version
camofox-browser --help
```

### 2. 一次性运行

.NET 10 的 `dnx` 会从 NuGet 下载、缓存并启动工具：

```bash
dnx MCEIndex.Mcp@4.0.1
```

### 3. 全局安装

```bash
dotnet tool install --global MCEIndex.Mcp --version 4.0.1
dotnet tool list --global
```

更新或卸载：

```bash
dotnet tool update --global MCEIndex.Mcp --version 4.0.1
dotnet tool uninstall --global MCEIndex.Mcp
```

全局工具的默认路径：

| 平台 | `command` |
|---|---|
| Windows | `%USERPROFILE%\.dotnet\tools\mceindex-mcp.exe` |
| Linux | `$HOME/.dotnet/tools/mceindex-mcp` |
| macOS | `$HOME/.dotnet/tools/mceindex-mcp` |

## 配置 MCP 客户端

MCP 客户端可以通过 `dnx` 直接启动：

```json
{
  "mcpServers": {
    "mceindex": {
      "command": "dnx",
      "args": ["MCEIndex.Mcp@4.0.1"]
    }
  }
}
```

Codex 使用相同的启动方式：

```toml
[mcp_servers.mceindex]
command = "dnx"
args = ["MCEIndex.Mcp@4.0.1"]
startup_timeout_sec = 60
tool_timeout_sec = 180
```

全局安装时，`command` 使用上表中的绝对路径，并删除 `args`。

服务默认连接 `http://127.0.0.1:9377/`。如果该地址没有服务，它会从 `PATH` 查找并按需启动 `camofox-browser`，刷新结束后停止自己启动的浏览器。也可以连接预先运行的 Camofox：

```text
MCEINDEX_CAMOFOX_URL=http://127.0.0.1:9377/
MCEINDEX_CAMOFOX_EXECUTABLE=/absolute/path/to/camofox-browser
```

非回环服务必须使用 HTTPS，并设置与 Camofox `CAMOFOX_ACCESS_KEY` 相同的 `MCEINDEX_CAMOFOX_ACCESS_KEY`。

## 工具

| 工具 | 用途 | 主要参数 |
|---|---|---|
| `discover_data` | 发现可查询主题、当前读数、历史趋势及改善或恶化判断 | 无 |
| `get_latest` | 返回六组最新读数及最近 13 个月趋势 | 无 |
| `get_indicator` | 按代码或中文名称读取指标及历史序列 | `indicator`、`months=24` |
| `list_pages` | 列出已索引栏目和刷新状态 | 无 |
| `get_page` | 读取栏目摘要、正文、表格或图表 | `page`、`view`、`offset`、`limit` |
| `search_index` | 搜索中文内容或指标代码 | `query`、`page`、`kind`、`mode`、`offset`、`limit` |
| `refresh_index` | 刷新全部栏目 | `force=false` |

`get_latest` 的每组 `trend` 包含历史序列、环比变化、同比变化、最近 3 个月均值相对前 3 个月的动量、`direction`、`assessment`、判断依据和口径解释。`direction` 描述数值走势；`assessment` 才表示经济含义。产业规模、就业、净财政贡献和消费按指标方向返回 `improving`、`deteriorating`、`stable` 或 `mixed`。CPI 和社融不能仅凭升降判断经济好坏，因此返回 `indeterminate`，避免制造虚假结论。历史不足时返回 `insufficientData`。

`get_indicator` 的 `months` 控制返回窗口，范围 2–120，默认 24。例如 `indicator=LEI-GDP, months=36`。环比和同比变化使用原序列单位；百分比指标表示百分点变化，不计算跨零时容易误导的相对百分比。读数同时保留网站值、统计期、来源和完整核验信息。数据期晚于审计期时保留上次审计的可信度、来源、算法和复现记录，并标注 `auditedPeriod`、`appliesToCurrentPeriod=false` 和 `dataUpdated=true`。`get_page` 与 `search_index` 使用 `offset` 和 `limit` 分页。

`get_page(view=charts)` 仅返回当前栏目的图表，不附带全局指标卡。图表标题和 HTML 标签会被清洗；月度日期统一为 `YYYY-MM`，其他日期使用 ISO 8601；每个数据点同时包含清洗后的数值 `value` 和面向展示的 `displayValue`。

`discover_data` 汇总六个主题、当前读数、趋势判断、指标意义、典型问题、页面目录和后续查询建议，适合在指标名称未知或问题范围较宽时使用。

## 数据更新

- 当前 MCP 进程的首个查询会刷新索引，并在刷新完成后读取 SQLite。
- 后续查询直接读取本地索引。
- 首次刷新失败且本地已有数据时，查询返回最近一次成功结果；空库返回 `INDEX_EMPTY`。
- `refresh_index(force=false)` 遵守 24 小时刷新间隔。
- `refresh_index(force=true)` 忽略 24 小时间隔，仍执行 60 秒硬冷却。
- 单页抓取失败时保留该页最近一次成功内容。

`refresh_index` 返回 `completed`、`partial` 或 `skipped`。

## 环境变量

| 环境变量 | 默认值 | 用途 |
|---|---|---|
| `MCEINDEX_BASE_URL` | `https://mceindex.com/` | 数据源地址；HTTP 仅用于本机测试 |
| `MCEINDEX_DB_PATH` | 平台缓存目录下的 `mceindex_mcp/mceindex.db` | SQLite 索引路径 |
| `MCEINDEX_CAMOFOX_URL` | `http://127.0.0.1:9377/` | Camofox HTTP 服务地址 |
| `MCEINDEX_CAMOFOX_EXECUTABLE` | 从 `PATH` 探测 | 无预运行服务时启动的 `camofox-browser` 路径 |
| `MCEINDEX_CAMOFOX_ACCESS_KEY` | 空 | 远程 Camofox 的 Bearer access key；非回环地址必填 |
| `MCEINDEX_CAMOFOX_PROFILE` | 平台缓存目录下的 `mceindex_mcp/camofox` | Camofox 持久化 profile 目录 |
| `MCEINDEX_TIMEOUT_MS` | `45000` | 单页超时 |
| `MCEINDEX_SETTLE_MS` | `1200` | DOM 稳定等待时间 |
| `MCEINDEX_REFRESH_INTERVAL_MS` | `86400000` | 普通刷新间隔 |
| `MCEINDEX_CRAWL_DELAY_MS` | `3000` | 页面请求间隔 |
| `MCEINDEX_CRAWL_CONCURRENCY` | `1` | 抓取并发，范围 1–4 |
| `MCEINDEX_MAX_PAGES` | `20` | 单次刷新页面上限，范围 5–100 |

Cloudflare 验证失败返回 `ACCESS_CHALLENGE`。其他错误码包括 `BROWSER_NOT_FOUND`、`LOAD_TIMEOUT`、`PAGE_NOT_FOUND`、`INDICATOR_NOT_FOUND`、`INDEX_EMPTY`、`INVALID_CONFIGURATION`、`EXTRACTION_FAILED`、`DATABASE_ERROR` 和 `INTERNAL_ERROR`。

## 开发

直接运行：

```bash
dotnet run --project src/MceIndex.Mcp/MceIndex.Mcp.csproj
```

构建与测试：

```bash
dotnet restore
dotnet build MceIndex.slnx --no-restore
dotnet test MceIndex.slnx --no-restore
dotnet pack src/MceIndex.Mcp/MceIndex.Mcp.csproj -c Release --no-restore -o artifacts
```

进程通过 stdio 传输 MCP JSON-RPC。stdout 专用于协议，stderr 输出运行日志。启动测试用 Camofox 后设置 `MCEINDEX_TEST_CAMOFOX_URL` 可运行浏览器集成测试。
