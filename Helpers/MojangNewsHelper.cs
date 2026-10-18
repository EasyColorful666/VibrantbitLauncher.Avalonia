using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using VibrantbitLauncher.ViewModels.Pages;

namespace VibrantbitLauncher.Helpers
{
    public class MojangNewsHelper
    {
        private const string NewsUrl = "https://launchercontent.mojang.com/v2/news.json";

        /// <summary>重试次数。新闻挂了不影响主功能，但一次抖动不该让首页空着。</summary>
        private const int MaxAttempts = 3;

        /// <summary>新闻 JSON 单次请求的超时（约 200 KB，20 秒足够；卡住就交给重试）。</summary>
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        /// <summary>
        /// 拉取启动器新闻。
        ///
        /// 以前是「裸 <c>GetStringAsync</c> + <c>Deserialize</c>」：没有重试、超时用的是
        /// HttpClient 默认的 100 秒，而且 <c>root</c> 为 null 时会在 <c>root.Entries</c> 上直接空引用。
        /// 现在补上重试与空数据保护；彻底失败时抛一句能看懂的话（调用方只做日志，不弹窗）。
        /// </summary>
        public static async Task<List<NewsItem>> GetNewsListAsync()
        {
            var jsonOptions = new JsonSerializerOptions
            {
                // 允许大小写宽松匹配（可选）
                PropertyNameCaseInsensitive = true
            };

            Exception? last = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    var json = await Http.GetStringAsync(NewsUrl);
                    var root = JsonSerializer.Deserialize<NewsRoot>(json, jsonOptions);

                    if (root?.Entries is not { Count: > 0 })
                        throw new InvalidOperationException("新闻数据为空或格式不符");

                    // 循环填充 ImagePath
                    foreach (var item in root.Entries)
                        item.ResolveImageUrl();

                    return root.Entries;
                }
                // 连 TaskCanceledException 一起重试：HttpClient 的超时也是「取消」，
                // 而弱网下超时恰恰是最常见的瞬时失败。
                catch (Exception ex)
                {
                    last = ex;
                    Serilog.Log.Warning(ex, "获取新闻列表失败（第 {Attempt}/{Max} 次）", attempt, MaxAttempts);

                    if (attempt < MaxAttempts)
                        await Task.Delay(400 * attempt);
                }
            }

            throw new HttpRequestException($"获取新闻列表失败（{FriendlyError.Describe(last)}）", last);
        }
    }
    // 根对象
    public class NewsRoot
    {
        [JsonPropertyName("entries")]
        public List<NewsItem> Entries { get; set; } = new();
    }

    // 单条新闻主体
    public class NewsItem : System.ComponentModel.INotifyPropertyChanged
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("date")]
        public string Date { get; set; } = string.Empty;

        // 你需要的统一图片地址，不参与序列化，运行时赋值
        private string _imagePath;
        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (_imagePath == value)
                    return;
                _imagePath = value;
                OnPropertyChanged(nameof(ImagePath));
                _ = LoadImageAsync(value);
            }
        }

        // 供 Image.Source 绑定的位图：Avalonia 不能把 http URL 字符串直接当图片来源，
        // 必须后台下载解码成 Bitmap（见 RemoteImageLoader）。
        private Avalonia.Media.IImage? _image;
        public Avalonia.Media.IImage? Image
        {
            get => _image;
            private set
            {
                if (ReferenceEquals(_image, value))
                    return;
                _image = value;
                OnPropertyChanged(nameof(Image));
            }
        }

        private async System.Threading.Tasks.Task LoadImageAsync(string url)
        {
            var bitmap = await RemoteImageLoader.LoadAsync(url).ConfigureAwait(true);
            if (!string.Equals(_imagePath, url, StringComparison.OrdinalIgnoreCase))
                return;
            Image = bitmap;
        }

        // 内部图片节点
        [JsonPropertyName("newsPageImage")]
        public NewsImage? NewsPageImage { get; set; }

        [JsonPropertyName("playPageImage")]
        public NewsImage? PlayPageImage { get; set; }

        // 解析完成后调用，自动拼接 ImagePath
        public void ResolveImageUrl()
        {
            if (NewsPageImage != null && !string.IsNullOrEmpty(NewsPageImage.Url))
            {
                ImagePath = "https://launchercontent.mojang.com" + NewsPageImage.Url;
            }
            else if (PlayPageImage != null && !string.IsNullOrEmpty(PlayPageImage.Url))
            {
                ImagePath = "https://launchercontent.mojang.com" + PlayPageImage.Url;
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    // 图片子模型
    public class NewsImage
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }
}
