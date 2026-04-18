# Test unpack of Streamwish + Vidhide to see if m3u8 is actually extractable
$ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"

function Unpack($packed) {
    # P.A.C.K.E.R unpacker in PowerShell (port of the JS algorithm)
    $m = [regex]::Match($packed, "eval\(function\(p,a,c,k,e,[dr]?\)\{.*?\}\(('|`")(.*?)\1,(\d+),(\d+),('|`")(.*?)\5\.split\('\|'\)")
    if (-not $m.Success) {
        # Try the more common variant
        $m = [regex]::Match($packed, "\}\(('|`")(.*?)\1,(\d+),(\d+),('|`")(.*?)\5\.split\('\|'\)", "Singleline")
    }
    if (-not $m.Success) { return $null }
    
    $payload = $m.Groups[2].Value
    $a = [int]$m.Groups[3].Value
    $c = [int]$m.Groups[4].Value
    $symtab = $m.Groups[6].Value.Split('|')
    
    # Decode helper: base-a encoding of integer
    function Decode-Id($id, $base) {
        if ($id -lt $base) {
            $chars = "0123456789abcdefghijklmnopqrstuvwxyz"
            return $chars[$id]
        }
        $result = Decode-Id ([Math]::Floor($id / $base)) $base
        $rem = $id % $base
        $chars = "0123456789abcdefghijklmnopqrstuvwxyz"
        return $result + $chars[$rem]
    }
    
    # Replace \w+ with symtab entry if indexed
    $result = [regex]::Replace($payload, '\b\w+\b', {
        param($mt)
        $word = $mt.Value
        # Parse the word as base-a number
        try {
            $idx = 0
            $chars = "0123456789abcdefghijklmnopqrstuvwxyz"
            foreach ($ch in $word.ToCharArray()) {
                $d = $chars.IndexOf($ch)
                if ($d -lt 0 -or $d -ge $a) { return $word }
                $idx = $idx * $a + $d
            }
            if ($idx -lt $symtab.Length -and $symtab[$idx]) {
                return $symtab[$idx]
            }
            return $word
        } catch { return $word }
    })
    
    return $result
}

function Fetch($url, $referer) {
    try {
        $res = Invoke-WebRequest -Uri $url -Headers @{
            "User-Agent"=$ua
            "Referer"=$referer
            "Accept"="text/html,application/xhtml+xml"
        } -MaximumRedirection 5 -UseBasicParsing -TimeoutSec 20
        return $res.Content
    } catch {
        Write-Host "  ERROR fetching: $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
}

Write-Host "=== STREAMWISH (sfastwish.com) ===" -ForegroundColor Cyan
$sw = Fetch "https://sfastwish.com/e/c2fl307yw4rg" "https://sfastwish.com/"
if ($sw) {
    # Find the eval(...) block
    $m = [regex]::Match($sw, "eval\(function\(p,a,c,k,e,d?\)\{.*?\.split\('\|'\)\)\)", "Singleline")
    if ($m.Success) {
        Write-Host "  Packed block length: $($m.Value.Length)"
        $unpacked = Unpack $m.Value
        if ($unpacked) {
            Write-Host "  Unpacked length: $($unpacked.Length)"
            # Find m3u8
            $urls = [regex]::Matches($unpacked, "(https?://[^\s''`"\\]+\.m3u8[^\s''`"\\]*)")
            if ($urls.Count -gt 0) {
                Write-Host "  [+] M3U8 URLs found in unpacked:" -ForegroundColor Green
                $urls | Select-Object -First 5 | ForEach-Object { Write-Host "      $($_.Value)" }
            } else {
                Write-Host "  [!] No m3u8 in unpacked — first 400 chars:" -ForegroundColor Yellow
                Write-Host "      $($unpacked.Substring(0, [Math]::Min(400, $unpacked.Length)))"
            }
        } else {
            Write-Host "  [X] Unpack returned null" -ForegroundColor Red
        }
    } else {
        Write-Host "  [X] No packed eval block matched" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "=== VIDHIDE (vidhidevip.com) correct path ===" -ForegroundColor Cyan
$vh = Fetch "https://vidhidevip.com/embed/o3zpuy8qksv6" "https://vidhidevip.com/"
if ($vh) {
    $m = [regex]::Match($vh, "eval\(function\(p,a,c,k,e,d?\)\{.*?\.split\('\|'\)\)\)", "Singleline")
    if ($m.Success) {
        Write-Host "  Packed block length: $($m.Value.Length)"
        $unpacked = Unpack $m.Value
        if ($unpacked) {
            Write-Host "  Unpacked length: $($unpacked.Length)"
            $urls = [regex]::Matches($unpacked, "(https?://[^\s''`"\\]+\.m3u8[^\s''`"\\]*)")
            if ($urls.Count -gt 0) {
                Write-Host "  [+] M3U8 URLs found:" -ForegroundColor Green
                $urls | Select-Object -First 5 | ForEach-Object { Write-Host "      $($_.Value)" }
            } else {
                Write-Host "  [!] No m3u8 in unpacked — first 400 chars:" -ForegroundColor Yellow
                Write-Host "      $($unpacked.Substring(0, [Math]::Min(400, $unpacked.Length)))"
            }
        }
    } else {
        Write-Host "  [X] No packed block" -ForegroundColor Red
    }
}
