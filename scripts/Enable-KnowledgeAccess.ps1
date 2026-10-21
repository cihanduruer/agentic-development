param(
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][string]$AppName
)
$ErrorActionPreference = 'Stop'
if ($env:KNOWLEDGE_MCP_TOKEN.Length -lt 32 -or $env:KNOWLEDGE_MANAGEMENT_TOKEN.Length -lt 32 -or
    $env:KNOWLEDGE_MCP_TOKEN -ceq $env:KNOWLEDGE_MANAGEMENT_TOKEN) {
    throw 'Distinct MCP and management tokens of at least 32 characters must be provided through environment secrets.'
}
# Do not enable writes until the deployed artifact enforces the management-token boundary.
$response = Invoke-WebRequest "https://$AppName.azurewebsites.net/api/knowledge/blocks" -SkipHttpErrorCheck
if ($response.StatusCode -notin @(401, 503)) {
    throw 'The deployed API does not enforce Knowledge Center access. Writes remain disabled.'
}
az webapp config appsettings set --resource-group $ResourceGroup --name $AppName --settings `
    "KnowledgeAccess__McpToken=$env:KNOWLEDGE_MCP_TOKEN" `
    "KnowledgeAccess__ManagementToken=$env:KNOWLEDGE_MANAGEMENT_TOKEN" `
    'WorkItemChat__UploadEnabled=true' --output none --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Knowledge access settings could not be applied.' }
if ($env:KNOWLEDGE_EMAIL_ENDPOINT -or $env:KNOWLEDGE_EMAIL_SENDER -or $env:KNOWLEDGE_ADMIN_EMAILS) {
    if (-not $env:KNOWLEDGE_EMAIL_ENDPOINT -or -not $env:KNOWLEDGE_EMAIL_SENDER -or -not $env:KNOWLEDGE_ADMIN_EMAILS) {
        throw 'Email sign-in requires endpoint, sender address and approved admin emails together.'
    }
    az webapp config appsettings set --resource-group $ResourceGroup --name $AppName --settings `
        "KnowledgeSignIn__EmailEndpoint=$env:KNOWLEDGE_EMAIL_ENDPOINT" `
        "KnowledgeSignIn__SenderAddress=$env:KNOWLEDGE_EMAIL_SENDER" `
        "KnowledgeSignIn__AllowedEmails=$env:KNOWLEDGE_ADMIN_EMAILS" --output none --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Knowledge email sign-in settings could not be applied.' }
}
