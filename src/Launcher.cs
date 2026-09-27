// Launcher.cs —— 启动一个应用。只做一件事：把「怎么启动」的两种分支写清楚。

using System;
using System.Diagnostics;
using System.IO;

namespace BreadLauncher
{
    public static class Launcher
    {
        /// <summary>启动成功返回 true；失败返回 false 并把原因写到 outError。</summary>
        private static bool IsProtocol(string path)
        {
            return !string.IsNullOrEmpty(path) && path.IndexOf("://", StringComparison.Ordinal) >= 0;
        }

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