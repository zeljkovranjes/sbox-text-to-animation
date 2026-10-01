# Captures the top-level windows of ONE process (the gate's own sbox-dev.exe) whose title contains a
# filter, every few seconds, with PrintWindow. Never captures the desktop or any other process.
param(
    [int]$ProcessId = 0,
    [string]$CommandLineMarker = "t2a-editor-rig",
    [string]$TitleFilter = "Text to Animation",
    [string]$OutDir = (Join-Path $PSScriptRoot "gate_shots"),
    [int]$IntervalSec = 4,
    [int]$MaxShots = 60
)
Add-Type -ReferencedAssemblies System.Drawing @"
using System; using System.Drawing; using System.Runtime.InteropServices; using System.Text; using System.Collections.Generic;
public static class Win {
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr p);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public struct RECT { public int L, T, R, B; }
  public static List<IntPtr> Find(int pid, string filter) {
    var list = new List<IntPtr>();
    EnumWindows((h, p) => { int wp; GetWindowThreadProcessId(h, out wp); if (wp == pid && IsWindowVisible(h)) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); if (sb.ToString().Contains(filter)) list.Add(h); } return true; }, IntPtr.Zero);
    return list;
  }
  public static bool Capture(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r); int w = r.R - r.L, hh = r.B - r.T; if (w < 50 || hh < 50) return false;
    using (var bmp = new Bitmap(w, hh)) using (var g = Graphics.FromImage(bmp)) { var dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); bmp.Save(path); }
    return true;
  }
}
"@
New-Item -ItemType Directory -Force $OutDir | Out-Null
# only editor processes started on the gate's scratch project (identified by command line)
function GatePids { @(Get-CimInstance Win32_Process -Filter "Name='sbox-dev.exe'" | Where-Object { $_.CommandLine -like "*$CommandLineMarker*" } | ForEach-Object { [int]$_.ProcessId }) }
$titles = Join-Path $OutDir "window_titles.txt"
for ($i = 0; $i -lt $MaxShots; $i++) {
    $pids = GatePids
    if ($pids.Count -eq 0) { if ($i -gt 5) { break } ; Start-Sleep -Seconds $IntervalSec; continue }
    $n = 0
    foreach ($gatePid in $pids) {
        foreach ($h in [Win]::Find($gatePid, "")) {
            $sb = New-Object System.Text.StringBuilder 256; [void][Win]::GetWindowText($h, $sb, 256); $t = $sb.ToString()
            Add-Content $titles "$i $gatePid '$t'"
            if ($t -like "*$TitleFilter*") { [void][Win]::Capture($h, (Join-Path $OutDir ("window_{0:D2}_{1}.png" -f $i, $n))); $n++ }
        }
    }
    Start-Sleep -Seconds $IntervalSec
}
