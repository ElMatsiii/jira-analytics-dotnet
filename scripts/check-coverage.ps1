param(
    [string]$CoverageFile = "coverage/coverage.cobertura.xml",
    [double]$RequiredThreshold = 60.0
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $CoverageFile)) {
    Write-Error "::error::Coverage file '$CoverageFile' does not exist!"
    exit 1
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "   MS3 (jira-analytics-dotnet) Coverlet Coverage Gate       " -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "Profile File       : $CoverageFile"
Write-Host "Required Threshold : $RequiredThreshold%"
Write-Host "------------------------------------------------------------"

[xml]$xml = Get-Content $CoverageFile
$lineRate = [double]$xml.coverage.'line-rate' * 100.0
$branchRate = [double]$xml.coverage.'branch-rate' * 100.0

$linesCovered = $xml.coverage.'lines-covered'
$linesValid = $xml.coverage.'lines-valid'
$branchesCovered = $xml.coverage.'branches-covered'
$branchesValid = $xml.coverage.'branches-valid'

$passLine = $lineRate -ge $RequiredThreshold
$passBranch = $branchRate -ge $RequiredThreshold

$statusLine = if ($passLine) { "PASS" } else { "FAIL" }
$statusBranch = if ($passBranch) { "PASS" } else { "FAIL" }

Write-Host ("Line Coverage   : {0,6:N2}% ({1}/{2})  [{3}]" -f $lineRate, $linesCovered, $linesValid, $statusLine)
Write-Host ("Branch Coverage : {0,6:N2}% ({1}/{2})  [{3}]" -f $branchRate, $branchesCovered, $branchesValid, $statusBranch)
Write-Host "------------------------------------------------------------"

if (-not ($passLine -and $passBranch)) {
    Write-Error "FAILURE: Coverage is below required $RequiredThreshold% threshold!"
    exit 1
} else {
    Write-Host "SUCCESS: All coverage metrics satisfy >= $RequiredThreshold% threshold!" -ForegroundColor Green
    exit 0
}
