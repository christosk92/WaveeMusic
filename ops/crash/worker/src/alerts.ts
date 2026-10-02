import type { Env, Kind } from "./types.js";

/**
 * Discord alerts for a NEW issue and a REGRESSION (#165, plan §W1 + appendix A2). The webhook URL is the secret
 * `DISCORD_WEBHOOK_URL`; unset (or not a Discord webhook URL) → no request at all.
 *
 * PRIVACY: an alert carries only the issue's title, kind, version/arch/channel, counts and an optional GitHub
 * number — never an exception message, install id, report id or log text — and mentions nobody
 * (`allowed_mentions.parse: []`, `@` stripped). index.ts raises one only for builds whose symmap the Worker holds,
 * so the public ingest key cannot spam the channel with made-up quads.
 */

export interface IssueEvent {
  transition: "new" | "regressed";
  fingerprint: string;
  title: string;
  kind: Kind;
  semver: string;
  quad: string;
  arch: string;
  channel: string;
  count: number;
  installs: number;
  githubIssue: number | null;
  /** `https://crash.cproducts.dev` — the dashboard link is `<origin>/issues/<fingerprint>`. */
  dashboardOrigin: string;
  at: string;
}

export interface AlertSink {
  issueEvent(e: IssueEvent): void;
}

const WEBHOOK_RX = /^https:\/\/(?:ptb\.|canary\.)?discord(?:app)?\.com\/api\/webhooks\/\d+\/[\w-]+$/;

/** Control characters and Discord markdown/mention characters out; capped. */
const safe = (s: string, max = 200) => s.replace(/[\u0000-\u001f`*_~|@\[\]()]/g, "").slice(0, max);

export function buildDiscordPayload(e: IssueEvent) {
  const head = e.transition === "new" ? "New crash issue" : "Regression — a resolved issue is back";
  return {
    username: "Wavee crashes",
    allowed_mentions: { parse: [] as string[] },
    embeds: [
      {
        title: safe(`${head}: ${e.title}`, 256),
        url: `${e.dashboardOrigin}/issues/${encodeURIComponent(e.fingerprint)}`,
        color: e.transition === "new" ? 0xd13438 : 0xf7630c,
        fields: [
          { name: "Kind", value: e.kind, inline: true },
          { name: "Version", value: `${safe(e.semver || "?", 64)} (${safe(e.quad, 64)}) · ${safe(e.arch, 16)}`, inline: true },
          { name: "Channel", value: safe(e.channel || "?"), inline: true },
          { name: "Reports", value: `${e.count} from ${e.installs} install${e.installs === 1 ? "" : "s"}`, inline: true },
          ...(e.githubIssue ? [{ name: "GitHub", value: `#${e.githubIssue}`, inline: true }] : []),
        ],
        footer: { text: `fingerprint ${e.fingerprint.slice(0, 12)}` },
        timestamp: e.at,
      },
    ],
  };
}

/** POSTs one alert; 5 s timeout. Never throws. Logs only the status / error name. */
export async function postDiscord(
  url: string | undefined,
  e: IssueEvent,
  f: typeof fetch = fetch,
): Promise<"sent" | "skipped" | "failed"> {
  if (!url || !WEBHOOK_RX.test(url)) return "skipped";
  try {
    const r = await f(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(buildDiscordPayload(e)),
      signal: AbortSignal.timeout(5000),
    });
    if (!r.ok) {
      console.warn("alerts.discord.failed", r.status);
      return "failed";
    }
    return "sent";
  } catch (err) {
    console.warn("alerts.discord.failed", err instanceof Error ? err.name : "error");
    return "failed";
  }
}

/** The production sink: posts after the response via `ctx.waitUntil` (fire-and-forget without a ctx). */
export const discordSink = (env: Env, ctx?: ExecutionContext): AlertSink => ({
  issueEvent(e) {
    const p = postDiscord(env.DISCORD_WEBHOOK_URL, e);
    if (ctx) ctx.waitUntil(p);
    else void p;
  },
});
