param(
    [Parameter(Mandatory)]
    [string] $Path
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "The deployment secret file '$Path' does not exist."
}

& chmod 600 -- $Path
if ($LASTEXITCODE -ne 0) {
    Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
    throw "Unable to restrict deployment secret file permissions for '$Path'."
}
