$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Get-AzureSqlAuthenticationMode.ps1'

function global:az {
    $actualArguments = @($args | ForEach-Object { $_.ToString() })
    $expectedArguments = @(
        'sql',
        'server',
        'ad-only-auth',
        'get',
        '--resource-group',
        'test-rg',
        '--name',
        'adopted-sql',
        '--query',
        'azureAdOnlyAuthentication',
        '--output',
        'tsv',
        '--only-show-errors'
    )
    $global:MockAzureSqlArguments = $actualArguments -join ' '
    $argumentsMatch = $actualArguments.Count -eq $expectedArguments.Count
    for ($index = 0; $argumentsMatch -and $index -lt $expectedArguments.Count; $index++) {
        $argumentsMatch = $actualArguments[$index] -ceq $expectedArguments[$index]
    }
    if (-not $argumentsMatch) {
        Write-Output 'Unsupported Azure CLI invocation.'
        $global:LASTEXITCODE = 2
        return
    }
    if ($global:MockAzureSqlExitCode -ne 0) {
        Write-Output $global:MockAzureSqlOutput
        $global:LASTEXITCODE = $global:MockAzureSqlExitCode
        return
    }
    Write-Output $global:MockAzureSqlOutput
    $global:LASTEXITCODE = 0
}

try {
    $successCases = @(
        @{ Output = 'true'; Expected = 'entraOnly' },
        @{ Output = 'false'; Expected = 'legacy' },
        @{ Output = 'TRUE'; Expected = 'entraOnly' },
        @{ Output = 'False'; Expected = 'legacy' }
    )
    foreach ($case in $successCases) {
        $global:MockAzureSqlOutput = $case.Output
        $global:MockAzureSqlExitCode = 0
        $actual = & $scriptPath -ResourceGroup test-rg -ServerName adopted-sql
        if ($actual -ne $case.Expected) {
            throw "Azure SQL authentication output '$($case.Output)' mapped to '$actual'."
        }
        if ($global:MockAzureSqlArguments -cne
            'sql server ad-only-auth get --resource-group test-rg --name adopted-sql --query azureAdOnlyAuthentication --output tsv --only-show-errors') {
            throw 'The classifier did not query the dedicated Entra-only authentication child resource.'
        }
    }

    $global:MockAzureSqlOutput = 'false'
    $global:MockAzureSqlExitCode = 0
    $null = az sql server ad-only-auth get `
        --resource-group test-rg `
        --name adopted-sql `
        --query azureADOnlyAuthentication `
        --output tsv `
        --only-show-errors
    if ($LASTEXITCODE -eq 0) {
        throw 'The Azure CLI mock accepted wrong-case query projection.'
    }
    $null = az sql server ad-only-auth get `
        --resource-group test-rg `
        --server-name adopted-sql `
        --query azureAdOnlyAuthentication `
        --output tsv `
        --only-show-errors
    if ($LASTEXITCODE -eq 0) {
        throw 'The Azure CLI mock accepted unsupported --server-name syntax.'
    }

    foreach ($invalidOutput in @('', 'null', 'unknown', "true`nfalse")) {
        $global:MockAzureSqlOutput = $invalidOutput
        $global:MockAzureSqlExitCode = 0
        try {
            & $scriptPath -ResourceGroup test-rg -ServerName adopted-sql
            throw "Invalid Azure SQL authentication output '$invalidOutput' was accepted."
        }
        catch {
            if ($_.Exception.Message -notmatch 'invalid Entra-only authentication value') {
                throw
            }
        }
    }

    $global:MockAzureSqlOutput = 'Forbidden'
    $global:MockAzureSqlExitCode = 1
    try {
        & $scriptPath -ResourceGroup test-rg -ServerName adopted-sql
        throw 'An Azure CLI authentication error was accepted.'
    }
    catch {
        if ($_.Exception.Message -notmatch
            'Unable to read the Azure SQL Entra-only authentication child resource') {
            throw
        }
    }
}
finally {
    Remove-Item Function:\global:az -ErrorAction SilentlyContinue
    Remove-Variable MockAzureSqlArguments -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable MockAzureSqlExitCode -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable MockAzureSqlOutput -Scope Global -ErrorAction SilentlyContinue
}

Write-Output 'Azure SQL authentication classification tests passed.'
exit 0
