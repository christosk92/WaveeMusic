import type { FeedbackSubmitRequest } from "./types";

const MAX_TITLE = 200;
const MAX_BODY = 5000;
const MAX_DIAGNOSTICS = 50_000;
const MAX_EMAIL = 254;
const MAX_ATTACHMENTS = 5;
const MAX_ATTACHMENT_BYTES = 5 * 1024 * 1024; // 5 MB base64 ≈ 6.67 MB encoded
const ALLOWED_CONTENT_TYPES = ["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"];

export function validate(req: FeedbackSubmitRequest): string | null {
  if (!req.title?.trim()) return "Title is required";
  if (req.title.length > MAX_TITLE) return `Title must be ${MAX_TITLE} characters or fewer`;
  if (!req.body?.trim()) return "Body is required";
  if (req.body.length > MAX_BODY) return `Body must be ${MAX_BODY} characters or fewer`;
  if (req.diagnosticsLog && req.diagnosticsLog.length > MAX_DIAGNOSTICS)
    return `Diagnostics log must be ${MAX_DIAGNOSTICS} characters or fewer`;
  if (req.contactEmail && req.contactEmail.length > MAX_EMAIL)
    return `Email must be ${MAX_EMAIL} characters or fewer`;
  if (req.type < 0 || req.type > 3) return "Invalid feedback type";
  if (req.severity < 0 || req.severity > 3) return "Invalid severity";
  if (req.reproducibility < 0 || req.reproducibility > 4) return "Invalid reproducibility";

  if (req.attachments) {
    if (req.attachments.length > MAX_ATTACHMENTS)
      return `Maximum ${MAX_ATTACHMENTS} attachments allowed`;
    for (const att of req.attachments) {
      if (!att.fileName || !att.base64Data || !att.contentType)
        return "Invalid attachment";
      if (!ALLOWED_CONTENT_TYPES.includes(att.contentType))
        return `Unsupported image type: ${att.contentType}`;
      // base64 is ~33% larger than raw bytes
      const estimatedBytes = (att.base64Data.length * 3) / 4;
      if (estimatedBytes > MAX_ATTACHMENT_BYTES)
        return `Attachment ${att.fileName} exceeds 5 MB limit`;
    }
  }

  return null;
}
