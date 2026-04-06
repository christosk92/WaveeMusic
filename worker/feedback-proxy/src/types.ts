export interface FeedbackAttachment {
  fileName: string;
  contentType: string;
  base64Data: string;
}

export interface FeedbackSubmitRequest {
  type: number; // 0=Bug, 1=Ticket, 2=FeatureRequest, 3=GeneralFeedback
  severity: number; // 0=Low, 1=Medium, 2=High, 3=Critical
  reproducibility: number; // 0=Always, 1=Sometimes, 2=Rarely, 3=Unable, 4=NotApplicable
  title: string;
  body: string;
  appVersion: string;
  osVersion: string;
  deviceInfo: string;
  includeDiagnostics: boolean;
  includeDeviceMetadata: boolean;
  isAnonymous: boolean;
  diagnosticsLog?: string | null;
  contactEmail?: string | null;
  attachments?: FeedbackAttachment[] | null;
}

export interface FeedbackSubmitResponse {
  id: string;
  status: string;
  message?: string;
}

export interface Env {
  GITHUB_TOKEN: string;
  FEEDBACK_API_KEY: string;
  GITHUB_REPO: string;
  FEEDBACK_IMAGES: R2Bucket;
  IMAGE_PUBLIC_URL: string; // e.g. "https://feedback-images.yourdomain.com"
}
