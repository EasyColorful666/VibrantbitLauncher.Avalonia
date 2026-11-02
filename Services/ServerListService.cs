using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 往退出时会被 Minecraft 读的 <c>servers.dat</c> 里写一条「房间」记录。
    ///
    /// <para>
    /// <b>为什么要碰这个文件</b>：EasyTier 建的是点对点虚拟网卡，原版的局域网广播只在同一
    /// 广播域里传播，房主的「对局域网开放」永远飘不到房客那边 —— 玩家看到的就是
    /// 「多人游戏里空空的」，只能去「直接连接」手抄 IP 和端口。把房间直接写成一条服务器，
    /// 打开多人游戏就在列表里，双击即进。
    /// </para>
    ///
    /// <para>
    /// <b>为什么必须「读进来再改」</b>：文件里除了玩家自己加过的服务器，还有图标（几十 KB 的
    /// base64）等字段。整个文件重写而不解析，等于把这些内容全洗掉。
    /// </para>
    /// </summary>
    public static class ServerListService
    {
        public const string FileName = "servers.dat";

        private const string ServersKey = "servers";

        /// <summary>
        /// 把某个实例的 <c>servers.dat</c> 里「地址为 host:port」的那条改名叫 <paramref name="name"/>，
        /// 没有就新增一条。返回 false 表示没写成功（错误在 <paramref name="error"/> 里）。
        /// </summary>
        public static bool TryEnsureEntry(
            string gameDir, string name, string host, int port, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(gameDir))
            {
                error = "没有可写入的游戏目录";
                return false;
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                error = "没有拿到房主地址";
                return false;
            }

            var path = Path.Combine(gameDir, FileName);
            var address = port == 25565 ? host : $"{host}:{port}";

            try
            {
                Directory.CreateDirectory(gameDir);

                var root = Load(path);
                if (root is null)
                {
                    // 文件存在但读不出来：绝不覆盖 —— 先留着让它自己坏，也别把玩家的列表抹掉
                    error = "现有的 servers.dat 读不出来，已跳过（不会覆盖它）";
                    return false;
                }

                var list = root.Get(ServersKey);

                if (list is null)
                {
                    list = NbtTag.List(ServersKey, NbtType.Compound);
                    root.Items.Add(list);
                }
                else if (list.Type != NbtType.List
                         || list.ElementType is not (NbtType.Compound or NbtType.End))
                {
                    error = "servers.dat 里的 servers 字段结构不认识，已跳过";
                    return false;
                }

                NbtTag? hit = null;

                foreach (var item in list.Items)
                {
                    if (item.Type != NbtType.Compound)
                        continue;

                    var ip = item.GetString("ip");
                    if (string.IsNullOrWhiteSpace(ip))
                        continue;

                    if (string.Equals(ip, address, StringComparison.OrdinalIgnoreCase)
                        || SameHost(ip, host))
                    {
                        hit = item;
                        break;
                    }
                }

                if (hit is null)
                {
                    hit = NbtTag.Compound();
                    hit.Items.Add(NbtTag.String("name", name));
                    hit.Items.Add(NbtTag.String("ip", address));
                    list.Items.Add(hit);

                    if (list.ElementType == NbtType.End)
                        list.ElementType = NbtType.Compound;
                }
                else
                {
                    var existingName = hit.GetString("name");
                    var existingIp = hit.GetString("ip");

                    if (string.Equals(existingName, name, StringComparison.Ordinal)
                        && string.Equals(existingIp, address, StringComparison.Ordinal))
                    {
                        // 已经是想要的样子：不写盘，免得每次轮询都改文件的修改时间
                        return true;
                    }

                    hit.Remove("name");
                    hit.Remove("ip");
                    hit.Items.Insert(0, NbtTag.String("ip", address));
                    hit.Items.Insert(0, NbtTag.String("name", name));
                }

                Save(path, root);
                Serilog.Log.Information("服务器列表：{Address} 已写入 {Path}", address, path);
                return true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "写入服务器列表失败：{Path}", path);
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 从列表里移除「地址为 host:port」的那条（房主关房时用）。
        /// 返回 true 表示确实删掉了东西。
        /// </summary>
        public static bool TryRemoveEntry(string gameDir, string host, int port)
        {
            if (string.IsNullOrWhiteSpace(gameDir) || string.IsNullOrWhiteSpace(host))
                return false;

            var path = Path.Combine(gameDir, FileName);

            try
            {
                var root = Load(path);
                var list = root?.Get(ServersKey);
                if (root is null || list is null)
                    return false;

                var removed = list.Items.RemoveAll(item =>
                    item.Type == NbtType.Compound && SameHost(item.GetString("ip"), host));

                if (removed == 0)
                    return false;

                Save(path, root);
                return true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "清理服务器列表失败：{Path}", path);
                return false;
            }
        }

        /// <summary>0 字节（游戏异常退出留下的空壳）—— 能安全地当成「没有列表」。</summary>
        private static bool IsEmptyFile(string path)
        {
            try
            {
                return new FileInfo(path).Length == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>ip 字段（可能是 <c>host</c> 或 <c>host:port</c>）是不是同一个主机。</summary>
        private static bool SameHost(string? ipField, string host)
        {
            if (string.IsNullOrWhiteSpace(ipField))
                return false;

            var value = ipField.Trim();

            // IPv6 的写法是 [::1]:25565，这里只处理 IPv4 / 主机名，碰到 '[' 就原样比
            if (value.StartsWith('['))
                return string.Equals(value, host, StringComparison.OrdinalIgnoreCase);

            var colon = value.IndexOf(':');
            var hostPart = colon < 0 ? value : value[..colon];

            return string.Equals(hostPart, host, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>读文件；文件不存在时返回一份只有空 servers 列表的新树。</summary>
        private static NbtTag? Load(string path)
        {
            if (!File.Exists(path) || IsEmptyFile(path))
            {
                // 0 字节是游戏异常退出留下的空壳，当成「还没有列表」处理即可；
                // 真有内容却读不出来的情况走下面的 catch，那边一律不覆盖。
                var fresh = NbtTag.Compound();
                fresh.Items.Add(NbtTag.List(ServersKey, NbtType.End));
                return fresh;
            }

            try
            {
                using var file = File.OpenRead(path);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);

                var root = NbtReader.Read(gzip);

                if (root is null || root.Type != NbtType.Compound)
                    return null;

                return root;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "读取服务器列表失败：{Path}", path);
                return null;
            }
        }

        /// <summary>
        /// 先写临时文件再替换：直接往原文件上写，一旦中途断电 / 被占用，玩家攒了几年的
        /// 服务器列表就只剩半个文件了。
        /// </summary>
        private static void Save(string path, NbtTag root)
        {
            var temp = path + ".vbl-tmp";

            using (var file = File.Create(temp))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            {
                NbtWriter.Write(gzip, root);
            }

            File.Move(temp, path, overwrite: true);
        }
    }
}
