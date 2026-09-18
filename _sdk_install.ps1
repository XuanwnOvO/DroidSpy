$ErrorActionPreference = "Continue"
$env:JAVA_HOME = "C:\JDK\jdk-17.0.12"
$sdk = "D:\Android\Sdk"
$bin = Join-Path $sdk "cmdline-tools\latest\bin\sdkmanager.bat"

$log = "D:\DroidSpy\_sdk_install.log"
Remove-Item $log -ErrorAction SilentlyContinue

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $bin
$psi.Arguments = "--sdk_root=`"$sdk`" --install `"platforms;android-35`" `"build-tools;35.0.0`" `"platform-tools`""
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.WorkingDirectory = "D:\DroidSpy"
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8

$p = [System.Diagnostics.Process]::Start($psi)
$init = "y`r`n" * 50
$p.StandardInput.Write($init)
$p.StandardInput.Flush()

$out = $p.StandardOutput.ReadToEnd()
$err = $p.StandardError.ReadToEnd()
$p.WaitForExit()

$out + "`r`n--- STDERR ---`r`n" + $err | Set-Content -Path $log -Encoding UTF8
Write-Host ("ExitCode=" + $p.ExitCode)
Write-Host "done"
