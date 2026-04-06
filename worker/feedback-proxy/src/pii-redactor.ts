const patterns: [RegExp, string][] = [
  [/[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}/g, "[REDACTED_EMAIL]"],
  [/Bearer\s+[A-Za-z0-9\-._~+/]+=*/g, "[REDACTED_TOKEN]"],
  [/(?:access_token|refresh_token|token)["=:]\s*["']?[A-Za-z0-9\-._~+/]{20,}["']?/gi, "[REDACTED_TOKEN]"],
  [/C:\\Users\\[^\\]+\\/g, "C:\\Users\\[REDACTED]\\"],
  [/\/home\/[^/]+\//g, "/home/[REDACTED]/"],
];

export function redact(input: string): string {
  if (!input) return input;
  let result = input;
  for (const [regex, replacement] of patterns) {
    result = result.replace(regex, replacement);
  }
  return result;
}
