using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
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

        /// <summary>正在拉 Minecraft 版本列表（防止初始化与自动刷新同时发起）。</summary>
        private bool _versionsLoading;

        /// <summary>
        /// 最近一次「发起数据加载」的时刻，用于自动刷新的节流。
        ///
        /// 页面刚建好时会立刻完成一次初始化加载，紧接着「切入页面」的刷新请求就到了 ——
        /// 靠这个时间戳把那次重复请求挡掉，只在距上次加载超过 <see cref="RefreshThrottleMs"/>
        /// 时才真的重新拉。**在加载开始时打戳**（而不是结束时），因为请求是异步的，
        /// 用开始时刻才能挡住加载期间冒出来的重复请求。
        /// </summary>
        private DateTime _lastDataLoadUtc = DateTime.MinValue;

        /// <summary>
        /// 自动刷新的最小间隔（毫秒）。
        /// 取 800ms：既能挡住「初始化刚结束 / 刚改完筛选」这类紧随其后的重复请求，
        /// 又不会把「启动后 1.3 秒的刷新」误伤掉（那时距最初的初始化已超过 800ms）。
        /// </summary>
        private const int RefreshThrottleMs = 800;

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

        private bool _hasError;
        private string _errorText = string.Empty;

        /// <summary>
        /// 上一次加载是否失败。
        ///
        /// 页面据此把空状态文案从「没有匹配的资源」换成「加载失败 + 重试」——
        /// 这两种情况此前共用一句「没有匹配的资源」，用户在网络不通时会被误导成
        /// 「筛选条件太严」而反复改筛选，永远试不出来。
        /// </summary>
        public bool HasError
        {
            get => _hasError;
            private set => SetProperty(ref _hasError, value);
        }

        /// <summary>加载失败的原因（已本地化，可直接显示）。</summary>
        public string ErrorText
        {
            get => _errorText;
            private set => SetProperty(ref _errorText, value);
        }

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
            await LoadMinecraftVersionsAsync();

            _isInitialized = true;
            RebuildTags();

            // 初始化期间若被 SetProjectType 改过类型，这里不要覆盖
            if (_projectTypeChangedDuringInit)
                _projectTypeChangedDuringInit = false;

            await ApplyFilterAsync();
        }

        /// <summary>
        /// 拉取（或补齐）Minecraft 版本列表。
        /// 抽成独立方法是为了让「自动刷新」也能复用 —— 原来这段只跑在构造时的初始化里，
        /// 页面开着的时候用户永远看不到新发布的版本。
        /// </summary>
        private async Task LoadMinecraftVersionsAsync()
        {
            // 初始化与自动刷新可能撞在一起（页面刚建好就被要求刷新），同一次拉取共用
            if (_versionsLoading)
                return;

            _versionsLoading = true;
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
            catch (OperationCanceledException ex)
            {
                // 取消 = 请求超时或程序正在退出（用户关窗口时在飞的 HTTPS 会被掐断，
                // 表现为 SocketException 995 / TaskCanceledException）。这是正常收尾，
                // 记 Warning 只会在日志里留下一段看着像崩溃的堆栈，降为 Debug。
                Serilog.Log.Debug(ex, "下载中心：Minecraft 版本列表加载被取消（超时或程序退出）");
            }
            catch (Exception ex)
            {
                // 版本清单拉不到**不影响资源搜索**（搜索不依赖它，版本筛选默认是「任意」），
                // 所以这里只记日志、不打断流程。原先是静默 catch，出问题时完全无线索。
                Serilog.Log.Warning(ex, "下载中心：Minecraft 版本列表加载失败（不影响资源搜索）");
            }
            finally
            {
                _versionsLoading = false;
            }
        }

        /// <summary>
        /// 刷新「Minecraft 版本列表 + 当前分类的资源列表」。
        ///
        /// <para>
        /// 两个调用时机：① 程序运行后 1.3 秒的启动预热（主窗口广播消息）；
        /// ② 每次切入本页（从别的页面进来、或在下载中心里切回这一分类）。
        /// </para>
        ///
        /// <para>
        /// 带 1 秒节流：这两个时机可能几乎同时发生（比如启动后立刻点开下载中心），
        /// 合并成一次就不会同一秒里发两遍请求。
        /// </para>
        /// </summary>
        public async Task RefreshAllAsync()
        {
            if ((DateTime.UtcNow - _lastDataLoadUtc).TotalMilliseconds < RefreshThrottleMs)
                return;

            // 记一条：刷新是低频事件（启动一次 + 每次进页面 / 切分类一次），
            // 出问题时这一条能直接证明「刷新到底有没有发生」。
            Serilog.Log.Information("下载页刷新：分类={Type}", _projectType);

            // 版本列表与资源列表互不依赖，**并行**拉 ——
            // 版本清单是一条可能重试好几轮的 HTTPS（弱网下十几秒），串行会把资源列表一起拖住。
            var versionsTask = LoadMinecraftVersionsAsync();

            // 首次初始化还在跑时不要插手：它自己会把资源列表拉好
            if (_isInitialized)
                await ApplyFilterAsync();

            await versionsTask;
        }

        /// <summary>按当前搜索词与筛选条件重新拉取第一页（会清空列表）。</summary>
        private async Task ApplyFilterAsync()
        {
            // 在开头打戳：这次加载「刚发生过」，紧随其后的自动刷新请求应当被跳过。
            _lastDataLoadUtc = DateTime.UtcNow;

            // 只有 CurseForge 作为"来源"时 Modrinth 接口无能为力，直接给出空列表 + 提示
            if (SelectedSource == SourceCurseForge)
            {
                HasError = false;
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
                HasError = false;
            }
            catch (OperationCanceledException)
            {
                // 被更新的请求顶掉，或者 HttpClient 超时，都属于正常收尾，静默
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested)
                    return;

                // ⚠️ 这里**不清空**已有列表。
                //
                // 原实现失败时 `modrinthMods.Clear()`，于是「本来能看到内容，网络抖一下整页变空」，
                // 而且空状态文案还是「没有匹配的资源」——用户会以为是筛选条件的问题。
                // 现在保留旧内容，只把「加载失败 + 原因」透出来（见 HasError / ErrorText）。
                HasError = true;
                ErrorText = DescribeLoadError(ex);
                Serilog.Log.Warning(ex, "加载社区资源失败：分类={Type} 关键词={Query}", _projectType, _lastSearchText);

                if (modrinthMods.Count == 0)
                {
                    TotalHits = 0;
                    _nextOffset = 0;
                }
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

        /// <summary>把加载异常翻成一句能直接显示、且带下一步动作的话。</summary>
        private static string DescribeLoadError(Exception ex)
        {
            // ModrinthException 的文案里已经写好了原因与做法
            if (ex is ModrinthException)
                return ex.Message;

            if (ex is AggregateException agg && agg.InnerException is not null)
                return DescribeLoadError(agg.InnerException);

            if (ex is OperationCanceledException or TimeoutException)
                return "连接 Modrinth 超时。Modrinth 服务器在境外，国内网络通常需要开启代理后重试。";

            if (ex is System.Net.Http.HttpRequestException)
                return "无法连接 Modrinth。请检查网络或开启代理后重试。";

            if (ex is JsonException)
                return "Modrinth 返回的数据不完整（响应被中途截断），通常是网络不稳，请重试。";

            return $"加载资源失败：{ex.Message}";
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
            var added = new List<ModrinthMod>();

            foreach (var hit in hits)
            {
                var item = new ModrinthMod
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
                };

                modrinthMods.Add(item);
                added.Add(item);
            }

            // 后台逐条翻译英文简介：不 await，列表立刻可见，译文回来后自动替换。
            if (added.Count > 0)
                _ = TranslateDescriptionsAsync(added);
        }

        /// <summary>
        /// 把一批卡片的英文简介翻成中文并回填。
        ///
        /// <para>
        /// 串行是刻意的：免费翻译端点对并发很敏感，一次打二十个请求只会换来一片 429。
        /// 串行的另一个好处是译文会「从上往下」陆续出现，看起来像在逐条加载，而不是某一刻整屏跳变。
        /// </para>
        /// </summary>
        private static async Task TranslateDescriptionsAsync(IReadOnlyList<ModrinthMod> items)
        {
            foreach (var item in items)
            {
                if (!TranslateService.LooksEnglish(item.Description))
                    continue;

                try
                {
                    var translated = await TranslateService.TranslateAsync(item.Description).ConfigureAwait(true);
                    if (!string.IsNullOrWhiteSpace(translated))
                        item.SetTranslatedDescription(translated);
                }
                catch (Exception ex)
                {
                    // 翻译是锦上添花，任何异常都不该冒到界面上
                    Serilog.Log.Debug(ex, "翻译资源简介失败：{Name}", item.Name);
                }
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

        /// <summary>Modrinth 给的原始简介（英文居多）。</summary>
        public string Description { get; set; } = string.Empty;

        private string? _translatedDescription;

        /// <summary>
        /// 卡片上真正显示的简介：翻好了显示中文，没翻好就是原文。
        /// 翻译在列表加载后后台跑，不阻塞首屏。
        /// </summary>
        public string DisplayDescription => _translatedDescription ?? Description;

        /// <summary>是否已经翻译过（界面据此显示「已翻译」小标记）。</summary>
        public bool IsTranslated => _translatedDescription is not null;

        /// <summary>回填译文（只在后台翻译成功后调用）。</summary>
        public void SetTranslatedDescription(string translated)
        {
            _translatedDescription = translated;
            OnPropertyChanged(nameof(DisplayDescription));
            OnPropertyChanged(nameof(IsTranslated));
        }

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
