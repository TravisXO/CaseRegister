<#
.SYNOPSIS
  Checks a deployed CaseRegister site from the outside: the app is up, the security headers are there, search engines are kept out, and the API
  refuses anyone who does not come through the proxy. Prints PASS or FAIL for each check and exits 1 if any failed.

.DESCRIPTION
  It sends a handful of ordinary requests and prints no body and no cookie value, only whether each check passed. It changes nothing on the site.
  It cannot sign in for you: after it passes, do the short manual checklist in docs/deployment.md (sign-in, two-factor, verify chain).

  The first request after a quiet spell can take about a minute, because Render's free plan puts the API to sleep after 15 idle minutes. The
  timeout here allows for that.

.PARAMETER SiteUrl
  The address of the site, such as https://caseregister.pages.dev

.PARAMETER ApiUrl
  Optional: the API's own address on Render, such as https://caseregister-api.onrender.com. With it, the script also checks that a direct request
  to Render without the proxy secret is refused. Without it those checks are skipped (and said to be).

.EXAMPLE
  .\scripts\smoke-test.ps1 -SiteUrl https://caseregister.pages.dev -ApiUrl https://caseregister-api.onrender.com
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$SiteUrl,

    [string]$ApiUrl,

    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Net.Http

$SiteUrl = $SiteUrl.TrimEnd('/')
if ($ApiUrl) { $ApiUrl = $ApiUrl.TrimEnd('/') }

foreach ($address in @($SiteUrl, $ApiUrl) | Where-Object { $_ }) {
    if ($address -notmatch '^https://[^/\s]+$') {
        throw "'$address' must be an https address with no path, such as https://caseregister.pages.dev"
    }
}

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $false
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)

$script:failures = 0
$script:passes = 0
$script:skipped = 0

function Request([string]$Url, [string]$Method = 'GET', [hashtable]$Headers = @{}) {
    $message = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($Method), $Url)
    foreach ($name in $Headers.Keys) { [void]$message.Headers.TryAddWithoutValidation($name, [string]$Headers[$name]) }
    try {
        $response = $client.SendAsync($message).GetAwaiter().GetResult()
    } catch {
        # A first request to a sleeping API can time out; try once more before calling it a failure.
        $message = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($Method), $Url)
        foreach ($name in $Headers.Keys) { [void]$message.Headers.TryAddWithoutValidation($name, [string]$Headers[$name]) }
        try {
            $response = $client.SendAsync($message).GetAwaiter().GetResult()
        } catch {
            throw "no answer from $Url (is it up? a sleeping API can take about a minute; try again)"
        }
    }

    $map = @{}
    foreach ($header in $response.Headers) { $map[$header.Key.ToLowerInvariant()] = @($header.Value) }
    foreach ($header in $response.Content.Headers) { $map[$header.Key.ToLowerInvariant()] = @($header.Value) }

    [pscustomobject]@{
        Status  = [int]$response.StatusCode
        Headers = $map
        Body    = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
}

function Header($Response, [string]$Name) {
    $values = $Response.Headers[$Name.ToLowerInvariant()]
    if ($values) { return ($values -join ', ') }
    return $null
}

function Check([string]$Name, [scriptblock]$Test) {
    try {
        $reason = & $Test
        if ($null -eq $reason -or $reason -eq $true) {
            Write-Host "[PASS] $Name" -ForegroundColor Green
            $script:passes++
        } else {
            Write-Host "[FAIL] $Name - $reason" -ForegroundColor Red
            $script:failures++
        }
    } catch {
        Write-Host "[FAIL] $Name - $($_.Exception.Message)" -ForegroundColor Red
        $script:failures++
    }
}

function Skip([string]$Name, [string]$Why) {
    Write-Host "[SKIP] $Name - $Why" -ForegroundColor Yellow
    $script:skipped++
}

# A header must be there and say something.
function Expect-Header($Response, [string]$Name, [string]$Pattern) {
    $value = Header $Response $Name
    if (-not $value) { return "$Name is missing" }
    if ($value -notmatch $Pattern) { return "$Name is '$value', which does not match $Pattern" }
    return $null
}

function First-Failure([string[]]$Results) {
    foreach ($result in $Results) { if ($result) { return $result } }
    return $null
}

# The one fixed word that must not be in a Server header: what the application is built on. (Cloudflare puts its own name there.)
$stackNames = 'kestrel|asp\.net|microsoft|iis|nginx|apache|express'

