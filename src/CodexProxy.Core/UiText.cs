namespace CodexProxy.Core;

public static class UiText
{
    public static string Language { get; set; } = "zh-CN";

    public static string Get(string chinese) => Language == "en-US" ? English.GetValueOrDefault(chinese, chinese) : chinese;

    private static readonly Dictionary<string, string> English = new()
    {
        ["未检测"] = "Not checked",
        ["检测 ChatGPT 与 OpenAI API 的网络可达性，不读取账号凭据。"] = "Check network access to ChatGPT and the OpenAI API without reading account credentials.",
        ["可用"] = "Available",
        ["不可用"] = "Unavailable",
        ["待检测"] = "Pending check",
        ["使用中"] = "In use",
        ["切换"] = "Switch",
        ["本地代理请求失败（{0}）。"] = "Local proxy request failed ({0}).",
        ["无法连接本地代理内核，或请求已超时。"] = "Cannot connect to the local proxy core, or the request timed out.",
        ["无法刷新订阅套餐信息；已保留上次缓存，请检查直连网络或稍后重试。"] = "Could not refresh subscription quota. The previous cache is kept. Check the direct connection or retry later.",
        ["无法启动 Mihomo。"] = "Cannot start Mihomo.",
        ["Mihomo 启动失败，请本地检查 core.log。"] = "Mihomo failed to start. Check core.log locally.",
        ["35 秒内未导入节点。请检查订阅能否直接获取，以及格式是否受 Mihomo 支持。"] = "No nodes were imported within 35 seconds. Check that the subscription can be fetched directly and its format is supported by Mihomo.",
        ["所选节点或所有节点检测失败，保留原选择。"] = "The selected node or all nodes failed the check. The previous selection is unchanged.",
        ["连接失败"] = "Connection failed",
        ["网络可达"] = "Network reachable",
        ["访问受限"] = "Access restricted",
        ["部分可达 / 待确认"] = "Partially reachable / Unconfirmed",
        ["ChatGPT：{0}\nOpenAI API：{1}\n检测时间：{2:HH:mm:ss}\n401 表示未认证但网络可达；403/429 可能是访问策略或限流。未验证登录、地区支持或模型调用。"] = "ChatGPT: {0}\nOpenAI API: {1}\nChecked at: {2:HH:mm:ss}\n401 means unauthenticated but network reachable; 403/429 may indicate access policies or rate limits. Sign-in, regional support, and model requests have not been verified.",
        ["超时"] = "Timed out",
        ["连接失败（网络或 TLS）"] = "Connection failed (network or TLS)",
        ["连接中"] = "Connected",
        ["已结束（最后采样）"] = "Ended (last sample)",
        ["目标已隐藏"] = "Destination hidden",
        ["检查 Codex 安装位置超时。"] = "Checking the Codex installation location timed out.",
        ["无法读取 Codex 桌面版安装信息。"] = "Cannot read the Codex desktop installation information.",
        ["未检测到 Codex 桌面版，请点击‘选择程序’手动选择已安装的桌面 EXE。"] = "Codex desktop was not found. Click Choose app to select its installed desktop EXE manually.",
        ["Codex 程序路径无效，请选择已存在的桌面 EXE。"] = "The Codex app path is invalid. Select an existing desktop EXE.",
        ["所选文件未识别为 Codex 桌面版。请勿选择 CLI 的 codex.exe 或其他软件。"] = "The selected file was not recognized as Codex desktop. Select its desktop app rather than the CLI codex.exe or another application.",
        ["Codex 已在运行。请保存工作并完全退出 Codex，再从这里启动。已有进程无法接收新的代理环境。"] = "Codex is already running. Save your work and exit Codex completely before launching it here. Existing processes cannot receive the new proxy environment.",
        ["无法启动 Codex 桌面版。"] = "Cannot start Codex desktop.",
        ["尚未通过此启动器打开 Codex"] = "Codex has not been opened through this launcher",
        ["Codex 已退出，或被旧实例接管"] = "Codex exited or was taken over by an existing instance",
        ["已观察到专用代理连接 · 模型请求仍需实测"] = "Dedicated proxy connection observed · Model requests still need verification",
        ["已携带代理设置启动 · 等待网络连接"] = "Launched with proxy settings · Waiting for network connections",
        ["订阅必须是 HTTP(S) 链接。"] = "The subscription must be an HTTP(S) URL.",
        ["未找到 Mihomo，请在订阅设置中选择 mihomo.exe。也可使用本机 v2rayN 的 Mihomo 内核。"] = "Mihomo was not found. Select mihomo.exe in Subscription settings. You can also use the Mihomo core from a local v2rayN installation.",
        ["无法限制状态目录权限，请使用自己的用户目录。"] = "Cannot restrict permissions on the settings directory. Use a directory in your own user profile.",
        ["请先在订阅设置中添加链接。"] = "Add a URL in Subscription settings first.",
        ["默认订阅"] = "Default subscription",
        ["订阅 {0}"] = "Subscription {0}",
        ["所选订阅已不存在，请重新选择。"] = "The selected subscription no longer exists. Select another subscription.",
        ["请填写订阅名称。"] = "Enter a subscription name.",
        ["请选择已保存的订阅。"] = "Select a saved subscription.",
        ["不支持的界面语言。"] = "Unsupported interface language.",
        ["内置内核"] = "Bundled core",
        ["当前选择"] = "Current selection",
        ["手动选择"] = "Manual selection",
        ["内核路径无效，请选择已存在的 Mihomo EXE。"] = "The core path is invalid. Select an existing Mihomo EXE.",
        ["无法读取 Mihomo 内核版本。"] = "Cannot read the Mihomo core version.",
        ["所选文件未识别为 Mihomo 内核。"] = "The selected file was not recognized as a Mihomo core.",
        ["读取内核版本超时，请重新选择 Mihomo EXE。"] = "Reading the core version timed out. Select a Mihomo EXE again.",
        ["官方更新查询失败（{0}）。"] = "Official update check failed ({0}).",
        ["无法查询官方更新，请检查网络连接后重试。"] = "Cannot check official updates. Check your network connection and try again."
    };
}
