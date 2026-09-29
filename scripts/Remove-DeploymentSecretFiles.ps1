param(
    [Parameter(Mandatory)]
    [string[]] $Path
)

$ErrorActionPreference = 'Stop'
foreach ($item in $Path) {
    if (Test-Path -LiteralPath $item) {
        Remove-Item -LiteralPath $item -Force -ErrorAction Stop
    }
    if (Test-Path -LiteralPath $item) {
        throw "Deployment secret file cleanup could not be proven for '$item'."
    }
}
