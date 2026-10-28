$ErrorActionPreference = 'Stop'
$base = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$out = Join-Path $base 'bin'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$refs = @('System.dll','System.Core.dll','System.Xml.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll') | ForEach-Object { '/r:' + (Join-Path $framework $_) }
$refs += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/r:' + (Join-Path "$framework\WPF" $_) }
$sources = @('Audio.cs','Engine.cs','Hotkey.cs','Shell.cs','Diagnostics.cs','Program.cs') | ForEach-Object { Join-Path $base $_ }
& "$framework\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /utf8output "/out:$out\MicSwitch.Chinese.exe" "/win32manifest:$base\app.manifest" "/win32icon:$base\mic-on.ico" "/resource:$base\mic-on.ico,mic-on.ico" "/resource:$base\mic-off.ico,mic-off.ico" "/resource:$base\Window.xaml,Window.xaml" @refs @sources
if ($LASTEXITCODE -ne 0) { throw '編譯失敗' }
Get-Item "$out\MicSwitch.Chinese.exe" | Select-Object FullName,Length
