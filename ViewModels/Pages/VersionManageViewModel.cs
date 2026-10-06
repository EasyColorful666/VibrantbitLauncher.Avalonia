using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.ViewModels.Pages
{
    /// <summary>
    /// 版本管理：管理某个版本「隔离目录」下的模组与世界（存档）。
    ///
    /// 目录约定必须与启动逻辑（RunPageViewModel.LaunchMinecraftManually）一致：
    /// 启动时固定传 <c>--gameDir &lt;mcFolder&gt;\versions\&lt;版本号&gt;</c>，
    /// 游戏只读写该目录下的 mods / saves / config / logs。
    /// 所以管理页也只能指向这里 —— 否则「管理页看到的」和「游戏实际加载的」不是同一套，
    /// 会出现「放进 mods 却加载不到」「打开目录打开的是全局目录」这类问题。
    /// </summary>
    public partial class VersionManageViewModel : ObservableObject
    {
        /// <summary>被禁用的模组文件后缀：foo.jar → foo.jar.disabled。</summary>
        private const string DisabledSuffix = ".disabled";

        private const string ModJarSuffix = ".jar";
        private const string DisabledJarSuffix = ModJarSuffix + DisabledSuffix;

        private readonly INotificationService _notification = AppServices.Notifications!;

        private readonly IDialogService _dialog = AppServices.Dialogs!;

        /// <summary>模组的全量列表；Mods 是它过滤后的展示用视图。</summary>
        private readonly List<ModItem> allMods = new();

        private string currentVersion = string.Empty;
        private string gameDir = string.Empty;
        private string modsFolder = string.Empty;
        private string savesFolder = string.Empty;
        private bool showMods = true;
        private string modSearchText = string.Empty;
        private string modCountText = "还没有模组";
        private string saveCountText = "还没有存档";

        public ObservableCollection<ModItem> Mods { get; } = new();
        public ObservableCollection<SaveItem> Saves { get; } = new();

        public string CurrentVersion
        {
            get => currentVersion;
            set => SetProperty(ref currentVersion, value);
        }

        public bool ShowMods
        {
            get => showMods;
            set => SetProperty(ref showMods, value);
        }

        /// <summary>当前版本的游戏根目录（版本隔离目录），用于「打开游戏目录」。</summary>
        public string GameDir
        {
            get => gameDir;
            private set => SetProperty(ref gameDir, value);
        }

        public string ModCountText
        {
            get => modCountText;
            private set => SetProperty(ref modCountText, value);
        }

        public string SaveCountText
        {
            get => saveCountText;
            private set => SetProperty(ref saveCountText, value);
        }

        /// <summary>模组搜索关键字，改变即过滤列表。</summary>
        public string ModSearchText
        {
            get => modSearchText;
            set
            {
                if (SetProperty(ref modSearchText, value))
                    ApplyModFilter();
            }
        }

        public RelayCommand LoadCommand { get; set; }
        public RelayCommand RefreshCommand { get; set; }
        public RelayCommand GoBackCommand { get; set; }

        // ---- 模组 ----
        public RelayCommand<string> OpenModFolderCommand { get; set; }
        public RelayCommand<ModItem> DeleteModCommand { get; set; }
        public RelayCommand ImportModsCommand { get; set; }
        public RelayCommand EnableAllModsCommand { get; set; }
        public RelayCommand DisableAllModsCommand { get; set; }
        public RelayCommand OpenModsFolderCommand { get; set; }

        // ---- 存档（世界） ----
        public RelayCommand<string> OpenSaveFolderCommand { get; set; }
        public RelayCommand<SaveItem> DeleteSaveCommand { get; set; }
        public RelayCommand ImportSaveFolderCommand { get; set; }
        public RelayCommand ImportSaveZipCommand { get; set; }
        public RelayCommand<SaveItem> ExportSaveCommand { get; set; }
        public RelayCommand<SaveItem> BeginRenameSaveCommand { get; set; }
        public RelayCommand<SaveItem> ConfirmRenameSaveCommand { get; set; }
        public RelayCommand<SaveItem> CancelRenameSaveCommand { get; set; }
        public RelayCommand OpenSavesFolderCommand { get; set; }

        public RelayCommand OpenGameFolderCommand { get; set; }

        public VersionManageViewModel()
        {
            LoadCommand = new RelayCommand(Load);
            RefreshCommand = new RelayCommand(async () => await LoadDataAsync());
            GoBackCommand = new RelayCommand(GoBack);

            OpenModFolderCommand = new RelayCommand<string>(OpenFolder);
            DeleteModCommand = new RelayCommand<ModItem>(DeleteMod);
            ImportModsCommand = new RelayCommand(ImportMods);
            EnableAllModsCommand = new RelayCommand(() => SetAllModsEnabled(true));
            DisableAllModsCommand = new RelayCommand(() => SetAllModsEnabled(false));
            OpenModsFolderCommand = new RelayCommand(() => OpenFolder(modsFolder));

            OpenSaveFolderCommand = new RelayCommand<string>(OpenFolder);
            DeleteSaveCommand = new RelayCommand<SaveItem>(DeleteSave);
            ImportSaveFolderCommand = new RelayCommand(ImportSaveFromFolder);
            ImportSaveZipCommand = new RelayCommand(ImportSaveFromZip);
            ExportSaveCommand = new RelayCommand<SaveItem>(ExportSave);
            BeginRenameSaveCommand = new RelayCommand<SaveItem>(BeginRenameSave);
            ConfirmRenameSaveCommand = new RelayCommand<SaveItem>(ConfirmRenameSave);
            CancelRenameSaveCommand = new RelayCommand<SaveItem>(CancelRenameSave);
            OpenSavesFolderCommand = new RelayCommand(() => OpenFolder(savesFolder));

            OpenGameFolderCommand = new RelayCommand(OpenGameFolder);

            WeakReferenceMessenger.Default.Register<string, string>(this, "McVersionForManage", (_, m) => OnVersionReceived(m));
        }

        private void OnVersionReceived(string version)
        {
            CurrentVersion = version;
            _ = LoadDataAsync();
        }

        private void Load()
        {
        }

        // ==================== 目录解析 ====================

        /// <summary>
        /// 计算当前版本的隔离目录。<c>versions\&lt;版本号&gt;</c> 不存在时退回游戏根目录，
        /// 避免把 mods/saves 建到一个并不存在的版本目录下造成困惑。
        /// </summary>
        private void ResolveFolders()
        {
            var mcFolder = SettingsService.ResolveMinecraftFolder();

            if (string.IsNullOrEmpty(CurrentVersion))
            {
                GameDir = mcFolder;
            }
            else
            {
                var versionDir = Path.Combine(mcFolder, "versions", CurrentVersion);
                GameDir = Directory.Exists(versionDir) ? versionDir : mcFolder;
            }

            modsFolder = Path.Combine(GameDir, "mods");
            savesFolder = Path.Combine(GameDir, "saves");
        }

        private async Task LoadDataAsync()
        {
            ResolveFolders();
            GameDir = Path.GetFullPath(GameDir);

            try
            {
                Directory.CreateDirectory(modsFolder);
                Directory.CreateDirectory(savesFolder);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "创建版本目录失败：{Path}", GameDir);
                _notification.Error($"无法创建模组 / 存档目录：{ex.Message}", "错误");
            }

            await LoadModsAsync();
            await LoadSavesAsync();
        }

        // ==================== 模组 ====================

        private async Task LoadModsAsync()
        {
            var folder = modsFolder;

            var items = await Task.Run(() =>
            {
                var list = new List<ModItem>();
                try
                {
                    if (!Directory.Exists(folder))
                        return list;

                    foreach (var file in Directory.GetFiles(folder, "*.jar*", SearchOption.TopDirectoryOnly))
                    {
                        // 只认 foo.jar / foo.jar.disabled，过滤掉 .jar.bak / .jar.old 之类的杂项
                        var isDisabled = file.EndsWith(DisabledJarSuffix, StringComparison.OrdinalIgnoreCase);
                        var isEnabled = file.EndsWith(ModJarSuffix, StringComparison.OrdinalIgnoreCase);
                        if (!isDisabled && !isEnabled)
                            continue;

                        var fi = new FileInfo(file);
                        list.Add(new ModItem
                        {
                            FileName = GetModDisplayName(file),
                            FullPath = file,
                            FolderPath = folder,
                            IsEnabled = !isDisabled,
                            FileSize = FormatSize(fi.Length)
                        });
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "读取模组目录失败：{Folder}", folder);
                }
                return list;
            });

            foreach (var old in allMods)
                old.PropertyChanged -= OnModPropertyChanged;

            allMods.Clear();
            foreach (var item in items)
            {
                item.PropertyChanged += OnModPropertyChanged;
                allMods.Add(item);
            }

            ApplyModFilter();
        }

        /// <summary>foo.jar / foo.jar.disabled → foo。</summary>
        private static string GetModDisplayName(string path)
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(DisabledJarSuffix, StringComparison.OrdinalIgnoreCase))
                return name[..^DisabledJarSuffix.Length];
            if (name.EndsWith(ModJarSuffix, StringComparison.OrdinalIgnoreCase))
                return name[..^ModJarSuffix.Length];
            return Path.GetFileNameWithoutExtension(name);
        }

        private void ApplyModFilter()
        {
            var keyword = modSearchText?.Trim();

            var filtered = string.IsNullOrEmpty(keyword)
                ? allMods
                : allMods.Where(m => m.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

            Mods.Clear();
            foreach (var item in filtered)
                Mods.Add(item);

            UpdateModCount();
        }

        private void UpdateModCount()
        {
            if (allMods.Count == 0)
            {
                ModCountText = "还没有模组";
                return;
            }

            var enabled = allMods.Count(m => m.IsEnabled);
            ModCountText = Mods.Count < allMods.Count
                ? $"筛选出 {Mods.Count} 个 · 共 {allMods.Count} 个（已启用 {enabled}）"
                : $"共 {allMods.Count} 个模组 · 已启用 {enabled}";
        }

        /// <summary>
        /// 勾选框变化 → 同步磁盘文件名。
        /// 用「目标状态」而不是「取反」来驱动，天然幂等：
        /// 重复触发、批量设置都不会把文件改乱。
        /// </summary>
        private void OnModPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ModItem.IsEnabled) || sender is not ModItem mod)
                return;

            SyncModFile(mod);
            UpdateModCount();
        }

        private void SyncModFile(ModItem mod)
        {
            try
            {
                var disabledOnDisk = mod.FullPath.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);

                // 磁盘状态和目标状态已经一致就不用动
                if (disabledOnDisk != mod.IsEnabled)
                    return;

                var target = mod.IsEnabled
                    ? mod.FullPath[..^DisabledSuffix.Length]
                    : mod.FullPath + DisabledSuffix;

                if (File.Exists(mod.FullPath))
                    File.Move(mod.FullPath, target);
                else
                {
                    // 文件被外部改过名：以磁盘为准回滚勾选状态，避免界面骗人
                    mod.IsEnabled = !mod.IsEnabled;
                    return;
                }

                mod.FullPath = target;
                Serilog.Log.Information("模组 {Name} 已{State}", mod.FileName, mod.IsEnabled ? "启用" : "禁用");
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "切换模组状态失败：{Path}", mod.FullPath);
                mod.IsEnabled = !mod.IsEnabled;
                _notification.Error($"切换模组状态失败：{ex.Message}", "错误");
            }
        }

        private void SetAllModsEnabled(bool enabled)
        {
            var targets = allMods.Where(m => m.IsEnabled != enabled).ToList();
            if (targets.Count == 0)
            {
                _notification.Show(enabled ? "所有模组都已启用" : "所有模组都已禁用", "提示", NotificationSeverity.Informational);
                return;
            }

            foreach (var mod in targets)
                mod.IsEnabled = enabled;

            UpdateModCount();
            _notification.Success($"已{(enabled ? "启用" : "禁用")} {targets.Count} 个模组", "成功");
        }

        private async void ImportMods()
        {
            var picked = await (AppServices.FilePicker?.PickFilesAsync("选择模组文件（可多选）", "jar") ?? Task.FromResult<List<string>>(new()));
            if (picked.Count == 0)
                return;

            Directory.CreateDirectory(modsFolder);

            var imported = 0;
            var failed = new List<string>();
            foreach (var source in picked)
            {
                try
                {
                    var target = Path.Combine(modsFolder, Path.GetFileName(source));

                    // 从 mods 目录里选自己 = 无意义操作
                    if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                        continue;

                    File.Copy(source, target, overwrite: true);
                    imported++;
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "导入模组失败：{File}", source);
                    failed.Add(Path.GetFileName(source));
                }
            }

            _ = LoadModsAsync();

            if (imported > 0)
                _notification.Success(failed.Count == 0 ? $"已导入 {imported} 个模组" : $"已导入 {imported} 个，{failed.Count} 个失败", "成功");
            else
                _notification.Error("没有导入任何模组", "错误");
        }

        private async void DeleteMod(ModItem mod)
        {
            var confirmed = await _dialog.ConfirmAsync(
                "确认删除",
                $"确定要删除模组 \"{mod.FileName}\" 吗？此操作不可撤销。",
                "删除", "取消");

            if (!confirmed)
                return;

            try
            {
                if (File.Exists(mod.FullPath))
                    File.Delete(mod.FullPath);

                mod.PropertyChanged -= OnModPropertyChanged;
                allMods.Remove(mod);
                Mods.Remove(mod);
                UpdateModCount();
                _notification.Success($"已删除模组 {mod.FileName}", "成功");
            }
            catch (Exception ex)
            {
                _notification.Error($"删除失败: {ex.Message}", "错误");
            }
        }

        // ==================== 存档（世界） ====================

        private async Task LoadSavesAsync()
        {
            var folder = savesFolder;

            var items = await Task.Run(() =>
            {
                var list = new List<SaveItem>();
                try
                {
                    if (!Directory.Exists(folder))
                        return list;

                    foreach (var dir in Directory.GetDirectories(folder))
                    {
                        var di = new DirectoryInfo(dir);
                        list.Add(new SaveItem
                        {
                            FolderName = di.Name,
                            FolderPath = dir,
                            LastPlayed = di.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                            Size = FormatSize(GetDirectorySize(dir))
                        });
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "读取存档目录失败：{Folder}", folder);
                }
                return list.OrderByDescending(s => s.LastPlayed).ToList();
            });

            Saves.Clear();
            foreach (var item in items)
                Saves.Add(item);

            UpdateSaveCount();
        }

        private void UpdateSaveCount()
        {
            SaveCountText = Saves.Count == 0 ? "还没有存档" : $"共 {Saves.Count} 个存档";
        }

        private static long GetDirectorySize(string dir)
        {
            try
            {
                return new DirectoryInfo(dir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>存档导入 / 导出是否正在进行 —— 异步化之后 UI 不再冻结，需要自己挡住连点。</summary>
        private bool _saveIoBusy;

        private async void ImportSaveFromFolder()
        {
            if (_saveIoBusy)
            {
                _notification.Show("正在处理上一个存档操作，请稍候", "提示", NotificationSeverity.Informational);
                return;
            }

            var source = await (AppServices.FilePicker?.PickFolderAsync("选择存档文件夹（内含 level.dat），也可选择包含多个存档的父目录") ?? Task.FromResult<string?>(null));
            if (string.IsNullOrEmpty(source))
                return;

            // 选中的可能就是存档本身，也可能是装着若干存档的父目录，两种都支持
            var worlds = new List<string>();
            if (IsWorldFolder(source))
                worlds.Add(source);
            else
                worlds.AddRange(Directory.GetDirectories(source).Where(IsWorldFolder));

            if (worlds.Count == 0)
            {
                _notification.Error("选中的文件夹不是有效的存档（缺少 level.dat）", "错误");
                return;
            }

            _saveIoBusy = true;
            int imported;
            try
            {
                // 复制存档是几百 MB 量级的文件 IO，必须离开 UI 线程，否则整个界面会定住
                imported = await Task.Run(() =>
                {
                    var count = 0;
                    foreach (var world in worlds)
                    {
                        try
                        {
                            CopyDirectory(world, MakeUniquePath(Path.Combine(savesFolder, Path.GetFileName(world))));
                            count++;
                        }
                        catch (Exception ex)
                        {
                            Serilog.Log.Error(ex, "导入存档失败：{World}", world);
                        }
                    }

                    return count;
                });
            }
            finally
            {
                _saveIoBusy = false;
            }

            _ = LoadSavesAsync();
            if (imported > 0)
                _notification.Success($"已导入 {imported} 个存档", "成功");
            else
                _notification.Error("导入失败", "错误");
        }

        private async void ImportSaveFromZip()
        {
            if (_saveIoBusy)
            {
                _notification.Show("正在处理上一个存档操作，请稍候", "提示", NotificationSeverity.Informational);
                return;
            }

            var picked = await (AppServices.FilePicker?.PickFilesAsync("选择存档压缩包（可多选）", "zip") ?? Task.FromResult<List<string>>(new()));
            if (picked.Count == 0)
                return;

            Directory.CreateDirectory(savesFolder);

            _saveIoBusy = true;
            var imported = 0;
            try
            {
                foreach (var zip in picked)
                {
                    // 解压 + 复制整包都在工作线程里做
                    var (ok, error) = await Task.Run(() => ImportOneZip(zip));

                    if (ok)
                    {
                        imported++;
                        continue;
                    }

                    _notification.Error($"导入 {Path.GetFileName(zip)} 失败：{error}", "错误");
                }
            }
            finally
            {
                _saveIoBusy = false;
            }

            if (imported > 0)
            {
                _ = LoadSavesAsync();
                _notification.Success($"已导入 {imported} 个存档", "成功");
            }
        }

        /// <summary>解压并导入单个存档压缩包。只做文件操作，不碰 UI —— 由调用方丢到线程池执行。</summary>
        private (bool Imported, string? Error) ImportOneZip(string zip)
        {
            var temp = Path.Combine(Path.GetTempPath(), "vbl_save_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                ZipFile.ExtractToDirectory(zip, temp);

                // 压缩包内常见两种布局：世界在根目录，或包了一层同名文件夹
                var root = temp;
                if (!IsWorldFolder(root))
                {
                    var inner = Directory.GetDirectories(root).FirstOrDefault(IsWorldFolder);
                    if (inner != null)
                        root = inner;
                }

                if (!IsWorldFolder(root))
                    return (false, "不是有效的存档压缩包（缺少 level.dat）");

                CopyDirectory(root, MakeUniquePath(Path.Combine(savesFolder, Path.GetFileNameWithoutExtension(zip))));
                return (true, null);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "导入存档压缩包失败：{Zip}", zip);
                return (false, ex.Message);
            }
            finally
            {
                TryDeleteDirectory(temp);
            }
        }

        private async void ExportSave(SaveItem save)
        {
            if (_saveIoBusy)
            {
                _notification.Show("正在处理上一个存档操作，请稍候", "提示", NotificationSeverity.Informational);
                return;
            }

            var target = await (AppServices.FilePicker?.PickSaveFileAsync("备份存档", save.FolderName + ".zip", "zip") ?? Task.FromResult<string?>(null));
            if (string.IsNullOrEmpty(target))
                return;

            _saveIoBusy = true;
            try
            {
                // 压缩整个存档同样是重 IO，丢到线程池
                await Task.Run(() =>
                {
                    if (File.Exists(target))
                        File.Delete(target);

                    ZipFile.CreateFromDirectory(save.FolderPath, target, CompressionLevel.Optimal, includeBaseDirectory: false);
                });

                _notification.Success($"已备份到 {Path.GetFileName(target)}", "成功");
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "备份存档失败：{Save}", save.FolderPath);
                _notification.Error($"备份失败: {ex.Message}", "错误");
            }
            finally
            {
                _saveIoBusy = false;
            }
        }

        private void BeginRenameSave(SaveItem save)
        {
            foreach (var other in Saves.Where(s => !ReferenceEquals(s, save)))
                other.IsRenaming = false;

            save.EditName = save.FolderName;
            save.IsRenaming = true;
        }

        private void CancelRenameSave(SaveItem save) => save.IsRenaming = false;

        private void ConfirmRenameSave(SaveItem save)
        {
            var newName = (save.EditName ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(newName))
            {
                _notification.Warning("名称不能为空", "提示");
                return;
            }

            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                _notification.Warning("名称包含非法字符", "提示");
                return;
            }

            if (string.Equals(newName, save.FolderName, StringComparison.Ordinal))
            {
                save.IsRenaming = false;
                return;
            }

            var target = Path.Combine(savesFolder, newName);
            if (Directory.Exists(target))
            {
                _notification.Warning($"已存在名为 {newName} 的存档", "提示");
                return;
            }

            try
            {
                Directory.Move(save.FolderPath, target);
                save.FolderName = newName;
                save.FolderPath = target;
                save.IsRenaming = false;
                _notification.Success($"已重命名为 {newName}", "成功");
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "重命名存档失败：{Save}", save.FolderPath);
                _notification.Error($"重命名失败: {ex.Message}", "错误");
            }
        }

        private async void DeleteSave(SaveItem save)
        {
            var confirmed = await _dialog.ConfirmAsync(
                "确认删除",
                $"确定要删除存档 \"{save.FolderName}\" 吗？此操作不可撤销。",
                "删除", "取消");

            if (!confirmed)
                return;

            try
            {
                if (Directory.Exists(save.FolderPath))
                    Directory.Delete(save.FolderPath, true);

                Saves.Remove(save);
                UpdateSaveCount();
                _notification.Success($"已删除存档 {save.FolderName}", "成功");
            }
            catch (Exception ex)
            {
                _notification.Error($"删除失败: {ex.Message}", "错误");
            }
        }

        // ==================== 通用 ====================

        /// <summary>
        /// 返回上一页。
        ///
        /// 优先走导航服务的返回栈 —— 从哪进来的就回哪去（「启动」页或「运行」页）。
        /// 只有当返回栈为空（例如直接深链进来）时，才回退到当前的「启动」页 LaunchPage。
        ///
        /// 注意：不要再写死回 RunPage —— 那是旧的独立运行页，已不在导航菜单里；
        /// 用户实际使用的「启动」页是 LaunchPage，写死 RunPage 就会出现「返回却回到了旧运行页面」。
        /// </summary>
        private void GoBack()
        {
            if (AppNavigation.Current is { } nav && nav.CanGoBack && nav.GoBack())
                return;

            AppNavigation.Navigate(typeof(Views.Pages.LaunchPage));
        }

        private void OpenFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);

                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _notification.Error($"打开目录失败: {ex.Message}", "错误");
            }
        }

        private void OpenGameFolder() => OpenFolder(GameDir);

        /// <summary>存档目录的判定标准：含 level.dat。</summary>
        private static bool IsWorldFolder(string dir)
            => Directory.Exists(dir) && File.Exists(Path.Combine(dir, "level.dat"));

        /// <summary>目标已存在时追加 -1 / -2 … 后缀，避免覆盖别人的存档。</summary>
        private static string MakeUniquePath(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path))
                return path;

            for (var i = 1; i < 1000; i++)
            {
                var candidate = $"{path}-{i}";
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                    return candidate;
            }
            return path + "-" + Guid.NewGuid().ToString("N")[..6];
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        private static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch { }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
            if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }
    }

    public class ModItem : ObservableObject
    {
        private bool isEnabled;

        public string FileName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty;
        public string FileSize { get; set; } = string.Empty;

        public bool IsEnabled
        {
            get => isEnabled;
            set => SetProperty(ref isEnabled, value);
        }
    }

    public class SaveItem : ObservableObject
    {
        private string folderName = string.Empty;
        private string folderPath = string.Empty;
        private string lastPlayed = string.Empty;
        private string size = string.Empty;
        private bool isRenaming;
        private string editName = string.Empty;

        public string FolderName
        {
            get => folderName;
            set => SetProperty(ref folderName, value);
        }

        public string FolderPath
        {
            get => folderPath;
            set => SetProperty(ref folderPath, value);
        }

        public string LastPlayed
        {
            get => lastPlayed;
            set => SetProperty(ref lastPlayed, value);
        }

        public string Size
        {
            get => size;
            set => SetProperty(ref size, value);
        }

        /// <summary>是否处于「内联重命名」编辑态。</summary>
        public bool IsRenaming
        {
            get => isRenaming;
            set => SetProperty(ref isRenaming, value);
        }

        /// <summary>重命名编辑框里的新名字。</summary>
        public string EditName
        {
            get => editName;
            set => SetProperty(ref editName, value);
        }
    }
}
