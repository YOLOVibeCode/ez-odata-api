import { describe, expect, it } from "vitest";
import { SkipTokenCodec } from "../../../src/odata/skiptoken.js";

const KEY = Buffer.from("test-signing-key-32-bytes-exactly!!", "utf8");

describe("SkipTokenCodec", () => {
  it("requires key of at least 16 bytes", () => {
    expect(() => new SkipTokenCodec(Buffer.from("tooshort"))).toThrow();
    expect(() => new SkipTokenCodec(Buffer.from("exactly-sixteen!!"))).not.toThrow();
  });

  it("encode → tryDecode round-trips correctly", () => {
    const codec = new SkipTokenCodec(KEY);
    const token = codec.encode(25);
    expect(typeof token).toBe("string");
    expect(codec.tryDecode(token)).toBe(25);
  });

  it("round-trips skip=0", () => {
    const codec = new SkipTokenCodec(KEY);
    expect(codec.tryDecode(codec.encode(0))).toBe(0);
  });

  it("round-trips large skip", () => {
    const codec = new SkipTokenCodec(KEY);
    expect(codec.tryDecode(codec.encode(1_000_000))).toBe(1_000_000);
  });

  it("returns null for tampered token", () => {
    const codec = new SkipTokenCodec(KEY);
    const token = codec.encode(10);
    const tampered = token.slice(0, -3) + "xxx";
    expect(codec.tryDecode(tampered)).toBeNull();
  });

  it("returns null for garbage", () => {
    const codec = new SkipTokenCodec(KEY);
    expect(codec.tryDecode("tampered-token")).toBeNull();
    expect(codec.tryDecode("aaaa")).toBeNull();
    expect(codec.tryDecode("")).toBeNull();
  });

  it("different keys produce different tokens", () => {
    const codec1 = new SkipTokenCodec(Buffer.from("key-one-sixteen-bytes-here!!!!!"));
    const codec2 = new SkipTokenCodec(Buffer.from("key-two-sixteen-bytes-here!!!!!"));
    const token = codec1.encode(42);
    expect(codec2.tryDecode(token)).toBeNull();
  });

  it("token is URL-safe base64 (no +, /, =)", () => {
    const codec = new SkipTokenCodec(KEY);
    const token = codec.encode(100);
    expect(token).not.toMatch(/[+/=]/);
  });
});
