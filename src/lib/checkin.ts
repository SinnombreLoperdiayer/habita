import qrcode from 'qrcode-generator';

export type PassData = { c: string; m: string; d: string; t: string; v: string; n: string; u: string; o: number; i: number; k: string };
export type Via = 'QR' | 'Búsqueda' | 'Manual';
export type CheckRecord = { token: string; pre?: number; at?: number; via?: Via };
export type Records = Record<number, CheckRecord>;

export const payloadOf = (meetingId: number, ownerId: number, token: string) => `HABITA|${meetingId}|${ownerId}|${token}`;

export function parsePayload(text: string) {
  const [tag, m, o, k] = text.trim().split('|');
  if (tag !== 'HABITA' || !m || !o || !k) return null;
  return { meetingId: Number(m), ownerId: Number(o), token: k };
}

export const normalize = (s: string) => s.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase().trim();

export function newToken() {
  const bytes = crypto.getRandomValues(new Uint8Array(5));
  return Array.from(bytes, b => b.toString(36).padStart(2, '0')).join('').slice(0, 8).toUpperCase();
}

export function qrPng(text: string, size = 320) {
  const q = qrcode(0, 'M');
  q.addData(text);
  q.make();
  const n = q.getModuleCount();
  const margin = 2;
  const cell = Math.max(1, Math.floor(size / (n + margin * 2)));
  const px = cell * (n + margin * 2);
  const canvas = document.createElement('canvas');
  canvas.width = px;
  canvas.height = px;
  const ctx = canvas.getContext('2d')!;
  ctx.fillStyle = '#fff';
  ctx.fillRect(0, 0, px, px);
  ctx.fillStyle = '#10261d';
  for (let r = 0; r < n; r++) for (let c = 0; c < n; c++) if (q.isDark(r, c)) ctx.fillRect((c + margin) * cell, (r + margin) * cell, cell, cell);
  return canvas.toDataURL('image/png');
}

export function encodePass(data: PassData) {
  const bytes = new TextEncoder().encode(JSON.stringify(data));
  let bin = '';
  bytes.forEach(b => { bin += String.fromCharCode(b); });
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function readPassFromHash(): PassData | null {
  const m = window.location.hash.match(/^#pase=([\w-]+)/);
  if (!m) return null;
  try {
    const b64 = m[1].replace(/-/g, '+').replace(/_/g, '/');
    const bin = atob(b64 + '='.repeat((4 - (b64.length % 4)) % 4));
    return JSON.parse(new TextDecoder().decode(Uint8Array.from(bin, ch => ch.charCodeAt(0)))) as PassData;
  } catch {
    return null;
  }
}

export const passLink = (data: PassData) => `${window.location.origin}${window.location.pathname}#pase=${encodePass(data)}`;

export function beep(ok: boolean) {
  try {
    const ctx = new AudioContext();
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.frequency.value = ok ? 880 : 220;
    gain.gain.value = 0.08;
    osc.connect(gain).connect(ctx.destination);
    osc.start();
    osc.stop(ctx.currentTime + (ok ? 0.12 : 0.3));
    osc.onended = () => { void ctx.close(); };
  } catch { /* sin audio */ }
}

export function downloadUrl(url: string, filename: string) {
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
}
