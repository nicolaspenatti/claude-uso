# Compila o claude-uso.exe com o compilador C# que já vem no Windows.
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
Set-Location $PSScriptRoot
& $csc /nologo /target:winexe /out:claude-uso.exe `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll UsoClaude.cs
if ($LASTEXITCODE -eq 0) { Write-Host "OK: claude-uso.exe gerado" } else { Write-Error "Falha na compilação" }
