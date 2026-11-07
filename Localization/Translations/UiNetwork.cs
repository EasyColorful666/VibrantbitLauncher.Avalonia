namespace VibrantbitLauncher.Localization.Translations;

/// <summary>
/// 词表：自定义下载源、网络体检、多线程测速（1.0.32.0 新增）。
///
/// <para>
/// 键必须与源码里的中文**逐字一致**：全角标点、「」、省略号「…」、行首空格都不能改，
/// 否则查不到就退回中文。带数字的句子写成 <c>{0}</c> 占位，同一值出现两次就写两个占位。
/// </para>
/// </summary>
internal static class UiNetwork
{
    public static void AddTo(Dictionary<string, string> d)
    {
        // ===== 下载源 =====
                                d["自定义源"] = "Custom source";
        d["自定义源地址"] = "Custom source addresses";
        d["自定义源（{0} 个）"] = "Custom source ({0})";
        d["自定义源（未配置，暂用内置镜像）"] = "Custom source (not configured — using the built-in mirror)";
        d["自定义源（自带镜像优先，可回退官方）"] = "Custom source (mirror first, official fallback)";
        d["自定义源：按你填的地址改写下载链接，连不上时回退官方"] =
            "Custom source: download links are rewritten to your addresses, falling back to the official ones";
        d["自动测速：镜像优先，镜像不行再回退官方"] =
            "Auto: mirror first, official when the mirror fails";
        d["一行一个，填到域名即可。地址需要与 BMCLAPI 的路径结构兼容：启动器会在后面自动拼 /maven/、/fabric-meta/、/version/… 等路径。多个源按顺序互为备份。首次安装原版游戏时的本体与资源文件仍按内置镜像逻辑处理，自定义源主要作用于版本清单、加载器依赖、模组与资源补全。请尽量用 https：http 明文地址在链路上可能被替换，带 sha1 的文件会被校验出来，没有哈希的文件挡不住。"] =
            "One per line, just the domain. The address has to follow BMCLAPI's path layout — the launcher appends /maven/, /fabric-meta/, /version/… itself. When you list several, they back each other up in order. A first-time vanilla install still uses the built-in mirror logic for the game itself and its assets; custom sources cover the version manifest, loader libraries, mods and asset repair. Prefer https: a plain http source can be swapped in transit — files with a sha1 still get verified, files without one do not.";
        d["还没有填自定义源。地址需要和 BMCLAPI 的路径结构兼容（根地址后面会自动拼 /maven/、/version/… 等路径）。"] =
            "No custom source yet. Addresses have to follow BMCLAPI's path layout (the launcher appends /maven/, /version/… to the root you enter).";
        d["已识别 {0} 个源：{1}"] = "Recognised {0} source(s): {1}";
        d["没有识别到可用的源，下载会退回内置镜像。"] =
            "No usable source was recognised — downloads fall back to the built-in mirror.";
        d["；{0} 行无法识别：{1}（{2}）"] = "; {0} line(s) not recognised: {1} ({2})";
        d["需要以 http:// 或 https:// 开头的完整地址"] = "Use a full address starting with http:// or https://";
        d["只填到域名即可，不要带路径"] = "Enter the domain only — no path";
        d["总连接数 ≈ {0} × {1} = {2} 条"] = "Total connections ≈ {0} × {1} = {2}";
        d["总连接数 ≈ {0} × {1} = {2} 条，已经超过多数镜像的容忍范围，可能反而变慢或被限流"] =
            "Total connections ≈ {0} × {1} = {2}, beyond what most mirrors tolerate — it may slow you down or get you rate-limited";
        d["{0} 条"] = "{0}";

        // ===== 网络体检 =====
        d["网络体检"] = "Network checkup";
        d["按「域名解析 → 建立连接 → 请求响应」三段分别计时，一眼看出慢在哪一段。体检只读前几 KB 数据，不会真的把文件下载下来。"] =
            "Times DNS, connection and response separately, so you can see which stage is slow. It reads only the first few KB, never a whole file.";
        d["测速连接数"] = "Test connections";
                d["多线程测速"] = "Speed test";
                d["多连接合计"] = "Combined";
        d["瞬时峰值"] = "Peak";
        d["多线程提升"] = "Speed-up";
        d["通畅"] = "Good";
        d["偏慢"] = "Slow";
        d["很慢"] = "Very slow";
        d["不通"] = "Unreachable";
        d["正在体检…"] = "Running the checkup…";
        d["已完成 {0} 项…"] = "{0} done…";
        d["已取消，完成 {0} 项"] = "Cancelled after {0} check(s)";
        d["体检完成：{0} 项里 {1} 项通，{2} 项不通"] =
            "Checkup done: {1} of {0} reachable, {2} unreachable";
        d["体检查出错：{0}"] = "The checkup failed: {0}";
        
        // 体检项目名
        d["官方版本清单"] = "Official version manifest";
        d["官方资源文件"] = "Official asset files";
        d["Fabric 依赖库"] = "Fabric libraries";
        d["Forge 依赖库"] = "Forge libraries";
        d["NeoForge 依赖库"] = "NeoForge libraries";
        d["Modrinth 模组站"] = "Modrinth";
        d["GitHub（联机内核下载）"] = "GitHub (multiplayer core download)";
        d["联机中继服务器"] = "Multiplayer relay";
        d["自定义源 {0}"] = "Custom source {0}";

        // 体检结论
        d["端口可连通"] = "Port reachable";
        d["地址格式不对，没有可解析的主机名"] = "Malformed address — no host name to resolve";
        d["DNS 解析超时（DNS 服务器无响应）"] = "DNS timed out (the DNS server didn't answer)";
        d["DNS 解析失败（域名查不到地址）"] = "DNS failed (the name has no address)";
        d["连接 {0}:{1} 超时（端口被拦或线路不通）"] = "Connecting to {0}:{1} timed out (port blocked or route dead)";
        d["连接 {0}:{1} 被拒绝"] = "Connecting to {0}:{1} was refused";
        d["服务器自己报错（HTTP {0}）"] = "The server itself errored (HTTP {0})";
        d["HTTP {0}（能连上，但该路径不可访问）"] = "HTTP {0} (reachable, but that path isn't accessible)";
        d["HTTPS 请求超时（握手或响应太慢）"] = "HTTPS request timed out (handshake or response too slow)";
        d["TLS 握手失败（证书或协议被中间人干扰）"] = "TLS handshake failed (certificate or protocol interfered with)";
        d["HTTPS 请求失败（连接被重置或代理配置有问题）"] = "HTTPS request failed (connection reset or bad proxy settings)";

        // ===== 多线程测速 =====
        d["准备测速…"] = "Preparing the speed test…";
        d["正在测单连接…"] = "Testing a single connection…";
        d["正在测 {0} 连接…"] = "Testing {0} connections…";
        d["测速完成 · {0}（{1} 秒读了 {2} MB）"] = "Done · {0} ({2} MB in {1} s)";
        d["测速未完成"] = "The speed test didn't finish";
        d["测速出错：{0}"] = "The speed test failed: {0}";
        d["拿不到可用的测试文件：版本清单和镜像都没回应，先做一次网络体检看看是哪一段断了"] =
            "No usable test file: neither the version manifest nor the mirror answered. Run the checkup first to see which stage is broken.";
        d["测速失败：{0}"] = "Speed test failed: {0}";
        d["一个字节都没测到，先做一次网络体检"] = "Not a single byte came through — run the checkup first";
        d["这个源不支持分块下载，只能单连接 —— 想提速请换一个支持 Range 的源"] =
            "This source doesn't support range requests, so only one connection can be used — switch to a source that does if you want more speed";
        d["多连接几乎没提升：链路本身已经跑满了，再把并发调高意义不大"] =
            "Extra connections barely helped: the link is already saturated, raising the counts won't do much";
        d["速度偏低：换个下载源，或把单文件分块数调到 16 以上再测一次"] =
            "On the slow side: try another source, or raise chunks per file above 16 and test again";
        d["多连接收益明显：这个源在单连接上限速，可以适当调高单文件分块数"] =
            "Extra connections clearly help: this source throttles each connection, so raising chunks per file is worthwhile";
        d["速度正常"] = "Speed looks fine";
    }
}
