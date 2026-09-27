using System;
using System.Collections.Generic;
using System.Reflection;

class AppsProbe
{
    [STAThread]
    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string[] want = new string[] { "任务管理器", "事件查看器", "计算机管理", "性能监视器", "资源监视器", "本地安全策略", "任务计划程序", "App Recovery" };
        Type t = Type.GetTypeFromProgID("Shell.Application");
        object shell = Activator.CreateInstance(t);
        object folder = shell.GetType().InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, new object[] { "shell:AppsFolder" });
        object items = folder.GetType().InvokeMember("Items", BindingFlags.InvokeMethod, null, folder, null);
        int count = Convert.ToInt32(items.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, items, null));
        int shown = 0;
        for (int i = 0; i < count && shown < 4; i++)
        {
            object item = items.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, items, new object[] { i });
            string name = Convert.ToString(item.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, item, null));
            string path = Convert.ToString(item.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, item, null));
            bool hit = false;
            for (int w = 0; w < want.Length; w++) if (string.Equals(name, want[w], StringComparison.Ordinal)) hit = true;
            if (!hit) continue;
            shown++;
            Console.WriteLine("======== " + name + "   Path=" + path);
            string[] keys = new string[] {
                "System.Link.TargetParsingPath", "System.AppUserModel.ID", "System.AppUserModel.RelaunchCommand",
                "System.AppUserModel.RelaunchIconResource", "System.AppUserModel.RelaunchDisplayNameResource",
                "System.ItemPathDisplay", "System.IconLocation", "System.Link.Arguments", "System.Link.TargetPath",
                "System.FileDescription", "System.Software.ProductName" };
            foreach (string k in keys)
            {
                object v = null;
                try { v = item.GetType().InvokeMember("ExtendedProperty", BindingFlags.InvokeMethod, null, item, new object[] { k }); }
                catch (Exception ex) { v = "<异常:" + ex.GetType().Name + ">"; }
                if (v != null) Console.WriteLine("   " + k + " = " + v);
            }
        }
    }
}