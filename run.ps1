# Starts Hatchet and opens it in the default browser. Stop it with Ctrl+C.
$url = 'http://localhost:5199'

$opener = Start-Job -ArgumentList $url -ScriptBlock {
    param($url)
    for ($i = 0; $i -lt 180; $i++) {
        try {
            Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2 | Out-Null
            Start-Process $url
            return
        } catch {
            Start-Sleep -Seconds 1
        }
    }
}

try {
    dotnet run --project (Join-Path $PSScriptRoot 'src/Factory.Web/Factory.Web.csproj')
} finally {
    Stop-Job $opener -ErrorAction SilentlyContinue
    Remove-Job $opener -Force -ErrorAction SilentlyContinue
}
