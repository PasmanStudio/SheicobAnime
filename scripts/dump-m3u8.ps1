$ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'

$targets = @(
  @{ N='streamwish'; R='https://sfastwish.com/';  U='https://cuyyro04xrqrh.premilkyway.com/hls2/01/14090/c2fl307yw4rg_n/master.m3u8?t=TmrPajIVg3hGleS2nTaidESz9ZLVIqCZS-ambxWpe_A&s=1776482534&e=129600&f=70453863&srv=z13jtbat10fahdh456&i=0.4&sp=500&p1=z13jtbat10fahdh456&p2=z13jtbat10fahdh456&asn=7303' },
  @{ N='vidhide';    R='https://vidhidevip.com/'; U='https://yjrnY6VWd3Y1oXUh.acek-cdn.com/hls2/01/07925/o3zpuy8qksv6_,l,n,h,.urlset/master.m3u8?t=hO0xoOQ1RV4ToTOj4nmv8QuI9MogyRM9_aj3ypguaH8&s=1776482536&e=129600&f=39626481&srv=e4RHfFUBYeX4H3m&i=0.4&sp=500&p1=e4RHfFUBYeX4H3m&p2=e4RHfFUBYeX4H3m&asn=7303' }
)

$outDir = 'c:\S\SheicobAnime\scripts\ResolverProbe\dumps'
foreach ($t in $targets) {
  $r = Invoke-WebRequest -Uri $t.U -Headers @{ Referer=$t.R; 'User-Agent'=$ua } -UseBasicParsing -TimeoutSec 20
  $text = [System.Text.Encoding]::UTF8.GetString($r.Content)
  $path = Join-Path $outDir "$($t.N).m3u8"
  [System.IO.File]::WriteAllText($path, $text)
  Write-Host ("Wrote {0} ({1} bytes, status {2})" -f $path, $r.Content.Length, $r.StatusCode)
  Write-Host "--- FIRST 40 LINES ---"
  ($text -split "`n")[0..40] | ForEach-Object { Write-Host $_ }
  Write-Host "--- END ---`n"
}
