# Standalone probe: inject one absolute mouse move through Interception and see if the cursor really moves.
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class Itc
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MouseStroke
    {
        public ushort state;
        public ushort flags;
        public short rolling;
        public int x;
        public int y;
        public uint information;
    }

    public struct POINT { public int X; public int Y; }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string path);

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr interception_create_context();

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void interception_destroy_context(IntPtr context);

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_send(IntPtr context, ushort device, ref MouseStroke stroke, uint nstroke);

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int interception_is_invalid(int device);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);
}
"@

$dll = Join-Path $PSScriptRoot 'Interception\Interception\library\x64\interception.dll'
Write-Output ("dll = " + $dll + " exists=" + (Test-Path $dll))
$module = [Itc]::LoadLibrary($dll)
Write-Output ("LoadLibrary = " + $module)

$ctx = [Itc]::interception_create_context()
Write-Output ("context = " + $ctx)
if ($ctx -eq [IntPtr]::Zero -or $ctx -eq [IntPtr](-1)) {
    Write-Output 'RESULT: context creation failed, driver not usable'
    exit 1
}

$kbd = @()
for ($i = 1; $i -le 10; $i++) { if ([Itc]::interception_is_invalid($i) -eq 0) { $kbd += ('0x{0:X2}' -f $i) } }
$mouse = @()
for ($i = 0x0B; $i -le 0x14; $i++) { if ([Itc]::interception_is_invalid($i) -eq 0) { $mouse += ('0x{0:X2}' -f $i) } }
Write-Output ("valid keyboard = " + ($kbd -join ',') + "  valid mouse = " + ($mouse -join ','))

$vx = [Itc]::GetSystemMetrics(76)
$vy = [Itc]::GetSystemMetrics(77)
$vw = [Itc]::GetSystemMetrics(78)
$vh = [Itc]::GetSystemMetrics(79)
Write-Output ("virtual desktop = $vw x $vh (origin $vx,$vy)  primary = " + [Itc]::GetSystemMetrics(0) + " x " + [Itc]::GetSystemMetrics(1))

$before = New-Object Itc+POINT
[void][Itc]::GetCursorPos([ref]$before)
Write-Output ("before cursor = " + $before.X + "," + $before.Y)

$targetX = 900
$targetY = 500

$s1 = New-Object Itc+MouseStroke
$s1.state = 0
$s1.flags = 0x03
$s1.x = [int][Math]::Round(($targetX - $vx) * 65535.0 / [Math]::Max(1, $vw - 1))
$s1.y = [int][Math]::Round(($targetY - $vy) * 65535.0 / [Math]::Max(1, $vh - 1))
$r1 = [Itc]::interception_send($ctx, 0x0B, [ref]$s1, 1)
Start-Sleep -Milliseconds 150
$after1 = New-Object Itc+POINT
[void][Itc]::GetCursorPos([ref]$after1)
Write-Output ("[absolute] send=" + $r1 + " normalized=(" + $s1.x + "," + $s1.y + ") cursor after = " + $after1.X + "," + $after1.Y)

[void][Itc]::SetCursorPos($targetX, $targetY)
Start-Sleep -Milliseconds 100
$s2 = New-Object Itc+MouseStroke
$s2.state = 0
$s2.flags = 0
$s2.x = 100
$s2.y = 0
$r2 = [Itc]::interception_send($ctx, 0x0B, [ref]$s2, 1)
Start-Sleep -Milliseconds 150
$after2 = New-Object Itc+POINT
[void][Itc]::GetCursorPos([ref]$after2)
Write-Output ("[relative +100] send=" + $r2 + " cursor after = " + $after2.X + "," + $after2.Y)

[Itc]::interception_destroy_context($ctx)

$ok = ($after1.X -ge ($targetX - 3)) -and ($after1.X -le ($targetX + 3))
if ($ok) { Write-Output 'RESULT: driver absolute injection works, problem is in the app' }
else { Write-Output 'RESULT: driver injection has no effect, problem is in driver/os layer' }
