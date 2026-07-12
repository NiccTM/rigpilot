import {
  MAX_REPORT_BYTES,
  extractSummary,
  validateApprovedReport,
  validateClientId
} from "./validation";

interface Env {
  REPORTS: R2Bucket;
  DB: D1Database;
  REPORT_RATE_LIMITER: RateLimit;
  REPORT_HASH_SALT: string;
}

interface ReportRow {
  delete_token_hash: string;
  object_key: string;
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    if (request.method === "GET" && url.pathname === "/health") {
      return json({ status: "ok", version: "v1" });
    }

    if (request.method === "POST" && url.pathname === "/v1/reports") {
      return createReport(request, env);
    }

    const deleteMatch = /^\/v1\/reports\/([a-f0-9]{32})$/i.exec(url.pathname);
    if (request.method === "DELETE" && deleteMatch?.[1]) {
      return deleteReport(request, env, deleteMatch[1].toLowerCase());
    }

    return json({ error: "not_found" }, 404);
  },

  async scheduled(_controller: ScheduledController, env: Env): Promise<void> {
    await env.DB.prepare(
      "DELETE FROM compatibility_reports WHERE summary_expires_utc <= ?"
    )
      .bind(new Date().toISOString())
      .run();
  }
};

async function createReport(request: Request, env: Env): Promise<Response> {
  const clientId = validateClientId(request.headers.get("x-pc-helper-install-id"));
  if (clientId === null) {
    return json({ error: "invalid_client_id" }, 400);
  }

  const rate = await env.REPORT_RATE_LIMITER.limit({ key: clientId });
  if (!rate.success) {
    return json({ error: "rate_limited" }, 429, { "retry-after": "60" });
  }

  if (!request.headers.get("content-type")?.toLowerCase().startsWith("application/json")) {
    return json({ error: "content_type_must_be_json" }, 415);
  }

  const contentEncoding = request.headers.get("content-encoding")?.toLowerCase();
  if (contentEncoding && contentEncoding !== "identity") {
    return json({ error: "compressed_requests_not_supported" }, 415);
  }

  const declaredLength = Number(request.headers.get("content-length") ?? "0");
  if (declaredLength > MAX_REPORT_BYTES) {
    return json({ error: "report_too_large" }, 413);
  }

  const bytes = await request.arrayBuffer();
  if (bytes.byteLength === 0 || bytes.byteLength > MAX_REPORT_BYTES) {
    return json({ error: "report_too_large" }, 413);
  }

  let report: unknown;
  try {
    report = JSON.parse(new TextDecoder().decode(bytes));
  } catch {
    return json({ error: "invalid_json" }, 400);
  }

  if (!validateApprovedReport(report)) {
    return json({ error: "invalid_or_unapproved_report" }, 400);
  }

  const summary = extractSummary(report);
  const reportId = crypto.randomUUID().replaceAll("-", "");
  const deleteToken = toHex(crypto.getRandomValues(new Uint8Array(32)));
  const deleteTokenHash = await sha256(`${env.REPORT_HASH_SALT}:${deleteToken}`);
  const created = new Date();
  const rawExpiry = new Date(created.getTime() + 30 * 24 * 60 * 60 * 1000);
  const summaryExpiry = new Date(created.getTime() + 365 * 24 * 60 * 60 * 1000);
  const objectKey = `v1/${created.toISOString().slice(0, 7)}/${reportId}.json`;

  await env.REPORTS.put(objectKey, bytes, {
    httpMetadata: { contentType: "application/json" },
    customMetadata: {
      reportId,
      createdUtc: created.toISOString(),
      expiresUtc: rawExpiry.toISOString()
    }
  });

  try {
    await env.DB.prepare(
      `INSERT INTO compatibility_reports(
        report_id, object_key, created_utc, raw_expires_utc, summary_expires_utc, delete_token_hash,
        app_version, os_build, cpu_name, gpu_names_json, motherboard_name, capability_counts_json)
       VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
    )
      .bind(
        reportId,
        objectKey,
        created.toISOString(),
        rawExpiry.toISOString(),
        summaryExpiry.toISOString(),
        deleteTokenHash,
        summary.appVersion,
        summary.osBuild,
        summary.cpuName,
        JSON.stringify(summary.gpuNames),
        summary.motherboardName,
        JSON.stringify(summary.capabilityCounts)
      )
      .run();
  } catch (error) {
    await env.REPORTS.delete(objectKey);
    throw error;
  }

  return json(
    {
      reportId,
      deleteToken,
      rawExpiresAt: rawExpiry.toISOString(),
      summaryExpiresAt: summaryExpiry.toISOString()
    },
    201
  );
}

async function deleteReport(request: Request, env: Env, reportId: string): Promise<Response> {
  const authorization = request.headers.get("authorization");
  if (!authorization?.startsWith("Bearer ")) {
    return json({ error: "missing_delete_token" }, 401);
  }

  const token = authorization.slice("Bearer ".length);
  if (!/^[a-f0-9]{64}$/i.test(token)) {
    return json({ error: "invalid_delete_token" }, 401);
  }

  const row = await env.DB.prepare(
    "SELECT delete_token_hash, object_key FROM compatibility_reports WHERE report_id = ?"
  )
    .bind(reportId)
    .first<ReportRow>();
  if (!row) {
    return json({ error: "not_found" }, 404);
  }

  const suppliedHash = await sha256(`${env.REPORT_HASH_SALT}:${token}`);
  if (!constantTimeEquals(suppliedHash, row.delete_token_hash)) {
    return json({ error: "invalid_delete_token" }, 401);
  }

  await env.REPORTS.delete(row.object_key);
  await env.DB.prepare("DELETE FROM compatibility_reports WHERE report_id = ?").bind(reportId).run();
  return new Response(null, { status: 204 });
}

async function sha256(value: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return toHex(new Uint8Array(digest));
}

function constantTimeEquals(left: string, right: string): boolean {
  if (left.length !== right.length) {
    return false;
  }

  let difference = 0;
  for (let index = 0; index < left.length; index++) {
    difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  }

  return difference === 0;
}

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (value) => value.toString(16).padStart(2, "0")).join("");
}

function json(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return Response.json(body, {
    status,
    headers: {
      "cache-control": "no-store",
      "content-type": "application/json; charset=utf-8",
      ...headers
    }
  });
}
