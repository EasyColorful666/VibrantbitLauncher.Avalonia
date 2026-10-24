using MinecraftLaunch.Base.Models.Authentication;
using VibrantbitLauncher.ViewModels.Windows;

namespace VibrantbitLauncher.Services
{
    /// <summary>
    /// 启动时恢复「上次使用的账户」。
    ///
    /// <para>
    /// 以前账户只在打开「账户」页时才被读回来（<c>AccountPageViewModel.Load</c> →
    /// <c>RestoreSavedAccounts</c>）。也就是说：启动器一打开就点「启动游戏」，
    /// <see cref="Models.MainModel.Account"/> 还是 null，只会得到一句「请先登录一个账户」，
    /// 哪怕配置里明明白白记着上次用的是谁。用户看到的就是「每次开机都得先去账户页点一下」。
    /// </para>
    ///
    /// <para>
    /// 这里在配置读完之后、窗口建出来之前做一次**纯本地**的恢复：
    /// 只按 <see cref="AppSettings.SelectedAccountUuid"/> 在已保存的账户里找回同一个对象，
    /// 不联网（微软账户的 access token 刷新仍由启动流程在真正启动游戏前完成，
    /// 见 <c>RunPageViewModel</c>）。这样既不会拖慢启动，也不会因为离线而失败。
    /// </para>
    /// </summary>
    public static class AccountRestorer
    {
        /// <summary>
        /// 按配置恢复上次选择的账户；没有记录 / 记录已失效时安全返回。
        /// </summary>
        public static void Restore()
        {
            var settings = SettingsService.Current;
            var uuid = settings.SelectedAccountUuid;

            if (string.IsNullOrWhiteSpace(uuid))
            {
                Serilog.Log.Information("账户恢复：配置里没有上次选择的账户，跳过");
                return;
            }

            var account = FindByUuid(settings, uuid);
            if (account is null)
            {
                // 账户被删了但 uuid 还留着 —— 清掉，免得界面以为「已选」
                // 而实际启动时又找不到人。
                Serilog.Log.Information("账户恢复：uuid {Uuid} 已不在账户列表里，清除选中记录", uuid);
                SettingsService.SetSelectedAccount(string.Empty);
                MainWindowViewModel.MainModel.Account = null;
                MainWindowViewModel.MainModel.IsMicrosoftAccount = false;
                return;
            }

            MainWindowViewModel.MainModel.Account = account;
            MainWindowViewModel.MainModel.IsMicrosoftAccount = account is MicrosoftAccount;

            Serilog.Log.Information("账户恢复：已恢复上次登录的账户 {Name}（{Type}）",
                account.Name, account.GetType().Name);
        }

        /// <summary>
        /// 在已保存的三类账户里按 uuid 找同一个实例。
        ///
        /// 必须是**配置里的那个对象实例**：账户页会把 <c>SettingsService.Current</c> 里的对象
        /// 直接放进列表，只有同名同实例，卡片上的「当前使用」标记才能对上
        /// （那里用的是 <c>ReferenceEquals</c>）。
        /// </summary>
        private static Account? FindByUuid(AppSettings settings, string uuid)
        {
            foreach (var account in settings.OfflineAccounts)
            {
                if (string.Equals(account.Uuid.ToString(), uuid, StringComparison.OrdinalIgnoreCase))
                    return account;
            }

            foreach (var account in settings.MicrosoftAccounts)
            {
                if (string.Equals(account.Uuid.ToString(), uuid, StringComparison.OrdinalIgnoreCase))
                    return account;
            }

            foreach (var account in settings.YggdrasilAccounts)
            {
                if (string.Equals(account.Uuid.ToString(), uuid, StringComparison.OrdinalIgnoreCase))
                    return account;
            }

            return null;
        }
    }
}
