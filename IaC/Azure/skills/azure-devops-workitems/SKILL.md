---
name: azure-devops-workitems
description: Manage Azure DevOps Work Items (create, query, update, list) via REST API. Use when the user asks about tasks, bugs, user stories, features, or any work item operations in Azure DevOps.
metadata:
  {
    "openclaw":
      {
        "emoji": "📋",
        "requires": { "env": ["ADO_ORG", "ADO_PROJECT", "AZURE_CLIENT_ID"] },
      },
  }
---

# Azure DevOps Work Items

Operate on Azure DevOps Work Items via the REST API using `curl`.

## Prerequisites

Environment variables on the host:

| Variable | Description | Example |
|----------|-------------|---------|
| `ADO_ORG` | DevOps organization name | `yongmams` |
| `ADO_PROJECT` | DevOps project name | `OpenClaw-PoC` |

**Authentication** — two modes (detect automatically):

### Mode A: Service Principal (DefaultAzureCredential)

| Variable | Description |
|----------|-------------|
| `AZURE_CLIENT_ID` | App Registration client ID |
| `AZURE_CLIENT_SECRET` | Client secret |
| `AZURE_TENANT_ID` | AAD tenant ID |

Get token via the helper script:
```powershell
$token = & C:\openclaw-skills\azure-devops-workitems\get-ado-token.ps1
$headers = @{ Authorization = "Bearer $token" }
```

Or via curl on Linux/Windows:
```bash
ADO_TOKEN=$(curl -s -X POST "https://login.microsoftonline.com/$AZURE_TENANT_ID/oauth2/v2.0/token" \
  -d "client_id=$AZURE_CLIENT_ID" \
  -d "client_secret=$AZURE_CLIENT_SECRET" \
  -d "scope=499b84ac-1321-427f-aa17-267ca6975798/.default" \
  -d "grant_type=client_credentials" | python -c "import sys,json; print(json.load(sys.stdin)['access_token'])")
```

Use: `Authorization: Bearer $ADO_TOKEN`

### Mode B: Personal Access Token (PAT)

| Variable | Description |
|----------|-------------|
| `ADO_PAT` | Base64-encoded `:raw-pat` string |

Use: `Authorization: Basic $ADO_PAT`

## Authentication Header (summary)

- If `AZURE_CLIENT_ID` is set → use Mode A (Bearer token, obtain first)
- If `ADO_PAT` is set → use Mode B (Basic auth)
- Bearer token expires after ~1 hour; re-obtain if you get 401

## Common Operations

### List Work Items by Query (WIQL)

```bash
curl -s -X POST \
  "https://dev.azure.com/$ADO_ORG/$ADO_PROJECT/_apis/wit/wiql?api-version=7.1" \
  -H "Authorization: Basic $ADO_PAT" \
  -H "Content-Type: application/json" \
  -d '{"query": "SELECT [System.Id],[System.Title],[System.State],[System.AssignedTo] FROM WorkItems WHERE [System.TeamProject] = @project ORDER BY [System.ChangedDate] DESC"}'
```

This returns work item IDs. Fetch full details with the batch endpoint below.

### Get Work Item Details (batch)

```bash
curl -s \
  "https://dev.azure.com/$ADO_ORG/$ADO_PROJECT/_apis/wit/workitems?ids=1,2,3&api-version=7.1" \
  -H "Authorization: Basic $ADO_PAT"
```

### Get Single Work Item

```bash
curl -s \
  "https://dev.azure.com/$ADO_ORG/$ADO_PROJECT/_apis/wit/workitems/1?api-version=7.1&\$expand=all" \
  -H "Authorization: Basic $ADO_PAT"
```

### Create Work Item

Use JSON Patch format. The `type` can be: `Task`, `Bug`, `User Story`, `Feature`, `Epic`.

```bash
curl -s -X POST \
  "https://dev.azure.com/$ADO_ORG/$ADO_PROJECT/_apis/wit/workitems/\$Task?api-version=7.1" \
  -H "Authorization: Basic $ADO_PAT" \
  -H "Content-Type: application/json-patch+json" \
  -d '[
    {"op": "add", "path": "/fields/System.Title", "value": "My new task"},
    {"op": "add", "path": "/fields/System.Description", "value": "Task description here"},
    {"op": "add", "path": "/fields/System.State", "value": "New"}
  ]'
```

Replace `\$Task` with `\$Bug`, `\$User%20Story`, `\$Feature`, or `\$Epic` for other types.

### Update Work Item

```bash
curl -s -X PATCH \
  "https://dev.azure.com/$ADO_ORG/$ADO_PROJECT/_apis/wit/workitems/1?api-version=7.1" \
  -H "Authorization: Basic $ADO_PAT" \
  -H "Content-Type: application/json-patch+json" \
  -d '[
    {"op": "replace", "path": "/fields/System.State", "value": "Active"},
    {"op": "add", "path": "/fields/System.AssignedTo", "value": "user@example.com"}
  ]'
```

### Add Comment to Work Item

```bash
curl -s -X POST \
  "https://dev.azure.com/$ADO_ORG/$ADO_PROJECT/_apis/wit/workitems/1/comments?api-version=7.1-preview.4" \
  -H "Authorization: Basic $ADO_PAT" \
  -H "Content-Type: application/json" \
  -d '{"text": "This is a comment"}'
```

### Delete Work Item

```bash
curl -s -X DELETE \
  "https://dev.azure.com/$ADO_ORG/$ADO_PROJECT/_apis/wit/workitems/1?api-version=7.1" \
  -H "Authorization: Basic $ADO_PAT"
```

### List Work Item Types

```bash
curl -s \
  "https://dev.azure.com/$ADO_ORG/$ADO_PROJECT/_apis/wit/workitemtypes?api-version=7.1" \
  -H "Authorization: Basic $ADO_PAT"
```

### List Iterations

```bash
curl -s \
  "https://dev.azure.com/$ADO_ORG/$ADO_PROJECT/_apis/work/teamsettings/iterations?api-version=7.1" \
  -H "Authorization: Basic $ADO_PAT"
```

### List Area Paths

```bash
curl -s \
  "https://dev.azure.com/$ADO_ORG/$ADO_PROJECT/_apis/wit/classificationnodes/Areas?\$depth=3&api-version=7.1" \
  -H "Authorization: Basic $ADO_PAT"
```

## WIQL Query Examples

**All open items assigned to me:**
```json
{"query": "SELECT [System.Id],[System.Title],[System.State] FROM WorkItems WHERE [System.AssignedTo] = @me AND [System.State] <> 'Closed' ORDER BY [System.ChangedDate] DESC"}
```

**All bugs in current iteration:**
```json
{"query": "SELECT [System.Id],[System.Title],[System.State] FROM WorkItems WHERE [System.WorkItemType] = 'Bug' AND [System.IterationPath] = @currentIteration ORDER BY [System.CreatedDate] DESC"}
```

**Items changed in last 7 days:**
```json
{"query": "SELECT [System.Id],[System.Title],[System.ChangedDate] FROM WorkItems WHERE [System.ChangedDate] >= @today - 7 ORDER BY [System.ChangedDate] DESC"}
```

## Workflow

1. Read `ADO_ORG`, `ADO_PROJECT`, `ADO_PAT` from environment.
2. If any is missing, tell the user those must be configured first.
3. For queries, use WIQL POST then batch-fetch details by ID.
4. For create/update, use JSON Patch format with `application/json-patch+json`.
5. Always display results in a readable format (table or summary).
