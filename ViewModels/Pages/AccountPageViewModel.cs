using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MinecraftLaunch.Base.Models.Authentication;
using MinecraftLaunch.Base.Models.Authentication.Yggdrasil;
using MinecraftLaunch.Components.Authenticator;
using MinecraftLaunch.Components.Provider;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VibrantbitLauncher.Models;
using VibrantbitLauncher.Services;
using VibrantbitLauncher.ViewModels.Windows;
using VibrantbitLauncher.Views.Windows;

namespace VibrantbitLauncher.ViewModels.Pages
{
    public partial class AccountPageViewModel : ObservableObject
    {
        private readonly INotificationService _notification = AppServices.Notifications!;
        ObservableCollection<User> users = new();
        ObservableCollection<string> userNames = new();
        ObservableCollection<Account> accounts = new();
        Account selectedAccount;
        User? currentUser;
        string selectedSkinPath = string.Empty;
        string selectedAccountName = "未选择账户";
        string selectedAccountType = string.Empty;
        YggdrasilAccountProfile yggdrasilAccountProfile;
        string offlineAccountName;

        public RelayCommand AuthenticateMicrosoftCommand { get; set; }
        public RelayCommand AuthenticateYggdrasilCommand { get; set; }
        public RelayCommand AuthenticateOfflineCommand { get; set; }
        public RelayCommand LoadCommand { get; set; }
        public ObservableCollection<User> Users
        {
            get => users;
            set => SetProperty(ref users, value);
        }
        public bool IsLoaded { get; set; }

        public string SelectedSkinPath
        {
            get => selectedSkinPath;
            set => SetProperty(ref selectedSkinPath, value);
        }

        public string SelectedAccountName
        {
            get => selectedAccountName;
            set => SetProperty(ref selectedAccountName, value);
        }

        public string SelectedAccountType
        {
            get => selectedAccountType;
            set => SetProperty(ref selectedAccountType, value);
        }

        private void UpdateSelectedDisplay(Account account)
        {
            var user = users.FirstOrDefault(u => u.Account == account);
            SelectedSkinPath = user?.FullSkinPath ?? string.Empty;
            SelectedAccountName = account?.Name ?? "未选择账户";
            SelectedAccountType = user?.AccountType ?? string.Empty;
            UpdateSelectionFlags(account);
        }

        /// <summary>
        /// 当前启动身份（Hero 卡直接绑这个对象）。
        ///
        /// 与 <see cref="UpdateSelectionFlags"/> 同源，避免「卡片亮着 A、顶部却写着 B」。
        /// 允许为 null —— 有账户但一个都没选时，Hero 位置显示「尚未选择启动身份」的占位。
        /// </summary>
        public User? CurrentUser
        {
            get => currentUser;
            private set
            {
                if (SetProperty(ref currentUser, value))
                    OnPropertyChanged(nameof(HasCurrentUser));
            }
        }

        public bool HasCurrentUser => currentUser is not null;

        /// <summary>是否已有账户。空 / 非空决定整页显示空状态还是身份墙。</summary>
        public bool HasUsers => users.Count > 0;

        /// <summary>「3 个账户」这类计数文案。集合变化时重新算一次，界面不用自己拼字符串。</summary>
        public string AccountCountText => users.Count == 0 ? string.Empty : $"{users.Count} 个账户";

