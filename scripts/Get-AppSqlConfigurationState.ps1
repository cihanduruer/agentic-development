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

if ($connectionString -match '(?i)^\s*@Microsoft\.KeyVault\(') {
    if ($connectionString -notmatch
        '(?i)^\s*@Microsoft\.KeyVault\(SecretUri=https://[^)]+/secrets/sql-connection-string/?\)\s*$') {
        throw 'The API SQL Key Vault reference is not the expected legacy secret reference.'
    }
    Write-Output 'legacy'
    exit 0
}

$builder = [System.Data.Common.DbConnectionStringBuilder]::new()
try {
    $builder.set_ConnectionString($connectionString)
}
catch {
    throw 'The API SQL setting is malformed.'
}

$values = [Collections.Generic.Dictionary[string, string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($key in $builder.Keys) {
    $keyName = [string]$key
    $values.Add($keyName, [string]$builder[$keyName])
}

$serverAliases = @('Server', 'Data Source', 'Address', 'Addr', 'Network Address')
$databaseAliases = @('Initial Catalog', 'Database')
$userAliases = @('User ID', 'UID', 'User')
$passwordAliases = @('Password', 'PWD')
$integratedAliases = @('Integrated Security', 'Trusted_Connection')
$allowedManagedIdentityKeys = @(
    $serverAliases
    $databaseAliases
    'Authentication'
    'Encrypt'
    'TrustServerCertificate'
    'Connection Timeout'
    'Connect Timeout'
)

function Get-PresentAliases([string[]] $Aliases) {
    return @($Aliases | Where-Object { $values.ContainsKey($_) })
}

$presentServers = @(Get-PresentAliases $serverAliases)
$presentDatabases = @(Get-PresentAliases $databaseAliases)
$presentUsers = @(Get-PresentAliases $userAliases)
$presentPasswords = @(Get-PresentAliases $passwordAliases)
$presentIntegrated = @(Get-PresentAliases $integratedAliases)
$authentication = if ($values.ContainsKey('Authentication')) {
    $values['Authentication'].Trim()
}
else {
    ''
}
$isManagedIdentityAuthentication = $authentication -in @(
    'Active Directory Default',
    'Active Directory Managed Identity'
)

if (-not $isManagedIdentityAuthentication) {
    if ($presentUsers.Count -gt 0 -or
        $presentPasswords.Count -gt 0 -or
        $authentication -eq 'Sql Password') {
        Write-Output 'legacy'
        exit 0
    }
    throw 'The API SQL setting uses an unsupported or ambiguous authentication mode.'
}

if ($presentUsers.Count -gt 0 -or
    $presentPasswords.Count -gt 0 -or
    $presentIntegrated.Count -gt 0) {
    throw 'The API SQL setting ambiguously combines managed identity with another authentication mechanism.'
}

foreach ($key in $values.Keys) {
    if ($key -notin $allowedManagedIdentityKeys) {
        throw "The API managed-identity SQL setting contains unsupported property '$key'."
    }
}

function Assert-SingleAssignment(
    [string[]] $Aliases,
    [string] $Description
) {
    $escapedAliases = $Aliases | ForEach-Object { [Regex]::Escape($_) }
    $assignmentPattern = "(?i)(^|;)\s*(?:$($escapedAliases -join '|'))\s*="
    if ([Regex]::Matches($connectionString, $assignmentPattern).Count -ne 1) {
        throw "The API managed-identity SQL setting must contain exactly one $Description assignment."
    }
}

function Assert-AtMostOneAssignment(
    [string[]] $Aliases,
    [string] $Description
) {
    $escapedAliases = $Aliases | ForEach-Object { [Regex]::Escape($_) }
    $assignmentPattern = "(?i)(^|;)\s*(?:$($escapedAliases -join '|'))\s*="
    if ([Regex]::Matches($connectionString, $assignmentPattern).Count -gt 1) {
        throw "The API managed-identity SQL setting contains duplicate $Description assignments."
    }
}

Assert-SingleAssignment $serverAliases 'server'
Assert-SingleAssignment $databaseAliases 'database'
Assert-SingleAssignment @('Authentication') 'authentication'
Assert-AtMostOneAssignment @('Encrypt') 'encryption'
Assert-AtMostOneAssignment @('TrustServerCertificate') 'server certificate validation'
Assert-AtMostOneAssignment @('Connection Timeout', 'Connect Timeout') 'connection timeout'
if ($presentServers.Count -ne 1 -or $presentDatabases.Count -ne 1) {
    throw 'The API managed-identity SQL setting contains conflicting target aliases.'
}

$actualServer = $values[$presentServers[0]].Trim()
$actualServer = $actualServer -replace '(?i)^tcp:', ''
$actualServer = $actualServer -replace ',1433$', ''
if ($actualServer -cne $ExpectedServer -and
    $actualServer -ine $ExpectedServer) {
    throw 'The API SQL setting does not target the expected server.'
}
if ($values[$presentDatabases[0]].Trim() -ine $ExpectedDatabase) {
    throw 'The API SQL setting does not target the expected database.'
}
if ($values.ContainsKey('Encrypt') -and $values['Encrypt'].Trim() -ine 'True') {
    throw 'The API managed-identity SQL setting must not disable encryption.'
}
if ($values.ContainsKey('TrustServerCertificate') -and
    $values['TrustServerCertificate'].Trim() -ine 'False') {
    throw 'The API managed-identity SQL setting must validate the server certificate.'
}
$timeoutKey = @('Connection Timeout', 'Connect Timeout') |
    Where-Object { $values.ContainsKey($_) } |
    Select-Object -First 1
if ($null -ne $timeoutKey) {
    $timeout = 0
    if (-not [int]::TryParse($values[$timeoutKey].Trim(), [ref]$timeout) -or
        $timeout -le 0) {
        throw 'The API managed-identity SQL setting has an invalid connection timeout.'
    }
}

if ($authentication -eq 'Active Directory Managed Identity') {
    Write-Output 'managedIdentityExplicit'
}
else {
    Write-Output 'managedIdentityDefault'
}
