import { Type } from "@sinclair/typebox";

// ---------------------------------------------------------------------------
// Azure AD token cache (Service Principal – client_credentials)
// ---------------------------------------------------------------------------

let cachedToken: { token: string; expiresAt: number } | null = null;

async function getAdoToken(): Promise<string> {
  if (cachedToken && Date.now() < cachedToken.expiresAt - 60_000) {
    return cachedToken.token;
  }

  const clientId = process.env.AZURE_CLIENT_ID;
  const clientSecret = process.env.AZURE_CLIENT_SECRET;
  const tenantId = process.env.AZURE_TENANT_ID;

  if (!clientId || !clientSecret || !tenantId) {
    throw new Error(
      "Missing Azure SP env vars: AZURE_CLIENT_ID, AZURE_CLIENT_SECRET, AZURE_TENANT_ID",
    );
  }

  const resp = await fetch(
    `https://login.microsoftonline.com/${encodeURIComponent(tenantId)}/oauth2/v2.0/token`,
    {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        grant_type: "client_credentials",
        client_id: clientId,
        client_secret: clientSecret,
        scope: "499b84ac-1321-427f-aa17-267ca6975798/.default",
      }),
    },
  );

  if (!resp.ok) {
    throw new Error(`Token request failed: ${resp.status} ${await resp.text()}`);
  }

  const data: any = await resp.json();
  cachedToken = {
    token: data.access_token,
    expiresAt: Date.now() + (data.expires_in as number) * 1000,
  };
  return cachedToken.token;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function getOrg(): string {
  return process.env.ADO_ORG ?? "yongmams";
}

function getProject(): string {
  return process.env.ADO_PROJECT ?? "OpenClaw-PoC";
}

function getOrgUrl(): string {
  return `https://dev.azure.com/${encodeURIComponent(getOrg())}`;
}

/** Resolve human-friendly link type name to ADO ref name. */
function getLinkType(name: string): string {
  const map: Record<string, string> = {
    parent: "System.LinkTypes.Hierarchy-Reverse",
    child: "System.LinkTypes.Hierarchy-Forward",
    related: "System.LinkTypes.Related",
    duplicate: "System.LinkTypes.Duplicate-Forward",
    "duplicate of": "System.LinkTypes.Duplicate-Reverse",
    successor: "System.LinkTypes.Dependency-Forward",
    predecessor: "System.LinkTypes.Dependency-Reverse",
    "tested by": "Microsoft.VSTS.Common.TestedBy-Forward",
    tests: "Microsoft.VSTS.Common.TestedBy-Reverse",
    affects: "Microsoft.VSTS.Common.Affects-Forward",
    "affected by": "Microsoft.VSTS.Common.Affects-Reverse",
  };
  const ref = map[name.toLowerCase()];
  if (!ref) throw new Error(`Unknown link type: ${name}`);
  return ref;
}

/** Call Azure DevOps REST API. `path` is appended to `https://dev.azure.com/{org}/`. */
async function adoFetch(path: string, init?: RequestInit): Promise<any> {
  const token = await getAdoToken();
  const url = `${getOrgUrl()}/${path}`;

  const headers = new Headers(init?.headers);
  headers.set("Authorization", `Bearer ${token}`);
  if (!headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }

  const resp = await fetch(url, { ...init, headers });

  if (!resp.ok) {
    const body = await resp.text();
    throw new Error(`ADO API ${resp.status}: ${body}`);
  }
  return resp.json();
}

// ---------------------------------------------------------------------------
// Plugin entry point
// ---------------------------------------------------------------------------