        /// <summary>
        /// 刷新所有卡片的「当前使用」标记。
        ///
        /// 卡片上的高亮、徽章都只认这一个标志 —— 不再依赖 RadioButton 的 GroupName，
        /// 否则会出现「VM 认为选了 A，界面上却亮着 B」这种双数据源不同步的问题。
        /// </summary>
        private void UpdateSelectionFlags(Account? account)
        {
            foreach (var u in users)
                u.IsSelected = account is not null && ReferenceEquals(u.Account, account);

            CurrentUser = account is null
                ? null
                : users.FirstOrDefault(u => ReferenceEquals(u.Account, account));
        }
        public AccountPageViewModel()
        {
            IsLoaded = false;

            // 账户增删后刷新空状态 / 计数文案。
            // 以前由 AccountPage 的 code-behind 逐个 x:Name 去开关 IsVisible，
            // 页面只剩绑定时，这一层通知就必须由 VM 自己发出。
            users.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasUsers));
                OnPropertyChanged(nameof(AccountCountText));
            };

            WeakReferenceMessenger.Default.Register<YggdrasilAccountProfile, string>(this, "YggdrasilAccountProfile", (_, m) => NewYggdrasilAccountProfile(m));
            WeakReferenceMessenger.Default.Register<string, string>(this, "OfflineAccountProfile", (_, m) => NewOfflineAccountProfile(m));
            AuthenticateMicrosoftCommand = new RelayCommand(AuthenticateMicrosoftAsync);
            AuthenticateYggdrasilCommand = new RelayCommand(AuthenticateYggdrasilAsync);
            AuthenticateOfflineCommand = new RelayCommand(AuthenticateOffline);
            LoadCommand = new RelayCommand(Load);
        }

        private void NewOfflineAccountProfile(string name)
        {
            offlineAccountName = name;
        }

        private void NewYggdrasilAccountProfile(YggdrasilAccountProfile profile)
        {
            yggdrasilAccountProfile = profile;
        }

        public void Load()
        {
            if (!IsLoaded)
            {
                RestoreSavedAccounts();
                IsLoaded = true;
            }
        }

        /// <summary>
        /// 从配置文件恢复已保存的账户，直接使用 MinecraftLaunch 的账户类型。
        /// </summary>
        private void RestoreSavedAccounts()
        {
            // 离线账户：使用史蒂夫皮肤
            foreach (var saved in SettingsService.Current.OfflineAccounts)
            {
                if (string.IsNullOrEmpty(saved.Name)) continue;
                var uuid = saved.Uuid.ToString();
                if (string.IsNullOrEmpty(ResolveFullSkinPath(uuid)))
                    EnsureSteveSkin(saved);
                AddAccountToList(saved, "Offline", ResolveAvatarPath(uuid), ResolveFullSkinPath(uuid));
                AutoSelectIfSaved(saved, uuid);
            }

            // 微软账户
            foreach (var saved in SettingsService.Current.MicrosoftAccounts)
            {
                var uuid = saved.Uuid.ToString();
                AddAccountToList(saved, "Microsoft", ResolveAvatarPath(uuid), ResolveFullSkinPath(uuid));
                AutoSelectIfSaved(saved, uuid);
            }

            // Yggdrasil 账户
            foreach (var saved in SettingsService.Current.YggdrasilAccounts)
            {
                var uuid = saved.Uuid.ToString();
                AddAccountToList(saved, "Yggdrasil", ResolveAvatarPath(uuid), ResolveFullSkinPath(uuid));
                AutoSelectIfSaved(saved, uuid);
            }
        }

        /// <summary>
        /// 为离线账户生成史蒂夫默认皮肤（完整皮肤 + 裁好的头像 + 正面立绘）。
        /// </summary>
        private static void EnsureSteveSkin(OfflineAccount account)
        {
            try
            {
                var resDir = Path.Combine(AppContext.BaseDirectory, "res");
                if (!Directory.Exists(resDir)) Directory.CreateDirectory(resDir);

                using var assetStream = Avalonia.Platform.AssetLoader.Open(
                    new Uri("avares://VibrantbitLauncher.Avalonia/Assets/steve.png"));
                using var ms = new MemoryStream();
                assetStream.CopyTo(ms);
                var skinBytes = ms.ToArray();

                // 统一走 SkinService：一次性写全 完整皮肤 + 裁好的头像 + 正面立绘，
                // 避免只写了部分文件导致皮肤管理窗口拿不到预览。
                SkinService.SaveLocalSkin(account.Uuid.ToString(), skinBytes);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "生成默认皮肤失败");
            }
        }

        /// <summary>
        /// 该账户是不是「换过自定义皮肤」。靠 <c>res/&lt;uuid&gt;.custom</c> 这个零字节标记文件判断 ——
        /// 光看头像文件是否存在不行：默认的史蒂夫皮肤也会写进同一个路径。
        /// </summary>
        private static bool HasCustomSkin(Account account)
        {
            try
            {
                var uuid = GetAccountUuid(account);
                if (string.IsNullOrEmpty(uuid))
                    return false;

                return File.Exists(Path.Combine(AppContext.BaseDirectory, "res", $"{uuid}.custom"));
            }
            catch
            {
                return false;
            }
        }

        private static void MarkCustomSkin(string uuid)
        {
            try
            {
                if (string.IsNullOrEmpty(uuid))
                    return;

                var dir = Path.Combine(AppContext.BaseDirectory, "res");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"{uuid}.custom"), string.Empty);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "记录自定义皮肤标记失败（不影响换肤结果）");
            }
        }

        /// <summary>
        /// 解析头像路径：优先使用按 UUID 保存的皮肤文件，不存在则回退到默认头像。
        /// </summary>
        private static string ResolveAvatarPath(string? uuid)
        {
            var defaultAvatar = "/Assets/gravatar.png";
            if (string.IsNullOrEmpty(uuid)) return defaultAvatar;

            var skinPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "res", $"{uuid}.png"));
            if (File.Exists(skinPath)) return skinPath;

            // 兼容旧路径 .\res\skin.png
            var legacyPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "res", "skin.png"));
            if (File.Exists(legacyPath)) return legacyPath;

            return defaultAvatar;
        }

        /// <summary>
        /// 解析完整皮肤路径（用于3D预览），不存在则返回空字符串。
        /// </summary>
        private static string ResolveFullSkinPath(string? uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return string.Empty;
            var fullPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "res", $"{uuid}_full.png"));
            return File.Exists(fullPath) ? fullPath : string.Empty;
        }

        private void AddAccountToList(Account account, string type, string imagePath, string fullSkinPath = "")
        {
            userNames.Add(account.Name);
            accounts.Add(account);
            users.Add(new User
            {
                Name = account.Name,
                Account = account,
                AccountType = type,
                SelectedCommand = new RelayCommand<Account>(Selected),
                DeleteCommand = new RelayCommand<Account>(DeleteAccount),
                SkinCommand = new RelayCommand<Account>(OpenSkinManager),
                ImagePath = imagePath,
                FullSkinPath = fullSkinPath,
                IsPlaceholderAvatar = type == "Offline" && !HasCustomSkin(account)
            });
        }

        /// <summary>
        /// 打开皮肤管理窗口。关闭后若皮肤有变化，刷新卡片头像与当前选中账户的皮肤预览。
        /// </summary>
        private async void OpenSkinManager(Account account)
        {
            if (account == null) return;

            var user = users.FirstOrDefault(u => u.Account == account);
            var type = user?.AccountType ?? "Offline";
            var uuid = GetAccountUuid(account);

            var window = new SkinManagerWindow(account, type, uuid, account.Name);
            await AppLifetime.ShowDialogAsync(window);

            if (!window.SkinChanged) return;

            // 头像/皮肤文件已更新。
            // 注意：换肤后头像文件路径**没变**，直接赋同名字符串不会触发通知，
            // 必须显式通知，界面才会重新读文件、把新头像显示出来。
            if (user != null)
            {
                // 用户真的换过皮肤了：写一个 sidecar 标记，下次启动就知道
                // 这个离线账户不该再显示默认人形图标了。
                MarkCustomSkin(uuid);

                user.IsPlaceholderAvatar = false;
                user.ImagePath = ResolveAvatarPath(uuid);
                user.FullSkinPath = ResolveFullSkinPath(uuid);
                user.NotifyAvatarChanged();
            }

            if (selectedAccount == account)
                UpdateSelectedDisplay(account);
        }

        private void AutoSelectIfSaved(Account account, string uuid)
        {
            if (uuid == SettingsService.Current.SelectedAccountUuid)
            {
                selectedAccount = account;
                MainWindowViewModel.MainModel.Account = account;
                MainWindowViewModel.MainModel.IsMicrosoftAccount = account is MicrosoftAccount;
                UpdateSelectedDisplay(account);
            }
        }

        /// <summary>
        /// 获取账户的 Uuid，兼容各账户类型。
        /// </summary>
        private static string GetAccountUuid(Account account)
        {
            return account switch
            {
                MicrosoftAccount ms => ms.Uuid.ToString(),
                YggdrasilAccount yg => yg.Uuid.ToString(),
                OfflineAccount off => off.Uuid.ToString(),
                _ => account.Name
            };
        }

        [DllImport("User32")]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("User32")]
        public static extern bool CloseClipboard();

        [DllImport("User32")]
        public static extern bool EmptyClipboard();

        [DllImport("User32")]
        public static extern bool IsClipboardFormatAvailable(int format);

        [DllImport("User32")]
        public static extern IntPtr GetClipboardData(int uFormat);

        [DllImport("User32", CharSet = CharSet.Unicode)]
        public static extern IntPtr SetClipboardData(int uFormat, IntPtr hMem);
        public static void SetText(string text)
        {
            // OpenClipboard 会被其它进程临时占用（剪贴板管理器、正在复制大量内容的程序），
            // 失败时重试即可。注意：绝不能在这里递归调用自己 ——
            // 一旦持续失败就是无限递归，直接栈溢出把进程带崩。
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        EmptyClipboard();

                        var handle = Marshal.StringToHGlobalUni(text);
                        if (SetClipboardData(13, handle) == IntPtr.Zero)
                        {
                            // 所有权没能交给系统，自己释放，避免这块内存泄漏
                            Marshal.FreeHGlobal(handle);
                        }
                    }
                    finally
                    {
                        CloseClipboard();
                    }

                    return;
                }

                System.Threading.Thread.Sleep(30);
            }
        }

        private async void AuthenticateMicrosoftAsync()
        {
            MicrosoftAuthenticator authenticator = new("f4c1c237-e68f-4866-a998-82f0db041c55");
            var authWindow = new MicrosoftAuthWindow();

            try
            {
                // 后台执行 DeviceFlow 认证
                var authTask = Task.Run(async () =>
                {
                    var token = await authenticator.DeviceFlowAuthAsync(dc =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Invoke(() => authWindow.SetCode(dc.UserCode, dc.VerificationUrl));
                    });
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() => Services.InlineHost.Close());
                    return token;
                });

                await AppLifetime.ShowDialogAsync(authWindow);

                if (authWindow.IsCancelled)
                    return;

                var oAuth2Token = await authTask;

                var userProfile = await authenticator.AuthenticateAsync(oAuth2Token);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await using var skinStream = await SkinProvider.GetMicrosoftSkinDataAsync(userProfile, cts.Token);

                using var ms = new MemoryStream();
                await skinStream.CopyToAsync(ms, cts.Token);
                var skinBytes = ms.ToArray();
                var resDir = Path.Combine(AppContext.BaseDirectory, "res");
                if (!Directory.Exists(resDir)) Directory.CreateDirectory(resDir);
                    var skinPath = Path.Combine(resDir, $"{userProfile.Uuid}.png");
                    SkinService.WriteHeadBitmap(skinPath, skinBytes);
                    // 同时保存完整皮肤（供皮肤管理与预览使用）
                    var fullSkinPath = Path.Combine(resDir, $"{userProfile.Uuid}_full.png");
                    File.WriteAllBytes(fullSkinPath, skinBytes);
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                    {
                        userNames.Add(userProfile.Name);
                        accounts.Add(userProfile);
                        users.Add(new User
                        {
                            Name = userProfile.Name,
                            Account = userProfile,
                            AccountType = "Microsoft",
                            SelectedCommand = new RelayCommand<Account>(Selected),
                            DeleteCommand = new RelayCommand<Account>(DeleteAccount),
                            SkinCommand = new RelayCommand<Account>(OpenSkinManager),
                            ImagePath = skinPath,
                            FullSkinPath = fullSkinPath
                        });
                        SettingsService.SaveMicrosoftAccount(userProfile);
                    });
            }
            catch (TaskCanceledException)
            {
                Services.InlineHost.Close();
            }
            catch (Exception ex)
            {
                Services.InlineHost.Close();
                await (AppServices.Dialogs?.ShowErrorAsync("错误", $"微软登录失败：{ex.Message}") ?? Task.CompletedTask);
            }
        }

        private async void AuthenticateYggdrasilAsync()
        {
            try
            {
                YggdrasilAuthenticatorWindow window = new();

                // 必须 await：外置账户信息由弹窗内的消息写入，早于弹窗关闭检查只会拿到 null。
                await AppLifetime.ShowDialogAsync(window);

                if (yggdrasilAccountProfile != null)
                {
                    YggdrasilAuthenticator authenticator = new(yggdrasilAccountProfile.Server, yggdrasilAccountProfile.Email, yggdrasilAccountProfile.Password);
                    var userProfile = await authenticator.AuthenticateAsync();
                    var account = userProfile.First() as YggdrasilAccount;

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await using var skinStream = await SkinProvider.GetYggdrasilSkinDataAsync(account, cts.Token);

                    using var ms = new MemoryStream();
                    await skinStream.CopyToAsync(ms, cts.Token);
                    var skinBytes = ms.ToArray();
                    var resDir = Path.Combine(AppContext.BaseDirectory, "res");
                    if (!Directory.Exists(resDir)) Directory.CreateDirectory(resDir);
                    var skinPath = Path.Combine(resDir, $"{account.Uuid}.png");
                    SkinService.WriteHeadBitmap(skinPath, skinBytes);
                    // 同时保存完整皮肤（供皮肤管理与预览使用）
                    var fullSkinPath = Path.Combine(resDir, $"{account.Uuid}_full.png");
                    File.WriteAllBytes(fullSkinPath, skinBytes);
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                    {
                        userNames.Add(account.Name);
                        accounts.Add(account);
                        users.Add(new User
                        {
                            Name = account.Name,
                            Account = account,
                            AccountType = "Yggdrasil",
                            SelectedCommand = new RelayCommand<Account>(Selected),
                            DeleteCommand = new RelayCommand<Account>(DeleteAccount),
                            SkinCommand = new RelayCommand<Account>(OpenSkinManager),
                            ImagePath = skinPath,
                            FullSkinPath = fullSkinPath
                        });
                        SettingsService.SaveYggdrasilAccount(account);
                    });
                }
            }
            catch (Exception ex)
            {
                _notification.Error($"用户登录失败" + "\n" + $"{ex.Message}", "错误");
            }
        }

        private async void AuthenticateOffline()
        {
            OfflineAuthenticatorWindow window = new();

            // 必须 await：用户名由弹窗内的 OfflineAccountProfile 消息写入，
            // 早于弹窗关闭就检查只会拿到上次的残留值（第一次为空 → 整个添加流程被跳过）。
            await AppLifetime.ShowDialogAsync(window);

            OfflineAuthenticator authenticator = new();
            if (!string.IsNullOrEmpty(offlineAccountName))
            {
                var userprofile = authenticator.Authenticate(offlineAccountName);

                // 使用史蒂夫皮肤
                var resDir = Path.Combine(AppContext.BaseDirectory, "res");
                if (!Directory.Exists(resDir)) Directory.CreateDirectory(resDir);
                var skinPath = Path.Combine(resDir, $"{userprofile.Uuid}.png");
                var fullSkinPath = Path.Combine(resDir, $"{userprofile.Uuid}_full.png");
                try
                {
                    // 从嵌入资源提取史蒂夫皮肤
                    using (var resourceStream = Avalonia.Platform.AssetLoader.Open(
                        new Uri("avares://VibrantbitLauncher.Avalonia/Assets/steve.png")))
                    using (var fs = new FileStream(fullSkinPath, FileMode.Create))
                    {
                        resourceStream.CopyTo(fs);
                    }
                    // 生成头部头像
                    var skinBytes = File.ReadAllBytes(fullSkinPath);
                    SkinService.WriteHeadBitmap(skinPath, skinBytes);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "离线账户默认皮肤生成失败");
                }

                Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                {
                    userNames.Add(userprofile.Name);
                    accounts.Add(userprofile);
                    users.Add(new User
                    {
                        Name = offlineAccountName,
                        Account = userprofile,
                        AccountType = "Offline",
                        SelectedCommand = new RelayCommand<Account>(Selected),
                        DeleteCommand = new RelayCommand<Account>(DeleteAccount),
                        SkinCommand = new RelayCommand<Account>(OpenSkinManager),
                        ImagePath = File.Exists(skinPath) ? skinPath : "/Assets/gravatar.png",
                        FullSkinPath = File.Exists(fullSkinPath) ? fullSkinPath : string.Empty
                    });
                    SettingsService.SaveOfflineAccount(userprofile);
                });
                _notification.Show($"离线用户{offlineAccountName}已创建", "提示", NotificationSeverity.Informational);
            }
        }

        void Selected(Account account)
        {
            selectedAccount = account;
            MainWindowViewModel.MainModel.Account = account;
            MainWindowViewModel.MainModel.IsMicrosoftAccount = account is MicrosoftAccount;
            SettingsService.SetSelectedAccount(GetAccountUuid(account));
            UpdateSelectedDisplay(account);
        }

        /// <summary>
        /// 删除账户：从列表和配置文件中移除。
        /// </summary>
        void DeleteAccount(Account account)
        {
            // 从 UI 列表移除
            var user = users.FirstOrDefault(u => u.Account == account);
            if (user != null)
                users.Remove(user);
            accounts.Remove(account);
            userNames.Remove(account.Name);

            // 从配置文件移除（统一使用 SettingsService 的删除方法）
            SettingsService.RemoveAccount(account);

            // 如果删除的是当前选中账户，清空选中状态
            if (selectedAccount == account)
            {
                selectedAccount = null;
                MainWindowViewModel.MainModel.Account = null;
                MainWindowViewModel.MainModel.IsMicrosoftAccount = false;
                SettingsService.SetSelectedAccount(string.Empty);
                UpdateSelectionFlags(null);
            }
        }
    }

    /// <summary>
    /// 账户卡片的数据模型。
    ///
    /// 实现 <see cref="ObservableObject"/> 是**必须**的：换皮肤后要就地更新 <see cref="ImagePath"/>，
    /// 如果类不发通知，界面上的头像不会刷新（此前用「把同一个对象重新塞回集合」来糊弄，
    /// 但 WPF 对同一实例的 Replace 会复用容器、且绑定属性没发通知，结果头像仍是旧的）。
    /// </summary>
    public class User : ObservableObject
    {
        public string Name { get; set; }
        public Account Account { get; set; }
        public string AccountType { get; set; }

        /// <summary>
        /// 账户类型的中文名。卡片上直接显示这个，而不是原始枚举串
        /// （「Microsoft」对普通玩家不如「微软账户」直观）。
        /// </summary>
        public string TypeDisplay => AccountType switch
        {
            "Microsoft" => "微软账户",
            "Yggdrasil" => "外置账户",
            "Offline" => "离线账户",
            _ => AccountType
        };

        private bool isSelected;
        /// <summary>
        /// 是否是「当前使用」的那个账户。
        ///
        /// 只由 <see cref="AccountPageViewModel"/> 统一刷新：卡片的高亮边框与右上角徽章
        /// 都绑这一个值。以前靠 RadioButton 的 GroupName 表达选中，但它和 VM 里的
        /// <c>selectedAccount</c> 是两份状态，自动恢复上次账户时两边会对不上。
        /// </summary>
        public bool IsSelected
        {
            get => isSelected;
            set => SetProperty(ref isSelected, value);
        }

        private bool isPlaceholderAvatar;

        /// <summary>
        /// 是否用 Win11（太阳谷）风格的默认账户图标代替头像。
        ///
        /// <para>
        /// 离线账户本来会生成一份史蒂夫皮肤当头像，但在「账户」这个界面里，
        /// 一排史蒂夫脸既分不清谁是谁，也和 Win11 的系统账户观感不搭。
        /// 这里改成：<b>没换过自定义皮肤的离线账户</b>显示太阳谷账户图标，
        /// 一旦用户真的换过皮肤（有 <c>res/&lt;uuid&gt;.custom</c> 标记）就显示真实头像。
        /// </para>
        /// </summary>
        public bool IsPlaceholderAvatar
        {
            get => isPlaceholderAvatar;
            set => SetProperty(ref isPlaceholderAvatar, value);
        }

        public RelayCommand<Account> SelectedCommand { get; set; }
        public RelayCommand<Account> DeleteCommand { get; set; }
        public RelayCommand<Account> SkinCommand { get; set; }

        private string imagePath = @"/Assets/gravatar.png";
        private string fullSkinPath = string.Empty;

        /// <summary>
        /// 头像「代次」。每换一次皮肤就 +1，用来强制 <see cref="ImagePath"/> 重新通知界面。
        /// 因为换肤后头像文件路径**并没有变化**（还是 <c>res/{uuid}.png</c>），
        /// 只赋值同名字符串时 <c>SetProperty</c> 认为「值没变」而不发通知，
        /// 界面就会一直停在旧头像上。
        /// </summary>
        private int avatarRevision;

        /// <summary>卡片头像图片路径。变更会通知界面刷新（换皮肤后靠它生效）。</summary>
        public string ImagePath
        {
            get => imagePath;
            set => SetProperty(ref imagePath, value);
        }

        /// <summary>完整皮肤文件路径。</summary>
        public string FullSkinPath
        {
            get => fullSkinPath;
            set => SetProperty(ref fullSkinPath, value);
        }

        /// <summary>
        /// 通知界面：头像已更新，请重新加载。
        /// 即便图片路径没变（换肤场景就是这样），也要让绑定重新求值、
        /// 让转换器重新读一次文件，否则界面会停在旧头像。
        /// </summary>
        public void NotifyAvatarChanged()
        {
            avatarRevision++;
            OnPropertyChanged(nameof(ImagePath));
            OnPropertyChanged(nameof(FullSkinPath));
        }
    }

    public class YggdrasilAccountProfile
    {
        public string Server { get; set; }
        public string Email { get; set; }
        public string Password { get; set; }
    }
}
