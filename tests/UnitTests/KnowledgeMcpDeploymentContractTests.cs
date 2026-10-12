namespace AgenticHotelBooking.UnitTests;

public sealed class KnowledgeMcpDeploymentContractTests
{
    [Fact]
    public void DeploymentWorkflowIsManualAndRestrictsDeployToExactMainSha()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github/workflows/deploy-knowledge-mcp.yml"));

        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  push:", workflow, StringComparison.Ordinal);
        Assert.Contains("github.ref == 'refs/heads/main'", workflow, StringComparison.Ordinal);
        Assert.Contains("$reviewedSourceSha -ne $env:GITHUB_SHA", workflow, StringComparison.Ordinal);
        Assert.Contains("KNOWLEDGE_MCP_ACCESS_TOKEN", workflow, StringComparison.Ordinal);
        Assert.Contains("${{ secrets.KNOWLEDGE_MCP_ACCESS_TOKEN }}", workflow, StringComparison.Ordinal);
        Assert.Contains("if: always()", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.Contains("environment: development", workflow, StringComparison.Ordinal);
        Assert.Contains("knowledge_revision:", workflow, StringComparison.Ordinal);
        Assert.Contains("reviewed_source_sha:", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentWorkflowDoesNotLogOrPersistDispatchInputsBeforeValidation()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github/workflows/deploy-knowledge-mcp.yml"));
        var initializationStart = workflow.IndexOf("Initialize sanitized evidence artifact", StringComparison.Ordinal);
        var validationStart = workflow.IndexOf("Verify main branch and exact reviewed source", StringComparison.Ordinal);
        var checkoutStart = workflow.IndexOf("actions/checkout@v4", StringComparison.Ordinal);
        var initialization = workflow[initializationStart..validationStart];
        var validation = workflow[validationStart..checkoutStart];

        Assert.True(initializationStart >= 0);
        Assert.True(validationStart > initializationStart);
        Assert.True(checkoutStart > validationStart);
        Assert.DoesNotContain("${{ inputs.", initialization, StringComparison.Ordinal);
        Assert.DoesNotContain("knowledgeRevision", initialization, StringComparison.Ordinal);
        Assert.DoesNotContain("${{ inputs.", validation, StringComparison.Ordinal);
        Assert.DoesNotContain("REVIEWED_SOURCE_SHA:", validation, StringComparison.Ordinal);
        Assert.DoesNotContain("KNOWLEDGE_REVISION:", validation, StringComparison.Ordinal);
        Assert.Contains("GITHUB_EVENT_PATH", validation, StringComparison.Ordinal);
        Assert.Contains("$reviewedSourceSha", validation, StringComparison.Ordinal);
        Assert.Contains("$knowledgeRevision", validation, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentWorkflowProtectsTheEmptyTokenParameterFileBeforeWritingSecretContent()
    {
        var workflow = File.ReadAllText(FindRepositoryFile(".github/workflows/deploy-knowledge-mcp.yml"));
        var createFile = workflow.IndexOf("FileMode]::CreateNew", StringComparison.Ordinal);
        var restrictFile = workflow.IndexOf("Protect-DeploymentSecretFile.ps1", StringComparison.Ordinal);
        var verifyPermissions = workflow.IndexOf("stat --format='%a'", StringComparison.Ordinal);
        var writeSecret = workflow.IndexOf("File]::WriteAllText", StringComparison.Ordinal);

        Assert.True(createFile >= 0);
        Assert.True(createFile < restrictFile);
        Assert.True(restrictFile < verifyPermissions);
        Assert.True(verifyPermissions < writeSecret);
    }

    [Fact]
    public void InfrastructureUsesOnlyAnIsolatedFreePlanAndSearchReaderRole()
    {
        var template = File.ReadAllText(FindRepositoryFile("infra/knowledge-mcp.bicep"));
        var roleTemplate = File.ReadAllText(FindRepositoryFile("infra/modules/knowledge-mcp-search-role.bicep"));

        Assert.Contains("name: 'ahb-dev-knowledge-mcp-f1-plan'", template, StringComparison.Ordinal);
        Assert.Contains("name: 'F1'", template, StringComparison.Ordinal);
        Assert.Contains("tier: 'Free'", template, StringComparison.Ordinal);
        Assert.Contains("alwaysOn: false", template, StringComparison.Ordinal);
        Assert.Contains("httpsOnly: true", template, StringComparison.Ordinal);
        Assert.Contains("name: 'ahb-dev-bj5rmi3w3ntgq-search'", template, StringComparison.Ordinal);
        Assert.Contains("principalId: app.identity.principalId", template, StringComparison.Ordinal);
        Assert.Contains("guid(search.id, principalId,", roleTemplate, StringComparison.Ordinal);
        Assert.Contains("'1407120a-92aa-4202-b7e9-c0e197c71c8f'", roleTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("B1", template, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Sql", template, StringComparison.Ordinal);
        Assert.Contains("@secure()", template, StringComparison.Ordinal);
        Assert.Contains("location string = 'westeurope'", template, StringComparison.Ordinal);
    }

    [Fact]
    public void KnowledgeMcpProjectIsInTheSolution()
    {
        var solution = File.ReadAllText(FindRepositoryFile("AgenticHotelBooking.slnx"));

        Assert.Contains("tools/KnowledgeMcp/KnowledgeMcp.csproj", solution, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, path.Replace('/', Path.DirectorySeparatorChar));
    }
}
