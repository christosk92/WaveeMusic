import Database from "better-sqlite3";
import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const WORKER_ROOT = join(__dirname, "..");

/** schema.sql (the frozen baseline) then every `migrations/NNNN_*.sql` in name order — exactly what a database
 *  created from scratch and brought up with `wrangler d1 migrations apply` holds. */
function schemaScripts(): string[] {
  const migrationsDir = join(WORKER_ROOT, "migrations");
  const migrations = readdirSync(migrationsDir)
    .filter((f) => /^\d{4}_.*\.sql$/.test(f))
    .sort()
    .map((f) => readFileSync(join(migrationsDir, f), "utf8"));
  return [readFileSync(join(WORKER_ROOT, "schema.sql"), "utf8"), ...migrations];
}

/**
 * A D1Database double backed by real, in-memory SQLite (better-sqlite3), loaded with the actual
 * schema.sql + migrations this worker ships. D1's query surface (`prepare().bind().first()/.all()/.run()`,
 * positional `?N` params, `INSERT ... ON CONFLICT ... DO UPDATE`, `meta.changes`) maps directly onto SQLite,
 * so this exercises the real SQL in src/store.ts rather than a hand-rolled stub — while staying entirely
 * local (no workerd/miniflare, which cannot install on this box; see the handback report).
 */
export function makeFakeD1(): D1Database {
  const db = new Database(":memory:");
  for (const script of schemaScripts()) db.exec(script);

  function toSqliteParams(sql: string, args: unknown[]): { sql: string; args: unknown[] } {
    // D1 uses ?1, ?2, ... . better-sqlite3 *parses* that syntax but only accepts binding it via an
    // object keyed by the number (`.run({1: v1, 2: v2})`), not positional varargs — every store.ts
    // query here references ?1..?N in strictly increasing, non-repeating order, so rewriting to plain
    // anonymous `?` and binding positionally is a safe, order-preserving translation.
    const rewritten = sql.replace(/\?\d+/g, "?");
    const converted = args.map((a) => (typeof a === "boolean" ? (a ? 1 : 0) : a));
    return { sql: rewritten, args: converted };
  }

  /** D1's `.all()` (and every `batch()` entry) runs any statement: a reader returns its rows; a write — which
   *  better-sqlite3's own `.all()` refuses — runs and reports `meta.changes`. */
  function execute<T>(sql: string, boundArgs: unknown[]): D1Result<T> {
    const { sql: s, args } = toSqliteParams(sql, boundArgs);
    const stmt = db.prepare(s);
    if (stmt.reader) return { results: stmt.all(...args) as T[], success: true, meta: { changes: 0 } as never };
    const info = stmt.run(...args);
    return { results: [], success: true, meta: { changes: info.changes } as never };
  }

  interface FakeStatement {
    /** Synchronous `.all()` — what `batch()` runs inside one SQLite transaction, as D1 does. */
    executeSync<T>(): D1Result<T>;
  }

  function makeStatement(sql: string) {
    let boundArgs: unknown[] = [];
    const api: D1PreparedStatement & FakeStatement = {
      bind(...args: unknown[]) {
        boundArgs = args;
        return api;
      },
      async first<T = unknown>(): Promise<T | null> {
        const { sql: s, args } = toSqliteParams(sql, boundArgs);
        const row = db.prepare(s).get(...args);
        return (row as T | undefined) ?? null;
      },
      async all<T = unknown>(): Promise<D1Result<T>> {
        return execute<T>(sql, boundArgs);
      },
      async run(): Promise<D1Result> {
        const { sql: s, args } = toSqliteParams(sql, boundArgs);
        const info = db.prepare(s).run(...args);
        return { results: [], success: true, meta: { changes: info.changes } as never };
      },
      async raw<T = unknown[]>(): Promise<T[]> {
        const { sql: s, args } = toSqliteParams(sql, boundArgs);
        return db.prepare(s).raw().all(...args) as T[];
      },
      executeSync<T>(): D1Result<T> {
        return execute<T>(sql, boundArgs);
      },
    } as unknown as D1PreparedStatement & FakeStatement;
    return api;
  }

  const fake: D1Database = {
    prepare(sql: string) {
      return makeStatement(sql);
    },
    async batch<T = unknown>(statements: D1PreparedStatement[]): Promise<D1Result<T>[]> {
      return db.transaction(() =>
        statements.map((s) => (s as unknown as FakeStatement).executeSync<T>()),
      )();
    },
    async exec(sql: string) {
      db.exec(sql);
      return { count: 0, duration: 0 };
    },
    async dump() {
      throw new Error("not implemented in fake D1");
    },
    withSession() {
      throw new Error("not implemented in fake D1");
    },
  } as unknown as D1Database;

  return fake;
}

