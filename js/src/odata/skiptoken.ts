import { createHmac, timingSafeEqual } from "node:crypto";

/**
 * Opaque, HMAC-signed pagination tokens (port of SkipTokenCodec.cs, spec 05 §4.2).
 * Phase 1 encodes the next offset; format is tamper-evident via HMAC-SHA256.
 * Tampered tokens → 400, never an altered query.
 */
export class SkipTokenCodec {
  private readonly key: Buffer;

  constructor(signingKey: Buffer | Uint8Array) {
    if (!signingKey || signingKey.length < 16) {
      throw new Error("Skip token signing key must be at least 16 bytes.");
    }
    this.key = Buffer.from(signingKey);
  }

  encode(nextSkip: number): string {
    const payload = String(nextSkip);
    const signature = this.sign(payload);
    return toBase64Url(`${payload}.${signature}`);
  }

  tryDecode(token: string): number | null {
    let decoded: string;
    try {
      decoded = fromBase64Url(token);
    } catch {
      return null;
    }

    const sep = decoded.lastIndexOf(".");
    if (sep <= 0) return null;

    const payload = decoded.slice(0, sep);
    const sig = decoded.slice(sep + 1);
    if (!fixedTimeEquals(this.sign(payload), sig)) return null;

    const n = parseInt(payload, 10);
    if (isNaN(n) || n < 0 || String(n) !== payload) return null;
    return n;
  }

  private sign(payload: string): string {
    return createHmac("sha256", this.key).update(payload).digest("hex");
  }
}

function fixedTimeEquals(a: string, b: string): boolean {
  const ab = Buffer.from(a, "utf8");
  const bb = Buffer.from(b, "utf8");
  if (ab.length !== bb.length) return false;
  return timingSafeEqual(ab, bb);
}

function toBase64Url(value: string): string {
  return Buffer.from(value, "utf8")
    .toString("base64")
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/, "");
}

function fromBase64Url(value: string): string {
  let padded = value.replace(/-/g, "+").replace(/_/g, "/");
  switch (padded.length % 4) {
    case 2:
      padded += "==";
      break;
    case 3:
      padded += "=";
      break;
  }
  return Buffer.from(padded, "base64").toString("utf8");
}
