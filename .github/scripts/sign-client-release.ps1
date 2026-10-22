# Signs a kiosk release's full package for Lanyard.Shared.Updates.ReleaseSigning (Security Issue
# 9.9). Reads the package's version, file name, size and hashes from the release's
# releases.win.json, signs that manifest with the CLIENT_RELEASE_SIGNING_KEY secret (an ECDSA P-256
# private key in PEM) and writes "<package>.sig" next to it. Kiosks from 1.0.44 refuse a package
# without a valid one, so this fails loudly rather than letting an unsigned release go out.
param(
    [Parameter(Mandatory = $true)][string] $FeedPath,
    [Parameter(Mandatory = $true)][string] $Version,
    [Parameter(Mandatory = $true)][string] $OutDir
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:CLIENT_RELEASE_SIGNING_KEY)) {
    throw "The CLIENT_RELEASE_SIGNING_KEY secret isn't set (Settings > Secrets and variables > Actions). See docs/CLIENT_RELEASE_SIGNING.md."
}

$feed = Get-Content -Raw -Path $FeedPath | ConvertFrom-Json
$asset = $feed.Assets | Where-Object { $_.Type -eq 'Full' -and $_.Version -eq $Version } | Select-Object -First 1

if ($null -eq $asset) {
    throw "$FeedPath has no full package for $Version."
}

# Must match ReleaseSigning.Manifest exactly.
$manifest = "LanyardClient|$($asset.Version)|$($asset.FileName)|$($asset.Size)|$("$($asset.SHA1)".ToUpperInvariant())|$("$($asset.SHA256)".ToUpperInvariant())"

$key = [System.Security.Cryptography.ECDsa]::Create()
try {
    $key.ImportFromPem($env:CLIENT_RELEASE_SIGNING_KEY)
    $signature = $key.SignData([System.Text.Encoding]::UTF8.GetBytes($manifest), [System.Security.Cryptography.HashAlgorithmName]::SHA256)
}
finally {
    $key.Dispose()
}

$signaturePath = Join-Path $OutDir "$($asset.FileName).sig"
Set-Content -NoNewline -Path $signaturePath -Value ([Convert]::ToBase64String($signature))

Write-Host "Signed $($asset.FileName) -> $signaturePath"
Write-Host "Manifest: $manifest"
