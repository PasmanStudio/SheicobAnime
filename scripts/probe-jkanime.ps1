$file = "$env:TEMP\jktest.html"
$content = [System.IO.File]::ReadAllText($file)

Write-Host "File size: $($content.Length)"

# Find var servers
$pos = $content.IndexOf("var servers")
Write-Host "var servers position: $pos"

if ($pos -ge 0) {
    $snippet = $content.Substring($pos, [Math]::Min(800, $content.Length - $pos))
    Write-Host $snippet
}

# Search for 'remote' keyword
$pos2 = $content.IndexOf('"remote"')
Write-Host "---remote keyword position: $pos2"
if ($pos2 -ge 0) {
    Write-Host $content.Substring([Math]::Max(0,$pos2-100), 300)
}

# Check for base64-looking strings
$base64matches = [regex]::Matches($content, '"remote"\s*:\s*"([A-Za-z0-9+/=]{20,})"')
Write-Host "--- base64 'remote' values: $($base64matches.Count)"
foreach ($bm in $base64matches) {
    $b64 = $bm.Groups[1].Value
    try {
        $decoded = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64)).Trim()
        Write-Host "  Decoded: $decoded"
        # Now probe that URL for embeddability
        try {
            $pr = Invoke-WebRequest $decoded -Method Get -UseBasicParsing -TimeoutSec 10 -MaximumRedirection 5
            $xfo = $pr.Headers["X-Frame-Options"]
            $csp = $pr.Headers["Content-Security-Policy"]
            Write-Host "    Status=$($pr.StatusCode) XFO='$xfo' CSP(frame)='$(if($csp){([regex]::Match($csp,'frame-ancestors[^;]+')).Value}else{'none'})'"
        } catch {
            Write-Host "    Probe failed: $($_.Exception.Message)"
        }
    } catch {
        Write-Host "  Raw (not valid base64): $($b64.Substring(0,[Math]::Min(60,$b64.Length)))"
    }
}
