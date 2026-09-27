// Launcher.cs —— 启动一个应用。只做一件事：把「怎么启动」的**三种**分支写清楚。
//   ① 协议地址（含 ://，例如 steam://、ms-settings:）→ 交给 Shell 打开
//   ② 磁盘上的真实文件（.exe / .lnk / .url）→ 交给 Shell 打开
//   ③ UWP / 商店应用（AUMID，没有独立 exe）→ 走 explorer.exe shell:AppsFolder\<AUMID>
//   ★以前这里写的是「两种」—— 漏掉的恰好是**失败率最高的第 ③ 种**。改分支之前先数一遍。

using System;
using System.Diagnostics;
using System.IO;

namespace BreadLauncher
{
    public static class Launcher
    {
        /// <summary>路径里有没有 <c>://</c>（协议地址，例如 <c>steam://</c>）。**没有任何 IO，也不会启动东西。**</summary>
        private static bool IsProtocol(string path)
        {
            return !string.IsNullOrEmpty(path) && path.IndexOf("://", StringComparison.Ordinal) >= 0;
        }

        /// <summary>启动成功返回 true；失败返回 false 并把原因写到 outError。</summary>
        public static bool Launch(AppEntry entry, out string outError)
        {
            outError = null;
            if (entry == null || string.IsNullOrEmpty(entry.Path))
            {
                outError = "这个条目没有可用的启动路径。";
                return false;
            }

            try
            {
                if (IsProtocol(entry.Path))
                {
                    // steam:// 这类协议地址不能走 explorer，直接交给 ShellExecute 解析
                    ProcessStartInfo shell = new ProcessStartInfo(entry.Path);
                    shell.UseShellExecute = true;
                    Process.Start(shell);
                    return true;
                }

                if (entry.IsRealFile)
                {
                    ProcessStartInfo psi = new ProcessStartInfo(entry.Path);
                    psi.UseShellExecute = true;
                    psi.WorkingDirectory = Path.GetDirectoryName(entry.Path);
                    Process.Start(psi);
                    return true;
                }

                // UWP / 打包应用：必须交给 explorer 去解析 AUMID，直接 Process.Start 会报「找不到文件」。
                ProcessStartInfo p = new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + entry.Path);
                p.UseShellExecute = false;
                p.CreateNoWindow = true;
                Process.Start(p);
                return true;
            }
            catch (Exception ex)
            {
                outError = ex.Message;
                return false;
            }
        }
    }
}