Write-Host "Smoke test of $SiteUrl" -ForegroundColor Cyan
if ($ApiUrl) { Write-Host "and the API at $ApiUrl" -ForegroundColor Cyan }
Write-Host 'The first request can take about a minute if the API is asleep.'
Write-Host ''

# --- The site ---------------------------------------------------------------------------------------------------

$landing = $null
Check 'The site serves the app' {
    $script:landing = Request "$SiteUrl/"
    if ($script:landing.Status -ne 200) { return "answered $($script:landing.Status)" }
    if ($script:landing.Body -notmatch '<div id="root">') { return 'the page is not the app' }
}

Check 'The site sends the strict security headers on its pages' {
    if (-not $script:landing) { return 'the page could not be fetched (see the first check)' }
    First-Failure @(
        (Expect-Header $script:landing 'Content-Security-Policy' "default-src 'none'"),
        (Expect-Header $script:landing 'Content-Security-Policy' "frame-ancestors 'none'"),
        (Expect-Header $script:landing 'Content-Security-Policy' 'script-src ''self'' https://challenges\.cloudflare\.com;'),
        (Expect-Header $script:landing 'Strict-Transport-Security' 'max-age=(\d{8,})'),
        (Expect-Header $script:landing 'X-Content-Type-Options' '^nosniff$'),
        (Expect-Header $script:landing 'Referrer-Policy' '^no-referrer$'),
        (Expect-Header $script:landing 'Permissions-Policy' 'camera=\(\)'),
        (Expect-Header $script:landing 'Cross-Origin-Opener-Policy' 'same-origin')
    )
}

Check 'The content security policy allows no inline script, no eval and no outside origin but Turnstile' {
    if (-not $script:landing) { return 'the page could not be fetched (see the first check)' }
    $policy = Header $script:landing 'Content-Security-Policy'
    if ($policy -match "unsafe-inline|unsafe-eval") { return 'it allows unsafe-inline or unsafe-eval' }
    $origins = [regex]::Matches($policy, 'https?://[^\s;]+') | ForEach-Object { $_.Value } | Sort-Object -Unique
    $extra = $origins | Where-Object { $_ -ne 'https://challenges.cloudflare.com' }
    if ($extra) { return "it names another origin: $($extra -join ', ')" }
}

Check 'The page is never cached for long, and built files are kept for a year' {
    if (-not $script:landing) { return 'the page could not be fetched (see the first check)' }
    $page = Expect-Header $script:landing 'Cache-Control' 'no-cache|no-store|max-age=0'
    if ($page) { return $page }
    $asset = [regex]::Match($script:landing.Body, '/assets/[A-Za-z0-9._-]+\.js').Value
    if (-not $asset) { return 'no built script is linked from the page' }
    $file = Request "$SiteUrl$asset"
    if ($file.Status -ne 200) { return "the script answered $($file.Status)" }
    return Expect-Header $file 'Cache-Control' 'immutable'
}

Check 'Search engines are kept out: robots.txt, the noindex meta tag and the X-Robots-Tag header' {
    if (-not $script:landing) { return 'the page could not be fetched (see the first check)' }
    $robots = Request "$SiteUrl/robots.txt"
    if ($robots.Status -ne 200 -or $robots.Body -notmatch '(?m)^Disallow:\s*/\s*$') { return 'robots.txt does not disallow everything' }
    if ($script:landing.Body -notmatch '<meta name="robots" content="[^"]*noindex') { return 'the page has no noindex meta tag' }
    return Expect-Header $script:landing 'X-Robots-Tag' 'noindex'
}

Check 'An unknown page is the app, not an error page that names software' {
    $unknown = Request "$SiteUrl/no/such/page"
    if ($unknown.Status -ne 200) { return "answered $($unknown.Status)" }
    $server = Header $unknown 'Server'
    if ($server -and $server -match $stackNames) { return "Server header says '$server'" }
}

Check 'The API reference page and description are not served' {
    foreach ($path in '/scalar/v1', '/openapi/v1.json') {
        $reference = Request "$SiteUrl$path"
        if ($reference.Body -match '"openapi"\s*:|Scalar API Reference') { return "$path serves the API description" }
    }
}

# --- The API, through the proxy -----------------------------------------------------------------------------------

$csrf = $null
Check 'The API answers through the proxy (the secret is accepted, the API is awake)' {
    $script:csrf = Request "$SiteUrl/api/auth/csrf"
    if ($script:csrf.Status -ne 200) { return "answered $($script:csrf.Status)" }
    if ($script:csrf.Body -notmatch '"token"') { return 'no anti-forgery token in the answer' }
}