export default function register(api: any) {
  const project = () => encodeURIComponent(getProject());

  // ── query ────────────────────────────────────────────────────────────────
  api.registerTool({
    name: "ado_query_workitems",
    description:
      "Query Azure DevOps work items via WIQL. " +
      "Returns matching items with id, title, state, type, assignee. " +
      "Use for listing, searching, or filtering work items.",
    parameters: Type.Object({
      query: Type.String({
        description:
          "WIQL query. Example: SELECT [System.Id],[System.Title],[System.State] " +
          "FROM WorkItems WHERE [System.TeamProject]='OpenClaw-PoC' ORDER BY [System.Id] DESC",
      }),
    }),
    async execute(_id: string, params: { query: string }) {
      const result = await adoFetch(
        `${project()}/_apis/wit/wiql?api-version=7.1`,
        { method: "POST", body: JSON.stringify({ query: params.query }) },
      );

      if (!result.workItems?.length) {
        return { content: [{ type: "text" as const, text: "No work items found." }] };
      }

      const ids = result.workItems.slice(0, 50).map((w: any) => w.id);
      const details = await adoFetch(
        `${project()}/_apis/wit/workitems?ids=${ids.join(",")}&api-version=7.1`,
      );

      const items = details.value.map((wi: any) => ({
        id: wi.id,
        title: wi.fields["System.Title"],
        state: wi.fields["System.State"],
        type: wi.fields["System.WorkItemType"],
        assignedTo: wi.fields["System.AssignedTo"]?.displayName ?? "Unassigned",
        createdDate: wi.fields["System.CreatedDate"],
      }));

      return { content: [{ type: "text" as const, text: JSON.stringify(items, null, 2) }] };
    },
  });

  // ── get ──────────────────────────────────────────────────────────────────
  api.registerTool({
    name: "ado_get_workitem",
    description: "Get full details of a single Azure DevOps work item by ID.",
    parameters: Type.Object({
      id: Type.Number({ description: "Work item ID" }),
    }),
    async execute(_id: string, params: { id: number }) {
      const wi = await adoFetch(
        `${project()}/_apis/wit/workitems/${params.id}?$expand=all&api-version=7.1`,
      );
      return {
        content: [
          {
            type: "text" as const,
            text: JSON.stringify(
              {
                id: wi.id,
                url: wi._links?.html?.href,
                fields: wi.fields,
                relations: wi.relations,
              },
              null,
              2,
            ),
          },
        ],
      };
    },
  });

  // ── create ───────────────────────────────────────────────────────────────
  api.registerTool({
    name: "ado_create_workitem",
    description:
      "Create a new Azure DevOps work item (Task, Bug, User Story, Epic, Feature, Issue).",
    parameters: Type.Object({
      type: Type.String({
        description: "Work item type: Task, Bug, User Story, Epic, Feature, Issue",
      }),
      title: Type.String({ description: "Title of the work item" }),
      description: Type.Optional(Type.String({ description: "HTML description" })),
      assignedTo: Type.Optional(Type.String({ description: "Assignee name or email" })),
      state: Type.Optional(Type.String({ description: "Initial state, e.g. New, Active" })),
      priority: Type.Optional(
        Type.Number({ description: "Priority: 1=Critical 2=High 3=Medium 4=Low" }),
      ),
      areaPath: Type.Optional(Type.String({ description: "Area path" })),
      iterationPath: Type.Optional(Type.String({ description: "Iteration path" })),
    }),
    async execute(_id: string, params: Record<string, any>) {
      const ops: Array<{ op: string; path: string; value: any }> = [
        { op: "add", path: "/fields/System.Title", value: params.title },
      ];
      if (params.description)
        ops.push({ op: "add", path: "/fields/System.Description", value: params.description });
      if (params.assignedTo)
        ops.push({ op: "add", path: "/fields/System.AssignedTo", value: params.assignedTo });
      if (params.state)
        ops.push({ op: "add", path: "/fields/System.State", value: params.state });
      if (params.priority != null)
        ops.push({
          op: "add",
          path: "/fields/Microsoft.VSTS.Common.Priority",
          value: params.priority,
        });
      if (params.areaPath)
        ops.push({ op: "add", path: "/fields/System.AreaPath", value: params.areaPath });
      if (params.iterationPath)
        ops.push({
          op: "add",
          path: "/fields/System.IterationPath",
          value: params.iterationPath,
        });

      const wi = await adoFetch(
        `${project()}/_apis/wit/workitems/$${encodeURIComponent(params.type)}?api-version=7.1`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json-patch+json" },
          body: JSON.stringify(ops),
        },
      );

      return {
        content: [
          {
            type: "text" as const,
            text: JSON.stringify(
              {
                id: wi.id,
                title: wi.fields["System.Title"],
                state: wi.fields["System.State"],
                type: wi.fields["System.WorkItemType"],
                url: wi._links?.html?.href,
              },
              null,
              2,
            ),
          },
        ],
      };
    },
  });

  // ── update ───────────────────────────────────────────────────────────────
  api.registerTool({
    name: "ado_update_workitem",
    description: "Update fields of an existing Azure DevOps work item.",
    parameters: Type.Object({
      id: Type.Number({ description: "Work item ID to update" }),
      title: Type.Optional(Type.String({ description: "New title" })),
      description: Type.Optional(Type.String({ description: "New description" })),
      state: Type.Optional(Type.String({ description: "New state: New, Active, Closed …" })),
      assignedTo: Type.Optional(Type.String({ description: "New assignee" })),
      priority: Type.Optional(Type.Number({ description: "New priority 1-4" })),
      comment: Type.Optional(Type.String({ description: "Add a discussion comment" })),
    }),
    async execute(_id: string, params: Record<string, any>) {
      const ops: Array<{ op: string; path: string; value: any }> = [];
      if (params.title)
        ops.push({ op: "replace", path: "/fields/System.Title", value: params.title });
      if (params.description)
        ops.push({
          op: "replace",
          path: "/fields/System.Description",
          value: params.description,
        });
      if (params.state)
        ops.push({ op: "replace", path: "/fields/System.State", value: params.state });
      if (params.assignedTo)
        ops.push({
          op: "replace",
          path: "/fields/System.AssignedTo",
          value: params.assignedTo,
        });
      if (params.priority != null)
        ops.push({
          op: "replace",
          path: "/fields/Microsoft.VSTS.Common.Priority",
          value: params.priority,
        });
      if (params.comment)
        ops.push({ op: "add", path: "/fields/System.History", value: params.comment });

      if (ops.length === 0) {
        return { content: [{ type: "text" as const, text: "No fields specified to update." }] };
      }

      const wi = await adoFetch(
        `${project()}/_apis/wit/workitems/${params.id}?api-version=7.1`,
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json-patch+json" },
          body: JSON.stringify(ops),
        },
      );

      return {
        content: [
          {
            type: "text" as const,
            text: JSON.stringify(
              {
                id: wi.id,
                title: wi.fields["System.Title"],
                state: wi.fields["System.State"],
                url: wi._links?.html?.href,
                message: "Work item updated successfully.",
              },
              null,
              2,
            ),
          },
        ],
      };
    },
  });

  // ── link ─────────────────────────────────────────────────────────────────
  api.registerTool({
    name: "ado_link_workitems",
    description:
      "Link two Azure DevOps work items together (parent/child, related, etc). " +
      "This is the tool to use for setting parent-child hierarchy.",
    parameters: Type.Object({
      sourceId: Type.Number({ description: "The work item ID to add the link FROM" }),
      targetId: Type.Number({ description: "The work item ID to link TO" }),
      linkType: Type.String({
        description:
          "Link type: parent, child, related, duplicate, duplicate of, " +
          "successor, predecessor, tested by, tests, affects, affected by",
      }),
      comment: Type.Optional(Type.String({ description: "Optional comment on the link" })),
    }),
    async execute(
      _id: string,
      params: { sourceId: number; targetId: number; linkType: string; comment?: string },
    ) {
      const rel = getLinkType(params.linkType);
      const targetUrl = `${getOrgUrl()}/${project()}/_apis/wit/workItems/${params.targetId}`;
      const ops = [
        {
          op: "add",
          path: "/relations/-",
          value: {
            rel,
            url: targetUrl,
            attributes: { comment: params.comment ?? "" },
          },
        },
      ];

      const wi = await adoFetch(
        `${project()}/_apis/wit/workitems/${params.sourceId}?api-version=7.1`,
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json-patch+json" },
          body: JSON.stringify(ops),
        },
      );

      return {
        content: [
          {
            type: "text" as const,
            text: JSON.stringify(
              {
                sourceId: wi.id,
                targetId: params.targetId,
                linkType: params.linkType,
                message: `Link "${params.linkType}" created: #${params.sourceId} → #${params.targetId}`,
              },
              null,
              2,
            ),
          },
        ],
      };
    },
  });

  // ── unlink ───────────────────────────────────────────────────────────────
  api.registerTool({
    name: "ado_unlink_workitems",
    description: "Remove a link between two Azure DevOps work items.",
    parameters: Type.Object({
      id: Type.Number({ description: "The work item ID to remove the link from" }),
      targetId: Type.Number({ description: "The linked work item ID to unlink" }),
      linkType: Type.String({
        description:
          "Link type to remove: parent, child, related, duplicate, etc.",
      }),
    }),
    async execute(
      _id: string,
      params: { id: number; targetId: number; linkType: string },
    ) {
      // First get current relations to find the index
      const wi = await adoFetch(
        `${project()}/_apis/wit/workitems/${params.id}?$expand=relations&api-version=7.1`,
      );

      const rel = getLinkType(params.linkType);
      const targetSuffix = `/${params.targetId}`;
      const relations: any[] = wi.relations ?? [];
      const idx = relations.findIndex(
        (r: any) => r.rel === rel && r.url?.endsWith(targetSuffix),
      );

      if (idx === -1) {
        return {
          content: [
            {
              type: "text" as const,
              text: `No "${params.linkType}" link found from #${params.id} to #${params.targetId}.`,
            },
          ],
        };
      }

      const ops = [{ op: "remove", path: `/relations/${idx}` }];
      await adoFetch(
        `${project()}/_apis/wit/workitems/${params.id}?api-version=7.1`,
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json-patch+json" },
          body: JSON.stringify(ops),
        },
      );

      return {
        content: [
          {
            type: "text" as const,
            text: `Link "${params.linkType}" removed: #${params.id} ↛ #${params.targetId}`,
          },
        ],
      };
    },
  });

  // ── add comment ──────────────────────────────────────────────────────────
  api.registerTool({
    name: "ado_add_comment",
    description:
      "Add a discussion comment to an Azure DevOps work item (Comments API, not System.History).",
    parameters: Type.Object({
      id: Type.Number({ description: "Work item ID" }),
      text: Type.String({ description: "Comment text (HTML or Markdown)" }),
      format: Type.Optional(
        Type.String({ description: "Format: markdown or html (default: html)" }),
      ),
    }),
    async execute(
      _id: string,
      params: { id: number; text: string; format?: string },
    ) {
      const fmt = params.format === "markdown" ? 0 : 1;
      const result = await adoFetch(
        `${project()}/_apis/wit/workItems/${params.id}/comments?format=${fmt}&api-version=7.2-preview.4`,
        { method: "POST", body: JSON.stringify({ text: params.text }) },
      );

      return {
        content: [
          {
            type: "text" as const,
            text: JSON.stringify(
              {
                commentId: result.id,
                workItemId: params.id,
                createdDate: result.createdDate,
                message: "Comment added successfully.",
              },
              null,
              2,
            ),
          },
        ],
      };
    },
  });

  // ── list comments ────────────────────────────────────────────────────────
  api.registerTool({
    name: "ado_list_comments",
    description: "List discussion comments on an Azure DevOps work item.",
    parameters: Type.Object({
      id: Type.Number({ description: "Work item ID" }),
      top: Type.Optional(
        Type.Number({ description: "Max comments to return (default 20)" }),
      ),
    }),
    async execute(_id: string, params: { id: number; top?: number }) {
      const top = params.top ?? 20;
      const result = await adoFetch(
        `${project()}/_apis/wit/workItems/${params.id}/comments?$top=${top}&api-version=7.2-preview.4`,
      );

      const comments = (result.comments ?? []).map((c: any) => ({
        id: c.id,
        text: c.text,
        createdBy: c.createdBy?.displayName ?? "Unknown",
        createdDate: c.createdDate,
      }));

      return {
        content: [
          {
            type: "text" as const,
            text: comments.length
              ? JSON.stringify(comments, null, 2)
              : "No comments found.",
          },
        ],
      };
    },
  });

  // ── my work items ────────────────────────────────────────────────────────
  api.registerTool({
    name: "ado_my_workitems",
    description:
      "List work items assigned to the current service principal or a specific user. " +
      "Defaults to items assigned to the SP identity used by this plugin.",
    parameters: Type.Object({
      assignee: Type.Optional(
        Type.String({
          description:
            "Assignee email/name to filter. Omit to query items assigned to the SP.",
        }),
      ),
      state: Type.Optional(
        Type.String({ description: "Filter by state, e.g. 'Active', 'New'" }),
      ),
    }),
    async execute(
      _id: string,
      params: { assignee?: string; state?: string },
    ) {
      const who = params.assignee ? `'${params.assignee}'` : "@Me";
      let wiql =
        `SELECT [System.Id],[System.Title],[System.State],[System.WorkItemType] ` +
        `FROM WorkItems WHERE [System.TeamProject]='${getProject()}' ` +
        `AND [System.AssignedTo]=${who}`;
      if (params.state) {
        wiql += ` AND [System.State]='${params.state}'`;
      }
      wiql += ` ORDER BY [System.ChangedDate] DESC`;

      const result = await adoFetch(
        `${project()}/_apis/wit/wiql?api-version=7.1`,
        { method: "POST", body: JSON.stringify({ query: wiql }) },
      );

      if (!result.workItems?.length) {
        return { content: [{ type: "text" as const, text: "No work items found." }] };
      }

      const ids = result.workItems.slice(0, 50).map((w: any) => w.id);
      const details = await adoFetch(
        `${project()}/_apis/wit/workitems?ids=${ids.join(",")}&api-version=7.1`,
      );

      const items = details.value.map((wi: any) => ({
        id: wi.id,
        title: wi.fields["System.Title"],
        state: wi.fields["System.State"],
        type: wi.fields["System.WorkItemType"],
        assignedTo: wi.fields["System.AssignedTo"]?.displayName ?? "Unassigned",
      }));

      return { content: [{ type: "text" as const, text: JSON.stringify(items, null, 2) }] };
    },
  });

  // ── batch get ────────────────────────────────────────────────────────────
  api.registerTool({
    name: "ado_batch_get_workitems",
    description: "Get multiple Azure DevOps work items by their IDs in a single call.",
    parameters: Type.Object({
      ids: Type.Array(Type.Number(), {
        description: "Array of work item IDs, e.g. [1, 2, 3]",
      }),
    }),
    async execute(_id: string, params: { ids: number[] }) {
      if (params.ids.length === 0) {
        return { content: [{ type: "text" as const, text: "No IDs provided." }] };
      }

      const idStr = params.ids.slice(0, 200).join(",");
      const details = await adoFetch(
        `${project()}/_apis/wit/workitems?ids=${idStr}&$expand=relations&api-version=7.1`,
      );

      const items = details.value.map((wi: any) => ({
        id: wi.id,
        title: wi.fields["System.Title"],
        state: wi.fields["System.State"],
        type: wi.fields["System.WorkItemType"],
        assignedTo: wi.fields["System.AssignedTo"]?.displayName ?? "Unassigned",
        relations: (wi.relations ?? []).map((r: any) => ({
          rel: r.rel,
          targetId: r.url?.match(/\/(\d+)$/)?.[1],
          comment: r.attributes?.comment,
        })),
      }));

      return { content: [{ type: "text" as const, text: JSON.stringify(items, null, 2) }] };
    },
  });
}
