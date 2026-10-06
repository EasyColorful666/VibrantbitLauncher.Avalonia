using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using MinecraftLaunch.Base.Models.Authentication;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VibrantbitLauncher.Services;
using UiMessageBox = FluentAvalonia.UI.Controls.FAContentDialog;

namespace VibrantbitLauncher.Views.Windows
{
    /// <summary>
    /// 皮肤管理窗口（Avalonia 版）：预览并更换当前账户的皮肤。
    ///
    /// 微软账户：通过 Mojang 官方接口上传/设置/重置皮肤（需要有效访问令牌）。
    /// 离线账户：微软接口用不上，改为写入本地皮肤文件，启动时由 RunPageViewModel 追加启动参数指定。
    /// 外置（Yggdrasil）账户：仅支持本地预览与保存，不做服务端上传。
    /// </summary>
    public partial class SkinManagerWindow : Window
    {
        private readonly string uuid;
        private readonly string accountName;
        private readonly string accountType;
        private readonly Account account;

        /// <summary>用户本次新选择的皮肤文件（本地路径）。为 null 表示未改动皮肤。</summary>
        private string? pendingSkinFile;

        /// <summary>是否执行了「重置为默认皮肤」。</summary>
        private bool resetRequested;

        /// <summary>窗口关闭后由调用方读取：皮肤是否发生了变化（需要刷新头像）。</summary>
        public bool SkinChanged { get; private set; }

        public SkinManagerWindow(
            Account account,
            string accountType,
            string uuid,
            string accountName)
        {
            InitializeComponent();

            this.account = account;
            this.accountType = accountType;
            this.uuid = uuid;
            this.accountName = accountName;

            accountNameText.Text = accountName;
            accountTypeText.Text = accountType;
            Title = $"皮肤管理 - {accountName}";

            Opened += (_, _) =>
            {
                LoadCurrentPreview();
                UpdateSkinState();
            };
        }

        /// <summary>
        /// 读取本地已有皮肤作为预览。
        /// 预览显示的是**角色正面整身立绘**（由皮肤各部位拼合），不是那张 64×64 的展开图 ——
        /// 展开图对普通用户来说看不出「穿上是什么样」。
        /// 若本地没有，则尝试从 Mojang 拉取当前皮肤。
        /// </summary>
        private void LoadCurrentPreview()
        {
            if (TrySetPreviewFromFullSkin())
                return;

            // 本地没有皮肤文件：微软账户尝试在线拉取一次（失败不影响窗口可用）
            if (accountType == "Microsoft")
                _ = PullOnlineSkinAsync();
            else
                SetFrontPreview(null);
        }

        /// <summary>用本地完整皮肤重新生成并显示正面视图。成功返回 true。</summary>
        private bool TrySetPreviewFromFullSkin()
        {
            var localFull = SkinService.FullSkinPath(uuid);
            if (!File.Exists(localFull))
                return false;

            try
            {
                var bytes = File.ReadAllBytes(localFull);
                var front = SkinService.SaveFrontView(uuid, bytes);
                SetFrontPreview(front);
                return true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "生成角色正面视图失败：{Uuid}", uuid);
                SetFrontPreview(null);
                return false;
            }
        }

        private async Task PullOnlineSkinAsync()
        {
            try
            {
                SetBusy(true);
                var info = await SkinService.GetPlayerSkinInfoAsync(account.Uuid, CancellationToken.None);
                if (info != null && !string.IsNullOrEmpty(info.SkinUrl))
                {
                    await SkinService.DownloadAndSaveAsync(uuid, info.SkinUrl);
                    TrySetPreviewFromFullSkin();
                    classicRadio.IsChecked = info.Variant != "slim";
                    slimRadio.IsChecked = info.Variant == "slim";
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "拉取在线皮肤失败：{Uuid}", uuid);
            }
            finally
            {
                SetBusy(false);
                UpdateSkinState();
            }
        }

