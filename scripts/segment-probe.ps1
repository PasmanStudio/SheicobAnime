$ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'

# Get SW media playlist (follow relative path in master)
$swMaster = 'https://cuyyro04xrqrh.premilkyway.com/hls2/01/14090/c2fl307yw4rg_n/master.m3u8?t=TmrPajIVg3hGleS2nTaidESz9ZLVIqCZS-ambxWpe_A&s=1776482534&e=129600&f=70453863&srv=z13jtbat10fahdh456&i=0.4&sp=500&p1=z13jtbat10fahdh456&p2=z13jtbat10fahdh456&asn=7303'
$mediaPl = 'https://cuyyro04xrqrh.premilkyway.com/hls2/01/14090/c2fl307yw4rg_n/index-v1-a1.m3u8?t=TmrPajIVg3hGleS2nTaidESz9ZLVIqCZS-ambxWpe_A&s=1776482534&e=129600&f=70453863&srv=z13jtbat10fahdh456&i=0.4&sp=500&p1=z13jtbat10fahdh456&p2=z13jtbat10fahdh456&asn=7303'

Write-Host "=== SW media playlist (no referer, with Origin=vercel) ==="
try {
    $r = Invoke-WebRequest -Uri $mediaPl -Headers @{ 'User-Agent'=$ua; 'Origin'='https://sheicobanime.vercel.app' } -UseBasicParsing -TimeoutSec 15
    Write-Host ("  status=$($r.StatusCode) bytes=$($r.Content.Length)")
    Write-Host ("  ACAO: {0}" -f $r.Headers['Access-Control-Allow-Origin'])
    $text = [System.Text.Encoding]::UTF8.GetString($r.Content)
    Write-Host "--- first 30 lines ---"
    ($text -split "`n")[0..30] | ForEach-Object { Write-Host "  $_" }
    Write-Host "--- extracting first segment URI ---"
    $segLine = ($text -split "`n") | Where-Object { $_ -and -not $_.StartsWith('#') } | Select-Object -First 1
    if ($segLine) {
        # Build absolute URL (relative to media playlist)
        $baseUri = [System.Uri]$mediaPl
        $segUri = New-Object System.Uri($baseUri, $segLine.Trim())
        Write-Host "  segment URL: $($segUri.AbsoluteUri)"
        Write-Host "`n=== Probing first .ts segment (no referer, Origin=vercel) ==="
        try {
            $seg = Invoke-WebRequest -Uri $segUri -Headers @{ 'User-Agent'=$ua; 'Origin'='https://sheicobanime.vercel.app' } -UseBasicParsing -TimeoutSec 20
            Write-Host ("  status=$($seg.StatusCode) bytes=$($seg.Content.Length)")
            Write-Host ("  Content-Type: {0}" -f $seg.Headers['Content-Type'])
            Write-Host ("  ACAO: {0}" -f $seg.Headers['Access-Control-Allow-Origin'])
            # Check first 4 bytes
            $bytes = $seg.Content[0..3]
            $hex = ($bytes | ForEach-Object { "{0:X2}" -f $_ }) -join ' '
            Write-Host "  first 4 bytes: $hex (MPEG-TS should start with 0x47)"
        } catch {
            Write-Host ("  seg FAIL: {0}" -f $_.Exception.Message) -ForegroundColor Red
        }
    }
} catch {
    Write-Host ("  FAIL: {0}" -f $_.Exception.Message) -ForegroundColor Red
}
