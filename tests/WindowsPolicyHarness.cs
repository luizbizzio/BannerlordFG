// SPDX-License-Identifier: GPL-3.0-only
// Compile and execute with RunWindowsPolicyTests.cmd. Tests production C# rules,
// real process path checks and the process-lifetime marker, not string patterns.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BannerlordFG.Common;
using BannerlordFG.Setup;

internal class WindowsPolicyHarness
{
    private static int passed;
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAILED: " + name);
        passed++;
        Console.WriteLine("PASS " + name);
    }
    private static RendererDecision.Host H(int pid, bool direct = false,
        bool module = false, bool caller = false)
    {
        return new RendererDecision.Host { Pid = pid, DirectGame = direct,
            ActiveModule = module, InGameCaller = caller };
    }
    private static RendererDecision.Activity A(int pid, ulong luid, double usage)
    {
        return new RendererDecision.Activity { Pid = pid, Luid = luid, Percent = usage };
    }
    private static void Selected(string name, RendererDecision.Host[] hosts,
        RendererDecision.Activity[] activity, int pid, ulong luid)
    {
        var choice = RendererDecision.Select(hosts, activity);
        Check(choice != null && choice.Pid == pid && choice.Luid == luid, name);
    }
    private static void PolicyTests()
    {
        // IDs 1/2 are illustrative Intel/NVIDIA/AMD adapter LUIDs. Selection
        // never receives the vendor name or GPU power ranking.
        Selected("Intel + NVIDIA: RTX game PID overrides active Intel launcher",
            new[] { H(101, caller:true), H(102, direct:true) },
            new[] { A(101, 1, 15), A(102, 2, 8) }, 102, 2);
        Selected("Intel + AMD: AMD game PID overrides active Intel launcher",
            new[] { H(101, caller:true), H(102, direct:true) },
            new[] { A(101, 1, 25), A(102, 3, 9) }, 102, 3);
        Selected("BLSE LauncherEx hosts game: in-game caller can render",
            new[] { H(201, caller:true) }, new[] { A(201, 2, 44) }, 201, 2);
        Selected("BLSE LauncherEx hosts game: live module marker",
            new[] { H(202, module:true) }, new[] { A(202, 3, 44) }, 202, 3);
        Selected("Vanilla launcher hosts game: in-game caller",
            new[] { H(203, caller:true) }, new[] { A(203, 1, 46) }, 203, 1);
        Selected("Game exe renderer and idle Launcher.Native",
            new[] { H(210, direct:true), H(211) },
            new[] { A(210, 2, 33), A(211, 1, 88) }, 210, 2);
        Selected("Direct game processes use measured LUID, not NVIDIA preference",
            new[] { H(220, direct:true) }, new[] { A(220, 1, 70), A(220, 2, 8) }, 220, 1);
        Selected("Active game on dedicated adapter of same vendor",
            new[] { H(221, direct:true) }, new[] { A(221, 4, 70), A(221, 5, 8) }, 221, 4);
        Check(RendererDecision.Select(new[] { H(230) },
            new[] { A(230, 1, 88) }) == null, "Idle Intel launcher is never a game renderer");
        Check(RendererDecision.Select(new[] { H(231, direct:true) },
            new[] { A(231, 1, 45), A(231, 2, 35) }) == null,
            "Competing adapters for one game PID must be unambiguous");
        Check(RendererDecision.Select(new[] { H(232, direct:true), H(233, direct:true) },
            new[] { A(232, 1, 12), A(233, 2, 11) }) == null,
            "Competing verified game processes remain ambiguous");
        Check(RendererDecision.Select(new[] { H(240, direct:true) },
            new[] { A(240, 1, double.NaN), A(240, 2, .1) }) == null,
            "Invalid and idle GPU counters never select an adapter");
        Check(GameRunningDecision.IsBusy(true,false,false,true), "Running native game blocks DLL edits");
        Check(GameRunningDecision.IsBusy(false,true,false,true), "BLSE active module blocks DLL edits");
        Check(GameRunningDecision.IsBusy(false,false,true,true), "Loaded owned DLL blocks uninstall");
        Check(!GameRunningDecision.IsBusy(false,false,false,true), "Idle launcher without owned DLL permits install");
        Check(GameRunningDecision.IsBusy(false,false,false,false), "Inaccessible modules fail closed");
    }
    private static void SpawnTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "BannerlordFG-policy-" + Guid.NewGuid().ToString("N"));
        string a = Path.Combine(root, "a"), b = Path.Combine(root, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        var processes = new List<Process>();
        try
        {
            string source = typeof(WindowsPolicyHarness).Assembly.Location;
            foreach (string name in new[] { "Bannerlord.exe", "Bannerlord.Native.exe",
                "TaleWorlds.MountAndBlade.Launcher.exe", "Bannerlord.BLSE.Launcher.exe",
                "Bannerlord.BLSE.LauncherEx.exe", "Bannerlord.BLSE.Standalone.exe",
                "Launcher.Native.exe" })
            {
                string executable = Path.Combine(a, name);
                File.Copy(source, executable);
                using (Process process = Process.Start(new ProcessStartInfo(executable, "fixture") {
                    UseShellExecute=false, CreateNoWindow=true }))
                {
                    Check(GpuDetection.IsExpectedGameProcess(process, a, _=>{}),
                        "Verified game path: " + name);
                    Check(!GpuDetection.IsExpectedGameProcess(process, b, _=>{}),
                        "Reject game path outside target: " + name);
                    Check(GpuDetection.ExpectedExecutableName(process.ProcessName) == name,
                        "Exact process name: " + name);
                    process.Kill();
                    process.WaitForExit();
                }
            }
            // The DLL-only worker cannot trust another process with a matching
            // name from another game installation.
            string foreign = Path.Combine(b, "Bannerlord.BLSE.LauncherEx.exe");
            File.Copy(source, foreign);
            using (Process foreignProcess = Process.Start(new ProcessStartInfo(foreign, "fixture") {
                UseShellExecute=false, CreateNoWindow=true }))
            {
                Check(!GpuDetection.IsExpectedGameProcess(foreignProcess, a, _=>{}),
                    "BLSE process in another installation is not trusted");
                foreignProcess.Kill();
                foreignProcess.WaitForExit();
            }
            using (Mutex marker = GameHostMarker.Register(a, Process.GetCurrentProcess().Id))
                Check(GameHostMarker.IsActive(a, Process.GetCurrentProcess().Id),
                    "Live in-game marker is visible");
            Check(!GameHostMarker.IsActive(a, Process.GetCurrentProcess().Id),
                "Closed game marker disappears");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "fixture") { Thread.Sleep(5000); return 0; }
        try { PolicyTests(); SpawnTests(); Console.WriteLine("All " + passed + " policy and process tests passed."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