        /// <summary>显示正面立绘预览。</summary>
        private void SetFrontPreview(string? frontPath)
        {
            if (string.IsNullOrEmpty(frontPath) || !File.Exists(frontPath))
            {
                previewImage.Source = null;
                previewHint.Text = "暂无皮肤预览";
                return;
            }

            try
            {
                // 从内存流加载：不参与 Avalonia 按 URI 的全局图片缓存。
                // 若改用 Bitmap(uri)，同一路径的皮肤文件被覆盖后仍会显示旧图（换肤不生效）。
                using var ms = new MemoryStream(File.ReadAllBytes(frontPath));
                previewImage.Source = new Bitmap(ms);
                previewImage.Stretch = Stretch.Uniform;
                previewHint.Text = "角色正面预览";
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "正面预览加载失败：{Path}", frontPath);
                previewHint.Text = "预览加载失败";
            }
        }

        private void UpdateSkinState()
        {
            var hasSkin = File.Exists(SkinService.FullSkinPath(uuid));
            skinStateText.Text = hasSkin ? "已设置自定义皮肤" : "使用默认皮肤";

            // 手臂模型：本地皮肤无法判断纤细与否，默认按经典选中
            if (classicRadio.IsChecked != true && slimRadio.IsChecked != true)
                classicRadio.IsChecked = true;
        }

        private async void OnChooseFileClick(object sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择皮肤文件",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("PNG 图片") { Patterns = new[] { "*.png" } }
                }
            });

            if (files.Count == 0)
                return;

            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
                return;

            var (valid, error) = SkinService.ValidateSkinFile(path);
            if (!valid)
            {
                ShowStatus(error, isError: true);
                return;
            }

            pendingSkinFile = path;
            resetRequested = false;
            SetFrontPreview(SkinService.SaveFrontView(uuid, File.ReadAllBytes(path)));
            ShowStatus($"已选择：{Path.GetFileName(path)}，点击「保存」应用", isError: false);
        }

        private async void OnChooseUrlClick(object sender, RoutedEventArgs e)
        {
            var input = new SkinUrlInputWindow();
            var ok = await input.ShowDialog<bool>(this);
            if (!ok)
                return;

            var url = input.SkinUrl;
            if (string.IsNullOrWhiteSpace(url)) return;

            try
            {
                SetBusy(true);
                var tempPath = Path.Combine(Path.GetTempPath(), $"vbl_skin_{Guid.NewGuid():N}.png");
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(tempPath, bytes);

                var (valid, error) = SkinService.ValidateSkinFile(tempPath);
                if (!valid)
                {
                    File.Delete(tempPath);
                    ShowStatus(error, isError: true);
                    return;
                }

                pendingSkinFile = tempPath;
                resetRequested = false;
                SetFrontPreview(SkinService.SaveFrontView(uuid, File.ReadAllBytes(tempPath)));
                ShowStatus("已从网址加载皮肤，点击「保存」应用", isError: false);
            }
            catch (Exception ex)
            {
                ShowStatus($"加载失败：{ex.Message}", isError: true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void OnResetClick(object sender, RoutedEventArgs e)
        {
            var confirm = new UiMessageBox
            {
                Title = "重置皮肤",
                Content = accountType == "Microsoft"
                    ? "确定要将该微软账户的皮肤重置为官方默认皮肤吗？"
                    : "确定要清除该账户的本地自定义皮肤并恢复默认皮肤吗？",
                PrimaryButtonText = "重置",
                CloseButtonText = "取消",
                DefaultButton = FAContentDialogButton.Primary
            };

            var result = await confirm.ShowAsync(this);
            if (result != FAContentDialogResult.Primary) return;

            resetRequested = true;
            pendingSkinFile = null;

            if (accountType == "Microsoft" && account is MicrosoftAccount ms)
            {
                try
                {
                    SetBusy(true);
                    await SkinService.ResetSkinAsync(ms);

                    // 线上已重置：重新拉一次线上皮肤（此时是官方默认），
                    // 拉不到就用内置 Steve 兜底，保证头像与预览都有内容。
                    TryDeleteLocalSkins();
                    var info = await SkinService.GetPlayerSkinInfoAsync(account.Uuid, CancellationToken.None);
                    if (info != null && !string.IsNullOrEmpty(info.SkinUrl))
                        await SkinService.DownloadAndSaveAsync(uuid, info.SkinUrl);
                    else
                        SkinService.RestoreDefaultSkin(uuid);

                    TrySetPreviewFromFullSkin();
                    UpdateSkinState();
                    ShowStatus("已重置为官方默认皮肤", isError: false);
                    SkinChanged = true;
                }
                catch (Exception ex)
                {
                    ShowStatus($"重置失败：{ex.Message}", isError: true);
                }
                finally
                {
                    SetBusy(false);
                }
            }
            else
            {
                // 离线 / 外置账户：恢复成内置默认 Steve（含重新裁剪头像），
                // 而不是单纯删除文件——否则卡片头像会掉成通用占位图。
                SkinService.RestoreDefaultSkin(uuid);
                TrySetPreviewFromFullSkin();
                UpdateSkinState();
                ShowStatus("已恢复默认皮肤", isError: false);
                SkinChanged = true;
            }
        }

        private async void OnApplyClick(object sender, RoutedEventArgs e)
        {
            // 未选择新皮肤、也没点重置 —— 无改动
            if (pendingSkinFile == null && !resetRequested)
            {
                Close();
                return;
            }

            var variant = slimRadio.IsChecked == true ? "slim" : "classic";

            try
            {
                SetBusy(true);

                if (accountType == "Microsoft" && account is MicrosoftAccount ms
                    && pendingSkinFile != null)
                {
                    // 微软账户：上传到官方 → 再拉回线上皮肤，保证本地与线上一致
                    await SkinService.UploadSkinAsync(ms, pendingSkinFile, variant);

                    var info = await SkinService.GetPlayerSkinInfoAsync(account.Uuid, CancellationToken.None);
                    if (info != null && !string.IsNullOrEmpty(info.SkinUrl))
                        await SkinService.DownloadAndSaveAsync(uuid, info.SkinUrl);
                    else
                        await Task.Run(() => SkinService.SaveLocalSkin(uuid, File.ReadAllBytes(pendingSkinFile)));
                }
                else if (pendingSkinFile != null)
                {
                    // 离线 / 外置账户：直接落盘
                    await Task.Run(() => SkinService.SaveLocalSkin(uuid, File.ReadAllBytes(pendingSkinFile)));
                }

                SkinChanged = true;
                ShowStatus("皮肤已保存", isError: false);
                Close();
            }
            catch (Exception ex)
            {
                ShowStatus($"保存失败：{ex.Message}", isError: true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void TryDeleteLocalSkins()
        {
            try
            {
                var full = SkinService.FullSkinPath(uuid);
                var head = SkinService.HeadSkinPath(uuid);
                var front = SkinService.FrontViewPath(uuid);
                if (File.Exists(full)) File.Delete(full);
                if (File.Exists(head)) File.Delete(head);
                if (File.Exists(front)) File.Delete(front);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "删除本地皮肤文件失败：{Uuid}", uuid);
            }
        }

        private void ShowStatus(string message, bool isError)
        {
            statusText.Text = message;
            statusBar.IsVisible = true;
            statusBar.Background = isError
                ? new SolidColorBrush(Color.Parse("#FDE7E9"))
                : new SolidColorBrush(Color.Parse("#DFF6DD"));
            statusText.Foreground = isError
                ? new SolidColorBrush(Color.Parse("#8B1A10"))
                : new SolidColorBrush(Color.Parse("#0F4A12"));
        }

        private void SetBusy(bool busy)
        {
            busyRing.IsVisible = busy;
            chooseFileButton.IsEnabled = !busy;
            chooseUrlButton.IsEnabled = !busy;
            applyButton.IsEnabled = !busy;
            resetButton.IsEnabled = !busy;
        }
    }
}
