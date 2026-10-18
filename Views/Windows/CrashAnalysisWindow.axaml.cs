using Avalonia.Controls;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
using MinecraftLaunch.Base.Enums;
using MinecraftLaunch.Base.Models.Game;
using MinecraftLaunch.Components.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VibrantbitLauncher.Helpers;
using VibrantbitLauncher.Services;

namespace VibrantbitLauncher.Views.Windows
{
    /// <summary>
    /// 崩溃分析窗口（Avalonia 版）。
    ///
    /// <para>
    /// 两个数据来源：<see cref="LogAnalyzer"/>（说原因）+ <see cref="CrashRepairService"/>（给动作）。
    /// 原因列表已翻成中文 —— 直接显示 <see cref="CrashReasons"/> 的枚举名（如
    /// <c>UnsupportedJavaClassVersionError</c>）对玩家没有任何意义。
    /// </para>
    /// </summary>
    public partial class CrashAnalysisWindow : UserControl, Services.IInlinePage
    {

    /// <summary>内嵌到主窗口时，顶栏显示的标题。</summary>
    public string InlineTitle => "崩溃分析";

        private readonly string _crashReportFolder;
        private readonly List<RepairActionItem> _repairs = new();
        private bool _busy;

        public CrashAnalysisWindow(MinecraftEntry? minecraft)
        {
            InitializeComponent();

            var mcFolder = Path.GetFullPath(SettingsService.ResolveMinecraftFolder());
            _crashReportFolder = Path.Combine(mcFolder, "crash-reports");

            var reasons = new List<CrashReasons>();
            var suspicious = new List<string>();

            if (minecraft != null)
            {
                try
                {
                    var analyzer = new LogAnalyzer(minecraft);
                    var result = analyzer.Analyze();

                    if (result?.CrashReasons is { } rs)
                        reasons.AddRange(rs);
                    if (result?.SuspiciousMods is { } sm)
                        suspicious.AddRange(sm.Where(s => !string.IsNullOrWhiteSpace(s)));
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "崩溃分析失败（继续给出通用建议）");
                }
            }

            // ---- 崩溃原因（中文） ----
            foreach (var reason in reasons.Distinct())
                ReasonsList.Items.Add(CrashRepairService.Describe(reason));

            if (ReasonsList.ItemCount == 0)
                ReasonsList.Items.Add("未能自动识别出明确原因，请查看崩溃报告文件。");

            // ---- 可疑模组 ----
            foreach (var mod in suspicious.Distinct(StringComparer.OrdinalIgnoreCase))
                SuspiciousModsList.Items.Add(mod);

            // 没有可疑模组时整块隐藏，避免一排「（空）」占地方
            SuspiciousPanel.IsVisible = SuspiciousModsList.ItemCount > 0;

            // ---- 一键修复 ----
            var crashText = TryReadCrashText();
            var actions = CrashRepairService.Build(minecraft, reasons, suspicious, crashText);

            foreach (var action in actions)
                _repairs.Add(new RepairActionItem(action));

            RepairList.ItemsSource = _repairs;
            RepairEmptyText.IsVisible = _repairs.Count == 0;
            RepairSection.IsVisible = _repairs.Count > 0;
            FixAllButton.IsEnabled = _repairs.Any(r => r.CanExecute);
        }

        // ==================== 修复动作 ====================

