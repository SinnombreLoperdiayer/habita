import { entraConfigured, getApiToken, notifySessionExpired, TokenError } from './auth';

export type Marker = { t_ms: number; kind: 'agenda' | 'vote' | 'note'; label: string };
export type RecStatus = 'recording' | 'queued' | 'transcribing' | 'drafting' | 'ready' | 'failed';
export type RecView = {
  id: string; meeting_id: number; title: string; status: RecStatus; error: string; consent_by: string; created_at: number;
  audio_deleted: boolean; draft: string; progress: { chunks: number; transcribed: number; failed: number };
  transcript: { t_ms: number; ts: string; text: string }[]; markers: Marker[];
};
export type Health = { ok: boolean; ai_provider: string };
export type Role = 'administrator' | 'registration_staff' | 'council' | 'fiscal_auditor' | 'owner';
export type Membership = { community_id: string; community_name: string; roles: Role[] };
export type Me = { person_id: string | null; email: string | null; display_name: string | null; is_super_admin: boolean; memberships: Membership[] };

export class ApiError extends Error {
  status: number;
  constructor(message: string, status: number) { super(message); this.status = status; }
}

/** Llama a la API con el token de la sesión. Los fallos de sesión salen como ApiError 401 y los de red como 0, que se reintentan. */
async function call<T>(path: string, init?: RequestInit, auth = true): Promise<T> {
  const headers = new Headers(init?.headers);
  if (auth) {
    try {
      const token = await getApiToken();
      if (token) headers.set('authorization', `Bearer ${token}`);
    } catch (e) {
      throw new ApiError(e instanceof Error ? e.message : 'Sin sesión.', e instanceof TokenError && e.kind === 'expired' ? 401 : 0);
    }
  }
  const res = await fetch(`/api${path}`, { ...init, headers });
  if (res.status === 401 && entraConfigured) notifySessionExpired();
  if (!res.ok) {
    let detail = res.status === 401 ? 'Tu sesión no es válida para el servidor. Vuelve a iniciar sesión.' : `Error ${res.status}`;
    try { detail = ((await res.json()) as { detail?: string }).detail ?? detail; } catch { /* sin cuerpo */ }
    throw new ApiError(detail, res.status);
  }
  return res.status === 204 ? (undefined as T) : (res.json() as Promise<T>);
}
const ID_RE = /^[A-Za-z0-9_-]{1,64}$/;
const rid = (id: string) => {
  if (!ID_RE.test(id)) throw new ApiError('Identificador de grabación inválido', 400);
  return encodeURIComponent(id);
};
const int = (n: number) => {
  if (!Number.isSafeInteger(n) || n < 0) throw new ApiError('Parámetro numérico inválido', 400);
  return n;
};
const json = (method: string, body: unknown): RequestInit => ({ method, headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });

export const api = {
  health: () => call<Health>('/health', undefined, false),
  me: () => call<Me>('/me'),
  start: (body: { community_id: string; meeting_id: number; title: string; consent_by: string; context: object }) => call<RecView>('/recordings', json('POST', body)),
  uploadChunk: (id: string, seq: number, startMs: number, durationMs: number, blob: Blob) =>
    call<void>(`/recordings/${rid(id)}/chunks/${int(seq)}?${new URLSearchParams({ start_ms: String(int(startMs)), duration_ms: String(int(durationMs)) })}`, { method: 'PUT', headers: { 'content-type': /^audio\/[\w.+-]+(;\s*codecs=[\w.,\s]+)?$/.test(blob.type) ? blob.type : 'audio/webm' }, body: blob }),
  finish: (id: string, markers: Marker[], context: object) => call<RecView>(`/recordings/${rid(id)}/finish`, json('POST', { markers, context })),
  get: (id: string) => call<RecView>(`/recordings/${rid(id)}`),
  regenerate: (id: string) => call<RecView>(`/recordings/${rid(id)}/regenerate`, { method: 'POST' }),
  saveDraft: (id: string, draft: string) => call<RecView>(`/recordings/${rid(id)}/draft`, json('PUT', { draft })),
  deleteAudio: (id: string) => call<RecView>(`/recordings/${rid(id)}/audio`, { method: 'DELETE' }),
};

// Cola de subida persistente en IndexedDB: el audio no se pierde si se cae la red o se cierra la pestaña.
export type Pending = { key: string; recId: string; seq: number; startMs: number; durationMs: number; blob: Blob };
const DB = 'habita-audio-v1';
const open = () => new Promise<IDBDatabase>((resolve, reject) => {
  const r = indexedDB.open(DB, 1);
  r.onupgradeneeded = () => r.result.createObjectStore('pending', { keyPath: 'key' });
  r.onsuccess = () => resolve(r.result);
  r.onerror = () => reject(r.error ?? new Error('No se pudo abrir la base de datos local.'));
});
const run = async <T,>(mode: IDBTransactionMode, fn: (s: IDBObjectStore) => IDBRequest<T>) => {
  const db = await open();
  return new Promise<T>((resolve, reject) => {
    const req = fn(db.transaction('pending', mode).objectStore('pending'));
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error ?? new Error('Fallo en la operación de la cola local.'));
  });
};
export const queue = {
  put: (p: Pending) => run('readwrite', s => s.put(p)),
  del: (key: string) => run('readwrite', s => s.delete(key)),
  all: () => run<Pending[]>('readonly', s => s.getAll()),
};
