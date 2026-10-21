param(
    [string]$ResourceGroup = 'agentic-hotelbookingdev',
    [string]$AppName = 'ahb-dev-bj5rmi3w3ntgq-api',
    [string]$Repository = 'cihanduruer/agentic-development',
    [string]$PythonExecutable = 'python'
)
$ErrorActionPreference = 'Stop'
& $PythonExecutable -c 'import nacl.public'
if ($LASTEXITCODE -ne 0) { throw 'Use an isolated Python environment with PyNaCl 1.6.2 for Agents-secret encryption.' }
$settings = az webapp config appsettings list --resource-group $ResourceGroup --name $AppName --output json --only-show-errors | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Cannot read protected API settings.' }
function New-KnowledgeToken {
    [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}
$mcp = ($settings | Where-Object name -eq 'KnowledgeAccess__McpToken').value
$management = ($settings | Where-Object name -eq 'KnowledgeAccess__ManagementToken').value
if ([string]::IsNullOrEmpty($mcp) -or $mcp.Length -lt 32) { $mcp = New-KnowledgeToken }
if ([string]::IsNullOrEmpty($management) -or $management.Length -lt 32 -or $management -ceq $mcp) {
    $management = New-KnowledgeToken
}
$mcp | gh secret set KNOWLEDGE_MCP_TOKEN --repo $Repository --env development
if ($LASTEXITCODE -ne 0) { throw 'Cannot persist the development MCP secret.' }
$management | gh secret set KNOWLEDGE_MANAGEMENT_TOKEN --repo $Repository --env development
if ($LASTEXITCODE -ne 0) { throw 'Cannot persist the development management secret.' }
$publicKey = gh api "repos/$Repository/agents/secrets/public-key" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the GitHub Agents public encryption key.' }
$encrypted = $mcp | & $PythonExecutable -c 'import sys,base64; from nacl.public import PublicKey,SealedBox; print(base64.b64encode(SealedBox(PublicKey(base64.b64decode(sys.argv[1]))).encrypt(sys.stdin.read().strip().encode())).decode())' $publicKey.key
if ($LASTEXITCODE -ne 0) { throw 'Cannot encrypt the read-only Agents secret.' }
gh api --method PUT "repos/$Repository/agents/secrets/COPILOT_MCP_HOTEL_KNOWLEDGE_TOKEN" `
    -f "key_id=$($publicKey.key_id)" -f "encrypted_value=$encrypted" --silent
if ($LASTEXITCODE -ne 0) { throw 'Cannot persist the read-only GitHub Agents MCP secret.' }
az webapp config appsettings set --resource-group $ResourceGroup --name $AppName --settings `
    "KnowledgeAccess__McpToken=$mcp" `
    "KnowledgeAccess__ManagementToken=$management" --output none --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Cannot persist protected knowledge tokens in Azure.' }
$mcp = $null
$management = $null
$settings = $null
$encrypted = $null
Write-Output 'Knowledge tokens persisted in Azure, development Actions secrets, and GitHub Agents secrets. No token values were printed.'
