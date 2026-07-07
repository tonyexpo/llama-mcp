# Starts the MCP server + a Cloudflare quick tunnel together and prints the
# public URL and bearer token to paste into an MCP client.
$ErrorActionPreference = "Stop"

$Port = if ($env:PORT) { $env:PORT } else { "5181" }
$Token = if ($env:LLAMA_MCP_TOKEN) { $env:LLAMA_MCP_TOKEN } else { [guid]::NewGuid().ToString("N") }

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Bin = Join-Path $ScriptDir "publish\win-x64\LlamaMcp.exe"

if (Test-Path $Bin) {
    $AppExe = $Bin
    $AppArgs = @("--urls", "http://localhost:$Port")
} else {
    Write-Warning "Published binary not found at $Bin, falling back to 'dotnet run' (dev mode)."
    $AppExe = "dotnet"
    $AppArgs = @("run", "--project", $ScriptDir, "-c", "Release", "--urls", "http://localhost:$Port")
}

if (-not (Get-Command cloudflared -ErrorAction SilentlyContinue)) {
    Write-Error "cloudflared not found. Install it: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/"
    exit 1
}

$env:Auth__BearerToken = $Token
$AppProcess = Start-Process -FilePath $AppExe -ArgumentList $AppArgs -PassThru -NoNewWindow

$CfLog = [System.IO.Path]::GetTempFileName()
$CfProcess = Start-Process -FilePath "cloudflared" -ArgumentList @("tunnel", "--url", "http://localhost:$Port") `
    -PassThru -NoNewWindow -RedirectStandardOutput $CfLog -RedirectStandardError "$CfLog.err"

try {
    Write-Host "Waiting for tunnel URL..."
    $Url = $null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        $Lines = (Get-Content $CfLog -ErrorAction SilentlyContinue) + (Get-Content "$CfLog.err" -ErrorAction SilentlyContinue)
        $Match = $Lines | Select-String -Pattern 'https://[a-zA-Z0-9.-]+\.trycloudflare\.com' | Select-Object -First 1
        if ($Match) {
            $Url = $Match.Matches[0].Value
            break
        }
    }

    if (-not $Url) {
        Write-Error "Could not determine tunnel URL, check $CfLog / $CfLog.err"
        exit 1
    }

    Write-Host ""
    Write-Host "MCP server ready:"
    Write-Host "  URL:   $Url/"
    Write-Host "  Token: $Token"
    Write-Host ""
    Write-Host "Press Ctrl+C to stop."

    Wait-Process -Id $AppProcess.Id
}
finally {
    Stop-Process -Id $AppProcess.Id -ErrorAction SilentlyContinue
    Stop-Process -Id $CfProcess.Id -ErrorAction SilentlyContinue
    Remove-Item $CfLog, "$CfLog.err" -ErrorAction SilentlyContinue
}
