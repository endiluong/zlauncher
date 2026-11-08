using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace ZLauncher;

/// <summary>
/// ZBoard, the self-hosted board app that runs in WSL Ubuntu (~/zboard, port 3000).
/// Everything goes through wsl.exe or the \\wsl.localhost share, so the launcher stays a normal, non-admin app.
/// </summary>
static class ZBoardControl
{
    public const string Distro = "ubuntu";
    public const string LocalUrl = "http://localhost:3000";
    public const string TailscaleUrl = "https://zboard.coat-halosaur.ts.net";
    public const string TaskName = "ZBoard";

    const string Home = @"\\wsl.localhost\" + Distro + @"\home\zlud";
    public const string RepoPath = Home + @"\zboard";
    public const string EnvPath = RepoPath + @"\.env";
    public const string ServerLog = RepoPath + @"\data\server.log";
    public const string AutostartLog = Home + @"\zboard-autostart.log";

    /// <summary>
    /// The boot task's command line for wsl.exe. Like scripts/windows/install-autostart.ps1, but:
    /// double quotes (wsl.exe --exec doesn't understand single quotes), and nvm is loaded explicitly
    /// (a non-interactive login shell never reaches the nvm lines in ~/.bashrc, so `node` would be missing).
    /// start.sh ends with `exec node`, so wsl.exe stays attached while ZBoard runs; that keeps the distro up.
    /// </summary>
    public const string TaskArguments = "-d Ubuntu --exec bash -lc \". ~/.nvm/nvm.sh; cd ~/zboard && exec ./start.sh >> ~/zboard-autostart.log 2>&1\"";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>Any HTTP answer (even 401) means the server is up.</summary>
    public static async Task<bool> IsRunningAsync()
    {
        try
        {
            using var r = await Http.GetAsync(LocalUrl + "/", HttpCompletionOption.ResponseHeadersRead);
            return true;
        }
        catch { return false; }
    }

    /// <summary>(Re)starts the server through ~/.local/bin/zb-start. Returns its last output line.</summary>
    public static async Task<(bool ok, string message)> StartAsync()
    {
        var (code, output) = await RunWslAsync("-d " + Distro + " -- bash -l /home/zlud/.local/bin/zb-start", TimeSpan.FromSeconds(90));
        if (code == 0) KeepDistroAwake();
        var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        return (code == 0, last.Length > 0 ? last : code == 0 ? "ZBoard is running" : "ZBoard did not start");
    }

    public static async Task<bool> StopAsync()
    {
        await RunWslAsync("-d " + Distro + " -- bash -lc \"fuser -k 3000/tcp\"", TimeSpan.FromSeconds(20));
        for (int i = 0; i < 20 && await IsRunningAsync(); i++) await Task.Delay(250);
        return !await IsRunningAsync();
    }

    /// <summary>
    /// zb-start puts the server in the background and its wsl.exe exits; WSL can then shut the idle distro down.
    /// A hidden wsl.exe stays attached while something listens on :3000 (flock: only one such watcher at a time).
    /// It outlives the launcher and ends by itself after Stop.
    /// </summary>
    static void KeepDistroAwake()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("wsl.exe",
                "-d " + Distro + " --exec flock -n /tmp/zboard-keepawake.lock bash -c \"sleep 5; while fuser -s 3000/tcp; do sleep 30; done\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { /* best effort */ }
    }

    static async Task<(int code, string output)> RunWslAsync(string args, TimeSpan timeout)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("wsl.exe", args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            })!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(); } catch { } return (-1, "Timed out"); }
            return (p.ExitCode, await stdout + await stderr);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    public static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }); }
    }

    // ---------- Start at boot (scheduled task) ----------

    public enum BootState { Missing, Registered, Outdated, Unknown }

    public sealed record BootInfo(BootState State, string Detail);

    /// <summary>Reads the task without admin rights (any user can query it).</summary>
    public static async Task<BootInfo> GetBootInfoAsync()
    {
        const string script =
            "$t = Get-ScheduledTask -TaskName 'ZBoard' -ErrorAction SilentlyContinue; if (-not $t) { 'MISSING'; exit } " +
            "$i = $t | Get-ScheduledTaskInfo; $a = $t.Actions | Select-Object -First 1; " +
            "'STATE=' + $t.State; 'USER=' + $t.Principal.UserId; 'EXE=' + $a.Execute; 'ARGS=' + $a.Arguments; " +
            "'LAST=' + $(if ($i.LastRunTime -and $i.LastRunTime.Year -gt 2000) { $i.LastRunTime.ToString('yyyy-MM-dd HH:mm') } else { '' }); " +
            "'RESULT=' + $i.LastTaskResult";
        var (code, output) = await RunPowerShellAsync(script);
        if (code != 0) return new(BootState.Unknown, "Couldn't read the scheduled task.");
        if (output.Trim() == "MISSING") return new(BootState.Missing, "Not set up. ZBoard starts only when you start it.");

        var v = output.Split('\n', StringSplitOptions.TrimEntries).Where(l => l.Contains('='))
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        string Get(string k) => v.TryGetValue(k, out var s) ? s : "";

        bool current = Path.GetFileName(Get("EXE")).Equals("wsl.exe", StringComparison.OrdinalIgnoreCase) && Get("ARGS") == TaskArguments;
        var detail = new StringBuilder($"Runs at startup as {Get("USER")} ({Get("STATE")}).");
        if (Get("LAST").Length > 0)
            detail.Append($" Last run {Get("LAST")}, result {(Get("RESULT") is "0" or "267009" ? "OK" : "0x" + (long.TryParse(Get("RESULT"), out var r) ? r.ToString("X") : Get("RESULT")))}.");
        if (!current) detail.Append(" It uses an older command: turn it off and on again to update it.");
        return new(current ? BootState.Registered : BootState.Outdated, detail.ToString());
    }

    /// <summary>
    /// Registers or removes the boot task from an elevated PowerShell (one UAC prompt).
    /// The task runs without anyone logged on, so Windows needs the account password: the elevated
    /// helper asks for it with the standard Windows credential prompt. The launcher never sees it.
    /// </summary>
    public static async Task<bool> SetBootAsync(bool on)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(BootScript(on)));
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -EncodedCommand {encoded}")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p != null) await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token);
        }
        catch (Win32Exception) { return false; }      // UAC cancelled
        catch (OperationCanceledException) { }
        var info = await GetBootInfoAsync();
        return on ? info.State is BootState.Registered : info.State is BootState.Missing;
    }

    /// <summary>The script the elevated helper runs.</summary>
    internal static string BootScript(bool on) => on
            ? "$ErrorActionPreference = 'Stop'; " +
              "$cred = Get-Credential -UserName \"$env:USERDOMAIN\\$env:USERNAME\" -Message 'Your Windows password, so ZBoard can start at boot before anyone logs in. Windows stores it with the scheduled task.'; " +
              "if (-not $cred) { exit 1 }; " +
              $"$action = New-ScheduledTaskAction -Execute 'wsl.exe' -Argument '{TaskArguments.Replace("'", "''")}'; " +
              "$trigger = New-ScheduledTaskTrigger -AtStartup; " +
              "$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable " +
              "-ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1); " +
              $"Register-ScheduledTask -TaskName '{TaskName}' -Action $action -Trigger $trigger -Settings $settings " +
              "-User $cred.UserName -Password $cred.GetNetworkCredential().Password -RunLevel Limited -Force | Out-Null"
            : $"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false";

    static async Task<(int code, string output)> RunPowerShellAsync(string script)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
            return (p.ExitCode, await output);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
