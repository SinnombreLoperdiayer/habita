export type Marker = { t_ms: number; kind: 'agenda' | 'vote' | 'note'; label: string };
export type RecStatus = 'recording' | 'queued' | 'transcribing' | 'drafting' | 'ready' | 'failed';
export type RecView = {
  id: string; meeting_id: number; title: string; status: RecStatus; error: string; consent_by: string; created_at: number;
  audio_deleted: boolean; draft: string; progress: { chunks: number; transcribed: number; failed: number };
  transcript: { t_ms: number; ts: string; text: string }[]; markers: Marker[];
};
export type Health = { ok: boolean; ai_provider: string };

export class ApiError extends Error {
  status: number;
  constructor(message: string, status: number) { super(message); this.status = status; }
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`/api${path}`, init);
  if (!res.ok) {
    let detail = `Error ${res.status}`;
    try { detail = ((await res.json()) as { detail?: string }).detail ?? detail; } catch { /* sin cuerpo */ }
    throw new ApiError(detail, res.status);
  }
  return res.status === 204 ? (undefined as T) : (res.json() as Promise<T>);
}
const json = (method: string, body: unknown): RequestInit => ({ method, headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });

export const api = {
  health: () => call<Health>('/health'),
  start: (body: { meeting_id: number; title: string; community: string; consent_by: string; context: object }) => call<RecView>('/recordings', json('POST', body)),
  uploadChunk: (id: string, seq: number, startMs: number, durationMs: number, blob: Blob) =>
    call<void>(`/recordings/${id}/chunks/${seq}?start_ms=${startMs}&duration_ms=${durationMs}`, { method: 'PUT', headers: { 'content-type': blob.type || 'audio/webm' }, body: blob }),
  finish: (id: string, markers: Marker[], context: object) => call<RecView>(`/recordings/${id}/finish`, json('POST', { markers, context })),
  get: (id: string) => call<RecView>(`/recordings/${id}`),
  regenerate: (id: string) => call<RecView>(`/recordings/${id}/regenerate`, { method: 'POST' }),
  saveDraft: (id: string, draft: string) => call<RecView>(`/recordings/${id}/draft`, json('PUT', { draft })),
  deleteAudio: (id: string) => call<RecView>(`/recordings/${id}/audio`, { method: 'DELETE' }),
};

// Cola de subida persistente en IndexedDB: el audio no se pierde si se cae la red o se cierra la pestaña.
export type Pending = { key: string; recId: string; seq: number; startMs: number; durationMs: number; blob: Blob };
const DB = 'habita-audio-v1';
const open = () => new Promise<IDBDatabase>((resolve, reject) => {
  const r = indexedDB.open(DB, 1);
  r.onupgradeneeded = () => r.result.createObjectStore('pending', { keyPath: 'key' });
  r.onsuccess = () => resolve(r.result);
  r.onerror = () => reject(r.error);
});
const run = async <T,>(mode: IDBTransactionMode, fn: (s: IDBObjectStore) => IDBRequest<T>) => {
  const db = await open();
  return new Promise<T>((resolve, reject) => {
    const req = fn(db.transaction('pending', mode).objectStore('pending'));
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
};
export const queue = {
  put: (p: Pending) => run('readwrite', s => s.put(p)),
  del: (key: string) => run('readwrite', s => s.delete(key)),
  all: () => run<Pending[]>('readonly', s => s.getAll()),
};
