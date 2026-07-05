// UnityMCPSetup — one-click Windows setup for the Unity <-> MCP <-> Claude Code bridge.
//
// Compiled to UnityMCPSetup.exe with the built-in .NET Framework csc (see build_exe.sh), so it
// needs no installs. Written in C# 5 (no string interpolation / expression-bodied members) to
// stay compatible with that compiler. Double-click to run. It:
//   1. Locates Windows Python.
//   2. Stages the MCP server to %USERPROFILE%\unity-mcp\server.
//   3. Installs the server's Python deps (mcp + pydantic).
//   4. Registers the MCP server with Claude Code (via WSL).
//   5. Ensures Unity Hub (offers to download+run the installer if missing).
//   6. Injects the Editor bridge into your Unity project, drops an autostart marker, and launches
//      Unity with the project open -- so the bridge starts listening automatically (comms open).
//
// The only interactive step is the Unity account sign-in during Hub/Editor install (Unity requires
// it -- no tool can bypass that).
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;

static class UnityMCPSetup
{
    const string DefaultDistro = "Ubuntu";
    const string DefaultSource = @"\\wsl.localhost\Ubuntu\home\hunter\unity-mcp";
    const string HubInstallerUrl = "https://public-cdn.cloud.unity3d.com/hub/prod/UnityHubSetup.exe";

    static string _distro = DefaultDistro;
    static string _source = DefaultSource;
    static bool _sourceProvided = false;

    static int Main(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--source") { _source = args[i + 1]; _sourceProvided = true; }
            if (args[i] == "--distro") _distro = args[i + 1];
        }
        if (!_sourceProvided) AutoDetectSource();

