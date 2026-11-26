using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;

// Controlled test child process. Never included in the shipped application.
Console.OutputEncoding = Encoding.UTF8;
Console.Error.Write("");
var modePath = Path.Combine(AppContext.BaseDirectory, "fixture.mode");
var mode = File.Exists(modePath) ? File.ReadAllText(modePath) : "version";
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fixture.pid"), Environment.ProcessId.ToString());
if (args.Contains("--fixture-child"))
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fixture.child.pid"), Environment.ProcessId.ToString());
    await Task.Delay(TimeSpan.FromSeconds(60)); return 0;
}
switch (mode)
{
    case "wait": await Task.Delay(TimeSpan.FromSeconds(60)); break;
    case "fail": Console.Error.WriteLine("controlled failure"); return 27;
    case "npm": Console.WriteLine("11.19.0"); break;
    case "launcher":
        if ((Marshal.PtrToStringUni(NativeCommandLine.GetCommandLineW()) ?? "").EndsWith(" -0p", StringComparison.Ordinal)) Console.WriteLine(" -V:3.14 * C:\\Python314\\python.exe");
        else { Console.Error.WriteLine("The legacy launcher did not recognize the quoted switch."); return 2; }
        break;
    case "orphan":
        using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--fixture-child" } }))
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!File.Exists(Path.Combine(AppContext.BaseDirectory, "fixture.child.pid")) && DateTime.UtcNow < deadline) await Task.Delay(10);
            Console.WriteLine("parent finished");
        }
        break;
    case "flood":
        for (var i = 0; i < 256; i++) { Console.Write(new string('A', 2048)); Console.Error.Write(new string('B', 2048)); }
        break;
    case "environment": Console.WriteLine(Environment.GetEnvironmentVariable("NODE_OPTIONS") ?? "clean"); break;
    default: Console.WriteLine("git version 2.50.1.windows.1 中文 路径"); break;
}
return 0;

internal static class NativeCommandLine
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetCommandLineW();
}
