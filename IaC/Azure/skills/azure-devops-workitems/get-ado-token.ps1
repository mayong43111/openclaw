<#
.SYNOPSIS
  Get Azure DevOps OAuth token using Service Principal credentials.
.DESCRIPTION
  Uses client_credentials grant to get a Bearer token for Azure DevOps API.
  Requires AZURE_CLIENT_ID, AZURE_CLIENT_SECRET, AZURE_TENANT_ID environment variables.
#>
param()

$clientId     = $env:AZURE_CLIENT_ID
$clientSecret = $env:AZURE_CLIENT_SECRET
$tenantId     = $env:AZURE_TENANT_ID

if (-not $clientId -or -not $clientSecret -or -not $tenantId) {
    Write-Error "AZURE_CLIENT_ID, AZURE_CLIENT_SECRET, and AZURE_TENANT_ID must be set"
    exit 1
}

$body = @{
    client_id     = $clientId
    client_secret = $clientSecret
    scope         = "499b84ac-1321-427f-aa17-267ca6975798/.default"
    grant_type    = "client_credentials"
}

try {
    $response = Invoke-RestMethod -Method POST `
        -Uri "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token" `
        -ContentType "application/x-www-form-urlencoded" `
        -Body $body
    Write-Output $response.access_token
} catch {
    Write-Error "Token acquisition failed: $_"
    exit 1
}
