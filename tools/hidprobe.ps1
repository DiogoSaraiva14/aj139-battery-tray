# Read-only HID probe: lists a device's vendor-defined HID collections, reads their feature reports
# and logs every input report they send for N seconds. Used to reverse-engineer the AJ139 receiver.
# Usage: powershell -ExecutionPolicy Bypass -File hidprobe.ps1 [-VendorId A8A5] [-ProductId 2255] [-ListenSec 60]
param([string]$VendorId = 'A8A5', [string]$ProductId = '2255', [int]$ListenSec = 60)
Add-Type -TypeDefinition @'
using System; using System.IO; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles; using System.Threading.Tasks; using System.Collections.Generic;
public static class HidProbe {
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
  [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
  [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
  [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
  [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);
  [DllImport("hid.dll")] static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] b, int len);
  [StructLayout(LayoutKind.Sequential)] public struct HIDP_CAPS { public ushort Usage, UsagePage, InputLen, OutputLen, FeatureLen;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] R; public ushort a,b,c,d,e,f,g,h,i,j; }
  public static string Hex(byte[] b, int n) { return BitConverter.ToString(b, 0, n).Replace("-", " "); }
  public static HIDP_CAPS Caps(string path) {
    using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
      if (h.IsInvalid) throw new Exception("open(0) failed " + Marshal.GetLastWin32Error());
      IntPtr p; HidD_GetPreparsedData(h, out p); HIDP_CAPS c; HidP_GetCaps(p, out c); HidD_FreePreparsedData(p); return c; } }
  public static List<string> Features(string path, int len) {
    var o = new List<string>();
    using (var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
      if (h.IsInvalid) { o.Add("  open(rw) failed " + Marshal.GetLastWin32Error()); return o; }
      for (int id = 0; id < 16; id++) {
        if (len > 0) { var b = new byte[len]; b[0] = (byte)id; if (HidD_GetFeature(h, b, len)) o.Add("  feature id " + id + ": " + Hex(b, len)); }
      } }
    return o; }
  public static Task<List<string>> Listen(string tag, string path, int len, int ms) {
    return Task.Run(() => { var o = new List<string>();
      var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
      if (h.IsInvalid) { o.Add(tag + " listen open failed " + Marshal.GetLastWin32Error()); return o; }
      using (var fs = new FileStream(h, FileAccess.Read, Math.Max(len,1), true)) {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) { var b = new byte[len]; var t = fs.ReadAsync(b, 0, len);
          if (!t.Wait(end - DateTime.Now > TimeSpan.Zero ? end - DateTime.Now : TimeSpan.Zero)) break;
          o.Add(DateTime.Now.ToString("HH:mm:ss.fff") + " " + tag + ": " + Hex(b, t.Result)); } }
      return o; }); }
}
'@
$guid = '{4d1e55b2-f16f-11cf-88cb-001111000030}'
$devs = Get-PnpDevice -PresentOnly -Class HIDClass | Where-Object { $_.InstanceId -match "^HID\\VID_$VendorId&PID_$ProductId" }
$tasks = @()
foreach ($d in $devs) {
  $path = '\\?\' + ($d.InstanceId -replace '\\','#') + "#$guid"
  $tag = ($d.InstanceId -split '\\')[1] -replace "VID_$VendorId&PID_$ProductId&",''
  try { $c = [HidProbe]::Caps($path) } catch { "$tag : $_"; continue }
  if ($c.UsagePage -lt 0xFF00) { continue }   # only vendor-defined collections; mouse/keyboard ones are locked by Windows
  "$tag  usagePage=0x{0:X4} usage=0x{1:X2} in={2} out={3} feat={4}" -f $c.UsagePage, $c.Usage, $c.InputLen, $c.OutputLen, $c.FeatureLen
  [HidProbe]::Features($path, $c.FeatureLen)
  if ($c.InputLen -gt 0) { $tasks += [HidProbe]::Listen($tag, $path, $c.InputLen, $ListenSec * 1000) }
}
"--- listening $ListenSec s for unsolicited reports..."
$tasks | ForEach-Object { $_.Wait(); $_.Result }
"--- done"