        Banner();
        try
        {
            string python = Step1_FindPython();
            string stageServer = Step2_StageServer();
            Step3_InstallDeps(python, stageServer);
            Step4_RegisterMcp(stageServer);
            Step5And6_Unity();
            Done();
        }
        catch (Exception e)
        {
            Err("Setup stopped: " + e.Message);
            Pause();
            return 1;
        }
        Pause();
        return 0;
    }

    // ---- Auto-detect the package path inside WSL (standalone double-click) --------------------

    static void AutoDetectSource()
    {
        // 1) path recorded by `unity-mcp-bridge connect` / setup
        if (UseIfValid(WslCap("cat ~/.unity-mcp-source 2>/dev/null"))) return;
        // 2) the globally-installed npm package
        if (UseIfValid(WslCap("wslpath -w $(npm root -g)/unity-ai-game-engine 2>/dev/null"))) return;
        // 3) a source checkout in the home dir
        UseIfValid(WslCap("wslpath -w $HOME/unity-mcp 2>/dev/null"));
    }

    static string WslCap(string bashCmd)
    {
        int code;
        string outp = Run("wsl.exe", "bash -lc \"" + bashCmd + "\"", out code);
        return (outp == null) ? "" : outp.Trim();
    }

    static bool UseIfValid(string unc)
    {
        if (string.IsNullOrEmpty(unc) || !unc.StartsWith(@"\\")) return false;
        try { if (!Directory.Exists(Path.Combine(unc, "server"))) return false; }
        catch { return false; }
        _source = unc;
        string[] parts = unc.Split('\\');   // \\wsl.localhost\<distro>\...
        if (parts.Length > 3 && parts[3].Length > 0) _distro = parts[3];
        return true;
    }

    // ---- Step 1: Python -----------------------------------------------------

    static string Step1_FindPython()
    {
        Head("1/6  Locating Windows Python");
        foreach (var cand in new[] { "python.exe", "py.exe" })
        {
            var path = Which(cand);
            string ver;
            if (path != null && TryVersion(path, out ver))
            {
                Ok("Found " + cand + "  (" + ver + ")  -> " + path);
                return path;
            }
        }
        throw new Exception("No Windows Python found. Install Python 3.11+ from https://python.org (tick 'Add to PATH'), then re-run.");
    }

    static bool TryVersion(string python, out string version)
    {
        int code;
        version = Run(python, "--version", out code).Trim();
        return code == 0 && version.StartsWith("Python");
    }

    // ---- Step 2: stage server to C: ----------------------------------------

    static string Step2_StageServer()
    {
        Head("2/6  Staging MCP server to your Windows profile");
        string srcServer = Path.Combine(_source, "server");
        if (!Directory.Exists(srcServer))
            throw new Exception("Cannot read server source at '" + srcServer + "'.\n" +
                "   Easiest fix: run the setup from WSL instead ->  unity-mcp-bridge setup\n" +
                "   (it auto-detects your package path + distro and passes them in),\n" +
                "   or pass --source <\\\\wsl.localhost\\...\\unity-ai-game-engine> manually.");

        string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "unity-mcp", "server");
        CopyDir(srcServer, dest, "__pycache__");
        Ok("Server staged -> " + dest);
        return dest;
    }

    // ---- Step 3: pip deps ---------------------------------------------------

    static void Step3_InstallDeps(string python, string stageServer)
    {
        Head("3/6  Installing Python dependencies (mcp + pydantic)");
        var req = Path.Combine(stageServer, "requirements.txt");
        Info("pip install --user (this can take a minute)...");
        int code;
        var outp = Run(python, "-m pip install --user -r \"" + req + "\"", out code);
        if (code != 0) { Console.WriteLine(outp); throw new Exception("pip install failed (see output above)."); }

        int c2;
        var check = Run(python, "-c \"import mcp, pydantic; print('ok')\"", out c2);
        if (c2 != 0 || !check.Contains("ok")) throw new Exception("Deps installed but import failed: " + check);
        Ok("mcp + pydantic ready on Windows Python.");
    }

    // ---- Step 4: register MCP with Claude Code (via WSL) --------------------

    static void Step4_RegisterMcp(string stageServer)
    {
        Head("4/6  Registering the MCP server with Claude Code");
        string script = Path.Combine(stageServer, "unity_editor_mcp.py");
        // Put the common npm global bin on PATH so `claude` resolves in a login shell.
        string inner = "export PATH=$HOME/.npm-global/bin:$HOME/.local/bin:$PATH; " +
                       "claude mcp remove unity-editor >/dev/null 2>&1; " +
                       "claude mcp add unity-editor -- python.exe '" + script + "'";
        string wslArgs = "-d " + _distro + " bash -lc \"" + inner + "\"";
        int code;
        var outp = Run("wsl.exe", wslArgs, out code);
        Console.WriteLine(outp.Trim());
        if (code != 0)
            Warn("Could not auto-register (the Claude CLI wasn't on the setup shell's PATH).\n" +
                 "   Finish it from WSL with:   unity-mcp-bridge connect\n" +
                 "   (or manually:  claude mcp add unity-editor -- python.exe \"" + script + "\")");
        else
            Ok("Registered. Reload Claude Code (restart it or /mcp) to load the unity_* tools.");
    }

    // ---- Steps 5 + 6: Unity Hub / project / bridge / launch ----------------

    static void Step5And6_Unity()
    {
        Head("5/6  Checking Unity");
        string editor = FindNewestUnityExe();
        if (editor == null)
        {
            if (!File.Exists(HubPath()))
            {
                Warn("Unity Hub is not installed.");
                if (Ask("Download and run the Unity Hub installer now? [Y/n] "))
                {
                    var inst = DownloadHubInstaller();
                    Info("Launching the Hub installer. After it finishes: sign in, install a Unity 6 (6000.0.x LTS) Editor, then re-run this exe.");
                    Process.Start(new ProcessStartInfo(inst) { UseShellExecute = true });
                }
                else OpenUrl("https://unity.com/download");
            }
            else
            {
                Warn("Unity Hub is installed but no Editor was found. Open Hub -> Installs -> Install Editor (6000.0.x LTS), then re-run.");
                Process.Start(new ProcessStartInfo(HubPath()) { UseShellExecute = true });
            }
            Info("Skipping bridge injection until a Unity Editor + project exist. Re-run this exe afterwards.");
            return;
        }
        Ok("Unity Editor found -> " + editor);

        Head("6/6  Injecting the bridge into your project");
        Info("Enter the full path to an existing Unity project folder (the one containing 'Assets'),");
        Info("or leave blank to optionally create a new one.");
        Console.Write("   Project path: ");
        string proj = (Console.ReadLine() ?? "").Trim().Trim('"');

        if (proj.Length == 0)
        {
            // Default No: only create a project (which generates the Assets folder) if the user opts in.
            if (!AskDefaultNo("Create a new Unity project (generates the Assets folder)? [y/N] "))
            { Warn("No project selected. Create one in Unity Hub (or re-run and choose Y), then re-run."); return; }
            string uname = Environment.GetEnvironmentVariable("USERNAME");
            if (uname == null || uname.Length == 0) uname = "you";
            Console.Write("   New project full path (e.g. C:\\Users\\" + uname + "\\UnityProjects\\MyGame): ");
            proj = (Console.ReadLine() ?? "").Trim().Trim('"');
            if (proj.Length == 0) { Warn("No path given; skipping project creation."); return; }
            if (!CreateUnityProject(editor, proj)) return;
        }

        if (!Directory.Exists(Path.Combine(proj, "Assets")))
            throw new Exception("'" + proj + "' does not look like a Unity project (no Assets folder).");

        string srcBridge = Path.Combine(_source, "unity", "Assets", "AgentBridge");
        if (!Directory.Exists(srcBridge)) throw new Exception("Cannot read bridge source at " + srcBridge);
        string destBridge = Path.Combine(proj, "Assets", "AgentBridge");
        CopyDir(srcBridge, destBridge, null);
        Ok("Bridge copied -> " + destBridge);

        File.WriteAllText(Path.Combine(proj, "agentbridge.autostart"), "Started by UnityMCPSetup.exe\n");
        Ok("Autostart marker written (bridge will listen on 127.0.0.1:8765 automatically).");

        Info("Launching Unity with your project (first import may take a minute)...");
        Process.Start(new ProcessStartInfo(editor, "-projectPath \"" + proj + "\"") { UseShellExecute = true });
        Ok("Unity launching. Watch for the Agent Bridge window: Window > Agent Bridge.");
    }

    static bool CreateUnityProject(string editor, string proj)
    {
        Info("Creating a new Unity project in batchmode (this can take a minute)...");
        Directory.CreateDirectory(proj);
        int code;
        var outp = Run(editor, "-batchmode -quit -createProject \"" + proj + "\"", out code);
        if (code != 0 || !Directory.Exists(Path.Combine(proj, "Assets")))
        {
            Console.WriteLine(outp);
            Warn("Automatic creation failed (Unity often needs a one-time interactive sign-in first).");
            Warn("Open the Editor once via Unity Hub to activate your license, or create the project in Hub, then re-run.");
            return false;
        }
        EnsureUgui(proj); // bare CLI projects omit uGUI (Canvas/Text/Button/Slider) — add it
        Ok("Project created -> " + proj);
        return true;
    }

    static void EnsureUgui(string proj)
    {
        try
        {
            var manifest = Path.Combine(proj, "Packages", "manifest.json");
            if (!File.Exists(manifest)) return;
            var txt = File.ReadAllText(manifest);
            if (txt.Contains("com.unity.ugui")) return;
            txt = txt.Replace("\"dependencies\": {", "\"dependencies\": {\n    \"com.unity.ugui\": \"2.5.0\",");
            File.WriteAllText(manifest, txt);
            Info("Added com.unity.ugui to the project (enables UI tools).");
        }
        catch { /* non-fatal */ }
    }

    // ---- Unity discovery helpers -------------------------------------------

    static string HubPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity Hub", "Unity Hub.exe");
    }

    static string FindNewestUnityExe()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor");
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root)
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .Select(d => Path.Combine(d, "Editor", "Unity.exe"))
            .FirstOrDefault(File.Exists);
    }

    static string DownloadHubInstaller()
    {
        string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "UnityHubSetup.exe");
        Info("Downloading Unity Hub installer...");
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        using (var wc = new WebClient()) wc.DownloadFile(HubInstallerUrl, dest);
        Ok("Downloaded -> " + dest);
        return dest;
    }

    // ---- Process / fs utilities --------------------------------------------

    static string Which(string exe)
    {
        int code;
        var p = Run("where.exe", exe, out code);
        if (code != 0) return null;
        return p.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    static string Run(string file, string args, out int exitCode)
    {
        var psi = new ProcessStartInfo(file, args);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        using (var p = Process.Start(psi))
        {
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            exitCode = p.ExitCode;
            return o + e;
        }
    }

    static void CopyDir(string src, string dest, string skip)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (skip != null && name == skip) continue;
            CopyDir(dir, Path.Combine(dest, name), skip);
        }
    }

    // ---- Console UI ---------------------------------------------------------

    static void Banner()
    {
        Console.Title = "Unity + MCP + Claude Code - Setup";
        Line(ConsoleColor.Cyan, "\n  ==========================================================");
        Line(ConsoleColor.Cyan,   "     Unity  <->  MCP  <->  Claude Code   -   one-click setup");
        Line(ConsoleColor.Cyan,   "  ==========================================================\n");
    }

    static void Head(string s)
    {
        Console.WriteLine();
        Line(ConsoleColor.White, "-- " + s + " " + new string('-', Math.Max(0, 50 - s.Length)));
    }

    static void Info(string s) { Console.WriteLine("   " + s); }
    static void Ok(string s) { Line(ConsoleColor.Green, "   [ok] " + s); }
    static void Warn(string s) { Line(ConsoleColor.Yellow, "   [!] " + s); }
    static void Err(string s) { Line(ConsoleColor.Red, "   [x] " + s); }

    static bool Ask(string prompt)
    {
        Console.Write("   " + prompt);
        var r = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        return r == "" || r == "y" || r == "yes";
    }

    // Default No: blank/anything-but-yes returns false.
    static bool AskDefaultNo(string prompt)
    {
        Console.Write("   " + prompt);
        var r = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        return r == "y" || r == "yes";
    }

    static void OpenUrl(string url) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }

    static void Done()
    {
        Line(ConsoleColor.Green, "\n  All automatable steps complete.");
        Info("Next: in Claude Code ask -- \"check the unity editor version, then create a red cube and screenshot it\".");
    }

    static void Pause()
    {
        Console.WriteLine("\n  Press any key to close...");
        try { Console.ReadKey(); } catch { }
    }

    static void Line(ConsoleColor c, string s)
    {
        var p = Console.ForegroundColor;
        Console.ForegroundColor = c;
        Console.WriteLine(s);
        Console.ForegroundColor = p;
    }
}
