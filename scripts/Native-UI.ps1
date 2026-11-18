param([ValidateSet('Inspect','Click','Capture','Close')][string]$Action='Inspect',[string]$Button,[string]$Image='native-window.png',[string]$TitlePrefix='AI BLACK HOLE · ABH')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class AbhWindows {
 public delegate bool EnumProc(IntPtr hwnd, IntPtr param);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr param);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr param);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int max);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd,uint msg,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd,uint msg,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 public static string Text(IntPtr hwnd){var text=new StringBuilder(8192);GetWindowText(hwnd,text,text.Capacity);return text.ToString();}
 public static IntPtr Find(string prefix){IntPtr found=IntPtr.Zero;EnumWindows((hwnd,param)=>{if(Text(hwnd).StartsWith(prefix,StringComparison.Ordinal)){found=hwnd;return false;}return true;},IntPtr.Zero);return found;}
}
'@
$script:window=[AbhWindows]::Find($TitlePrefix)
if($script:window -eq [IntPtr]::Zero){throw 'Native ABH window not found'}
$script:controls=[Collections.Generic.List[object]]::new()
[AbhWindows]::EnumChildWindows($script:window,{param($hwnd,$param) $text=[AbhWindows]::Text($hwnd);if($text){$script:controls.Add([pscustomobject]@{Handle=$hwnd;Text=$text})};return $true},[IntPtr]::Zero) | Out-Null
switch($Action){
 'Inspect' { $script:controls | Select-Object Text | Format-List }
 'Click' { $control=$script:controls | Where-Object Text -eq $Button | Select-Object -First 1;if(-not $control){throw 'Button not found'};[AbhWindows]::PostMessage($control.Handle,0x00F5,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null;[pscustomobject]@{Clicked=$Button} }
 'Close' { [AbhWindows]::PostMessage($script:window,0x0010,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null }
 'Capture' { $rect=[AbhWindows+RECT]::new();[AbhWindows]::GetWindowRect($script:window,[ref]$rect) | Out-Null;$bitmap=[Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top);$graphics=[Drawing.Graphics]::FromImage($bitmap);$dc=$graphics.GetHdc();try{[AbhWindows]::PrintWindow($script:window,$dc,2) | Out-Null}finally{$graphics.ReleaseHdc($dc)};$bitmap.Save($Image,[Drawing.Imaging.ImageFormat]::Png);$graphics.Dispose();$bitmap.Dispose();Write-Output $Image }
}
