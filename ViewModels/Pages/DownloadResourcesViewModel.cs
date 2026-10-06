using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.ViewModels.Pages
{
    /// <summary>
    /// 社区资源浏览器（下载中心右侧内容区）。
    ///
    /// 版式对齐参考界面：顶部搜索框 + 搜索按钮；下面一行筛选（来源 / 标签 / 排序方式 / 刷新）
    /// 与第二行筛选（版本 / 加载器 / 「已加载 N / 共 M 条」计数）；下面是资源卡片列表。
    /// 分类切换由 <see cref="SetProjectType"/> 驱动，换类型会重置筛选并重新拉取。
    ///
    /// 加载策略：**增量分页**。首屏只拉一页（<see cref="PageSize"/> 条），
    /// 列表滚到底部时再由页面调用 <see cref="LoadMoreCommand"/> 追加下一页，
    /// 避免一次性把成千上万条灌进列表造成卡顿。
    /// </summary>
    public class DownloadResourcesViewModel : ObservableObject
    {
        /// <summary>每页拉取的条数，与计数文案里的「已加载 N 条」共用一个来源。</summary>
        private const int PageSize = 20;

        private string _projectType = ModrinthSearchService.TypeMod;
        private readonly ObservableCollection<ModrinthMod> modrinthMods = new();

        // 筛选
        private string _selectedSource = SourceModrinth;
        private string _selectedTag = "全部";
        private string _selectedSort = DefaultSort;
        private string _selectedVersion = "任意";
        private string _selectedLoader = "不限";
        private string _searchText = string.Empty;
        private string _lastSearchText = string.Empty;
        private long _totalHits;
        private bool _isLoading;
        private bool _isLoadingMore;
        private bool _isInitialized;

        /// <summary>下一页的起始偏移（已成功加载的条数）。</summary>
        private int _nextOffset;

        /// <summary>取消上一次请求：换分类/改筛选时旧请求的结果不该覆盖新结果。</summary>
        private CancellationTokenSource? _searchCts;

        /// <summary>
        /// 初始化期间是否有过 SetProjectType。
        ///
        /// InitializeAsync 是异步的：构造页面 → SetProjectType("modpack") 时，
        /// 版本列表可能还在 await 中。若初始化结束时无条件按当前 _projectType 重跑一次，
        /// 就会把刚设置好的类型再覆盖回默认值 —— 表现就是"切了分类但列表没变"。
        /// </summary>
        private bool _projectTypeChangedDuringInit;

        // ── 下拉候选项（文案对齐参考界面） ──

        public const string SourceModrinth = "Modrinth";
        public const string SourceCurseForge = "CurseForge";
        private const string DefaultSort = "默认";

        public ObservableCollection<string> Sources { get; } = new() { SourceModrinth, SourceCurseForge };

        /// <summary>标签可选项：随分类变化（SetProjectType 里重建）。</summary>
        public ObservableCollection<string> Tags { get; } = new() { "全部" };

        public ObservableCollection<string> Sorts { get; } = new()
        {
            DefaultSort, "下载量", "关注数", "最近更新", "最新发布"
        };

        /// <summary>可选 Minecraft 版本（异步从 VanillaInstaller 加载）。</summary>
        public ObservableCollection<string> MinecraftVersions { get; } = new() { "任意" };

        /// <summary>可选模组加载器。</summary>
        public ObservableCollection<string> Loaders { get; } = new()
        {
            "不限", "fabric", "forge", "quilt", "neoforge"
        };

        public ObservableCollection<ModrinthMod> ModrinthMods => modrinthMods;

        /// <summary>当前分类对应的 Modrinth 项目类型（mod / modpack / resourcepack / shader / datapack）。</summary>
        public string ProjectType => _projectType;

        public string SearchText
        {
            get => _searchText;
            set => SetProperty(ref _searchText, value);
        }

        public string SelectedSource
        {
            get => _selectedSource;
            set => SetProperty(ref _selectedSource, value);
        }

        public string SelectedTag
        {
            get => _selectedTag;
            set
            {
                if (SetProperty(ref _selectedTag, value) && _isInitialized)
                    _ = ApplyFilterAsync();
            }
        }

        public string SelectedSort
        {
            get => _selectedSort;
            set
            {
                if (SetProperty(ref _selectedSort, value) && _isInitialized)
                    _ = ApplyFilterAsync();
            }
        }

        public string SelectedVersion
        {
            get => _selectedVersion;
            set
            {
                if (SetProperty(ref _selectedVersion, value) && _isInitialized)
                    _ = ApplyFilterAsync();
            }
        }

        public string SelectedLoader
        {
            get => _selectedLoader;
            set
            {
                if (SetProperty(ref _selectedLoader, value) && _isInitialized)
                    _ = ApplyFilterAsync();
            }
        }

        public long TotalHits
        {
            get => _totalHits;
            private set
            {
                if (SetProperty(ref _totalHits, value))
                {
                    OnPropertyChanged(nameof(CountText));
                    OnPropertyChanged(nameof(CanLoadMore));
                }
            }
        }

        /// <summary>「已加载 20 / 共 76724 条」。列表是增量加载的，所以报「已加载」而不是「显示前」。</summary>
        public string CountText => $"已加载 {modrinthMods.Count} / 共 {TotalHits} 条";

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        /// <summary>正在追加下一页（列表底部显示「正在加载更多…」）。</summary>
        public bool IsLoadingMore
        {
            get => _isLoadingMore;
            private set => SetProperty(ref _isLoadingMore, value);
        }

        /// <summary>还有下一页可加载（已加载条数 < 命中总数）。</summary>
        public bool CanLoadMore => modrinthMods.Count < TotalHits;

        public RelayCommand SearchCommand { get; }
        public RelayCommand RefreshCommand { get; }
        public RelayCommand LoadMoreCommand { get; }

        public DownloadResourcesViewModel()
        {
            // 追加/清空条目时同步刷新计数文案与「还有下一页」状态
            modrinthMods.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(CountText));
                OnPropertyChanged(nameof(CanLoadMore));
            };

            SearchCommand = new RelayCommand(() =>
            {
                _lastSearchText = SearchText ?? string.Empty;
                _ = ApplyFilterAsync();
            });

            RefreshCommand = new RelayCommand(() => _ = ApplyFilterAsync());

            // 滚动到底部时由页面触发；正在加载 / 已到末尾时直接忽略
            LoadMoreCommand = new RelayCommand(() => _ = LoadMoreAsync());

            _ = InitializeAsync();
        }

        /// <summary>切换分类：换类型并重新拉一遍列表。</summary>
        public void SetProjectType(string projectType)
        {
            // 即使类型相同也要保证筛选/列表与目标类型一致（首次进入时 _projectType 就是 mod，
            // 早期 return 会让"模组"分类拿不到 RebuildTags 的结果）。
            if (!_isInitialized)
            {
                _projectType = projectType;
                _projectTypeChangedDuringInit = true;
                return;
            }

            if (string.Equals(_projectType, projectType, StringComparison.OrdinalIgnoreCase))
                return;

            _projectType = projectType;
            _lastSearchText = string.Empty;
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));

            RebuildTags();
            modrinthMods.Clear();
            _nextOffset = 0;
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(CanLoadMore));

            _ = ApplyFilterAsync();
        }

        /// <summary>
        /// 标签候选项随分类变化：整合包/资源包/光影的可用分类和 mod 完全不同，
        /// 沿用固定列表会出现"选了必然 0 结果"的选项。
        /// </summary>
        private void RebuildTags()
        {
            Tags.Clear();
            Tags.Add("全部");

            foreach (var tag in TagsFor(_projectType))
                Tags.Add(tag);

            _selectedTag = "全部";
            OnPropertyChanged(nameof(SelectedTag));
        }

        private static IEnumerable<string> TagsFor(string projectType) => projectType switch
        {
            ModrinthSearchService.TypeModpack => new[]
            {
                "adventure", "challenging", "combat", "kitchen-sink", "lightweight",
                "magic", "multiplayer", "optimization", "quests", "technology"
            },
            ModrinthSearchService.TypeResourcePack => new[]
            {
                "16x", "32x", "64x", "128x", "256x", "512x+",
                "audio", "blocks", "core-shaders", "decoration", "fonts",
                "gui", "items", "locale", "modded", "models", "realistic", "themed", "vanilla-like"
            },
            ModrinthSearchService.TypeShader => new[]
            {
                "atmosphere", "bloom", "cartoon", "colored-lighting", "fantasy",
                "foliage", "low", "medium", "high", "path-tracing", "pbr",
                "realistic", "reflections", "semi-realistic", "shadows", "vanilla-like"
            },
            ModrinthSearchService.TypeDatapack => new[]
            {
                "adventure", "cursed", "decoration", "economy", "equipment",
                "game-mechanics", "library", "magic", "mobs", "optimization",
                "social", "storage", "technology", "utility", "worldgen"
            },
            _ => new[]
            {
                "adventure", "decoration", "economy", "equipment", "food", "game-mechanics",
                "library", "magic", "management", "minigame", "mobs", "optimization",
                "social", "storage", "technology", "transportation", "utility", "worldgen"
            }
        };

        /// <summary>排序方式文案 → Modrinth 的 index 参数。</summary>
        private string SortIndex => SelectedSort switch
        {
            "下载量" => "downloads",
            "关注数" => "follows",
            "最近更新" => "updated",
            "最新发布" => "newest",
            _ => "relevance"
        };

        /// <summary>异步初始化：后台获取 Minecraft 版本列表，然后加载资源。</summary>
        private async Task InitializeAsync()
        {
            try
            {
                var versions = await Task.Run(async () =>
                {
                    var entries = await MinecraftVersionCache.GetAsync();
                    return entries.Select(v => v.Id).ToList();
                });

                foreach (var v in versions)
                {
                    if (!MinecraftVersions.Contains(v))
                        MinecraftVersions.Add(v);
                }
            }
            catch
            {
                // 版本加载失败不影响主功能
            }

            _isInitialized = true;
            RebuildTags();

            // 初始化期间若被 SetProjectType 改过类型，这里不要覆盖
            if (_projectTypeChangedDuringInit)
                _projectTypeChangedDuringInit = false;

            await ApplyFilterAsync();
        }

        /// <summary>按当前搜索词与筛选条件重新拉取第一页（会清空列表）。</summary>
        private async Task ApplyFilterAsync()
        {
            // 只有 CurseForge 作为"来源"时 Modrinth 接口无能为力，直接给出空列表 + 提示
            if (SelectedSource == SourceCurseForge)
            {
                modrinthMods.Clear();
                TotalHits = 0;
                _nextOffset = 0;
                OnPropertyChanged(nameof(CountText));
                OnPropertyChanged(nameof(CanLoadMore));
                return;
            }

            _searchCts?.Cancel();
            var cts = new CancellationTokenSource();
            _searchCts = cts;

            IsLoading = true;
            try
            {
                var result = await SearchPageAsync(0, cts.Token);

                if (cts.IsCancellationRequested)
                    return;

                // 先接管总数，再清空并填入第一页
                TotalHits = result.TotalHits;
                modrinthMods.Clear();
                AppendHits(result.Hits);
                _nextOffset = modrinthMods.Count;
            }
            catch (OperationCanceledException)
            {
                // 被更新的请求顶掉，或者 HttpClient 超时，都属于正常收尾，静默
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested)
                    return;

                modrinthMods.Clear();
                TotalHits = 0;
                _nextOffset = 0;
                await (AppServices.Dialogs?.ShowErrorAsync("错误", $"加载资源失败：{ex.Message}") ?? Task.CompletedTask);
            }
            finally
            {
                if (ReferenceEquals(_searchCts, cts))
                {
                    IsLoading = false;
                    OnPropertyChanged(nameof(CountText));
                    OnPropertyChanged(nameof(CanLoadMore));
                }
            }
        }

        /// <summary>
        /// 追加下一页（滚动到底部时调用）。
        /// 与首屏共用同一个 CancellationTokenSource 家族：切换筛选会让页码归零，
        /// 旧的"加载更多"结果因为 offset 对不上而被丢弃。
        /// </summary>
        private async Task LoadMoreAsync()
        {
            if (IsLoading || IsLoadingMore || !CanLoadMore)
                return;

            var cts = _searchCts;
            if (cts is null || cts.IsCancellationRequested)
                return;

            var offsetAtRequest = _nextOffset;
            IsLoadingMore = true;
            try
            {
                var result = await SearchPageAsync(offsetAtRequest, cts.Token);

                // 期间用户改了筛选 / 换了分类 → 这次结果作废
                if (cts.IsCancellationRequested || offsetAtRequest != _nextOffset)
                    return;

                AppendHits(result.Hits);
                _nextOffset = modrinthMods.Count;

                // 某些筛选下 total_hits 未必精确，以最后一页实际返回条数为准
                if (result.Hits.Count < PageSize)
                    TotalHits = modrinthMods.Count;
            }
            catch (OperationCanceledException)
            {
                // 正常收尾
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested)
                    Serilog.Log.Warning(ex, "加载更多资源失败");
            }
            finally
            {
                IsLoadingMore = false;
                OnPropertyChanged(nameof(CountText));
                OnPropertyChanged(nameof(CanLoadMore));
            }
        }

        /// <summary>拉取指定偏移的一页。</summary>
        private Task<ModrinthSearchService.SearchResult> SearchPageAsync(int offset, CancellationToken token)
            => ModrinthSearchService.SearchAsync(
                _projectType,
                string.IsNullOrWhiteSpace(_lastSearchText) ? null : _lastSearchText,
                SelectedVersion == "任意" ? null : SelectedVersion,
                SelectedLoader == "不限" ? null : SelectedLoader,
                SelectedTag == "全部" ? null : SelectedTag,
                PageSize,
                SortIndex,
                offset,
                cancellationToken: token);

        /// <summary>把一页搜索结果追加到列表末尾。</summary>
        private void AppendHits(IEnumerable<ModrinthSearchService.Hit> hits)
        {
            foreach (var hit in hits)
            {
                modrinthMods.Add(new ModrinthMod
                {
                    Name = hit.Title,
                    Author = hit.Author,
                    Description = hit.Description,
                    ImagePath = hit.IconUrl ?? string.Empty,
                    ProjectId = hit.ProjectId,
                    Downloads = FormatCount(hit.Downloads),
                    UpdatedText = FormatRelativeTime(hit.DateModified),
                    Tags = string.Join(" · ", BuildTags(hit)),
                    Source = SourceModrinth
                });
            }
        }

        /// <summary>
        /// 卡片上的标签行：分类 · 加载器 · 最新支持的版本。
        /// 参考界面的排布是「分类 · 加载器 版本+」，这里合成一条字符串，
        /// 用 TextBlock 直接显示，比给每张卡片建一个 ItemsControl 便宜得多。
        /// </summary>
        private static IEnumerable<string> BuildTags(ModrinthSearchService.Hit hit)
        {
            var categories = (hit.DisplayCategories.Count > 0 ? hit.DisplayCategories : hit.Categories)
                .Where(c => !IsLoader(c))
                .Distinct()
                .ToList();

            foreach (var category in categories.Take(2))
                yield return category;

            var loaders = hit.Categories.Where(IsLoader).Distinct().ToList();
            if (loaders.Count > 0)
                yield return string.Join(" / ", loaders);

            var latest = LatestVersion(hit.Versions);
            if (!string.IsNullOrEmpty(latest))
                yield return latest + "+";
        }

        private static bool IsLoader(string value) =>
            value is "fabric" or "forge" or "quilt" or "neoforge";

        /// <summary>取最新支持的正式版本号：过滤快照/预发布，再按版本号排序取最大。</summary>
        private static string LatestVersion(List<string> versions)
        {
            var stable = versions
                .Where(v => !v.Contains("snapshot", StringComparison.OrdinalIgnoreCase)
                            && !v.Contains("pre", StringComparison.OrdinalIgnoreCase)
                            && !v.Contains("w", StringComparison.OrdinalIgnoreCase)
                            && !v.Contains("rc", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var pool = stable.Count > 0 ? stable : versions;
            if (pool.Count == 0)
                return string.Empty;

            return pool.OrderByDescending(v => Version.TryParse(Normalize(v), out var parsed) ? parsed : new Version()).First();
        }

        /// <summary>把 "1.21" / "1.21.4" 补成 <see cref="Version"/> 能解析的形式（1.21 → 1.21.0）。</summary>
        private static string Normalize(string version)
        {
            var parts = version.Split('.');
            return parts.Length switch
            {
                0 => "0.0",
                1 => version + ".0.0",
                2 => version + ".0",
                _ => version
            };
        }

        /// <summary>1.75 亿 / 2.68 亿 这类中文计数，与参考界面一致。</summary>
        private static string FormatCount(long value)
        {
            if (value >= 100_000_000)
                return (value / 100_000_000d).ToString("0.##") + " 亿";
            if (value >= 10_000)
                return (value / 10_000d).ToString("0.#") + " 万";
            return value.ToString();
        }

        /// <summary>相对时间：5 天前 / 3 个月前 / 1 年前。</summary>
        private static string FormatRelativeTime(DateTimeOffset? time)
        {
            if (time is null)
                return string.Empty;

            var span = DateTimeOffset.UtcNow - time.Value;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
            if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)} 个月前";
            return $"{(int)(span.TotalDays / 365)} 年前";
        }
    }

    /// <summary>列表卡片的数据（一行资源）。</summary>
    public class ModrinthMod : ObservableObject
    {
        private string _imagePath = string.Empty;
        private Avalonia.Media.IImage? _icon;

        public string Name { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>项目图标的远程 URL（来自 Modrinth 搜索结果）。</summary>
        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (!SetProperty(ref _imagePath, value))
                    return;

                // 地址变化时异步拉取位图；界面绑的是 Icon，不是这个字符串。
                _ = LoadIconAsync(value);
            }
        }

        /// <summary>供 <c>Image.Source</c> 绑定的位图。URL 未设 / 加载失败时为 null（图区留白）。</summary>
        public Avalonia.Media.IImage? Icon
        {
            get => _icon;
            private set => SetProperty(ref _icon, value);
        }

        private async Task LoadIconAsync(string url)
        {
            var bitmap = await Helpers.RemoteImageLoader.LoadAsync(url).ConfigureAwait(true);
            // 期间地址又被改掉（列表复用）时丢弃这次结果，避免串图
            if (!string.Equals(_imagePath, url, StringComparison.OrdinalIgnoreCase))
                return;
            Icon = bitmap;
        }

        public string ProjectId { get; set; } = string.Empty;
        public string Downloads { get; set; } = string.Empty;
        public string UpdatedText { get; set; } = string.Empty;
        public string Tags { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }
}
