# MCEIndex MCP 使用指南（Windows 版）

本文档介绍如何在 Windows 环境下配置并使用 MCEIndex MCP 服务。

---

## 一、 文件准备

当前目录下已编译生成单文件可执行程序：
- `mceindex-mcp.exe`（约 15 MB，纯静态编译，无任何额外运行时依赖）

建议将其放置在一个固定的工作目录，例如：
`E:\Tools\mceindex-mcp\mceindex-mcp.exe` 或 `C:\Users\你的用户名\AppData\Local\Programs\mceindex-mcp\mceindex-mcp.exe`。

---

## 二、 主流 MCP 客户端配置指南

MCEIndex MCP 基于标准 stdio（标准输入输出）通道通信，兼容所有支持 Model Context Protocol 的客户端。

### 1. Claude Desktop (Windows)

1. 打开 Claude Desktop 配置文件：
   按下 `Win + R`，输入 `%APPDATA%\Claude\claude_desktop_config.json` 并回车。
2. 在 `mcpServers` 节点下添加 `mceindex` 配置（**注意 Windows 路径的双反斜杠 `\\`**）：

```json
{
  "mcpServers": {
    "mceindex": {
      "command": "E:\\Projects\\mecindex-mcp\\mceindex-mcp.exe",
      "args": []
    }
  }
}
```

3. 重启 Claude Desktop，工具列表中显示 `discover_data`、`get_latest` 等 7 个工具即表示配置成功。

---

### 2. Cursor（AI 代码编辑器）

1. 打开 Cursor，进入 **Settings** -> **Features** -> **MCP**。
2. 点击 **+ Add New MCP Server**：
   - **Name**: `mceindex`
   - **Type**: `command`
   - **Command**: `E:\Projects\mecindex-mcp\mceindex-mcp.exe`
3. 保存后，Cursor 会自动启动并显示 7 个已注册的 Tools（绿色状态）。

---

### 3. VS Code 插件（Cline / Roo Code）

1. 打开 VS Code，进入 Cline 或 Roo Code 的 MCP 设置页面（点击工具栏的 MCP 图标）。
2. 在 `mcp_settings.json` 中配置：

```json
{
  "mcpServers": {
    "mceindex": {
      "command": "E:\\Projects\\mecindex-mcp\\mceindex-mcp.exe",
      "args": [],
      "disabled": false,
      "autoApprove": []
    }
  }
}
```

---

### 4. Cherry Studio / Chatbox

1. 进入 **设置 (Settings)** -> **MCP 服务器 (MCP Servers)**。
2. 添加新服务器：
   - **名称**: `mceindex`
   - **协议类型**: `Stdio`
   - **可执行文件路径**: `E:\Projects\mecindex-mcp\mceindex-mcp.exe`
3. 启用并在对话面板中勾选该 MCP 服务。

---

## 三、 典型提问场景与 Prompt 示例

配置完成后，你可以直接用自然语言与 AI 对话，AI 会自动调用对应的 MCP 工具：

### 场景 1：宏观经济数据探索（优先触发 `discover_data`）
> **用户提示词**：
> “最近中国经济有哪些值得关注的宏观指标？给我做个概览分析。”
> “MCEIndex 收录了哪些经济主题和核心读数？”

### 场景 2：最新月度总览与趋势（优先触发 `get_latest`）
> **用户提示词**：
> “帮我获取最新的中国经济指数总览，重点看看五大新产业的规模和就业支撑情况。”
> “当前消费（有意义社零）和通胀（有意义 CPI）的表现如何？有改善吗？”

### 场景 3：单项指标深度时间序列下钻（优先触发 `get_indicator`）
> **用户提示词**：
> “查询一下最近 36 个月的 LEI-GDP（新产业占 GDP 比重）历史走势与环比、同比变化。”
> “看看‘有意义社融’（MSF）过去 2 年的读数和动量。”

### 场景 4：按关键词全文检索（优先触发 `search_index`）
> **用户提示词**：
> “在 MCEIndex 索引中搜索有关‘新能源汽车’和‘集成电路’的所有正文与表格。”
> “搜索关于‘外卖骑手’和‘网约车司机’的数据口径说明。”

### 场景 5：图表与表格抽取（优先触发 `get_page`）
> **用户提示词**：
> “读取‘五大新产业续命指数’（LI_Monthly）页面中的所有清洗后图表数据点。”

### 场景 6：手动强制刷新数据（优先触发 `refresh_index`）
> **用户提示词**：
> “帮我强制刷新一下 MCEIndex 本地数据索引。”

---

## 四、 进阶：环境变量配置

如果需要自定义数据存储路径或浏览器行为，可以在系统环境变量或 MCP 客户端的 `env` 字段中配置：

```json
{
  "mcpServers": {
    "mceindex": {
      "command": "E:\\Projects\\mecindex-mcp\\mceindex-mcp.exe",
      "env": {
        "MCEINDEX_DB_PATH": "D:\\Data\\mceindex.db",
        "MCEINDEX_TIMEOUT_MS": "60000"
      }
    }
  }
}
```

| 环境变量 | 默认值 | 作用说明 |
|---|---|---|
| `MCEINDEX_DB_PATH` | `%LOCALAPPDATA%\mceindex_mcp\mceindex.db` | 本地 SQLite 缓存路径 |
| `MCEINDEX_BROWSER_EXECUTABLE` | 自动检测系统 Edge（`msedge.exe`）或 Chrome | 自定义指定的浏览器路径 |
| `MCEINDEX_TIMEOUT_MS` | `45000`（45 秒） | 爬取单页面的最大等待超时 |
| `MCEINDEX_REFRESH_INTERVAL_MS` | `86400000`（24 小时） | 自动刷新间隔 |
| `MCEINDEX_CRAWL_DELAY_MS` | `3000`（3 秒） | 页面之间的抓取间隔（防封保护） |

---

## 五、 常见问题与排查 (FAQ)

1. **第一次查询时为什么要等待数秒？**
   - 首次启动且本地 SQLite 为空时，服务会自动触发一次后台静默抓取（耗时约 6 到 7 秒完成 Streamlit SPA 数据水合与 Plotly 图表抽取）。
   - 抓取完成后数据将永久写入本地 SQLite，**后续所有查询均在 0 毫秒内极速返回**。
2. **需要手动保持浏览器打开吗？**
   - **完全不需要**。浏览器由 Go 内部以 `--headless` 静默后台方式拉起，抓取完毕后会自动彻底退出并释放所有内存。
3. **如何排查连接日志？**
   - 本服务将运行日志统一输出到 `stderr`，不会干扰 `stdout` 的 MCP JSON-RPC 协议通道。在 Claude Desktop 或 Cursor 的 MCP 日志面板中可以直接查看到带有时间戳的运行日志。
