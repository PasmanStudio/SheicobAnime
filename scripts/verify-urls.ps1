$ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'

$targets = @(
  @{ N='STREAMWISH m3u8'; R='https://sfastwish.com/';    U='https://cuyyro04xrqrh.premilkyway.com/hls2/01/14090/c2fl307yw4rg_n/master.m3u8?t=TmrPajIVg3hGleS2nTaidESz9ZLVIqCZS-ambxWpe_A&s=1776482534&e=129600&f=70453863&srv=z13jtbat10fahdh456&i=0.4&sp=500&p1=z13jtbat10fahdh456&p2=z13jtbat10fahdh456&asn=7303' },
  @{ N='VIDHIDE m3u8';    R='https://vidhidevip.com/';   U='https://yjrnY6VWd3Y1oXUh.acek-cdn.com/hls2/01/07925/o3zpuy8qksv6_,l,n,h,.urlset/master.m3u8?t=hO0xoOQ1RV4ToTOj4nmv8QuI9MogyRM9_aj3ypguaH8&s=1776482536&e=129600&f=39626481&srv=e4RHfFUBYeX4H3m&i=0.4&sp=500&p1=e4RHfFUBYeX4H3m&p2=e4RHfFUBYeX4H3m&asn=7303' },
  @{ N='MP4UPLOAD mp4';   R='https://www.mp4upload.com/'; U='https://a4.mp4upload.com:183/d/xkx5xendz3b4quuoysrqyp2scwcbvelqoblyouzyixgg7kt73dog2fgxvdy4ogrm2yv3u6ew/video.mp4' }
)

foreach ($t in $targets) {
  Write-Host ("`n--- {0} ---" -f $t.N) -ForegroundColor Cyan
  Write-Host ("  URL: {0}" -f $t.U.Substring(0,[Math]::Min(120,$t.U.Length)))
  try {
    $r = Invoke-WebRequest -Uri $t.U -Headers @{ Referer=$t.R; 'User-Agent'=$ua } -TimeoutSec 20 -MaximumRedirection 5 -UseBasicParsing
    Write-Host ("  GET status: {0}" -f $r.StatusCode) -ForegroundColor Green
    Write-Host ("  Content-Type: {0}" -f $r.Headers['Content-Type'])
    Write-Host ("  Bytes: {0}" -f $r.RawContentLength)
    if ($r.Content -and $r.Content.Length -lt 4000) {
      Write-Host ("  Body preview: {0}" -f ($r.Content.Substring(0,[Math]::Min(400,$r.Content.Length)) -replace "`n","\n"))
    } elseif ($r.Content -and $r.Content.StartsWith('#EXTM3U')) {
      Write-Host '  [+] Valid HLS master manifest' -ForegroundColor Green
      $lines = $r.Content -split "`n" | Select-Object -First 10
      $lines | ForEach-Object { Write-Host "    $_" }
    }
  } catch {
    $status = $null
    try { $status = $_.Exception.Response.StatusCode.value__ } catch {}
    Write-Host ("  GET FAILED: {0} (status={1})" -f $_.Exception.Message, $status) -ForegroundColor Red
  }
}
