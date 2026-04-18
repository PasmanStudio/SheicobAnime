# Test script — probe the 3 Sheicob mirrors for Re:Zero S4E2 and see what really comes back
$ErrorActionPreference = "Continue"
$ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"

function Probe($name, $url, $referer) {
    Write-Host ""
    Write-Host "=== $name ===" -ForegroundColor Cyan
    Write-Host "URL: $url"
    try {
        $res = Invoke-WebRequest -Uri $url -Headers @{
            "User-Agent"=$ua
            "Referer"=$referer
            "Accept"="text/html,application/xhtml+xml"
            "Accept-Language"="en-US,en;q=0.9"
        } -MaximumRedirection 5 -UseBasicParsing -TimeoutSec 20
        Write-Host "Status: $($res.StatusCode)"
        Write-Host "Length: $($res.Content.Length) bytes"
        $html = $res.Content
        
        # Look for packed JS eval block
        if ($html -match 'eval\(function\(p,a,c,k,e,d?\)') {
            Write-Host "  [+] Packed JS eval() block detected" -ForegroundColor Green
        } else {
            Write-Host "  [!] No packed JS found" -ForegroundColor Yellow
        }
        
        # Look for .m3u8 references
        $m3u8 = [regex]::Matches($html, '(https?://[^\s"''<>]+\.m3u8[^\s"''<>]*)')
        if ($m3u8.Count -gt 0) {
            Write-Host "  [+] Found $($m3u8.Count) .m3u8 URL(s) in raw HTML:" -ForegroundColor Green
            $m3u8 | Select-Object -First 3 | ForEach-Object { Write-Host "      $($_.Value)" }
        }
        
        # Look for .mp4 references
        $mp4 = [regex]::Matches($html, '(https?://[^\s"''<>]+\.mp4[^\s"''<>]*)')
        if ($mp4.Count -gt 0) {
            Write-Host "  [+] Found $($mp4.Count) .mp4 URL(s) in raw HTML:" -ForegroundColor Green
            $mp4 | Select-Object -First 3 | ForEach-Object { Write-Host "      $($_.Value)" }
        }
        
        # Look for common provider patterns
        if ($html -match '(?i)cloudflare|captcha|access denied|turnstile') {
            Write-Host "  [!] Possible Cloudflare/captcha/turnstile wall" -ForegroundColor Red
        }
        if ($html -match '(?i)<title>([^<]{1,120})</title>') {
            Write-Host "  Title: $($matches[1])"
        }
        
        # Save first 500 chars
        $snippet = $html.Substring(0, [Math]::Min(800, $html.Length))
        Write-Host "  First bytes: $($snippet -replace '\s+',' ' | Out-String -Stream | Select-Object -First 1)"
    } catch {
        Write-Host "  [X] ERROR: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# Mp4Upload
Probe "MP4UPLOAD" "https://www.mp4upload.com/embed-vmr4f84e9pz0.html" "https://www.mp4upload.com/"

# Streamwish (sfastwish mirror)
Probe "STREAMWISH" "https://sfastwish.com/e/c2fl307yw4rg" "https://sfastwish.com/"

# Vidhide
Probe "VIDHIDE" "https://vidhidevip.com/embed/o3zpuy8qksv6" "https://vidhidevip.com/"

# Also try vidhide with different path since our resolver uses embed-{id}.html
Probe "VIDHIDE-alt" "https://vidhidevip.com/embed-o3zpuy8qksv6.html" "https://vidhidevip.com/"
