param(
    [Parameter(Mandatory)]
    [string] $ExpectedServer,

    [Parameter(Mandatory)]
    [string] $ExpectedDatabase
)

$ErrorActionPreference = 'Stop'
$connectionString = $env:APP_SQL_CONNECTION_STRING
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    Write-Output 'unconfigured'
    exit 0
}

if ($connectionString -match '(?i)@Microsoft\.KeyVault' -or
    $connectionString -match '(?i)(^|;)\s*(User ID|UID|Password|PWD)\s*=') {
    if ($connectionString -match '(?i)Authentication\s*=\s*Active Directory') {
        throw 'The API SQL setting ambiguously combines managed-identity and legacy credential properties.'
    }
    Write-Output 'legacy'
    exit 0
}

$escapedServer = [Regex]::Escape($ExpectedServer)
$escapedDatabase = [Regex]::Escape($ExpectedDatabase)
if ($connectionString -notmatch "(?i)(^|;)\s*Server\s*=\s*(tcp:)?$escapedServer(,1433)?\s*(;|$)" -or
    $connectionString -notmatch "(?i)(^|;)\s*(Initial Catalog|Database)\s*=\s*$escapedDatabase\s*(;|$)") {
    throw 'The API SQL setting does not target the expected server and database.'
}

if ($connectionString -match
    '(?i)(^|;)\s*Authentication\s*=\s*Active Directory Managed Identity\s*(;|$)') {
    Write-Output 'managedIdentityExplicit'
    exit 0
}
if ($connectionString -match
    '(?i)(^|;)\s*Authentication\s*=\s*Active Directory Default\s*(;|$)') {
    Write-Output 'managedIdentityDefault'
    exit 0
}

throw 'The API SQL setting uses an unsupported or ambiguous authentication mode.'
