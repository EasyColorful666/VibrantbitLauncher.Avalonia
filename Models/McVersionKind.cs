using System;
using System.Collections.Generic;

namespace VibrantbitLauncher.Models
{
    /// <summary>
    /// 下载页的版本大类。四个值对应界面上的四个筛选勾选框。
    /// </summary>
    public enum McVersionKind
    {
        /// <summary>正式版（清单里 <c>type = release</c>）。</summary>
        Release,

        /// <summary>测试版 / 快照（清单里 <c>type = snapshot</c>）。</summary>
        Snapshot,

        /// <summary>远古版（清单里 <c>type = old_alpha / old_beta</c>）。</summary>
        Legacy,

        /// <summary>愚人节版（官方清单把它们标成 snapshot，只能按 id 认）。</summary>
        AprilFools,
    }

    /// <summary>
    /// 版本 ID → 大类 的判定。
    ///
    /// <para>
    /// 为什么要单独一份愚人节名单：Mojang 的版本清单里**没有**「愚人节」这个 type，
    /// 那些版本（15w14a、3D Shareware v1.34、24w14potato …）的 <c>type</c> 一律是
    /// <c>snapshot</c>。不按 id 认的话，它们会被混进「测试版」，而玩家点开
    /// 「测试版」时通常想看的是真正的开发快照，不是梗版本。
    /// </para>
    ///
    /// <para>
    /// 名单来自 2026-10 实测的 920 条清单，8 个愚人节版本全部命中。
    /// 以后新增愚人节版本时，只需要往 <see cref="AprilFoolsVersions"/> 里加一行 id；
    /// 认不出来最坏的结果是它落回「测试版」，不会丢版本。
    /// </para>
    /// </summary>
    public static class McVersionKindClassifier
    {
        /// <summary>
        /// 愚人节版本的 id 白名单（忽略大小写）。
        /// 实测清单里 <c>22w13oneBlockAtATime</c> 的实际 id 是全小写，所以不能按原文比对。
        /// </summary>
        private static readonly HashSet<string> AprilFoolsVersions = new(StringComparer.OrdinalIgnoreCase)
        {
            "15w14a",                 // 2015 Love and Hugs
            "1.RV-Pre1",              // 2016 Trendy
            "3D Shareware v1.34",     // 2019
            "20w14infinite",          // 2020
            "22w13oneBlockAtATime",   // 2022
            "23w13a_or_b",            // 2023
            "24w14potato",            // 2024
            "25w14craftmine",         // 2025
        };

        /// <summary>
        /// 判定一个大类。<paramref name="type"/> 为空或未知时按「正式版」处理 ——
        /// 宁可多显示，也不要让某条版本从所有筛选里同时消失（那等于凭空丢版本）。
        /// </summary>
        public static McVersionKind Classify(string? version, string? type)
        {
            if (!string.IsNullOrWhiteSpace(version) && AprilFoolsVersions.Contains(version.Trim()))
                return McVersionKind.AprilFools;

            return (type ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "snapshot" => McVersionKind.Snapshot,
                "old_alpha" or "old_beta" => McVersionKind.Legacy,
                _ => McVersionKind.Release,
            };
        }
    }
}
