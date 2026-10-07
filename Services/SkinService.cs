using MinecraftLaunch.Base.Models.Authentication;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VibrantbitLauncher.Helpers;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 皮肤服务：读取 / 更换账户皮肤。
    ///
    /// <para>
    /// <b>微软账户</b>走 Mojang 官方接口（见 https://zh.minecraft.wiki/w/Mojang_API ）：
    /// </para>
    /// <list type="bullet">
    /// <item>读：<c>GET https://sessionserver.mojang.com/session/minecraft/profile/{uuid}?unsigned=false</c>，
    /// 无需鉴权；返回的 <c>properties[].value</c> 是 Base64，解开后取 <c>textures.SKIN.url</c> 与
    /// <c>textures.SKIN.metadata.model</c>（slim = Alex 细手臂，否则 classic）。</item>
    /// <item>写：<c>POST https://api.minecraftservices.com/minecraft/profile/skins</c>，
    /// 请求头 <c>Authorization: Bearer {Minecraft 访问令牌}</c>；
    /// 上传本地图片用 <c>multipart/form-data</c>（<c>variant</c> + <c>file</c>），
    /// 按链接设置用 JSON（<c>variant</c> + <c>url</c>）。</item>
    /// <item>重置：<c>DELETE .../skins/active</c>。</item>
    /// </list>
    ///
    /// <para>
    /// <b>离线 / 外置账户</b>官方接口不认，改为把皮肤落到本地：完整皮肤 <c>res/{uuid}_full.png</c>、
    /// 头像 <c>res/{uuid}.png</c>。离线账户启动时再由 <c>RunPageViewModel</c> 追加启动参数
    /// <c>--skinPath</c> 指向这个文件。
    /// </para>
    /// </summary>
    public static class SkinService
    {
        private static readonly HttpClient Http = CreateClient();

        /// <summary>皮肤模型变体，对应 Mojang API 的 variant 字段。</summary>
        public const string VariantClassic = "classic";
        public const string VariantSlim = "slim";

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            try { client.DefaultRequestHeaders.UserAgent.ParseAdd("VibrantbitLauncher/1.0"); } catch { }
            return client;
        }

        /// <summary>皮肤文件目录（与头像共用 res）。</summary>
        public static string SkinDirectory => Path.Combine(AppContext.BaseDirectory, "res");

        /// <summary>完整皮肤文件路径（64×64 或 64×32），用于预览与启动参数。</summary>
        public static string FullSkinPath(string uuid)
            => Path.Combine(SkinDirectory, $"{Sanitize(uuid)}_full.png");

        /// <summary>头像文件路径（由完整皮肤裁出的头部），用于账户卡片圆形头像。</summary>
        public static string HeadSkinPath(string uuid)
            => Path.Combine(SkinDirectory, $"{Sanitize(uuid)}.png");

        /// <summary>
        /// 角色正面视图（由皮肤各部位拼合），用于皮肤管理的整身预览。
        /// 卡片列表仍用 <see cref="HeadSkinPath"/> 的方头像，只有详情页用这张。
        /// </summary>
        public static string FrontViewPath(string uuid)
            => Path.Combine(SkinDirectory, $"{Sanitize(uuid)}_front.png");

        private static string Sanitize(string uuid)
            => string.IsNullOrWhiteSpace(uuid) ? "unknown" : uuid.Trim();

        // ================= 角色正面视图拼合 =================

        // 64×64 皮肤里各部位「正面」的矩形（x, y, w, h）。
        // 底层（base layer）—— 皮肤真正的「身体」外形。
        private static readonly (int X, int Y, int W, int H) HeadFront = (8, 8, 8, 8);
        private static readonly (int X, int Y, int W, int H) BodyFront = (20, 20, 8, 12);
        private static readonly (int X, int Y, int W, int H) RightArmFront = (44, 20, 4, 12);
        private static readonly (int X, int Y, int W, int H) LeftArmFront = (36, 52, 4, 12);
        private static readonly (int X, int Y, int W, int H) RightLegFront = (4, 20, 4, 12);
        private static readonly (int X, int Y, int W, int H) LeftLegFront = (20, 52, 4, 12);

        // 叠加层（overlay / hat layer）—— 帽子、头发、外套、袖子、裤腿等「外部材质」。
        // 几乎每一张现代皮肤的真实外观都画在这一层，漏掉它人物就会「少一层衣服」。
        private static readonly (int X, int Y, int W, int H) HeadOverlay = (40, 8, 8, 8);
        private static readonly (int X, int Y, int W, int H) BodyOverlay = (20, 36, 8, 12);
        private static readonly (int X, int Y, int W, int H) RightArmOverlay = (44, 36, 4, 12);
        private static readonly (int X, int Y, int W, int H) LeftArmOverlay = (52, 52, 4, 12);
        private static readonly (int X, int Y, int W, int H) RightLegOverlay = (4, 36, 4, 12);
        private static readonly (int X, int Y, int W, int H) LeftLegOverlay = (4, 52, 4, 12);

        /// <summary>正面视图画布尺寸（像素），各部位按等比例放大后的排版基线。</summary>
        private const int FrontCanvasWidth = 16;
        private const int FrontCanvasHeight = 32;
        /// <summary>放大倍数：皮肤原始尺寸太小，放大后预览才清楚。</summary>
        private const int FrontScale = 1;

        /// <summary>
        /// 把皮肤的各部位「正面」拼成一张整身正面立绘。
        ///
        /// 布局（以皮肤原始像素为单位，画布 16×32）：
        /// <code>
        ///           [头 8x8]        y=0
        ///   [右臂]  [身体 8x12] [左臂]  y=8
        ///           [右腿][左腿]    y=20
        /// </code>
        /// 手臂固定为「自然下垂」姿态。
        ///
        /// 每个部位都按「先底层、再叠加层（帽子/外套/袖子/裤腿）」的顺序 alpha 混合，
        /// 否则会丢掉皮肤的「外部材质」，人物看起来像没穿衣服。
        ///
        /// 旧版 64×32 皮肤没有左侧手脚的独立贴图，按 Minecraft 的约定镜像右侧，
        /// 同时也不存在叠加层，直接跳过。
        /// 返回拼好的图片路径；失败返回空字符串。
        /// </summary>
        public static string? SaveFrontView(string uuid, byte[] skinBytes)
        {
            try
            {
                Directory.CreateDirectory(SkinDirectory);

                var skin = RgbaBitmap.Decode(skinBytes);
                var legacy = skin.Width == 64 && skin.Height == 32;

                var canvas = new RgbaBitmap(FrontCanvasWidth * FrontScale, FrontCanvasHeight * FrontScale);

                (Rgba Rgb, byte A) Sample((int X, int Y, int W, int H) rect, int x, int y, bool mirror)
                {
                    var sx = rect.X + (mirror ? rect.W - 1 - x : x);
                    var sy = rect.Y + y;
                    if (sx < 0 || sy < 0 || sx >= skin.Width || sy >= skin.Height)
                        return (default, 0);
                    var p = skin.GetPixel(sx, sy);
                    return (p, p.A);
                }

                // 把某个矩形按 alpha 混合画到画布上（over 合成，保留透明像素）
                void Paint((int X, int Y, int W, int H) rect, int destX, int destY, bool mirror)
                {
                    for (var y = 0; y < rect.H; y++)
                    {
                        for (var x = 0; x < rect.W; x++)
                        {
                            var (px, alpha) = Sample(rect, x, y, mirror);
                            if (alpha == 0) continue;

                            var dx = (destX + x) * FrontScale;
                            var dy = (destY + y) * FrontScale;
                            for (var oy = 0; oy < FrontScale; oy++)
                            {
                                for (var ox = 0; ox < FrontScale; ox++)
                                {
                                    canvas.SetPixel(dx + ox, dy + oy, AlphaBlend(px, canvas.GetPixel(dx + ox, dy + oy)));
                                }
                            }
                        }
                    }
                }

                // 拼一个部位：底层 + 叠加层，叠加层按同一目标坐标覆盖上去
                void BlitPart(
                    (int X, int Y, int W, int H) baseRect,
                    (int X, int Y, int W, int H) overlayRect,
                    int destX, int destY)
                {
                    var mirrorBase = legacy && baseRect == LeftArmFront;
                    var mirrorOverlay = legacy && overlayRect == LeftArmOverlay;

                    // 旧皮肤：左臂/左腿用右侧贴图镜像
                    if (mirrorBase)
                        Paint(RightArmFront, destX, destY, true);
                    else
                        Paint(baseRect, destX, destY, false);

                    if (legacy) return; // 旧皮肤没有叠加层

                    Paint(overlayRect, destX, destY, mirrorOverlay);
                }

                // 头（居中，画布宽 16 → 头宽 8，左起 x=4）
                BlitPart(HeadFront, HeadOverlay, 4, 0);
                // 身体（居中，宽 8，左起 x=4）y = 8
                BlitPart(BodyFront, BodyOverlay, 4, 8);
                // 右臂（在身体左侧）宽 4，左起 x=0；左臂在身体右侧，左起 x=12
                BlitPart(RightArmFront, RightArmOverlay, 0, 8);
                BlitPart(LeftArmFront, LeftArmOverlay, 12, 8);
                // 右腿（左起 x=4）/ 左腿（左起 x=8），y = 20
                BlitPart(RightLegFront, RightLegOverlay, 4, 20);
                BlitPart(LeftLegFront, LeftLegOverlay, 8, 20);

                var path = FrontViewPath(uuid);
                canvas.Save(path);
                return path;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "拼合角色正面视图失败");
                return null;
            }
        }

        /// <summary>标准「over」alpha 混合：把 src 叠在 dst 之上。</summary>
        private static Rgba AlphaBlend(Rgba src, Rgba dst)
        {
            if (src.A == 255) return src;
            if (src.A == 0) return dst;

            var sa = src.A / 255f;
            var da = dst.A / 255f;
            var outA = sa + da * (1 - sa);
            if (outA <= 0f) return default;

            byte Mix(byte s, byte d)
                => (byte)Math.Clamp((s * sa + d * da * (1 - sa)) / outA + 0.5f, 0f, 255f);

            return new Rgba(Mix(src.R, dst.R), Mix(src.G, dst.G), Mix(src.B, dst.B), (byte)Math.Clamp(outA * 255f + 0.5f, 0f, 255f));
        }

        // ================= 本地文件 =================

        // ================= 头像裁剪 =================

        // 64×64 皮肤里头部「正面底图」与「叠加层（帽子/头发）」的矩形。
        private const int HeadBaseX = 8, HeadBaseY = 8;
        private const int HeadOverlayX = 40, HeadOverlayY = 8;
        private const int HeadSize = 8;

        /// <summary>头像输出边长（与原 MinecraftLaunch.Skin 一致）。</summary>
        private const int HeadOutputSize = 60;

        /// <summary>
        /// 读取图片尺寸（不解码像素）。读不出来时返回 false。
        /// </summary>
        public static bool TryReadImageSize(string path, out int width, out int height)
        {
            width = height = 0;
            try
            {
                return RgbaBitmap.TryReadSize(path, out width, out height);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "读取图片尺寸失败：{Path}", path);
                return false;
            }
        }

        /// <summary>
        /// 校验皮肤文件：必须是 PNG，且尺寸为 64×64（新版）或 64×32（旧版）。
        /// 返回 (是否合法, 失败原因)。
        /// </summary>
        public static (bool Valid, string Error) ValidateSkinFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return (false, "皮肤文件不存在。");

                if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
                    return (false, "皮肤必须是 PNG 图片。");

                if (!TryReadImageSize(path, out var width, out var height))
                    return (false, "无法读取皮肤文件：不是有效的图片。");

                if (width != 64 || (height != 64 && height != 32))
                    return (false, $"皮肤尺寸必须是 64×64 或 64×32，当前为 {width}×{height}。");

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, $"无法读取皮肤文件：{ex.Message}");
            }
        }

        /// <summary>
        /// 把皮肤字节写到本地，并**同步重新裁剪头像与正面立绘**。
        /// 换皮肤后头像会立即跟着更新，不会残留旧头像。
        /// </summary>
        public static void SaveLocalSkin(string uuid, byte[] skinBytes)
        {
            Directory.CreateDirectory(SkinDirectory);

            var full = FullSkinPath(uuid);
            File.WriteAllBytes(full, skinBytes);

            Thread.Sleep(20); // 让文件系统时间戳前进，避免同一 tick 内读取到旧缓存
            TryWriteHead(uuid, skinBytes);
            SaveFrontView(uuid, skinBytes);
        }

        /// <summary>下载线上的皮肤并保存到本地（含重新裁剪头像）。</summary>
        public static async Task DownloadAndSaveAsync(string uuid, string skinUrl)
        {
            var bytes = await Http.GetByteArrayAsync(skinUrl);
            SaveLocalSkin(uuid, bytes);
        }

        /// <summary>
        /// 从完整皮肤字节裁出 60×60 头像并写到指定路径。
        /// 失败时抛出，由调用方决定如何提示（头像裁不出来属于可见功能缺失，不该静默吞掉）。
        /// </summary>
        public static void WriteHeadBitmap(string path, byte[] skinBytes)
        {
            var head = BuildHeadBitmap(skinBytes);
            head.Save(path);
        }

        /// <summary>
        /// 从完整皮肤裁出头部头像（写到 <see cref="HeadSkinPath"/>）。
        /// </summary>
        private static void TryWriteHead(string uuid, byte[] skinBytes)
        {
            try
            {
                WriteHeadBitmap(HeadSkinPath(uuid), skinBytes);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "皮肤头像裁剪失败（完整皮肤已保存）");
                throw new InvalidOperationException("皮肤已保存，但从皮肤裁剪头像失败。", ex);
            }
        }

        /// <summary>
        /// 裁头像：底图取头部正面 8×8（(8,8)–(16,16)），叠加层取 8×8（(40,8)–(48,16)），
        /// 叠加层**只在完全不透明（A=255）时**覆盖底图，再最近邻放大到 60×60。
        ///
        /// <para>
        /// 这套判定与参数是刻意**逐字照搬原 MinecraftLaunch.Skin 的行为**的
        /// （含「叠加层必须 A=255 才生效」这个略显粗糙的规则）。
        /// 目的就是让换库前后生成的头像逐像素一致，不引入任何视觉变化。
        /// </para>
        /// </summary>
        public static RgbaBitmap BuildHeadBitmap(byte[] skinBytes)
        {
            var skin = RgbaBitmap.Decode(skinBytes);

            var merged = new RgbaBitmap(HeadSize, HeadSize);
            for (var y = 0; y < HeadSize; y++)
            {
                for (var x = 0; x < HeadSize; x++)
                {
                    var basePixel = skin.GetPixel(HeadBaseX + x, HeadBaseY + y);
                    var overlayPixel = skin.GetPixel(HeadOverlayX + x, HeadOverlayY + y);

                    merged.SetPixel(x, y, overlayPixel.A == 255 ? overlayPixel : basePixel);
                }
            }

            return merged.ResizeNearest(HeadOutputSize, HeadOutputSize);
        }

        /// <summary>
        /// 读取内置的 Steve 皮肤（编译期资源 Assets/steve.png），作为「默认皮肤」。
        /// 网络不可用、或账户未设置皮肤时用它兜底，保证头像永远有像样的内容。
        /// </summary>
        public static byte[]? TryReadDefaultSteveSkin()
        {
            try
            {
                var uri = new Uri("avares://VibrantbitLauncher.Avalonia/Assets/steve.png");
                using var stream = Avalonia.Platform.AssetLoader.Open(uri);
                if (stream == null) return null;

                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "读取内置默认皮肤失败");
                return null;
            }
        }

        /// <summary>
        /// 把该账户的皮肤恢复成内置默认 Steve（含重新裁剪头像与正面立绘）。
        /// 重置皮肤后调用，避免头像被删掉后只剩一个通用占位图。
        /// </summary>
        public static bool RestoreDefaultSkin(string uuid)
        {
            var bytes = TryReadDefaultSteveSkin();
            if (bytes == null) return false;

            SaveLocalSkin(uuid, bytes);
            return true;
        }

        // ================= 微软账户（Mojang 官方接口） =================

        /// <summary>读到的皮肤信息。</summary>
        public sealed class SkinInfo
        {
            public string SkinUrl { get; init; } = string.Empty;
            public string Variant { get; init; } = VariantClassic;
        }

        /// <summary>
        /// 读取玩家当前皮肤。走 sessionserver，无需鉴权。
        /// 失败（网络不可达、未设置皮肤等）时返回 null。
        /// </summary>
        public static async Task<SkinInfo?> GetPlayerSkinInfoAsync(Guid uuid, CancellationToken token)
        {
            try
            {
                var url = $"https://sessionserver.mojang.com/session/minecraft/profile/{uuid:N}?unsigned=false";
                var json = await Http.GetStringAsync(url, token);

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("properties", out var properties))
                    return null;

                foreach (var prop in properties.EnumerateArray())
                {
                    if (!prop.TryGetProperty("value", out var valueNode))
                        continue;

                    var value = valueNode.GetString();
                    if (string.IsNullOrEmpty(value))
                        continue;

                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                    using var textureDoc = JsonDocument.Parse(decoded);

                    if (!textureDoc.RootElement.TryGetProperty("textures", out var textures)
                        || !textures.TryGetProperty("SKIN", out var skin))
                        continue;

                    var skinUrl = skin.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (string.IsNullOrEmpty(skinUrl))
                        continue;

                    var variant = VariantClassic;
                    if (skin.TryGetProperty("metadata", out var meta)
                        && meta.TryGetProperty("model", out var model)
                        && string.Equals(model.GetString(), "slim", StringComparison.OrdinalIgnoreCase))
                    {
                        variant = VariantSlim;
                    }

                    return new SkinInfo { SkinUrl = skinUrl, Variant = variant };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "读取玩家皮肤失败：{Uuid}", uuid);
            }

            return null;
        }

        /// <summary>上传本地图片作为微软账户皮肤（multipart）。成功后服务器返回空负载。</summary>
        public static Task UploadSkinAsync(MicrosoftAccount account, string filePath, string variant)
            => UploadSkinAsync(account, File.ReadAllBytes(filePath), variant);

        /// <summary>
        /// 上传本地图片字节作为微软账户皮肤（multipart）。
        ///
        /// <para>
        /// 严格对齐官方文档的 curl 写法：
        /// <c>curl -X POST -H "Authorization: Bearer &lt;token&gt;" -F variant=classic
        /// -F file="@skin.png;type=image/png" https://api.minecraftservices.com/minecraft/profile/skins</c>
        /// </para>
        ///
        /// <para>
        /// 手工构造每个 part 的 <c>Content-Disposition</c> / <c>Content-Type</c>，
        /// 是为了让发出的报文和上面的 curl **逐字节等价**：
        /// </para>
        /// <list type="bullet">
        /// <item><c>name</c> 必须带引号（<c>name="variant"</c>）；用 <c>MultipartFormDataContent.Add(content, name)</c>
        /// 的默认写法会生成**不带引号**的 <c>name=variant</c>，严格的网关可能拒收。</item>
        /// <item><c>variant</c> 的 <c>Content-Type</c> 固定为 <c>text/plain</c>，**不能带 <c>charset</c>**
        /// （<c>StringContent</c> 默认会附加 <c>charset=utf-8</c>）。</item>
        /// <item><c>file</c> 的 <c>filename</c> 用普通引号写法，不带 <c>filename*=utf-8''</c> 扩展参数。</item>
        /// </list>
        /// </summary>
        public static async Task UploadSkinAsync(MicrosoftAccount account, byte[] skinBytes, string variant)
        {
            EnsureToken(account);

            using var request = new HttpRequestMessage(
                HttpMethod.Post, "https://api.minecraftservices.com/minecraft/profile/skins");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

            var content = new MultipartFormDataContent();

            // 第一部分：variant（classic / slim）
            var variantContent = new ByteArrayContent(Encoding.UTF8.GetBytes(NormalizeVariant(variant)));
            variantContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            variantContent.Headers.ContentDisposition =
                new ContentDispositionHeaderValue("form-data") { Name = "\"variant\"" };
            content.Add(variantContent);

            // 第二部分：file（PNG 图片数据）
            var fileContent = new ByteArrayContent(skinBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            fileContent.Headers.ContentDisposition =
                new ContentDispositionHeaderValue("form-data") { Name = "\"file\"", FileName = "\"skin.png\"" };
            content.Add(fileContent);

            request.Content = content;

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await DescribeFailureAsync(response));
        }

        /// <summary>按图片 URL 设置微软账户皮肤（JSON 负载）。</summary>
        public static async Task SetSkinByUrlAsync(MicrosoftAccount account, string skinUrl, string variant)
        {
            EnsureToken(account);

            using var request = new HttpRequestMessage(
                HttpMethod.Post, "https://api.minecraftservices.com/minecraft/profile/skins");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

            var payload = JsonSerializer.Serialize(new { variant = NormalizeVariant(variant), url = skinUrl });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await DescribeFailureAsync(response));
        }

        /// <summary>把微软账户皮肤重置为官方默认（DELETE .../skins/active）。</summary>
        public static async Task ResetSkinAsync(MicrosoftAccount account)
        {
            EnsureToken(account);

            using var request = new HttpRequestMessage(
                HttpMethod.Delete, "https://api.minecraftservices.com/minecraft/profile/skins/active");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await DescribeFailureAsync(response));
        }

        private static void EnsureToken(MicrosoftAccount account)
        {
            if (account is null || string.IsNullOrWhiteSpace(account.AccessToken))
                throw new InvalidOperationException("账户缺少有效的访问令牌，请重新登录该微软账户。");
        }

        private static string NormalizeVariant(string variant)
            => string.Equals(variant, VariantSlim, StringComparison.OrdinalIgnoreCase) ? VariantSlim : VariantClassic;

        private static async Task<string> DescribeFailureAsync(HttpResponseMessage response)
        {
            var text = (int)response.StatusCode switch
            {
                401 => "访问令牌已失效，请重新登录该微软账户。",
                403 => "该账户没有更换皮肤的权限（可能需要拥有 Minecraft Java 版，或账号受家长控制限制）。",
                404 => "该微软账户下没有找到 Minecraft 档案（可能尚未购买 Java 版）。",
                429 => "请求过于频繁，请稍后再试。",
                _ => $"服务器返回 {(int)response.StatusCode}。"
            };

            try
            {
                var body = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(body) && body.Length <= 200)
                    text += $"\n{body}";
            }
            catch { /* 读不到 body 不影响主提示 */ }

            return text;
        }
    }
}
