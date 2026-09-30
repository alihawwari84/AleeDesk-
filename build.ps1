$ErrorActionPreference = 'Stop'
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$root = $PSScriptRoot
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$src = 'Protocol','Relay','Lan','Native','Ui','Host','Viewer','MainForm','Program' | ForEach-Object { "$root\src\$_.cs" }
& $csc /nologo /target:winexe /optimize+ /main:RemoteDesk.Program /out:"$root\dist\ALeeDesk.exe" `
    /win32icon:"$root\assets\app.ico" "/resource:$root\assets\avatar.png,ALeeDesk.avatar.png" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll @src
if ($LASTEXITCODE -ne 0) { throw 'ALeeDesk.exe build failed' }
Write-Host "Built: $root\dist\ALeeDesk.exe"