Check 'API responses carry the API''s own security headers' {
    if (-not $script:csrf) { return 'the API did not answer (see the check above)' }
    First-Failure @(
        (Expect-Header $script:csrf 'Content-Security-Policy' "default-src 'none'"),
        (Expect-Header $script:csrf 'Strict-Transport-Security' 'max-age=(\d{8,})'),
        (Expect-Header $script:csrf 'X-Content-Type-Options' '^nosniff$'),
        (Expect-Header $script:csrf 'Cache-Control' 'no-store'),
        (Expect-Header $script:csrf 'Referrer-Policy' '^no-referrer$')
    )
}

Check 'The cookie the API sets is a __Host- cookie that is Secure, HttpOnly and SameSite=Strict (it came through untouched)' {
    if (-not $script:csrf) { return 'the API did not answer (see the check above)' }
    $cookies = $script:csrf.Headers['set-cookie']
    if (-not $cookies) { return 'no Set-Cookie came back' }
    foreach ($cookie in $cookies) {
        $name = ($cookie -split '=')[0]
        if ($name -notmatch '^__Host-') { return "cookie '$name' is not a __Host- cookie" }
        foreach ($attribute in 'secure', 'httponly', 'samesite=strict', 'path=/') {
            if ($cookie -notmatch "(?i)(^|;\s*)$([regex]::Escape($attribute))(;|$)") { return "cookie '$name' lacks $attribute" }
        }
        if ($cookie -match '(?i);\s*domain=') { return "cookie '$name' has a Domain" }
    }
}

Check 'Nothing in the headers names the software behind the site' {
    if (-not $script:csrf) { return 'the API did not answer (see the check above)' }
    foreach ($response in @($script:landing, $script:csrf)) {
        $server = Header $response 'Server'
        if ($server -and $server -match $stackNames) { return "Server header says '$server'" }
        if (Header $response 'X-Powered-By') { return 'X-Powered-By is present' }
        if (Header $response 'X-AspNet-Version') { return 'X-AspNet-Version is present' }
    }
}

Check 'Anyone sending their own proxy secret or visitor address through the site changes nothing' {
    $withFakes = Request "$SiteUrl/api/auth/csrf" 'GET' @{ 'X-Proxy-Secret' = 'a-visitors-guess'; 'X-Client-Ip' = '6.6.6.6'; 'X-Forwarded-For' = '6.6.6.6' }
    if ($withFakes.Status -ne 200) { return "answered $($withFakes.Status): the site's own secret should have been used" }
}

# --- The API, directly ---------------------------------------------------------------------------------------------

if ($ApiUrl) {
    Check 'Render answers its liveness check with no secret (the one thing it does)' {
        $live = Request "$ApiUrl/health/live"
        if ($live.Status -ne 200 -or $live.Body -ne 'Healthy') { return "answered $($live.Status)" }
    }

    Check 'A direct request to Render without the secret is refused' {
        foreach ($path in '/api/auth/csrf', '/health/ready', '/api/auth/session') {
            $direct = Request "$ApiUrl$path"
            if ($direct.Status -ne 403) { return "$path answered $($direct.Status), not 403" }
        }
    }

    Check 'A direct request with a wrong secret, or a made-up visitor address, is refused too' {
        $wrong = Request "$ApiUrl/api/auth/csrf" 'GET' @{ 'X-Proxy-Secret' = 'a-wrong-secret-a-wrong-secret-a-wrong-secret-a-wrong-secret'; 'X-Client-Ip' = '6.6.6.6' }
        if ($wrong.Status -ne 403) { return "answered $($wrong.Status), not 403" }
    }

    Check 'The refusal gives nothing away' {
        $direct = Request "$ApiUrl/api/auth/csrf"
        if ($direct.Body -match '(?i)secret|proxy|cloudflare|render') { return 'the refusal says too much' }
        $server = Header $direct 'Server'
        if ($server -and $server -match $stackNames) { return "Server header says '$server'" }
    }
} else {
    Skip 'Render answers liveness with no secret, and refuses everything else' 'no -ApiUrl given'
}

# --- Summary --------------------------------------------------------------------------------------------------------

Write-Host ''
$colour = if ($script:failures -eq 0) { 'Green' } else { 'Red' }
Write-Host "$($script:passes) passed, $($script:failures) failed, $($script:skipped) skipped." -ForegroundColor $colour
Write-Host 'Still to do by hand (docs/deployment.md): sign in, two-factor, and Verify chain on the audit page.'

$client.Dispose()
if ($script:failures -gt 0) { exit 1 }
