import type { Env, FeedbackSubmitRequest, FeedbackSubmitResponse } from "./types";
import { validate } from "./validator";
import { redact } from "./pii-redactor";
import { formatTitle, formatBody, getLabels } from "./issue-formatter";

const RATE_LIMIT_MAX = 10;
const RATE_LIMIT_WINDOW_SECONDS = 60;

function jsonResponse(body: object, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "Content-Type": "application/json",
      "Access-Control-Allow-Origin": "*",
    },
  });
}

function corsPreflightResponse(): Response {
  return new Response(null, {
    status: 204,
    headers: {
      "Access-Control-Allow-Origin": "*",
      "Access-Control-Allow-Methods": "POST, OPTIONS",
      "Access-Control-Allow-Headers": "Content-Type, X-Api-Key",
      "Access-Control-Max-Age": "86400",
    },
  });
}

async function checkRateLimit(ip: string): Promise<boolean> {
  const key = `ratelimit:${ip}`;
  const cache = caches.default;
  const cached = await cache.match(new Request(`https://ratelimit.local/${key}`));

  let count = 0;
  if (cached) {
    count = parseInt(await cached.text(), 10) || 0;
  }

  if (count >= RATE_LIMIT_MAX) return false;

  const updated = new Response(String(count + 1), {
    headers: { "Cache-Control": `s-maxage=${RATE_LIMIT_WINDOW_SECONDS}` },
  });
  await cache.put(new Request(`https://ratelimit.local/${key}`), updated);
  return true;
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    // CORS preflight
    if (request.method === "OPTIONS") {
      return corsPreflightResponse();
    }

    // Only POST /api/feedback
    if (url.pathname !== "/api/feedback" || request.method !== "POST") {
      return jsonResponse({ error: "Not found" }, 404);
    }

    // Auth check
    const apiKey = request.headers.get("X-Api-Key");
    if (!apiKey || apiKey !== env.FEEDBACK_API_KEY) {
      return jsonResponse({ error: "Unauthorized" }, 401);
    }

    // Rate limit by IP
    const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
    if (!(await checkRateLimit(ip))) {
      return jsonResponse({ error: "Rate limit exceeded. Try again later." }, 429);
    }

    // Parse body
    let req: FeedbackSubmitRequest;
    try {
      req = await request.json();
    } catch {
      return jsonResponse({ error: "Invalid request body" }, 400);
    }

    // Validate
    const validationError = validate(req);
    if (validationError) {
      return jsonResponse({ error: validationError }, 400);
    }

    // PII redaction
    req.body = redact(req.body);
    if (req.diagnosticsLog) {
      req.diagnosticsLog = redact(req.diagnosticsLog);
    }

    // Upload attachments to R2 (if any)
    const imageUrls: string[] = [];
    if (req.attachments && req.attachments.length > 0 && env.FEEDBACK_IMAGES) {
      const issueId = crypto.randomUUID().slice(0, 8);
      for (const att of req.attachments) {
        try {
          const bytes = Uint8Array.from(atob(att.base64Data), (c) => c.charCodeAt(0));
          const ext = att.fileName.split(".").pop() || "png";
          const key = `feedback/${issueId}/${Date.now()}-${att.fileName}`;
          await env.FEEDBACK_IMAGES.put(key, bytes, {
            httpMetadata: { contentType: att.contentType },
          });
          imageUrls.push(`${env.IMAGE_PUBLIC_URL}/${key}`);
        } catch (uploadErr) {
          console.error("Failed to upload attachment:", uploadErr);
        }
      }
    }

    // Format GitHub Issue
    const title = formatTitle(req);
    const body = formatBody(req, imageUrls);
    const labels = getLabels(req);

    // Create GitHub Issue
    try {
      const ghResponse = await fetch(
        `https://api.github.com/repos/${env.GITHUB_REPO}/issues`,
        {
          method: "POST",
          headers: {
            Authorization: `Bearer ${env.GITHUB_TOKEN}`,
            Accept: "application/vnd.github+json",
            "User-Agent": "wavee-feedback-worker",
            "Content-Type": "application/json",
          },
          body: JSON.stringify({ title, body, labels }),
        }
      );

      if (!ghResponse.ok) {
        const errorText = await ghResponse.text();
        console.error(`GitHub API error: ${ghResponse.status} ${errorText}`);
        return jsonResponse({ error: "Failed to submit feedback. Please try again." }, 502);
      }

      const issue = (await ghResponse.json()) as { number: number };
      const response: FeedbackSubmitResponse = {
        id: String(issue.number),
        status: "Open",
        message: "Feedback submitted successfully",
      };
      return jsonResponse(response, 201);
    } catch (err) {
      console.error("Unexpected error creating GitHub issue:", err);
      return jsonResponse({ error: "Internal server error" }, 500);
    }
  },
};
