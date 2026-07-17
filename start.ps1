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
    Write-Warning "Published binary not found at $Bin, falling back to a Release build (dev mode)."
    & dotnet build $ScriptDir -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet build failed."
        exit 1
    }
    # Launch the built DLL directly (dotnet <dll>) instead of 'dotnet run':
    # Start-Process -PassThru below can't reliably track 'dotnet run's PID on
    # Windows -- it returns a PID that stops existing once the real server is
    # up, so Wait-Process fails and the finally block tears the tunnel down
    # right after "MCP server ready" prints. 'dotnet <dll>' runs in the exact
    # process Start-Process spawns, same as the published-exe branch above.
    $Dll = Join-Path $ScriptDir "bin\Release\net10.0\LlamaMcp.dll"
    $AppExe = "dotnet"
    # Start-Process -ArgumentList joins array elements with a plain space and
    # does not quote elements that contain one -- $Dll's path breaks in two
    # here whenever it's under a directory with a space (e.g. a Windows user
    # profile like "C:\Users\Jane Doe\..."), passing dotnet.exe a truncated
    # first argument. Wrap it in literal quotes so it survives as one token.
    $AppArgs = @("`"$Dll`"", "--urls", "http://localhost:$Port")
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