        private async void RepairButton_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: RepairActionItem item })
                return;

            await RunAsync(new[] { item });
        }

        private async void FixAllButton_Click(object? sender, RoutedEventArgs e)
        {
            var pending = _repairs.Where(r => r.CanExecute).ToList();
            if (pending.Count == 0)
                return;

            await RunAsync(pending);
        }

        private async Task RunAsync(IReadOnlyList<RepairActionItem> items)
        {
            if (_busy)
                return;

            _busy = true;
            try
            {
                var ok = 0;
                var failed = new List<string>();

                foreach (var item in items)
                {
                    var outcome = await item.ApplyAsync();
                    if (outcome.Success)
                        ok++;
                    else
                        failed.Add(outcome.Message);
                }

                FixAllButton.IsEnabled = _repairs.Any(r => r.CanExecute);

                if (failed.Count == 0)
                {
                    await ShowMessageAsync("修复完成",
                        $"已执行 {ok} 项修复。请重新启动游戏验证；如果问题依旧，可再次点「一键修复」查看剩余建议。");
                }
                else
                {
                    await ShowMessageAsync("部分修复未能完成",
                        $"成功 {ok} 项，{failed.Count} 项需要手动处理：\n\n"
                        + string.Join("\n", failed.Take(5)));
                }
            }
            finally
            {
                _busy = false;
            }
        }

        // ==================== 其它按钮 ====================

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Services.InlineHost.Close();

        private void OpenCrashFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Directory.Exists(_crashReportFolder))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", _crashReportFolder) { UseShellExecute = true });
                }
                else
                {
                    _ = ShowMessageAsync("提示", "崩溃报告目录不存在：" + _crashReportFolder);
                }
            }
            catch (Exception ex)
            {
                _ = ShowMessageAsync("错误", "无法打开目录：" + ex.Message);
            }
        }

        /// <summary>显示一个跟随主题的消息框。</summary>
        private Task ShowMessageAsync(string title, string message)
        {
            var box = new FAContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "确定",
                DefaultButton = FAContentDialogButton.Primary,
            };

            return box.ShowAsync();
        }

        /// <summary>
        /// 读一份崩溃报告文本用于补充判断（LogAnalyzer 覆盖不到的信号，如 OutOfMemoryError）。
        /// 取 crash-reports 里最新的一份；没有再退回 logs/latest.log 的尾部。
        /// </summary>
        private string? TryReadCrashText()
        {
            try
            {
                if (Directory.Exists(_crashReportFolder))
                {
                    var newest = new DirectoryInfo(_crashReportFolder)
                        .GetFiles("*.txt")
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .FirstOrDefault();

                    if (newest is not null)
                        return ReadHead(newest.FullName, 128 * 1024);
                }

                var latestLog = Path.Combine(GameInstanceHelper.GetMinecraftFolder(), "logs", "latest.log");
                if (File.Exists(latestLog))
                    return ReadTail(latestLog, 128 * 1024);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "读取崩溃报告文本失败");
            }

            return null;
        }

        private static string ReadHead(string path, int maxBytes)
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(maxBytes, stream.Length)];
            var read = stream.Read(buffer, 0, buffer.Length);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }

        private static string ReadTail(string path, int maxBytes)
        {
            using var stream = File.OpenRead(path);
            var len = (int)Math.Min(maxBytes, stream.Length);
            stream.Seek(-len, SeekOrigin.End);
            var buffer = new byte[len];
            var read = stream.Read(buffer, 0, len);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
    }

    /// <summary>
    /// 修复动作的界面包装：负责「执行一次后按钮变成结果文案」这点状态。
    /// </summary>
    public sealed class RepairActionItem : INotifyPropertyChanged
    {
        private readonly RepairAction _action;
        private string _resultText = string.Empty;
        private bool _hasResult;
        private bool _done;

        public RepairActionItem(RepairAction action) => _action = action;

        public string Title => _action.Title;
        public string Detail => _action.Detail;
        public string KindText => _action.KindText;
        public string ActionLabel => _action.ActionLabel;

        /// <summary>能执行、且还没执行过。</summary>
        public bool CanExecute => _action.CanExecute && !_done && _action.Execute is not null;

        /// <summary>有执行体才显示按钮；纯建议不显示按钮（避免点了个没反应的控件）。</summary>
        public bool ShowButton => _action.Execute is not null;

        public string ResultText
        {
            get => _resultText;
            private set
            {
                _resultText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResultText)));
            }
        }

        public bool HasResult
        {
            get => _hasResult;
            private set
            {
                _hasResult = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasResult)));
            }
        }

        public async Task<RepairOutcome> ApplyAsync()
        {
            if (_action.Execute is null)
                return RepairOutcome.Fail("这条只是建议，需要手动处理");

            RepairOutcome outcome;
            try
            {
                outcome = await _action.Execute(System.Threading.CancellationToken.None);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "执行修复失败：{Title}", _action.Title);
                outcome = RepairOutcome.Fail($"{_action.Title}：{ex.Message}");
            }

            // 失败时保留按钮（用户可能修好外部条件后想重试）；成功则收起按钮
            _done = outcome.Success;
            ResultText = (outcome.Success ? "✓ " : "✗ ") + outcome.Message;
            HasResult = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanExecute)));

            return outcome;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
