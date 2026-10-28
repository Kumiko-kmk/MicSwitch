$ErrorActionPreference='Stop'
$source=Split-Path $PSScriptRoot
$out=Join-Path $source 'bin\tests'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach($test in @('Harness','FocusTest','CaptureTest')) {
 & $compiler /nologo /target:exe /platform:x64 /optimize+ "/out:$out\$test.exe" (Join-Path $PSScriptRoot "$test.cs") (Join-Path $source 'Audio.cs')
 if($LASTEXITCODE -ne 0){throw "Test build failed: $test"}
}
Write-Output "Test tools: $out"