/** A minimal R2Bucket double, in-memory. */
export function makeFakeR2(): R2Bucket {
  const store = new Map<string, { body: ArrayBuffer; contentType?: string }>();

  const fake: R2Bucket = {
    async put(key: string, value: any, options?: any) {
      let buf: ArrayBuffer;
      if (typeof value === "string") buf = new TextEncoder().encode(value).buffer as ArrayBuffer;
      else if (value instanceof ArrayBuffer) buf = value;
      else if (ArrayBuffer.isView(value)) buf = value.buffer as ArrayBuffer;
      else buf = new ArrayBuffer(0);
      store.set(key, { body: buf, contentType: options?.httpMetadata?.contentType });
      return {} as R2Object;
    },
    async get(key: string) {
      const entry = store.get(key);
      if (!entry) return null;
      const body = entry.body;
      return {
        key,
        body: new ReadableStream({
          start(controller) {
            controller.enqueue(new Uint8Array(body));
            controller.close();
          },
        }),
        async arrayBuffer() {
          return body;
        },
        async text() {
          return new TextDecoder().decode(body);
        },
        async json() {
          return JSON.parse(new TextDecoder().decode(body));
        },
      } as unknown as R2ObjectBody;
    },
    async head(key: string) {
      return store.has(key) ? ({} as R2Object) : null;
    },
    async delete(key: string | string[]) {
      const keys = Array.isArray(key) ? key : [key];
      for (const k of keys) store.delete(k);
    },
    async list() {
      return { objects: [], truncated: false, delimitedPrefixes: [] } as unknown as R2Objects;
    },
    async createMultipartUpload() {
      throw new Error("not implemented in fake R2");
    },
    resumeMultipartUpload() {
      throw new Error("not implemented in fake R2");
    },
  } as unknown as R2Bucket;

  return fake;
}

export function makeFakeRate(alwaysAllow = true): { limit: (o: { key: string }) => Promise<{ success: boolean }> } {
  return { limit: async () => ({ success: alwaysAllow }) };
}

// ── .symmap fixture builder — exact byte layout from plan §I ───────────────────────────────────────
export interface SymmapEntryFixture {
  rva: number;
  size: number;
  name: string;
}

export function buildSymmap(
  entries: SymmapEntryFixture[],
  guid: Uint8Array = new Uint8Array([
    0x12, 0x34, 0x56, 0x7e, 0x9a, 0xbc, 0x34, 0x12, 0xde, 0xf0, 0x01, 0x23, 0x45, 0x67, 0x89, 0xab,
  ]),
  age = 1,
  imageSize = BigInt(0x100000),
): ArrayBuffer {
  const sorted = [...entries].sort((a, b) => a.rva - b.rva);

  // string table: each name NUL-terminated, in the same order as sorted entries; offsets recorded.
  const encoder = new TextEncoder();
  const nameBytes = sorted.map((e) => encoder.encode(e.name));
  const offsets: number[] = [];
  let cursor = 0;
  for (const nb of nameBytes) {
    offsets.push(cursor);
    cursor += nb.length + 1; // + NUL
  }
  const stringTableBytes = cursor;

  const headerBytes = 44;
  const entryBytes = sorted.length * 12;
  const total = headerBytes + entryBytes + stringTableBytes;
  const buf = new ArrayBuffer(total);
  const dv = new DataView(buf);

  dv.setUint8(0, "W".charCodeAt(0));
  dv.setUint8(1, "S".charCodeAt(0));
  dv.setUint8(2, "Y".charCodeAt(0));
  dv.setUint8(3, "M".charCodeAt(0));
  dv.setUint32(4, 1, true); // version
  dv.setUint32(8, sorted.length, true); // count
  dv.setUint32(12, stringTableBytes, true);
  new Uint8Array(buf, 16, 16).set(guid);
  dv.setUint32(32, age, true);
  dv.setBigUint64(36, imageSize, true);

  let off = headerBytes;
  for (let i = 0; i < sorted.length; i++) {
    dv.setUint32(off, sorted[i]!.rva, true);
    dv.setUint32(off + 4, sorted[i]!.size, true);
    dv.setUint32(off + 8, offsets[i]!, true);
    off += 12;
  }

  const stringTableStart = headerBytes + entryBytes;
  const stringTableView = new Uint8Array(buf, stringTableStart, stringTableBytes);
  let pos = 0;
  for (const nb of nameBytes) {
    stringTableView.set(nb, pos);
    pos += nb.length;
    stringTableView[pos] = 0;
    pos += 1;
  }

  return buf;
}
