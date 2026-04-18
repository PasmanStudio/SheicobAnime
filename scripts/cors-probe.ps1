$ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'
$swUrl = 'https://cuyyro04xrqrh.premilkyway.com/hls2/01/14090/c2fl307yw4rg_n/master.m3u8?t=TmrPajIVg3hGleS2nTaidESz9ZLVIqCZS-ambxWpe_A&s=1776482534&e=129600&f=70453863&srv=z13jtbat10fahdh456&i=0.4&sp=500&p1=z13jtbat10fahdh456&p2=z13jtbat10fahdh456&asn=7303'
$vhUrl = 'https://yjrnY6VWd3Y1oXUh.acek-cdn.com/hls2/01/07925/o3zpuy8qksv6_,l,n,h,.urlset/master.m3u8?t=hO0xoOQ1RV4ToTOj4nmv8QuI9MogyRM9_aj3ypguaH8&s=1776482536&e=129600&f=39626481&srv=e4RHfFUBYeX4H3m&i=0.4&sp=500&p1=e4RHfFUBYeX4H3m&p2=e4RHfFUBYeX4H3m&asn=7303'

function Probe($label, $url, $withReferer, $referer) {
    Write-Host "`n--- $label ---" -ForegroundColor Cyan
    $h = @{ 'User-Agent' = $ua; 'Origin' = 'https://sheicobanime.vercel.app' }
    if ($withReferer) { $h['Referer'] = $referer }
    try {
        $r = Invoke-WebRequest -Uri $url -Headers $h -UseBasicParsing -TimeoutSec 10
        Write-Host ("  status=$($r.StatusCode) bytes=$($r.Content.Length)")
        $corsKeys = $r.Headers.Keys | Where-Object { $_ -match 'Access-Control' }
        if ($corsKeys.Count -eq 0) {
            Write-Host '  CORS: NONE' -ForegroundColor Yellow
        } else {
            foreach ($k in $corsKeys) { $v = $r.Headers[$k]; Write-Host ("  {0}: {1}" -f $k, $v) }
        }
    } catch {
        $s = try { $_.Exception.Response.StatusCode.value__ } catch { 'n/a' }
        Write-Host "  FAIL status=$s msg=$($_.Exception.Message)" -ForegroundColor Red
    }
}

Probe 'SW no-Referer' $swUrl $false $null
Probe 'SW with-Referer' $swUrl $true 'https://sfastwish.com/'
Probe 'VH no-Referer' $vhUrl $false $null
Probe 'VH with-Referer' $vhUrl $true 'https://vidhidevip.com/'
