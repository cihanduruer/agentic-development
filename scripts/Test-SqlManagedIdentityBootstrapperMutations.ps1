$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $repositoryRoot 'tools\SqlManagedIdentityBootstrapper\SqlManagedIdentityBootstrap.cs'
$originalBytes = [System.IO.File]::ReadAllBytes($sourcePath)
$utf8 = [System.Text.UTF8Encoding]::new($false)
$original = $utf8.GetString($originalBytes)
$testProject = Join-Path $repositoryRoot 'tests\UnitTests\AgenticHotelBooking.UnitTests.csproj'

$mutations = @(
    @{
        Name = 'weaken-exact-set'
        Replacements = @(
            @{
                Pattern = '(?s)                IF @ExistingDirectPermissionCount <> 7.*?                END;\r?\n\r?\n                SET @Command ='
                Value = @'
                IF @ExistingDirectPermissionCount = 0
                BEGIN
                    THROW 51000, 'The API principal has unexpected direct database permissions.', 1;
                END;

                SET @Command =
'@
            }
        )
    },
    @{
        Name = 'skip-revoke-execution'
        Replacements = @(
            @{
                Pattern = "(?s)(                    N'REVOKE SELECT, INSERT, DELETE ON OBJECT::dbo\.AgentEvents FROM ' \+\r?\n                    QUOTENAME\(@apiPrincipalName\) \+ N';';\r?\n)                EXEC sys\.sp_executesql @Command;"
                Value = '$1                -- mutation skipped revoke execution'
            }
        )
    },
    @{
        Name = 'skip-direct-removal-postcheck'
        Replacements = @(
            @{
                Pattern = "(?s)\r?\n                IF EXISTS \(\r?\n                    SELECT 1\r?\n                    FROM sys\.database_permissions\r?\n                    WHERE grantee_principal_id = @ExistingApiPrincipalId\r?\n                \)\r?\n                BEGIN\r?\n                    THROW 51009, 'The API principal still has direct database permissions after legacy migration\.', 1;\r?\n                END;"
                Value = ''
            }
        )
    },
    @{
        Name = 'ignore-runtime-role-members'
        Replacements = @(
            @{
                Pattern = "(?s)\r?\n            IF @ExistingRuntimeRoleId IS NOT NULL\r?\n               AND EXISTS \(\r?\n                   SELECT 1\r?\n                   FROM sys\.database_role_members\r?\n                   WHERE role_principal_id = @ExistingRuntimeRoleId.*?THROW 51012, 'The runtime role has unexpected database principals as members\.', 1;\r?\n            END;"
                Value = ''
            },
            @{
                Pattern = "(?s)\r?\n            IF \(\r?\n                SELECT COUNT_BIG\(\*\)\r?\n                FROM sys\.database_role_members\r?\n                WHERE role_principal_id = @RuntimeRoleId\r?\n            \) <> 1.*?THROW 51013, 'The runtime role does not have the exact expected membership\.', 1;\r?\n            END;"
                Value = ''
            }
        )
    },
    @{
        Name = 'skip-runtime-role-grant-postcheck'
        Replacements = @(
            @{
                Pattern = "(?s)\r?\n            IF \(\r?\n                SELECT COUNT_BIG\(\*\)\r?\n                FROM sys\.database_permissions AS permissions\r?\n                WHERE permissions\.grantee_principal_id = @RuntimeRoleId.*?THROW 51006, 'The runtime role does not have the exact expected permission set\.', 1;\r?\n            END;"
                Value = ''
            }
        )
    }
)

try {
    foreach ($mutation in $mutations) {
        $mutated = $original
        foreach ($replacement in $mutation.Replacements) {
            $regex = [regex]::new($replacement.Pattern)
            if ($regex.Matches($mutated).Count -ne 1) {
                throw "Mutation '$($mutation.Name)' did not match exactly one source block."
            }

            $mutated = $regex.Replace($mutated, $replacement.Value, 1)
        }

        [System.IO.File]::WriteAllText($sourcePath, $mutated, $utf8)
        & dotnet test $testProject `
            --no-restore `
            --filter 'FullyQualifiedName~SqlManagedIdentityBootstrapperTests' `
            --verbosity quiet
        $testExitCode = $LASTEXITCODE
        [System.IO.File]::WriteAllBytes($sourcePath, $originalBytes)

        if ($testExitCode -eq 0) {
            throw "Mutation '$($mutation.Name)' survived the bootstrapper test suite."
        }

        Write-Host "Mutation '$($mutation.Name)' was rejected by the test suite."
    }
}
finally {
    [System.IO.File]::WriteAllBytes($sourcePath, $originalBytes)
}

Write-Host 'SQL managed identity bootstrapper mutation tests passed.'
exit 0
