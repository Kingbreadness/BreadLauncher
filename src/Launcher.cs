// Launcher.cs —— 启动一个应用。只做一件事：把「怎么启动」的**三种**分支写清楚。
//   ① 协议地址（含 ://，例如 steam://、ms-settings:）→ 交给 Shell 打开
//   ② 磁盘上的真实文件（.exe / .lnk / .url）→ 交给 Shell 打开（**只有这一种能「以管理员身份运行」**）
//   ③ UWP / 商店应用（AUMID，没有独立 exe）→ 走 explorer.exe shell:AppsFolder\<AUMID>
//   ★以前这里写的是「两种」—— 漏掉的恰好是**失败率最高的第 ③ 种**。改分支之前先数一遍。
//
// ★★2026-11-06 新增「按应用提权」：用户场景是「某个软件平时能开，但要管理员才解锁全部功能」。
//   所以不是把整个面板提权，而是**只对某个条目**用 ShellExecute 的 `runas` verb（每次弹一次 UAC）。
//   ③ 那种（UWP / 协议项）没有独立 exe，提不了权 —— 调用方会收到 ElevationUnsupported 再回退普通启动。

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace BreadLauncher
{
    /// <summary>启动结果。★别用 bool：用户自己取消 UAC（返回 false）和真的启动失败，处置方式完全不同。</summary>
    public enum LaunchResult
    {
        /// <summary>启动成功（包括「已经以管理员身份在跑，这次没再弹 UAC」）。</summary>
        Ok,
        /// <summary>用户在 UAC 对话框上点了「否」。**不是错误**：不弹提示、不关面板，当没发生。</summary>
        Cancelled,
        /// <summary>这个条目提不了权（UWP / 协议地址，没有独立 exe）。调用方应回退普通启动并告诉用户。</summary>
        ElevationUnsupported,
        /// <summary>真的失败（路径不对、权限不够、被拦）。原因在 outError。</summary>
        Failed
    }

    public static class Launcher
    {
        /// <summary>路径里有没有 <c>://</c>（协议地址，例如 <c>steam://</c>）。**没有任何 IO，也不会启动东西。**</summary>
        private static bool IsProtocol(string path)
        {
            return !string.IsNullOrEmpty(path) && path.IndexOf("://", StringComparison.Ordinal) >= 0;
        }

        /// <summary>这个条目**能不能**以管理员身份启动：只有「磁盘上的真实文件」可以（UWP / 协议地址没有 exe 可提权）。</summary>
        public static bool CanElevate(AppEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Path)) return false;
            if (IsProtocol(entry.Path)) return false;
            return entry.IsRealFile;
        }

        /// <summary>
        /// **纯函数**：算出这次要怎么启动（不真的启动）—— 探针直接断言这个，不用真的弹 UAC。
        /// 返回 null 表示参数不成立（原因在 outError）。
        /// </summary>
        public static ProcessStartInfo BuildStartInfo(AppEntry entry, bool asAdmin, out string outError)
        {
            outError = null;
            if (entry == null || string.IsNullOrEmpty(entry.Path))
            {
                outError = "这个条目没有可用的启动路径。";
                return null;
            }

            if (IsProtocol(entry.Path))
            {
                // steam:// 这类协议地址不能走 explorer，直接交给 ShellExecute 解析（也无法提权）
                ProcessStartInfo shell = new ProcessStartInfo(entry.Path);
                shell.UseShellExecute = true;
                return shell;
            }

            if (entry.IsRealFile)
            {
                ProcessStartInfo psi = new ProcessStartInfo(entry.Path);
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(entry.Path);
                // ★提权就靠这一个 verb：ShellExecute 的 runas 会触发 UAC。只对真实文件设。
                if (asAdmin) psi.Verb = "runas";
                return psi;
            }

            // UWP / 打包应用：必须交给 explorer 去解析 AUMID，直接 Process.Start 会报「找不到文件」。
            // （explorer 会以当前登录用户身份解析，所以商店应用提不了权 —— 这也是 CanElevate 返回 false 的原因）
            ProcessStartInfo p = new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + entry.Path);
            p.UseShellExecute = false;
            p.CreateNoWindow = true;
            return p;
        }

        /// <summary>
        /// 启动。asAdmin=true 时走「以管理员身份运行」（只对真实文件有效；别的条目返回 ElevationUnsupported，
        /// 由调用方决定怎么回退）。**成功返回 Ok**；用户取消 UAC 返回 Cancelled（不是错误）。
        /// </summary>
        public static LaunchResult Launch(AppEntry entry, bool asAdmin, out string outError)
        {
            outError = null;
            if (asAdmin && CanElevate(entry) == false)
            {
                outError = "这个应用没有独立的 exe（商店应用 / 协议链接），没法以管理员身份运行。";
                return LaunchResult.ElevationUnsupported;
            }

            ProcessStartInfo psi = BuildStartInfo(entry, asAdmin, out outError);
            if (psi == null) return LaunchResult.Failed;

            try
            {
                Process.Start(psi);
                return LaunchResult.Ok;
            }
            catch (Win32Exception wex)
            {
                // 1223 = ERROR_CANCELLED：用户在 UAC 上点了「否」。这是用户的选择，不是故障。
                if (wex.NativeErrorCode == 1223)
                {
                    outError = null;
                    return LaunchResult.Cancelled;
                }
                outError = wex.Message;
                return LaunchResult.Failed;
            }
            catch (Exception ex)
            {
                outError = ex.Message;
                return LaunchResult.Failed;
            }
        }

        /// <summary>老签名（等价于 asAdmin=false），给不想关心提权的地方用。</summary>
        public static bool Launch(AppEntry entry, out string outError)
        {
            return Launch(entry, false, out outError) == LaunchResult.Ok;
        }
    }
}
