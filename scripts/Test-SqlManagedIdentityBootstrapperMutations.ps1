$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $repositoryRoot 'tools\SqlManagedIdentityBootstrapper\SqlManagedIdentityBootstrap.cs'
$originalBytes = [System.IO.File]::ReadAllBytes($sourcePath)
$utf8 = [System.Text.UTF8Encoding]::new($false)
$original = $utf8.GetString($originalBytes)
$unitTestProject =
    Join-Path $repositoryRoot 'tests\UnitTests\AgenticHotelBooking.UnitTests.csproj'
$integrationTestProject =
    Join-Path $repositoryRoot 'tests\IntegrationTests\AgenticHotelBooking.IntegrationTests.csproj'

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
        Name = 'accept-effective-instead-of-direct-membership'
        Replacements = @(
            @{
                Pattern = "(?s)               OR NOT EXISTS \(\r?\n                   SELECT 1\r?\n                   FROM sys\.database_role_members\r?\n                   WHERE role_principal_id = @RuntimeRoleId\r?\n                     AND member_principal_id = @ExistingApiPrincipalId\r?\n               \)"
                Value = "               OR COALESCE(IS_ROLEMEMBER(N'hotel_booking_runtime', @apiPrincipalName), 0) <> 1"
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
    },
    @{
        Name = 'skip-runtime-role-delegation-precheck'
        Replacements = @(
            @{
                Pattern = "(?s)\r?\n            IF @ExistingRuntimeRoleId IS NOT NULL\r?\n               AND EXISTS \(\r?\n                   SELECT 1\r?\n                   FROM sys\.database_permissions AS permissions\r?\n                   WHERE permissions\.class = 4\r?\n                     AND permissions\.major_id = @ExistingRuntimeRoleId\r?\n               \)\r?\n            BEGIN\r?\n                THROW 51017, 'The runtime role has delegated database-principal permissions\.', 1;\r?\n            END;"
                Value = ''
            }
        )
    },
    @{
        Name = 'skip-runtime-role-delegation-postcheck'
        Replacements = @(
            @{
                Pattern = "(?s)\r?\n            IF EXISTS \(\r?\n                SELECT 1\r?\n                FROM sys\.database_permissions AS permissions\r?\n                WHERE permissions\.class = 4\r?\n                  AND permissions\.major_id = @RuntimeRoleId\r?\n            \)\r?\n            BEGIN\r?\n                THROW 51018, 'The runtime role has delegated database-principal permissions after bootstrap\.', 1;\r?\n            END;"
                Value = ''
            }
        )
    },
    @{
        Name = 'semantically-bypass-runtime-role-owner-checks'
        TestProject = $integrationTestProject
        Filter =
            'FullyQualifiedName~UnexpectedRuntimeRoleOwnerFailsClosed'
        Replacements = @(
            @{
                Pattern = 'AND owning_principal_id = @DboPrincipalId'
                Value =
                    'AND (owning_principal_id = @DboPrincipalId OR owning_principal_id IS NULL OR owning_principal_id IS NOT NULL)'
            },
            @{
                Pattern = 'owning_principal_id IS NULL\r?\n                         OR owning_principal_id <> @DboPrincipalId'
                Value =
                    'owning_principal_id IS NULL AND owning_principal_id <> @DboPrincipalId'
            }
        )
    },
    @{
        Name = 'semantically-bypass-api-identity-check'
        TestProject = $integrationTestProject
        Filter =
            'FullyQualifiedName~ExistingApiPrincipalIdentityMismatchFailsClosed'
        Replacements = @(
            @{
                Pattern =
                    "(?s)(            IF )\(\r?\n                SELECT COUNT_BIG\(\*\).*?\r?\n               \)(?=\r?\n            BEGIN\r?\n                DECLARE @ActualApiAuthenticationType)"
                Value = '${1}1 = 0'
            }
        )
    },
    @{
        Name = 'misclassify-two-identity-candidates'
        TestProject = $integrationTestProject
        Filter =
            'FullyQualifiedName~DistinctNameAndSidMatchesFailClosedWithAmbiguousDiagnostic'
        Replacements = @(
            @{
                Pattern =
                    '(?s)(DECLARE @ActualApiAuthenticationType nvarchar\(60\);\r?\n                IF \(.*?\r?\n                \)) > 1'
                Value = '${1} > 2'
            }
        )
    },
    @{
        Name = 'misreport-api-authentication-type'
        TestProject = $integrationTestProject
        Filter =
            'FullyQualifiedName~ExistingApiPrincipalIdentityMismatchFailsClosed'
        Replacements = @(
            @{
                Pattern =
                    "COALESCE\(@ActualApiAuthenticationType, N'<missing>'\)"
                Value = '@apiAuthenticationType'
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
        $testProject = if ($mutation.TestProject) {
            $mutation.TestProject
        }
        else {
            $unitTestProject
        }
        $filter = if ($mutation.Filter) {
            $mutation.Filter
        }
        else {
            'FullyQualifiedName~SqlManagedIdentityBootstrapperTests'
        }
        & dotnet test $testProject `
            --no-restore `
            --filter $filter `
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
