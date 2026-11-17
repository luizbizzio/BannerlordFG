// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class UninstallSafetyHarness
{
    private const string ReleaseHash =
        "6cd5704b47ff883666df7fec80ce55d113ecc1097a1db5caa3a1058bc007c2d0";
    private static readonly Type Installer = Assembly.LoadFrom(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BannerlordFG.Maintenance.dll"))
        .GetType("BannerlordFG.Setup.OptiInstaller", true);
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        _passed++;
        Console.WriteLine("PASS " + name);
    }

    private static string Hash(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    private static string ShortHash(string value)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                value.ToLowerInvariant()))).Replace("-", "").Substring(0, 12);
    }

    private static object Call(string name, params object[] args)
    {
        try
        {
            return Installer.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, args);
        }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private sealed class Fixture
    {
        internal string Bin;
        internal string State;
        internal string Manifest;
        internal string Proxy;
        internal string Managed;
        internal string Ini;
        internal string Foreign;
        internal List<string> Messages = new List<string>();
        internal Action<string> Log => Messages.Add;
    }

    private static Fixture Create(string root, string name, string proxy,
        string foreignName = null)
    {
        var f = new Fixture();
        string home = Path.Combine(root, name);
        f.Bin = Path.Combine(home, "game", "bin", "Win64_Shipping_Client");
        f.State = Path.Combine(home, "state");
        Directory.CreateDirectory(f.Bin);
        Directory.CreateDirectory(f.State);
        File.WriteAllText(Path.Combine(f.Bin, "Bannerlord.exe"), "fixture");
        f.Proxy = Path.Combine(f.Bin, proxy);
        f.Ini = Path.Combine(f.Bin, "OptiScaler.ini");
        f.Managed = Path.Combine(f.Bin, "OptiScaler", "managed.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(f.Managed));
        File.WriteAllText(f.Proxy, "owned OptiScaler proxy");
        File.WriteAllText(f.Ini, "original settings");
        File.WriteAllText(f.Managed, "owned dependency");
        if (foreignName != null)
        {
            f.Foreign = Path.Combine(f.Bin, foreignName);
            File.WriteAllText(f.Foreign, "foreign ReShade or wrapper");
        }
        f.Manifest = Path.Combine(f.State, "install-" + ShortHash(f.Bin) + ".json");
        var installed = new object[] {
            new Dictionary<string, string> { { "relativePath", "OptiScaler.dll" },
                { "sha256", Hash(f.Proxy) } },
            new Dictionary<string, string> { { "relativePath", "OptiScaler.ini" },
                { "sha256", Hash(f.Ini) } },
            new Dictionary<string, string> { { "relativePath", "OptiScaler\\managed.dll" },
                { "sha256", Hash(f.Managed) } }
        };
        var post = new Dictionary<string, string> {
            { proxy, Hash(f.Proxy) }, { "OptiScaler.ini", Hash(f.Ini) },
            { "OptiScaler\\managed.dll", Hash(f.Managed) } };
        var manifest = new Dictionary<string, object> {
            { "gameBin", f.Bin }, { "tag", "nightly-20261116" },
            { "archiveSha256", ReleaseHash }, { "stage", "complete" },
            { "proxyFileName", proxy }, { "movedProxySha256", Hash(f.Proxy) },
            { "installedFiles", installed }, { "postinstallFiles", post } };
        File.WriteAllText(f.Manifest, new JavaScriptSerializer().Serialize(manifest));
        return f;
    }

    private static void InState(Fixture f, Action action)
    {
        Environment.SetEnvironmentVariable("BFG_VERIFIED_STATE_DIR", f.State);
        try { action(); }
        finally { Environment.SetEnvironmentVariable("BFG_VERIFIED_STATE_DIR", null); }
    }

    private static bool Fails(Fixture f)
    {
        try { InState(f, () => Call("Uninstall", f.Bin, f.Log)); return false; }
        catch (Exception) { return true; }
    }

    private static void Uninstall(Fixture f)
    {
        InState(f, () => Call("Uninstall", f.Bin, f.Log));
    }

    private static bool Complete(Fixture f)
    {
        bool result = false;
        InState(f, () => result = (bool)Call("HasCompleteManagedInstall", f.Bin));
        return result;
    }

    private static void LoggingInstallTests(string root)
    {
        string original = "[Upscalers]\r\nDx11Upscaler = auto\r\n" +
            "[FrameGen]\r\nEnabled = auto\r\nFGInput = Upscaler\r\nFGOutput = XeFG\r\nDebugView = auto\r\n" +
            "[XeFG]\r\nInterpolationCount = 0\r\nForceBorderless = false\r\n" +
            "[Menu]\r\nOverlayMenu = false\r\nShowFps = false\r\nFpsOverlayType = 1\r\nDisableSplash = false\r\n" +
            "[Framerate]\r\nFramerateLimit = 120\r\n" +
            "[UpscaleRatio]\r\nUpscaleRatioOverrideEnabled = true\r\n" +
            "[QualityOverrides]\r\nQualityRatioOverrideEnabled = true\r\n" +
            "[Log]\r\nLogToFile = false\r\nLogLevel = 0\r\nSingleFile = false\r\nKeep = user choice\r\n" +
            "[Unrelated]\r\nPreference = custom\r\n";
        string ini = Path.Combine(root, "logging-install.ini");
        File.WriteAllText(ini, original);
        Call("Configure", ini, true, (Action<string>)(_ => { }), false);
        string configured = File.ReadAllText(ini);
        Check(configured.Contains("LogToFile = true\r\n") &&
            configured.Contains("LogLevel = 2\r\n") &&
            configured.Contains("SingleFile = true\r\n"),
            "New managed installation enables OptiScaler file Info logging");
        Check(configured.Contains("Keep = user choice\r\n") &&
            configured.Contains("[Unrelated]\r\nPreference = custom\r\n") &&
            File.ReadAllText(ini + ".bannerlordfg-original.bak") == original,
            "Installation preserves unrelated user settings and original INI backup");
        string userPreference = configured.Replace("LogLevel = 2", "LogLevel = 1");
        File.WriteAllText(ini, userPreference);
        var unmanaged = Create(root, "unmanaged-logging", "winmm.dll");
        File.Delete(unmanaged.Manifest);
        File.WriteAllText(unmanaged.Ini, userPreference);
        InState(unmanaged, () => Call("ConfigureExistingManagedLiveControls",
            unmanaged.Bin, unmanaged.Log));
        Check(File.ReadAllText(unmanaged.Ini) == userPreference,
            "Unmanaged OptiScaler logging preferences remain untouched");
        var managedEdited = Create(root, "managed-logging-edited", "winmm.dll");
        File.WriteAllText(managedEdited.Ini, userPreference);
        InState(managedEdited, () => Call("ConfigureExistingManagedLiveControls",
            managedEdited.Bin, managedEdited.Log));
        Check(File.ReadAllText(managedEdited.Ini).Contains("LogLevel = 1") &&
            File.ReadAllText(managedEdited.Ini).Contains("LogToFile = true"),
            "Later managed user logging preferences survive live-control migration");
        string noLog = (string)Call("ConfigureLoggingDefaults", "[Other]\nKeep = yes\n");
        Check(noLog.Contains("[Log]\nLogToFile = true\nLogLevel = 2\nSingleFile = true\n") &&
            noLog.Contains("[Other]\nKeep = yes\n"),
            "Missing Log section is added without changing unrelated settings");
    }

    private static void Run(string root)
    {
        LoggingInstallTests(root);
        var winmm = Create(root, "winmm-reshade-d3d11", "winmm.dll", "d3d11.dll");
        Call("VerifiedRemovalPlan", winmm.Bin, winmm.Manifest);
        Check(Complete(winmm), "Complete fixture recognized before uninstall");
        Uninstall(winmm);
        Check(!File.Exists(winmm.Proxy) && !File.Exists(winmm.Managed) &&
            !File.Exists(winmm.Ini) && !File.Exists(winmm.Manifest),
            "Successful winmm uninstall removes only managed files");
        Check(File.Exists(winmm.Foreign), "ReShade d3d11.dll remains untouched");
        Check(Fails(winmm) && File.Exists(winmm.Foreign),
            "Repeated uninstall cannot claim a foreign file");

        var reshadeDxgi = Create(root, "winmm-reshade-dxgi", "winmm.dll", "dxgi.dll");
        Uninstall(reshadeDxgi);
        Check(File.Exists(reshadeDxgi.Foreign) && !File.Exists(reshadeDxgi.Proxy),
            "ReShade dxgi.dll survives managed winmm uninstall");

        var dxgi = Create(root, "dxgi-foreign-winmm", "dxgi.dll", "winmm.dll");
        Uninstall(dxgi);
        Check(File.Exists(dxgi.Foreign) && !File.Exists(dxgi.Proxy),
            "Foreign winmm.dll survives managed dxgi uninstall");

        var dxgiWithReshade = Create(root, "dxgi-reshade-d3d11", "dxgi.dll", "d3d11.dll");
        Uninstall(dxgiWithReshade);
        Check(File.Exists(dxgiWithReshade.Foreign) &&
            !File.Exists(dxgiWithReshade.Proxy) && !File.Exists(dxgiWithReshade.Manifest),
            "ReShade d3d11.dll survives managed dxgi uninstall");

        var denied = Create(root, "denied", "winmm.dll");
        File.SetAttributes(denied.Managed, FileAttributes.ReadOnly);
        Check(Fails(denied) && File.Exists(denied.Proxy) && File.Exists(denied.Ini) &&
            File.Exists(denied.Manifest), "Access denied preflight prevents partial removal");
        File.SetAttributes(denied.Managed, FileAttributes.Normal);
        Uninstall(denied);
        Check(!File.Exists(denied.Manifest), "Denied uninstall retries safely after blocker clears");

        var midDelete = Create(root, "denied-after-preflight", "winmm.dll");
        bool midDeleteFailed = false;
        try
        {
            InState(midDelete, () => Call("UninstallCore", midDelete.Bin, midDelete.Log,
                new Action<string>(relative =>
                {
                    if (relative == "winmm.dll")
                        File.SetAttributes(midDelete.Proxy, FileAttributes.ReadOnly);
                })));
        }
        catch (UnauthorizedAccessException) { midDeleteFailed = true; }
        Check(midDeleteFailed && !File.Exists(midDelete.Ini) &&
            File.Exists(midDelete.Proxy) && File.Exists(midDelete.Manifest) &&
            Directory.GetFiles(Path.Combine(midDelete.State, "recovery"),
                "status.txt", SearchOption.AllDirectories).Any(path =>
                    File.ReadAllText(path).Contains("Incomplete")),
            "Mid-removal access denial retains manifest and recovery journal");
        File.SetAttributes(midDelete.Proxy, FileAttributes.Normal);
        Uninstall(midDelete);
        Check(!File.Exists(midDelete.Proxy) && !File.Exists(midDelete.Manifest),
            "Mid-removal access denial resumes without re-claiming files");

        var modified = Create(root, "modified-binary", "winmm.dll");
        File.WriteAllText(modified.Managed, "changed by another owner");
        Check(Fails(modified) && File.Exists(modified.Proxy) && File.Exists(modified.Manifest),
            "Modified managed DLL is never deleted");

        var missing = Create(root, "missing-dependency", "winmm.dll");
        File.Delete(missing.Managed);
        Check(!Complete(missing), "Missing dependency marks install incomplete");
        Uninstall(missing);
        Check(!File.Exists(missing.Proxy) && !File.Exists(missing.Manifest),
            "Missing file can be safely retried and completed");

        var interrupted = Create(root, "interrupted", "winmm.dll");
        File.Delete(interrupted.Ini);
        Check(!Complete(interrupted), "Interrupted uninstall is detected as incomplete");
        InState(interrupted, () =>
        {
            object[] args = { interrupted.Bin, null };
            bool recoverable = (bool)Call("CanRecoverIncompleteInstall", args);
            Check(recoverable, "Partial removal passes verified recovery preflight");
            Call("RecoverIncompleteInstall", interrupted.Bin, interrupted.Log);
        });
        Check(!File.Exists(interrupted.Manifest) && !File.Exists(interrupted.Proxy),
            "Recovery removes remaining owned files and manifest");

        var renamed = Create(root, "rename-interrupted", "winmm.dll");
        var renameMap = (Dictionary<string, object>)new JavaScriptSerializer()
            .DeserializeObject(File.ReadAllText(renamed.Manifest));
        renameMap.Remove("postinstallFiles");
        renameMap.Remove("movedProxySha256");
        renameMap.Remove("stage");
        File.WriteAllText(renamed.Manifest, new JavaScriptSerializer().Serialize(renameMap));
        Uninstall(renamed);
        Check(!File.Exists(renamed.Proxy) && !File.Exists(renamed.Manifest),
            "Interrupted proxy rename is recovered only with original recorded hash");

        var foreignProxy = Create(root, "rename-foreign", "winmm.dll");
        var foreignMap = (Dictionary<string, object>)new JavaScriptSerializer()
            .DeserializeObject(File.ReadAllText(foreignProxy.Manifest));
        foreignMap.Remove("postinstallFiles");
        foreignMap.Remove("movedProxySha256");
        foreignMap.Remove("stage");
        File.WriteAllText(foreignProxy.Manifest, new JavaScriptSerializer().Serialize(foreignMap));
        File.WriteAllText(foreignProxy.Proxy, "foreign proxy bytes");
        Check(Fails(foreignProxy) && File.Exists(foreignProxy.Proxy) &&
            File.Exists(foreignProxy.Managed) && File.Exists(foreignProxy.Manifest),
            "Interrupted rename refuses a foreign proxy");

        var edited = Create(root, "edited-config", "winmm.dll");
        File.WriteAllText(edited.Ini, "user custom settings");
        Uninstall(edited);
        string[] backups = Directory.GetFiles(Path.Combine(edited.State, "recovery"),
            "OptiScaler.ini", SearchOption.AllDirectories);
        Check(backups.Length == 1 && File.ReadAllText(backups[0]) == "user custom settings",
            "Modified OptiScaler.ini is preserved in verified recovery storage");

        var editedBackup = Create(root, "edited-original-backup", "winmm.dll");
        string originalBackup = Path.Combine(editedBackup.Bin,
            "OptiScaler.ini.bannerlordfg-original.bak");
        File.WriteAllText(originalBackup, "original upstream settings");
        var backupMap = (Dictionary<string, object>)new JavaScriptSerializer()
            .DeserializeObject(File.ReadAllText(editedBackup.Manifest));
        ((Dictionary<string, object>)backupMap["postinstallFiles"])
            ["OptiScaler.ini.bannerlordfg-original.bak"] = Hash(originalBackup);
        backupMap["expectedBackupSha256"] = Hash(originalBackup);
        File.WriteAllText(editedBackup.Manifest, new JavaScriptSerializer().Serialize(backupMap));
        File.WriteAllText(originalBackup, "edited original backup");
        Uninstall(editedBackup);
        string[] preservedBackups = Directory.GetFiles(Path.Combine(editedBackup.State,
            "recovery"), "OptiScaler.ini.bannerlordfg-original.bak", SearchOption.AllDirectories);
        Check(preservedBackups.Length == 1 &&
            File.ReadAllText(preservedBackups[0]) == "edited original backup",
            "Modified original INI backup is preserved before uninstall");

        var invalid = Create(root, "invalid-manifest", "winmm.dll");
        string json = File.ReadAllText(invalid.Manifest);
        var map = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
        var post = (Dictionary<string, object>)map["postinstallFiles"];
        post["..\\foreign.dll"] = Hash(invalid.Managed);
        File.WriteAllText(invalid.Manifest, new JavaScriptSerializer().Serialize(map));
        Check(Fails(invalid) && File.Exists(invalid.Proxy) && File.Exists(invalid.Manifest),
            "Unsafe manifest path is rejected before deletion");

        var noOriginalList = Create(root, "missing-original-ownership", "winmm.dll");
        var noOriginalMap = (Dictionary<string, object>)new JavaScriptSerializer()
            .DeserializeObject(File.ReadAllText(noOriginalList.Manifest));
        noOriginalMap.Remove("installedFiles");
        File.WriteAllText(noOriginalList.Manifest,
            new JavaScriptSerializer().Serialize(noOriginalMap));
        Check(Fails(noOriginalList) && File.Exists(noOriginalList.Proxy) &&
            File.Exists(noOriginalList.Managed) && File.Exists(noOriginalList.Manifest),
            "Manifest without original ownership cannot delete files");

        var forgedProxy = Create(root, "mismatched-proxy-ownership", "winmm.dll");
        var forgedMap = (Dictionary<string, object>)new JavaScriptSerializer()
            .DeserializeObject(File.ReadAllText(forgedProxy.Manifest));
        forgedMap["movedProxySha256"] = Hash(forgedProxy.Managed);
        File.WriteAllText(forgedProxy.Manifest,
            new JavaScriptSerializer().Serialize(forgedMap));
        Check(Fails(forgedProxy) && File.Exists(forgedProxy.Proxy) &&
            File.Exists(forgedProxy.Managed) && File.Exists(forgedProxy.Manifest),
            "Proxy claim must match the originally copied managed DLL");

        var missingClaim = Create(root, "manifest-omits-ini", "winmm.dll");
        var omitted = (Dictionary<string, object>)new JavaScriptSerializer()
            .DeserializeObject(File.ReadAllText(missingClaim.Manifest));
        ((Dictionary<string, object>)omitted["postinstallFiles"]).Remove("OptiScaler.ini");
        File.WriteAllText(missingClaim.Manifest, new JavaScriptSerializer().Serialize(omitted));
        Check(Fails(missingClaim) && File.Exists(missingClaim.Ini) &&
            File.Exists(missingClaim.Proxy) && File.Exists(missingClaim.Manifest),
            "Complete manifest missing INI ownership is rejected before deletion");
        InState(missingClaim, () =>
        {
            object[] args = { missingClaim.Bin, null };
            Check(!(bool)Call("CanRecoverIncompleteInstall", args),
                "Repair preflight rejects an invalid complete manifest");
        });

        var untracked = Create(root, "untracked-nested", "winmm.dll");
        string foreign = Path.Combine(untracked.Bin, "OptiScaler", "foreign.dll");
        File.WriteAllText(foreign, "third-party");
        Check(Fails(untracked) && File.Exists(foreign) && File.Exists(untracked.Manifest),
            "Untracked nested DLL is preserved with recovery manifest");
        File.Delete(foreign); // Test fixture only; production never deletes it.
        Uninstall(untracked);
        Check(!File.Exists(untracked.Manifest), "Partial uninstall resumes after user resolves conflict");

        var locked = Create(root, "locked", "winmm.dll");
        string ready = Path.Combine(root, "lock-ready");
        var child = new Process { StartInfo = new ProcessStartInfo {
            FileName = Process.GetCurrentProcess().MainModule.FileName,
            Arguments = "--hold \"" + locked.Managed + "\" \"" + ready + "\"",
            UseShellExecute = false, CreateNoWindow = true } };
        try
        {
            if (!child.Start()) throw new IOException("Lock helper did not start.");
            for (int i = 0; i < 100 && !File.Exists(ready); i++) Thread.Sleep(50);
            Check(File.Exists(ready) && Fails(locked) && File.Exists(locked.Proxy) &&
                File.Exists(locked.Manifest), "Other-process lock prevents partial removal");
        }
        finally { if (!child.HasExited) child.Kill(); child.WaitForExit(); child.Dispose(); }
        Uninstall(locked);
        Check(!File.Exists(locked.Manifest), "Locked uninstall retries after process exits");

        var linked = Create(root, "junction", "winmm.dll");
        string outside = Path.Combine(root, "junction-target");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "managed.dll"), "owned dependency");
        Directory.Delete(Path.GetDirectoryName(linked.Managed), true);
        var junction = Process.Start(new ProcessStartInfo {
            FileName = "cmd.exe", Arguments = "/d /c mklink /J \"" +
                Path.GetDirectoryName(linked.Managed) + "\" \"" + outside + "\"",
            UseShellExecute = false, CreateNoWindow = true });
        junction.WaitForExit();
        Check(junction.ExitCode == 0 && Fails(linked) && File.Exists(linked.Proxy) &&
            File.Exists(Path.Combine(outside, "managed.dll")),
            "Directory reparse point cannot redirect managed deletion");
        Directory.Delete(Path.GetDirectoryName(linked.Managed)); // Removes junction only.
    }

    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--inspect")
        {
            object[] check = { Path.GetFullPath(args[1]), null };
            bool safe = (bool)Call("CanRecoverIncompleteInstall", check);
            Console.WriteLine("Read-only recovery preflight: " +
                (safe ? "eligible" : "blocked") + "; reason=" + (check[1] ?? "none"));
            return safe ? 0 : 1;
        }
        if (args.Length == 3 && args[0] == "--hold")
        {
            using (new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.None))
            {
                File.WriteAllText(args[2], "ready");
                Thread.Sleep(30000);
            }
            return 0;
        }
        string root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(root);
        try { Run(root); Console.WriteLine("All " + _passed + " uninstall safety checks passed."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            // A failed assertion must not turn fixture cleanup into a walk
            // through the test junction.
            string junction = Path.Combine(root, "junction", "game", "bin",
                "Win64_Shipping_Client", "OptiScaler");
            if (Directory.Exists(junction) &&
                (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(junction);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
