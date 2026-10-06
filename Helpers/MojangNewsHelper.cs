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

        public static async Task<List<NewsItem>> GetNewsListAsync()
        {
            using HttpClient httpClient = new HttpClient();
            string json = await httpClient.GetStringAsync(NewsUrl);     

            // 配置解析选项
            var jsonOptions = new JsonSerializerOptions
            {
                // 允许大小写宽松匹配（可选）
                PropertyNameCaseInsensitive = true
            };

            // 反序列化根对象
            var root = JsonSerializer.Deserialize<NewsRoot>(json, jsonOptions);

            // 循环填充 ImagePath
            foreach (var item in root.Entries)
            {
                item.ResolveImageUrl();
            }

            return root.Entries;
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
