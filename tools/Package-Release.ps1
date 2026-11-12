param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "$PSScriptRoot\..\dist",
    [string]$CertificatePath = "",
    [string]$CertificatePassword = "",
    [string]$CertificateBase64 = "",
    [switch]$NoSign,
    [string]$TimestampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path "$PSScriptRoot\.."
$DotnetExe = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { "C:\dev\tools\dotnet\dotnet.exe" }

if (-not (Test-Path $DotnetExe) -and $DotnetExe -ne "dotnet") {
    throw "Could not find dotnet SDK at $DotnetExe."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$PublishDir = Join-Path $OutputDir "publish-$Runtime"
Remove-Item -Recurse -Force $PublishDir -ErrorAction SilentlyContinue

Write-Host "Publishing CloudDriveMount ($Configuration / $Runtime)..." -ForegroundColor Cyan
& $DotnetExe publish "$RepoRoot\src\CloudDriveMount.App\CloudDriveMount.App.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $PublishDir `
    --nologo

$PublishedExe = Join-Path $PublishDir "CloudDriveMount.exe"
$TargetExe = Join-Path $OutputDir "CloudDriveMount.exe"
Copy-Item -Force $PublishedExe $TargetExe
Remove-Item -Recurse -Force $PublishDir -ErrorAction SilentlyContinue

$Sign = -not $NoSign

if ($Sign) {
    if (-not $CertificatePath -and $env:WINDOWS_SIGNING_CERTIFICATE_FILE) {
        $CertificatePath = $env:WINDOWS_SIGNING_CERTIFICATE_FILE
    }
    if (-not $CertificatePassword) {
        $CertificatePassword = if ($env:WINDOWS_SIGNING_CERTIFICATE_PASSWORD) {
            $env:WINDOWS_SIGNING_CERTIFICATE_PASSWORD
        } elseif ($env:WINDOWS_CERTIFICATE_PASSWORD) {
            $env:WINDOWS_CERTIFICATE_PASSWORD
        } elseif ($env:SIGNING_CERTIFICATE_PASSWORD) {
            $env:SIGNING_CERTIFICATE_PASSWORD
        } else {
            ""
        }
    }
    if (-not $CertificateBase64) {
        $CertificateBase64 = if ($env:WINDOWS_SIGNING_CERTIFICATE_BASE64) {
            $env:WINDOWS_SIGNING_CERTIFICATE_BASE64
        } elseif ($env:WINDOWS_CERTIFICATE_BASE64) {
            $env:WINDOWS_CERTIFICATE_BASE64
        } elseif ($env:SIGNING_CERTIFICATE_BASE64) {
            $env:SIGNING_CERTIFICATE_BASE64
        } else {
            ""
        }
    }

    $cert = $null

    if ($CertificatePath -and (Test-Path $CertificatePath)) {
        Write-Host "Loading code-signing certificate from $CertificatePath..." -ForegroundColor Cyan
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath, $CertificatePassword)
    } elseif ($CertificateBase64) {
        Write-Host "Loading code-signing certificate from environment secret..." -ForegroundColor Cyan
        $certBytes = [System.Convert]::FromBase64String($CertificateBase64.Trim())
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $certBytes,
            $CertificatePassword,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet
        )
    } else {
        Write-Host "No certificate secret configured; generating self-signed release code-signing certificate via .NET..." -ForegroundColor Yellow
        $rsa = [System.Security.Cryptography.RSA]::Create(2048)
        $req = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            "CN=CloudDrive Mount Release, O=Oceanside-VR",
            $rsa,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
        )
        $oids = [System.Security.Cryptography.OidCollection]::new()
        $null = $oids.Add([System.Security.Cryptography.Oid]::new("1.3.6.1.5.5.7.3.3"))
        $req.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $false))
        $cert = $req.CreateSelfSigned((Get-Date).AddDays(-1), (Get-Date).AddYears(5))
    }

    if ($cert) {
        try {
            $PublicCertPath = Join-Path $OutputDir "CloudDriveMount-public.cer"
            $certBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
            [System.IO.File]::WriteAllBytes($PublicCertPath, $certBytes)
            Write-Host "Exported public certificate: $PublicCertPath" -ForegroundColor DarkGray

            Write-Host "Signing CloudDriveMount.exe with $($cert.Subject) [Thumbprint: $($cert.Thumbprint)]..." -ForegroundColor Cyan
            $signed = $false
            if ($TimestampServer) {
                try {
                    $sig = Set-AuthenticodeSignature -FilePath $TargetExe -Certificate $cert -TimestampServer $TimestampServer -HashAlgorithm SHA256 -ErrorAction Stop
                    if ($sig.Status -eq "Valid" -or $sig.Status -eq "UnknownError") {
                        $signed = $true
                        Write-Host "Applied Authenticode signature with RFC-3161 timestamp ($TimestampServer)." -ForegroundColor Green
                    }
                } catch {
                    Write-Warning "Timestamp server '$TimestampServer' failed: $_"
                }
            }
            if (-not $signed) {
                $null = Set-AuthenticodeSignature -FilePath $TargetExe -Certificate $cert -HashAlgorithm SHA256 -ErrorAction Stop
                Write-Host "Applied Authenticode signature (without timestamp)." -ForegroundColor Green
            }

            $sigStatus = Get-AuthenticodeSignature -FilePath $TargetExe
            Write-Host "Signature Status: $($sigStatus.Status) ($($sigStatus.StatusMessage))" -ForegroundColor Cyan
        } catch {
            Write-Warning "Failed to sign binary: $_"
        }
    }
}

$Hash = (Get-FileHash -Path $TargetExe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$TargetExe.sha256" -Value "$Hash  CloudDriveMount.exe" -Encoding ASCII

Write-Host "Created portable single-file executable: $TargetExe" -ForegroundColor Green
Write-Host "SHA-256: $Hash" -ForegroundColor DarkGray

$ZipPath = Join-Path $OutputDir "CloudDriveMount-win-x64.zip"
Remove-Item -Path $ZipPath -Force -ErrorAction SilentlyContinue

$FilesToZip = @(
    $TargetExe,
    "$TargetExe.sha256"
)
$WinFspMsi = Join-Path $RepoRoot "lib\winfsp\winfsp.msi"
if (Test-Path $WinFspMsi) {
    $FilesToZip += $WinFspMsi
}
$PublicCertPath = Join-Path $OutputDir "CloudDriveMount-public.cer"
if (Test-Path $PublicCertPath) {
    $FilesToZip += $PublicCertPath
}

Compress-Archive -Path $FilesToZip -DestinationPath $ZipPath -Force
$ZipHash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$ZipPath.sha256" -Value "$ZipHash  CloudDriveMount-win-x64.zip" -Encoding ASCII

Write-Host "Created distribution zip package: $ZipPath" -ForegroundColor Green
Write-Host "SHA-256: $ZipHash" -ForegroundColor DarkGray
