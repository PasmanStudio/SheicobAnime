<#
.SYNOPSIS
    Post-deploy smoke test for SheicobAnime production services.
.DESCRIPTION
    Validates that API, frontend, and supporting services are responding correctly.
    Run after deploying to Railway/Vercel.
.PARAMETER ApiUrl
    Base URL of the production API (e.g. https://api.sheicobanime.com)
.PARAMETER WebUrl
    Base URL of the production frontend (e.g. https://sheicobanime.com)
.PARAMETER AdminKey
    Admin API key for testing admin endpoints (optional)
.EXAMPLE
    .\smoke-test.ps1 -ApiUrl "https://api.sheicobanime.com" -WebUrl "https://sheicobanime.com"
#>
param(
    [Parameter(Mandatory)][string]$ApiUrl,
    [Parameter(Mandatory)][string]$WebUrl,
    [string]$AdminKey
)

$ErrorActionPreference = "Continue"
$passed = 0
$failed = 0
$results = @()

function Test-Endpoint {
    param([string]$Name, [string]$Url, [int]$ExpectedStatus = 200, [string]$Contains = "", [hashtable]$Headers = @{})

    try {
        $params = @{ Uri = $Url; Method = "GET"; UseBasicParsing = $true; TimeoutSec = 15 }
        if ($Headers.Count -gt 0) { $params.Headers = $Headers }

        $response = Invoke-WebRequest @params -ErrorAction Stop
        $status = $response.StatusCode
        $body = $response.Content

        if ($status -ne $ExpectedStatus) {
            $script:failed++
            $script:results += [PSCustomObject]@{ Test = $Name; Status = "FAIL"; Detail = "Expected $ExpectedStatus, got $status" }
            return
        }

        if ($Contains -and $body -notmatch [regex]::Escape($Contains)) {
            $script:failed++
            $script:results += [PSCustomObject]@{ Test = $Name; Status = "FAIL"; Detail = "Response missing '$Contains'" }
            return
        }

        $script:passed++
        $script:results += [PSCustomObject]@{ Test = $Name; Status = "PASS"; Detail = "HTTP $status" }
    }
    catch {
        $script:failed++
        $detail = $_.Exception.Message
        if ($_.Exception.Response) { $detail = "HTTP $($_.Exception.Response.StatusCode.value__)" }
        $script:results += [PSCustomObject]@{ Test = $Name; Status = "FAIL"; Detail = $detail }
    }
}

Write-Host "`n=== SheicobAnime Production Smoke Test ===" -ForegroundColor Cyan
Write-Host "API:  $ApiUrl"
Write-Host "Web:  $WebUrl"
Write-Host ""

# ─── API Tests ────────────────────────────────────────────
Test-Endpoint -Name "API Health" -Url "$ApiUrl/health" -Contains "healthy"
Test-Endpoint -Name "API Genres" -Url "$ApiUrl/genres" -Contains "data"
Test-Endpoint -Name "API Series (paginated)" -Url "$ApiUrl/series?page=1&pageSize=5" -Contains "data"

# ─── Admin endpoint (requires key) ───────────────────────
if ($AdminKey) {
    Test-Endpoint -Name "Admin Scrape Jobs" -Url "$ApiUrl/admin/scrape-jobs" -Headers @{ "X-Admin-Key" = $AdminKey } -Contains "data"
    Test-Endpoint -Name "Admin Blocked Slugs" -Url "$ApiUrl/admin/blocked-slugs" -Headers @{ "X-Admin-Key" = $AdminKey } -Contains "data"
} else {
    Write-Host "  Skipping admin tests (no -AdminKey provided)" -ForegroundColor Yellow
}

# ─── Frontend Tests ───────────────────────────────────────
Test-Endpoint -Name "Frontend Homepage" -Url "$WebUrl" -ExpectedStatus 200
Test-Endpoint -Name "Frontend Genres Page" -Url "$WebUrl/genres" -ExpectedStatus 200
Test-Endpoint -Name "Frontend Search Page" -Url "$WebUrl/search?q=test" -ExpectedStatus 200

# ─── CORS preflight ──────────────────────────────────────
try {
    $corsResponse = Invoke-WebRequest -Uri "$ApiUrl/health" -Method OPTIONS -Headers @{
        "Origin" = $WebUrl
        "Access-Control-Request-Method" = "GET"
    } -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop

    $corsHeader = $corsResponse.Headers["Access-Control-Allow-Origin"]
    if ($corsHeader) {
        $passed++
        $results += [PSCustomObject]@{ Test = "CORS Preflight"; Status = "PASS"; Detail = "Allow-Origin: $corsHeader" }
    } else {
        $failed++
        $results += [PSCustomObject]@{ Test = "CORS Preflight"; Status = "FAIL"; Detail = "No Access-Control-Allow-Origin header" }
    }
}
catch {
    $failed++
    $results += [PSCustomObject]@{ Test = "CORS Preflight"; Status = "FAIL"; Detail = $_.Exception.Message }
}

# ─── Health endpoint response time ────────────────────────
try {
    $healthResponse = Invoke-RestMethod -Uri "$ApiUrl/health" -TimeoutSec 15
    if ($healthResponse.totalMs) {
        $passed++
        $results += [PSCustomObject]@{ Test = "Health Response Time"; Status = "PASS"; Detail = "db=${($healthResponse.dbMs)}ms cache=${($healthResponse.cacheMs)}ms total=${($healthResponse.totalMs)}ms" }
    } else {
        $passed++
        $results += [PSCustomObject]@{ Test = "Health Response Time"; Status = "PASS"; Detail = "Timing fields not present (older API version)" }
    }
}
catch {
    $failed++
    $results += [PSCustomObject]@{ Test = "Health Response Time"; Status = "FAIL"; Detail = $_.Exception.Message }
}

# ─── Security headers ────────────────────────────────────
try {
    $secResponse = Invoke-WebRequest -Uri "$ApiUrl/health" -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
    $secHeaders = @("X-Content-Type-Options", "X-Frame-Options", "Referrer-Policy")
    $secMissing = @()
    foreach ($h in $secHeaders) {
        if (-not $secResponse.Headers[$h]) { $secMissing += $h }
    }
    if ($secMissing.Count -eq 0) {
        $passed++
        $results += [PSCustomObject]@{ Test = "Security Headers"; Status = "PASS"; Detail = "All required headers present" }
    } else {
        $failed++
        $results += [PSCustomObject]@{ Test = "Security Headers"; Status = "FAIL"; Detail = "Missing: $($secMissing -join ', ')" }
    }
}
catch {
    $failed++
    $results += [PSCustomObject]@{ Test = "Security Headers"; Status = "FAIL"; Detail = $_.Exception.Message }
}

# ─── Correlation ID header ───────────────────────────────
try {
    $corrResponse = Invoke-WebRequest -Uri "$ApiUrl/health" -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
    if ($corrResponse.Headers["X-Correlation-Id"]) {
        $passed++
        $results += [PSCustomObject]@{ Test = "Correlation ID Header"; Status = "PASS"; Detail = "X-Correlation-Id=$($corrResponse.Headers['X-Correlation-Id'])" }
    } else {
        $failed++
        $results += [PSCustomObject]@{ Test = "Correlation ID Header"; Status = "FAIL"; Detail = "No X-Correlation-Id header in response" }
    }
}
catch {
    $failed++
    $results += [PSCustomObject]@{ Test = "Correlation ID Header"; Status = "FAIL"; Detail = $_.Exception.Message }
}

# ─── Admin auth required (should be 401 without key) ─────
try {
    $noAuthResponse = Invoke-WebRequest -Uri "$ApiUrl/admin/scrape-jobs" -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
    $failed++
    $results += [PSCustomObject]@{ Test = "Admin Auth Required"; Status = "FAIL"; Detail = "Admin endpoint accessible without API key" }
}
catch {
    if ($_.Exception.Response -and $_.Exception.Response.StatusCode.value__ -eq 401) {
        $passed++
        $results += [PSCustomObject]@{ Test = "Admin Auth Required"; Status = "PASS"; Detail = "Returns 401 without X-Admin-Key" }
    } else {
        $failed++
        $results += [PSCustomObject]@{ Test = "Admin Auth Required"; Status = "FAIL"; Detail = $_.Exception.Message }
    }
}

# ─── No secrets in frontend HTML ──────────────────────────
try {
    $htmlResponse = Invoke-WebRequest -Uri "$WebUrl" -UseBasicParsing -TimeoutSec 15 -ErrorAction Stop
    $html = $htmlResponse.Content
    $secretPatterns = @("ADMIN_API_KEY", "DATABASE_URL", "REDIS_URL", "SENTRY_DSN", "RESEND_API_KEY", "password", "secret")
    $foundSecrets = @()
    foreach ($p in $secretPatterns) {
        if ($html -match $p) { $foundSecrets += $p }
    }
    if ($foundSecrets.Count -eq 0) {
        $passed++
        $results += [PSCustomObject]@{ Test = "No Secrets in HTML"; Status = "PASS"; Detail = "No sensitive strings found in frontend HTML" }
    } else {
        $failed++
        $results += [PSCustomObject]@{ Test = "No Secrets in HTML"; Status = "FAIL"; Detail = "Found: $($foundSecrets -join ', ')" }
    }
}
catch {
    $failed++
    $results += [PSCustomObject]@{ Test = "No Secrets in HTML"; Status = "FAIL"; Detail = $_.Exception.Message }
}

# ─── Results ──────────────────────────────────────────────
Write-Host "`n─── Results ─────────────────────────────────" -ForegroundColor Cyan
$results | ForEach-Object {
    $color = if ($_.Status -eq "PASS") { "Green" } else { "Red" }
    Write-Host "  [$($_.Status)] $($_.Test): $($_.Detail)" -ForegroundColor $color
}

Write-Host "`n─── Summary ─────────────────────────────────" -ForegroundColor Cyan
Write-Host "  Passed: $passed" -ForegroundColor Green
Write-Host "  Failed: $failed" -ForegroundColor $(if ($failed -gt 0) { "Red" } else { "Green" })

if ($failed -gt 0) {
    Write-Host "`n  SMOKE TEST FAILED — $failed test(s) need attention" -ForegroundColor Red
    exit 1
} else {
    Write-Host "`n  ALL SMOKE TESTS PASSED" -ForegroundColor Green
    exit 0
}
