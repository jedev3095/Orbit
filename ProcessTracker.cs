using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orbit;

public record ProcessIdentity(int Id, DateTime StartedUtc, string Name);

public static class ProcessTracker
{
    public static void Assign(Account a, ProcessIdentity identity) { a.ProcessId = identity.Id; a.ProcessStartedUtc = identity.StartedUtc; a.ProcessName = identity.Name; }
    public static void Clear(Account a) { a.ProcessId = null; a.ProcessStartedUtc = null; a.ProcessName = ""; }
    internal static bool Matches(Account a, ProcessIdentity identity) => a.ProcessId == identity.Id && a.ProcessStartedUtc == identity.StartedUtc && a.ProcessName == identity.Name && identity.Name == "RobloxPlayerBeta";
    // Pin the process handle before querying identity. Kill/window operations use
    // this same Process, never a second PID lookup after validation.
    public static Process? OpenOwned(Account a)
    {
        if (a.ProcessId is not int id || a.ProcessStartedUtc is null || a.ProcessName != "RobloxPlayerBeta") return null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(id); _ = process.Handle;
            if (!process.HasExited && Matches(a, new(process.Id, process.StartTime.ToUniversalTime(), process.ProcessName))) return process;
        }
        catch { }
        process?.Dispose(); return null;
    }
    public static bool IsRunning(Account a) { using var process = OpenOwned(a); return process is not null; }
    public static bool Stop(Account a) { using var process = OpenOwned(a); if (process is null) return false; process.Kill(); return true; }
    // Windows' process command-line query is used only to identify launches made by Orbit.
    public static ProcessIdentity? Find(string tracker)
    {
        if (tracker.Length == 0) return null;
        foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            using (process)
            {
                try
                {
                    var buffer = Marshal.AllocHGlobal(65536);
                    try
                    {
                        if (NtQueryInformationProcess(process.Handle, 60, buffer, 65536, out _) != 0) continue;
                        var str = Marshal.PtrToStructure<UnicodeString>(buffer);
                        var cmd = Marshal.PtrToStringUni(str.Buffer, str.Length / 2) ?? "";
                        if (System.Text.RegularExpressions.Regex.IsMatch(cmd, @"(?:-b\s+|browsertrackerid:)" + System.Text.RegularExpressions.Regex.Escape(tracker) + @"(?:\s|\+|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return new(process.Id, process.StartTime.ToUniversalTime(), process.ProcessName);
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                catch { }
            }
        }
        return null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr handle, int info, IntPtr buffer, int length, out int returned);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint);
    public static void Capture(Account a)
    {
        try { using var p = OpenOwned(a); if (p is not null && GetWindowRect(p.MainWindowHandle, out var r) && r.Right > r.Left && r.Left > -30000) a.Fields["Orbit.Window"] = $"{r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}"; } catch { }
    }
    public static void Restore(Account a)
    {
        try { if (!a.Fields.TryGetValue("Orbit.Window", out var text)) return; var n = text.Split(',').Select(int.Parse).ToArray(); if (n.Length != 4 || n[2] < 100 || n[3] < 100) return; using var p = OpenOwned(a); if (p is not null) MoveWindow(p.MainWindowHandle, n[0], n[1], n[2], n[3], true); } catch { }
    }
}
