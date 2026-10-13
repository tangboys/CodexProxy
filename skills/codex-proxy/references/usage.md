# 使用说明

本技能管理 Windows Codex 专用代理。桌面版使用 C# WPF 管理器，CLI 使用 Python 标准库启动器；两者共用本地配置和独立 Mihomo 内核。没有复制 v2rayN 或 233boy/v2ray 源码。桌面便携包附带内核，独立技能包中的 CLI 脚本使用本机内核。

## 桌面版可视化管理

桌面程序从 [GitHub Releases](https://github.com/tangboys/CodexProxy/releases) 获取。源码构建后位于项目 dist/<版本>/CodexProxy.exe。优化阶段直接使用 EXE，安装包在运行验证完成后再制作。双击 EXE，或运行：

```powershell
& "$env:USERPROFILE\.codex\skills\codex-proxy\codex-proxy.ps1" desktop
```

在“设置 → 常规”打开“订阅设置”，点击“＋ 新增”，填写名称和链接。“保存”保留为备用订阅；选择左侧某个订阅后点击“保存并启用”才切换使用，同时只启用一个订阅、不合并节点。保存正在使用的订阅也会验证并更新连接。旧单订阅自动迁移，列表和启用选择会保存；选中订阅后显示已保存链接，可直接修改，取消“显示订阅链接”可遮挡。编辑留空保留原链接。0.11.1 起当前订阅也可删除：停止自有代理、清空当前配置，其他订阅不自动启用，Codex 本身保留。重新启用订阅后需保存工作并完全退出旧 Codex，再从启动器打开以应用新的代理配置。

每次新开管理器默认停止；停止时也显示按订阅保存的节点列表、最后可用状态与延迟，并显示检测时间。可先预选手动节点或自动模式，再点击“启动代理”。停止时“检测节点”通过临时隔离内核测速，“刷新节点”重新获取和验证当前订阅；两者完成后清理临时内核，正式代理保持停止。没有节点快照的旧配置从已有 YAML 缓存生成预览，缺少缓存时点击刷新。“检测 Codex”仍需先启动代理。保存并启用订阅先用临时内核验证，成功后更新配置，失败保留原连接；保存配置或选择内核不改变停止状态，也不修改系统代理。

取消自动选择时固定内核当前节点，不重新测速；停止时没有当前节点则固定列表选中节点或首个节点。保存后仍保持停止，重开管理器保留手动预选。运行时若控制器或保存失败会明确报错；普通切换和启动时仍进行原有节点检查。

0.12 概览显示 HTTP/SOCKS 混合端口和单独的浏览器桥地址，明确区分配置、就绪和停止。在“设置 → 常规”停止代理后输入 1–65535 并保存，或留空/点击自动分配；保存和启动均检查 TCP/UDP 占用及控制器冲突。不会结束端口占用者；检查不保留端口。改端口保留密码和控制器，释放旧浏览器桥但不关闭 Codex；保存工作并完全退出 Codex，再从管理器启动才能采用新端口。

订阅下载方式同页手选“直连 / 系统代理 / 当前专用代理”，同时控制节点与套餐获取，失败不自动换路。直连默认绕过普通 HTTP 系统/环境代理，外部 TUN 或透明路由仍可能影响它。系统模式使用 .NET 默认代理；Mihomo 的节点下载保存目标 URL 对应的 HTTP/HTTPS/SOCKS5 代理快照，PAC 变化和跨主机重定向可能不同，不支持的协议或认证会明确报错。当前专用代理需要已有节点、正式内核已启动；首次导入先用直连或系统代理。修改方式后下次刷新节点、启用订阅或重启内核加载到节点下载；当前内核每 24 小时原生更新此前仍沿用旧方式。测速/自动优选间隔保持 5 分钟。这些选择不改系统设置。

套餐区按上述所选方式通过单次 GET 读取订阅响应头；直连/系统代理不依赖正式内核，专用代理方式依赖它已运行，停止时仍可读缓存。接受 subscription-userinfo 以及 x-amz-meta-subscription-userinfo 等以连字符连接的兼容前缀，字段大小写无关。使用固定兼容 UA clash-verge/v2.5.9（节点下载也相同），只读响应头，不处理正文。已用为上传加下载，正的总量用于计算剩余和进度；缺少字段或总量为 0 时显示未知，不视为无限。“刷新用量”仅更新套餐，不改节点或 Codex 检测结果；“刷新节点”更新节点后在后台另取套餐。数据和读取时间按订阅 URL 哈希持久保存，打开或切换订阅时后台更新，停止时也能查看，直连/系统代理方式也可刷新。请求失败保留该订阅上次缓存并提示；请求成功却没有有效字段时显示未知。套餐数据可能延迟，不能用左下本机内核累计流量代替。实现参考 [Clash Verge Rev 订阅解析](https://github.com/clash-verge-rev/clash-verge-rev/blob/84312a5cd171b338663c347d3182bd162306801f/src-tauri/src/config/prfitem.rs)及其[请求实现](https://github.com/clash-verge-rev/clash-verge-rev/blob/84312a5cd171b338663c347d3182bd162306801f/src-tauri/src/utils/network.rs)。

0.11 将主页整理为“概览”，节点和日志各有独立页面。设置内嵌“常规、内核、更新”：语言、状态刷新间隔和 Codex 路径集中在常规页。后台 Codex 检测期间可继续浏览页面或手动选择节点；刷新、启用订阅或切换内核先停止旧检测并等待清理，再依次执行变更。隐藏内核/更新页会取消扫描或查询；应用内核的事务需等待完成。该版本仅编译，交互和真实 Codex 请求由用户验证，步骤见程序目录的 `TEST-RESULTS.md`，真实消息检查放在最后。

“设置 → 常规”的语言下拉可切换简体中文和 English，并在重启后恢复选择。状态刷新可保存为 5/10/30/60 秒，流量仍每秒采样。Tab 保留键盘导航，去掉虚线焦点框。“设置 → 更新”显示本机版本、已知发布版本和发布时间：API 限流/网络失败时改读公开版本清单，标明来源；两处都失败时保留上次成功数据和时间并提示失败。缓存 5 分钟，手动检查仍遵守服务器限流等待；不自动下载安装。0.5 起当前 Windows 用户会话只保留一个管理器，从不同目录重复启动也唤起已有窗口。首次升级前，请在旧管理器中点击“退出程序”再打开新版；只关闭窗口仍会留在托盘。

“设置 → 内核”可查看内置和本机多个兼容 Mihomo 的来源、版本与完整路径，选择后点击“应用所选内核”。内核已嵌入 EXE，运行时提取到 `%LOCALAPPDATA%\codex-proxy\cores\bundled-<哈希前16位>\mihomo.exe`，不再需要同目录的 bin。首次优先内置；旧版保存的外部内核路径不自动更改。切换保留监听端口和凭据，但短暂重启内核，正在进行的请求可能需要重试。“检查内核更新”只查询官方稳定版并提供发布页入口，不自动替换；Alpha 版本不进行稳定版大小比较。内核检查仍可能被 GitHub API 限流，不读取本项目的版本清单。

第一次接入桌面版需要先“启动代理”，保存工作并完全退出旧 Codex，再点击管理器的“启动 Codex”。不能给已经运行的进程注入新的代理环境；程序遇到旧实例会明确拒绝启动，不强制结束用户进程。“停止代理”只停止本工具拥有的正式内核，保留 Codex 与本地转发桥，按原端口重开后新请求可重试；已建立的流式请求可能失败。不能代停其他启动器拥有的内核。关闭管理窗口收起到系统托盘；左侧或托盘的“退出程序”清理本工具拥有的内核与转发桥。代理运行状态和 Codex 接入状态分别显示，内核就绪不代表模型请求成功。

桌面版接入已分别验证本机 Chromium 的代理参数和 Rust app-server 的环境代理，后者已通过本地 Responses 测试服务完成模型会话。真实订阅、登录和账号模型调用仍需在从管理器启动的桌面版中实测。WSL 后端未验证。

从便携包自带 `skills/codex-proxy` 直接执行可相对定位 EXE。安装独立技能 ZIP 后，将 EXE 的绝对路径写入技能目录的 `desktop-path.txt`。不要将整个便携包联接到技能目录内，以免嵌套技能被重复发现：

```powershell
Set-Content -LiteralPath '<已安装技能目录>\desktop-path.txt' -Value '<便携包根目录>\CodexProxy.exe' -Encoding utf8
```

以下是 CLI 的用法；管理器持有内核时，CLI 的检测和切换命令会复用该内核，也会影响从管理器启动的桌面版的新连接。

桌面程序启动时自动查找 Codex 安装并显示路径；“选择程序”可手选并保存，拒绝 CLI 和其他程序。节点国旗按名称推断，不能作为 IP 位置证明。左下流量来自专用内核；“流量日志”每秒采样、保留本次运行最近 300 条，可能漏过短连接，结束连接的字节是最后采样值。

“检测 Codex”逐节点检查 ChatGPT 用量接口及 OpenAI `/v1/models`；通过临时独立内核进行，不改变主代理选择。单地址 8 秒超时，可停止；不读取账号、不发模型请求。401 是未认证但网络可达，403 / 429 / 451 提示受限或限流，不能保证真实账号使用成功。结果仅本次运行保留，更换或刷新订阅后需重测；不会改变按普通检测地址延迟进行的自动优选策略。

## 直接使用

在普通 PowerShell 终端执行：

```powershell
$proxyLauncher = Join-Path $env:USERPROFILE '.codex\skills\codex-proxy\codex-proxy.ps1'
& $proxyLauncher
```

首次会隐藏输入订阅链接，之后显示菜单：检测节点、手动选择、自动选择、启动 Codex、刷新订阅。手动选择用编号；命令模式则使用节点完整名称。

若使用解压后的文件夹，将 `$proxyLauncher` 设为该文件夹中的 `codex-proxy.ps1` 绝对路径即可。要让 CLI 发现技能，将整个 `codex-proxy` 文件夹放入 `$HOME/.agents/skills/`。请只选择一个技能目录安装，避免重复发现。新 CLI 会话可使用 `$codex-proxy`。

## 命令模式

| 命令 | 行为 |
| --- | --- |
| `desktop` / `panel` | 打开 C# 原生桌面代理管理器 |
| `setup` | 隐藏输入订阅，验证内核、获取节点并保存；默认自动模式 |
| `list` | 列出节点，不把未测速节点标为可用 |
| `test` | 并行检测节点的可达性、HTTP 状态和延迟 |
| `select '完整节点名'` | 检测后固定节点；无名称则输入编号 |
| `auto` | 检测成功后启用持续自动选择，每 300 秒检测一次 |
| `refresh` | 立即更新订阅；内核运行时另每 24 小时更新一次 |
| `status` | 查看是否配置、当前模式、内核是否运行；不显示订阅和密钥 |
| `run` | 检测当前选择后启动 Codex，退出时清理自己启动的内核 |
| `menu` / 无参数 | 保持内核运行的交互菜单；输入 0 退出并清理 |

```powershell
& $proxyLauncher setup
& $proxyLauncher test
& $proxyLauncher select '你的节点完整名称'
& $proxyLauncher auto
& $proxyLauncher run
& $proxyLauncher run -- resume --last
& $proxyLauncher run -- exec '解释当前项目的结构'
```

有多个可用内核或需要自行指定路径时：

```powershell
& $proxyLauncher setup --core 'D:\v2rayN-windows-64\bin\mihomo\mihomo.exe'
```

CLI 可通过 --core 显式指定本机 Mihomo；桌面版优先使用内置内核，也支持检测常见客户端目录。不要因找不到内核就修改系统 PATH；直接传 `--core` 即可。

订阅支持 Mihomo 能解析的 Clash YAML（含 `proxies`）、原始 URI 列表和 Base64 URI 列表，具体协议及参数受内核版本限制。不支持把任意客户端专用的加密订阅当作通用订阅。导入完整 Clash 配置时只作为节点 provider 读取，不采用其中的 TUN、监听地址、路由或系统设置。CLI 脚本的首次订阅下载走直连，不读取桌面版新增的下载方式设置；若直连无法访问，先从已有客户端导出可直连的订阅地址后再导入，脚本不会自动打开系统代理。

## 自定义检测地址

默认检测 `https://www.gstatic.com/generate_204`，要求 HTTP 204。检测使用 HEAD 请求，因此自定义服务需支持 HEAD。

```powershell
& $proxyLauncher setup --test-url 'https://你的服务.example/health' --expected-status '200'
```

例如确认 OpenAI API 的网络路径时，可使用 `https://api.openai.com/v1/models`、预期状态 `401`；这只能检查未认证请求能否到达该 API，不能验证 ChatGPT 登录、账号资格、地区支持或模型权限。自定义网关应选择自己的检查地址，勿在 URL 中放 API Key。实际模型请求仍要在启动后的 Codex 中验证。

## 作用范围和生命周期

启动器创建 `HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY` 和仅含本机地址的 `NO_PROXY`，只通过子进程环境传递给 Codex。每次启动用临时 CLI 配置 `features.respect_system_proxy=false`，不写入 Codex 配置文件。Codex 的子工具若继承并遵守代理变量，也会走该代理；不遵守代理变量的程序不能由本技能强制代理。

在原来已启动的 Codex 中调用技能只能配置后续启动或切换已接入的独立内核，不能改变父进程的启动环境。必须通过 `run` 或菜单启动新的 Codex。CLI 内使用本技能切换节点时会复用经过密钥认证的当前内核。已有的长连接不会强制断开，需要新请求或重试后生效。

普通命令执行结束会关闭自己的内核；选择会保存供下次启动恢复。菜单中的 Codex 退出后返回菜单，内核继续运行，退出菜单后清理。另一个命令借用正在运行的内核时不会终止它。强制结束启动器可能留下该独立内核；先在任务管理器核实其命令行含本技能状态目录，再关闭该单独进程，勿按进程名批量结束其他客户端。

脚本不改 Windows 系统代理、WinHTTP、TUN、路由或全局环境。已有系统代理/TUN 不会被关闭；若目标是其他软件直连，需先关闭它们。节点失效会导致请求失败或在下次健康检查后切换，不保证正在流式生成的请求无中断。所有节点不可用时拒绝启动，空节点组使用 REJECT，不回退到 DIRECT。

## 本地数据和故障排查

默认状态目录为 `%LOCALAPPDATA%\codex-proxy`，Windows ACL 限制为当前用户访问。此目录含订阅链接、节点凭据、随机控制器密钥、随机本地代理密码和内核日志；凭据是本地明文，不能上传、提交版本库或分享原始日志。订阅隐藏输入避免写入 shell 历史；程序也不回显链接。

代理和控制器均只绑定 `127.0.0.1`，代理需要用户名密码，控制器需要随机 Bearer 密钥。CLI 端口在 setup 时选择，若后来被占用，退出专用启动器后重新 setup；桌面版可停止后在设置里改混合端口。不要把监听地址改为 `0.0.0.0`。系统模式使用的上游凭据可能写入受私有目录权限保护的内核配置，同样不可分享。

`setup --url-file <绝对路径>` 可从 UTF-8 文本读取订阅链接，适合 Codex 代为配置。文件仅含一条 URL，完成后清理该单独临时文件。独立测试或第二套状态可用 `--state-dir <路径>`，该参数必须位于子命令前。

需要 Python 3.10+，没有额外 Python 包依赖。PowerShell 启动器自动找 `py -3`、实际 Python 安装和本机 Codex 随附 Python。若执行策略阻止脚本，可仅对本次进程运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File $proxyLauncher status
```

缺少内核时从 [Mihomo 官方 Releases](https://github.com/MetaCubeX/mihomo/releases) 获取符合机器架构的稳定版本，解压后用 `--core` 指定。已验证 v1.19.25 和 v1.19.31；不自动下载或执行安装脚本。配置错误时在本机查看状态目录下的 `core.log`，报告时只提取脱敏的错误原因。

## 验证

Python 单元测试验证环境隔离、参数原样传递、全节点失败时拒绝启动。指定 `MIHOMO_CORE` 并令 `TEST_PYTHON` 指向当前 Python 可运行本地集成测试：

```powershell
$env:MIHOMO_CORE = 'D:\Clash Verge\verge-mihomo.exe'
$env:TEST_PYTHON = 'C:\Path\To\python.exe'
& $env:TEST_PYTHON (Join-Path (Split-Path $proxyLauncher) 'scripts\test_codex_proxy.py')
```

集成测试只用临时目录、本地订阅和本地代理节点，验证 YAML / URI / Base64 导入（SS、VMess、VLESS、Trojan）、含特殊字符的手选名称、自动故障切换、错误状态排除、刷新失败保留旧节点、代理认证、内核复用与清理，以及真实 Codex CLI 经过代理访问本地模拟 Responses 服务。模拟模型请求使用无真实凭据的独立 CODEX_HOME，不调用真实模型。真实订阅、TLS 节点和真实 OpenAI 请求需要用户自己的网络进行最终验证。

参考：[v2rayN](https://github.com/2dust/v2rayN)、[233boy/v2ray](https://github.com/233boy/v2ray)、[Mihomo 订阅格式](https://wiki.metacubex.one/en/config/proxy-providers/content/)、[策略组](https://wiki.metacubex.one/en/config/proxy-groups/)、[控制 API](https://wiki.metacubex.one/en/api/)、[Codex 技能发现路径](https://developers.openai.com/codex/skills/)。
