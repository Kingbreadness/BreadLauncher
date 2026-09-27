using System;
using System.Runtime.InteropServices;
using System.Threading;

internal static class MarshalTest
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHCreateItemFromParsingName")]
    private static extern int SHCreateItemFromParsingNameAlpha(string pszPath, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHCreateItemFromParsingName")]
    private static extern int SHCreateItemFromParsingNameBeta(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc,
        [In, MarshalAs(UnmanagedType.LPStruct)] ref Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHCreateItemFromParsingName")]
    private static extern int SHCreateItemFromParsingNameGamma(string pszPath, IntPtr pbc, IntPtr riid, out IntPtr ppv);

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr p, int f);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();

    private static readonly Guid IID = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private static void Try(string tag, Func<string, IntPtr> f, string target)
    {
        IntPtr ppv = IntPtr.Zero;
        int hr = 0;
        try { ppv = f(target); } catch (Exception e) { Console.WriteLine(tag + "  异常 " + e.GetType().Name + " " + e.Message); return; }
        Console.WriteLine(tag.PadRight(8) + " hr=0x" + hr.ToString("X8") + "  ppv=" + (ppv != IntPtr.Zero));
    }

    private static void Body(string target)
    {
        Console.WriteLine("公寓=" + Thread.CurrentThread.GetApartmentState() + "  目标=" + target);
        Guid g1 = IID, g2 = IID, g3 = IID;
        IntPtr p1 = IntPtr.Zero, p2 = IntPtr.Zero, p3 = IntPtr.Zero;
        int hr1 = SHCreateItemFromParsingName(target, IntPtr.Zero, ref g1, out p1);
        Console.WriteLine("A-裸ref  hr=0x" + hr1.ToString("X8") + "  ppv=" + (p1 != IntPtr.Zero));
        int hr2 = SHCreateItemFromParsingNameAlpha(target, IntPtr.Zero, ref g2, out p2);
        Console.WriteLine("B-别名  hr=0x" + hr2.ToString("X8") + "  ppv=" + (p2 != IntPtr.Zero));
        int hr3 = SHCreateItemFromParsingNameBeta(target, IntPtr.Zero, ref g3, out p3);
        Console.WriteLine("C-LPStruct hr=0x" + hr3.ToString("X8") + "  ppv=" + (p3 != IntPtr.Zero));
        IntPtr mem = Marshal.AllocHGlobal(16);
        Marshal.StructureToPtr(IID, mem, false);
        IntPtr p4 = IntPtr.Zero;
        int hr4 = SHCreateItemFromParsingNameGamma(target, IntPtr.Zero, mem, out p4);
        Console.WriteLine("D-IntPtr hr=0x" + hr4.ToString("X8") + "  ppv=" + (p4 != IntPtr.Zero));
        Marshal.FreeHGlobal(mem);
    }

    private static void Mta(object state)
    {
        CoInitializeEx(IntPtr.Zero, 0);
        Console.WriteLine("--- MTA 线程 ---");
        Body((string)state);
        CoUninitialize();
    }

    [STAThread]
    private static void Main(string[] args)
    {
        string target = args.Length > 0 ? args[0] : "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
        Body(target);
        Thread t = new Thread(Mta);
        t.SetApartmentState(ApartmentState.MTA);
        t.Start(target);
        t.Join();
    }
}