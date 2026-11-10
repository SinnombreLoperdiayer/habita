import { useCallback, useEffect, useRef, useState } from 'react';
import { AlertTriangle, CheckCheck, Copy, Download, FileText, Loader2, Mic, RefreshCw, Square, Trash2 } from 'lucide-react';
import { downloadUrl } from '@/lib/checkin';
import { ApiError, api, queue } from '@/lib/recorderApi';
import type { Health, Marker, RecStatus, RecView } from '@/lib/recorderApi';

type Meeting = { id: number; title: string; date: string; time: string; venue: string };
type Poll = { id: number; title: string; yes: number; no: number; abstain: number; closed: boolean };
type Props = {
  meeting: Meeting; community: string; agenda: { title: string }[]; agendaStep: number;
  attendance: { present: number; total: number }; polls: Poll[]; notify: (msg: string) => void;
};
type Phase = 'idle' | 'recording' | 'finishing' | 'result';
type Active = { id: string; markers: Marker[] };
type Hist = { id: string; title: string; at: number };

const SEGMENT_MS = 60_000;
const ACTIVE_KEY = 'habita-rec-active';
const HIST_KEY = 'habita-rec-history';
const card = 'rounded-2xl border border-border bg-card';
const btn = 'inline-flex items-center justify-center gap-1.5 rounded-lg px-3 py-2 text-xs font-medium transition-opacity hover:opacity-85 disabled:opacity-40';
const btnPrimary = `${btn} bg-primary text-primary-foreground`;
const btnGhost = `${btn} border border-border bg-card`;
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const clock = (ms: number) => { const s = Math.floor(ms / 1000); return `${String(Math.floor(s / 3600)).padStart(2, '0')}:${String(Math.floor(s % 3600 / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`; };
const STATUS_TEXT: Record<RecStatus, string> = { recording: 'Grabando', queued: 'En cola', transcribing: 'Transcribiendo audio', drafting: 'Redactando borrador', ready: 'Borrador listo', failed: 'Falló el procesamiento' };

function loadJson<T>(key: string, fallback: T): T { try { return JSON.parse(localStorage.getItem(key) ?? '') as T; } catch { return fallback; } }
function pickMime() { return ['audio/webm;codecs=opus', 'audio/mp4', 'audio/webm', 'audio/ogg;codecs=opus'].find(m => typeof MediaRecorder !== 'undefined' && MediaRecorder.isTypeSupported(m)) ?? ''; }

function providerNoteOf(health: Health | null): { bad: boolean; text: string } | null {
  if (!health) return null;
  switch (health.ai_provider) {
    case 'offline': return { bad: true, text: 'No se alcanza el servidor. El audio se guardará en este dispositivo y se subirá al reconectar.' };
    case 'none': return { bad: true, text: 'No hay proveedor de IA configurado en el servidor (Azure OpenAI). Se grabará, pero no se podrá transcribir.' };
    case 'demo': return { bad: true, text: 'Modo demostración: la transcripción y la redacción son SIMULADAS (no usan tu audio ni un modelo de IA). Configura Azure OpenAI en el servidor para transcribir de verdad.' };
    default: return { bad: false, text: 'Proveedor de IA: Azure OpenAI.' };
  }
}

function StatusIcon({ active, status }: Readonly<{ active: boolean; status: RecStatus }>) {
  if (active) return <Loader2 size={18} className="animate-spin text-primary" />;
  if (status === 'ready') return <CheckCheck size={18} className="text-positive" />;
  return <AlertTriangle size={18} className="text-negative" />;
}

/** Mide el nivel del micrófono y lo pinta en la barra; devuelve el AudioContext para cerrarlo luego. */
function startLevelMeter(stream: MediaStream, bar: { current: HTMLDivElement | null }, raf: { current: number }) {
  const ctx = new AudioContext();
  const an = ctx.createAnalyser();
  an.fftSize = 512;
  ctx.createMediaStreamSource(stream).connect(an);
  const buf = new Uint8Array(an.fftSize);
  const loop = () => {
    an.getByteTimeDomainData(buf);
    let peak = 0;
    for (const v of buf) peak = Math.max(peak, Math.abs(v - 128));
    if (bar.current) bar.current.style.width = `${Math.min(100, (peak / 128) * 160)}%`;
    raf.current = requestAnimationFrame(loop);
  };
  loop();
  return ctx;
}

export default function Recorder({ meeting, community, agenda, agendaStep, attendance, polls, notify }: Readonly<Props>) {
  const [phase, setPhase] = useState<Phase>('idle');
  const [health, setHealth] = useState<Health | null>(null);
  const [consentBy, setConsentBy] = useState('');
  const [consentOk, setConsentOk] = useState(false);
  const [elapsed, setElapsed] = useState(0);
  const [markersView, setMarkersView] = useState<Marker[]>([]);
  const [pending, setPending] = useState(0);
  const [note, setNote] = useState('');
  const [view, setView] = useState<RecView | null>(null);
  const [edited, setEdited] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [hist, setHist] = useState<Hist[]>(() => loadJson<Hist[]>(HIST_KEY, []));
  const [interrupted, setInterrupted] = useState<Active | null>(() => loadJson<Active | null>(ACTIVE_KEY, null));

  const phaseRef = useRef<Phase>('idle');
  const recIdRef = useRef('');
  const streamRef = useRef<MediaStream | null>(null);
  const mrRef = useRef<MediaRecorder | null>(null);
  const segTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const t0 = useRef(0);
  const seq = useRef(0);
  const lastDone = useRef<Promise<void>>(Promise.resolve());
  const markers = useRef<Marker[]>([]);
  const levelRef = useRef<HTMLDivElement>(null);
  const audioCtx = useRef<AudioContext | null>(null);
  const raf = useRef(0);
  const wake = useRef<WakeLockSentinel | null>(null);
  const flushLock = useRef<Promise<boolean>>(Promise.resolve(true));
  const mime = useRef(pickMime());
  const segmentRef = useRef<() => void>(() => undefined);
  const prevStep = useRef(agendaStep);
  const prevClosed = useRef(new Set(polls.filter(p => p.closed).map(p => p.id)));
  const ctxRef = useRef({ meeting, agenda, attendance, polls });
  useEffect(() => { ctxRef.current = { meeting, agenda, attendance, polls }; });

  const buildContext = useCallback(() => {
    const c = ctxRef.current;
    return {
      date: c.meeting.date, time: c.meeting.time, venue: c.meeting.venue, agenda: c.agenda,
      attendance: { present: c.attendance.present, total: c.attendance.total, percent: Math.round((c.attendance.present / Math.max(1, c.attendance.total)) * 1000) / 10 },
      polls: c.polls.map(p => ({ title: p.title, yes: p.yes, no: p.no, abstain: p.abstain })),
    };
  }, []);

  const addMarker = useCallback((kind: Marker['kind'], label: string) => {
    if (phaseRef.current !== 'recording') return;
    markers.current.push({ t_ms: Date.now() - t0.current, kind, label });
    localStorage.setItem(ACTIVE_KEY, JSON.stringify({ id: recIdRef.current, markers: markers.current } satisfies Active));
  }, []);

  useEffect(() => { api.health().then(setHealth).catch(() => setHealth({ ok: false, ai_provider: 'offline' })); }, []);

  // Marcadores automáticos: cambio de punto del orden del día y cierre de votaciones.
  useEffect(() => {
    if (prevStep.current !== agendaStep) {
      prevStep.current = agendaStep;
      addMarker('agenda', agenda[agendaStep]?.title ?? 'Cierre del orden del día');
    }
  }, [agendaStep, agenda, addMarker]);
  useEffect(() => {
    polls.filter(p => p.closed && !prevClosed.current.has(p.id)).forEach(p => addMarker('vote', `Votación cerrada: ${p.title}`));
    prevClosed.current = new Set(polls.filter(p => p.closed).map(p => p.id));
  }, [polls, addMarker]);

  // ---- subida con cola persistente -----------------------------------
  const doFlush = useCallback(async () => {
    const items = await queue.all();
    setPending(items.length);
    // El servidor guarda cada fragmento por su número (upsert), así que el orden de subida no importa.
    const results = await Promise.all(items.map(async p => {
      try {
        await api.uploadChunk(p.recId, p.seq, p.startMs, p.durationMs, p.blob);
        await queue.del(p.key);
        return true;
      } catch (e) {
        if (e instanceof ApiError && (e.status === 404 || e.status === 422)) { await queue.del(p.key); return true; }
        return false;
      }
    }));
    setPending((await queue.all()).length);
    return results.every(Boolean);
  }, []);
  const flush = useCallback(() => (flushLock.current = flushLock.current.then(doFlush, doFlush)), [doFlush]);
  useEffect(() => {
    void flush();
    const t = setInterval(() => { void flush(); }, 8000);
    return () => clearInterval(t);
  }, [flush]);

  // ---- captura en segmentos independientes ---------------------------
  const startSegment = useCallback(() => {
    const stream = streamRef.current;
    if (!stream) return;
    const mr = new MediaRecorder(stream, { mimeType: mime.current || undefined, audioBitsPerSecond: 32000 });
    const parts: Blob[] = [];
    const n = seq.current++;
    const start = Date.now() - t0.current;
    const recId = recIdRef.current;
    lastDone.current = new Promise<void>(resolve => {
      mr.ondataavailable = e => { if (e.data.size) parts.push(e.data); };
      mr.onstop = () => {
        const durationMs = Date.now() - t0.current - start;
        const blob = new Blob(parts, { type: mr.mimeType || mime.current || 'audio/webm' });
        if (blob.size && durationMs >= 1000) void queue.put({ key: `${recId}-${n}`, recId, seq: n, startMs: start, durationMs, blob }).then(() => flush()).finally(resolve);
        else resolve();
      };
    });
    mr.start();
    mrRef.current = mr;
    segTimer.current = setTimeout(() => { const old = mrRef.current; segmentRef.current(); old?.stop(); }, SEGMENT_MS);
  }, [flush]);
  useEffect(() => { segmentRef.current = startSegment; }, [startSegment]);

  const releaseMedia = useCallback(() => {
    clearTimeout(segTimer.current);
    cancelAnimationFrame(raf.current);
    streamRef.current?.getTracks().forEach(t => t.stop());
    streamRef.current = null;
    void audioCtx.current?.close();
    audioCtx.current = null;
    void wake.current?.release();
    wake.current = null;
  }, []);

  const start = async () => {
    setError('');
    if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined') { setError('Este navegador no permite grabar audio.'); return; }
    setBusy(true);
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true } });
      const rec = await api.start({ meeting_id: meeting.id, title: meeting.title, community, consent_by: consentBy, context: buildContext() });
      streamRef.current = stream;
      recIdRef.current = rec.id;
      t0.current = Date.now();
      seq.current = 0;
      markers.current = [];
      phaseRef.current = 'recording';
      if (agendaStep < agenda.length) markers.current.push({ t_ms: 0, kind: 'agenda', label: agenda[agendaStep].title });
      localStorage.setItem(ACTIVE_KEY, JSON.stringify({ id: rec.id, markers: markers.current } satisfies Active));
      setMarkersView([...markers.current]);
      setInterrupted(null);
      setElapsed(0);
      setPhase('recording');
      startSegment();
      audioCtx.current = startLevelMeter(stream, levelRef, raf);
      try { wake.current = (await navigator.wakeLock?.request('screen')) ?? null; } catch { /* opcional */ }
    } catch (e) {
      releaseMedia();
      setError(e instanceof DOMException ? 'No se pudo acceder al micrófono. Revisa el permiso del navegador.' : (e as Error).message);
    }
    setBusy(false);
  };

  useEffect(() => {
    if (phase !== 'recording') return;
    const t = setInterval(() => { setElapsed(Date.now() - t0.current); setMarkersView([...markers.current]); }, 1000);
    const warn = (e: BeforeUnloadEvent) => e.preventDefault();
    const vis = () => { if (document.visibilityState === 'visible' && !wake.current) navigator.wakeLock?.request('screen').then(w => { wake.current = w; }).catch(() => undefined); };
    window.addEventListener('beforeunload', warn);
    document.addEventListener('visibilitychange', vis);
    return () => { clearInterval(t); window.removeEventListener('beforeunload', warn); document.removeEventListener('visibilitychange', vis); };
  }, [phase]);

  const flushWithRetry = async (retries: number): Promise<boolean> => {
    if (await flush()) return true;
    if (retries <= 0) return false;
    await sleep(1500);
    return flushWithRetry(retries - 1);
  };

  const finish = async (recId: string, mks: Marker[]) => {
    setBusy(true);
    setError('');
    try {
      const ok = await flushWithRetry(3);
      if (!ok) throw new Error('No se pudieron subir todos los fragmentos de audio. Revisa la conexión y vuelve a intentarlo: el audio sigue guardado en este dispositivo.');
      const res = await api.finish(recId, mks, buildContext());
      localStorage.removeItem(ACTIVE_KEY);
      const h = [{ id: res.id, title: meeting.title, at: Date.now() }, ...hist.filter(x => x.id !== res.id)].slice(0, 10);
      localStorage.setItem(HIST_KEY, JSON.stringify(h));
      setHist(h);
      setInterrupted(null);
      setView(res);
      setEdited(null);
      phaseRef.current = 'result';
      setPhase('result');
    } catch (e) {
      setError((e as Error).message);
      phaseRef.current = 'idle';
      setPhase('idle');
      setInterrupted(loadJson<Active | null>(ACTIVE_KEY, null));
    }
    setBusy(false);
  };

  const stopAndFinish = async () => {
    setPhase('finishing');
    phaseRef.current = 'finishing';
    clearTimeout(segTimer.current);
    markers.current.push({ t_ms: Date.now() - t0.current, kind: 'note', label: 'Fin de la grabación' });
    mrRef.current?.stop();
    await lastDone.current;
    releaseMedia();
    await finish(recIdRef.current, markers.current);
  };

  // ---- seguimiento del procesamiento ---------------------------------
  const active = view && ['queued', 'transcribing', 'drafting'].includes(view.status);
  const viewId = view?.id;
  useEffect(() => {
    if (!active || !viewId) return;
    const t = setInterval(() => { api.get(viewId).then(setView).catch(() => undefined); }, 2000);
    return () => clearInterval(t);
  }, [active, viewId]);

  const act = async (fn: () => Promise<RecView>, okMsg?: string) => {
    setBusy(true);
    setError('');
    try { setView(await fn()); setEdited(null); if (okMsg) notify(okMsg); } catch (e) { setError((e as Error).message); }
    setBusy(false);
  };
  const openOld = (id: string) => act(() => api.get(id)).then(() => { phaseRef.current = 'result'; setPhase('result'); });
  const draft = edited ?? view?.draft ?? '';

  const providerNote = providerNoteOf(health);
  const addNote = () => {
    const text = note.trim();
    if (!text) return;
    addMarker('note', text);
    setMarkersView([...markers.current]);
    setNote('');
  };

  return (
    <div className="space-y-5">
      {providerNote && <div className={`flex items-start gap-2 rounded-xl border p-3 text-xs leading-5 ${providerNote.bad ? 'border-negative/30 bg-negative/10' : 'border-hero-border bg-hero text-muted-foreground'}`}><AlertTriangle size={14} className="mt-0.5 shrink-0" />{providerNote.text}</div>}
      {error && <div role="alert" className="flex items-start gap-2 rounded-xl border border-negative/30 bg-negative/10 p-3 text-xs leading-5"><AlertTriangle size={14} className="mt-0.5 shrink-0 text-negative" />{error}</div>}

      {phase === 'idle' && (
        <div className="grid gap-5 xl:grid-cols-[1.3fr_1fr]">
          <section className={`${card} p-5`}>
            <h2 className="text-sm font-semibold">Grabar la sesión</h2>
            <p className="mt-1 text-xs leading-5 text-muted-foreground">El audio se divide en fragmentos de 1 minuto que se suben mientras grabas. Al terminar, se transcribe y se redacta un borrador del acta organizado por puntos del orden del día.</p>
            <label className="mt-5 block space-y-1.5 text-xs"><span className="text-muted-foreground">Persona que autoriza la grabación (presidente o administrador)</span>
              <input value={consentBy} onChange={e => setConsentBy(e.target.value)} placeholder="Nombre completo" className="w-full rounded-xl border border-border bg-background px-3 py-2.5 text-sm outline-none focus:border-primary" /></label>
            <label className="mt-4 flex items-start gap-2.5 text-xs leading-5"><input type="checkbox" checked={consentOk} onChange={e => setConsentOk(e.target.checked)} className="mt-1" />
              <span>Se informó a los asistentes que la sesión será grabada para elaborar el acta, y la asamblea autoriza la grabación y el tratamiento de la voz como dato personal (Ley 1581 de 2012).</span></label>
            <button disabled={busy || !consentOk || consentBy.trim().length < 3} onClick={() => { void start(); }} className={`${btnPrimary} mt-5 px-4 py-2.5`}>{busy ? <Loader2 size={14} className="animate-spin" /> : <Mic size={14} />}Iniciar grabación</button>
            <p className="mt-3 text-xs text-muted-foreground">Asamblea: {meeting.title}</p>
          </section>
          <section className={`${card} p-5`}>
            {interrupted && <div className="mb-5 rounded-xl border border-border bg-hero p-3 text-xs leading-5"><p className="font-medium">Se detectó una grabación interrumpida</p><p className="mt-1 text-muted-foreground">Sube el audio pendiente y genera el borrador con lo que se alcanzó a grabar.</p><button disabled={busy} className={`${btnPrimary} mt-3`} onClick={() => { void finish(interrupted.id, interrupted.markers); }}>{busy && <Loader2 size={13} className="animate-spin" />}Recuperar y procesar</button></div>}
            <h2 className="text-sm font-semibold">Grabaciones anteriores</h2>
            {hist.length === 0 ? <p className="mt-3 text-xs text-muted-foreground">Aún no hay grabaciones.</p> : (
              <ul className="mt-3 divide-y divide-border">{hist.map(h => <li key={h.id} className="flex items-center justify-between gap-2 py-2.5"><div className="min-w-0"><p className="truncate text-sm">{h.title}</p><p className="text-xs text-muted-foreground">{new Date(h.at).toLocaleString('es-CO')}</p></div><button className={btnGhost} onClick={() => { void openOld(h.id); }}>Abrir</button></li>)}</ul>
            )}
          </section>
        </div>
      )}

      {(phase === 'recording' || phase === 'finishing') && (
        <div className="grid gap-5 xl:grid-cols-[1.3fr_1fr]">
          <section className={`${card} p-5`}>
            <div className="flex items-center gap-3"><span className="relative flex h-3 w-3"><span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-negative opacity-60" /><span className="relative inline-flex h-3 w-3 rounded-full bg-negative" /></span><span className="text-sm font-semibold">{phase === 'finishing' ? 'Finalizando…' : 'Grabando'}</span></div>
            <p className="mt-4 text-5xl font-semibold tabular-nums tracking-tight">{clock(elapsed)}</p>
            <div className="mt-5 h-2 overflow-hidden rounded-full bg-secondary" aria-label="Nivel de audio"><div ref={levelRef} className="h-full w-0 rounded-full bg-positive transition-[width] duration-75" /></div>
            <p className="mt-2 text-xs text-muted-foreground">{pending > 0 ? `${pending} fragmento(s) pendientes de subir (guardados en este dispositivo)` : 'Todo el audio está respaldado en el servidor'}</p>
            <div className="mt-5 flex gap-2"><input value={note} onChange={e => setNote(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') addNote(); }} placeholder="Marcar un momento (p. ej. proposición del apto. A-302)" aria-label="Nota" className="min-w-0 flex-1 rounded-xl border border-border bg-background px-3 py-2 text-sm outline-none focus:border-primary" />
              <button className={btnGhost} disabled={!note.trim()} onClick={addNote}>Marcar</button></div>
            <button disabled={busy || phase === 'finishing'} onClick={() => { void stopAndFinish(); }} className={`${btn} mt-5 bg-negative px-4 py-2.5 text-white`}>{phase === 'finishing' ? <Loader2 size={14} className="animate-spin" /> : <Square size={14} />}Finalizar y generar acta</button>
            <p className="mt-3 text-xs leading-5 text-muted-foreground">Mantén esta pestaña abierta. Puedes navegar a otras secciones de Habita mientras se graba.</p>
          </section>
          <section className={`${card} p-5`}>
            <h2 className="text-sm font-semibold">Marcadores</h2>
            <p className="mt-1 text-xs leading-5 text-muted-foreground">Se crean solos al avanzar el orden del día o cerrar una votación. Ordenan el acta por puntos.</p>
            <ul className="mt-3 divide-y divide-border">{markersView.map(m => <li key={`${m.t_ms}-${m.kind}-${m.label}`} className="flex gap-3 py-2 text-xs"><span className="w-14 shrink-0 tabular-nums text-muted-foreground">{clock(m.t_ms)}</span><span>{m.label}</span></li>)}</ul>
          </section>
        </div>
      )}

      {phase === 'result' && view && (
        <div className="space-y-5">
          <section className={`${card} p-5`}>
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div className="flex items-center gap-2.5">
                <StatusIcon active={!!active} status={view.status} />
                <div><p className="text-sm font-semibold">{STATUS_TEXT[view.status]}</p><p className="text-xs text-muted-foreground">{view.progress.transcribed}/{view.progress.chunks} fragmentos transcritos{view.progress.failed ? ` · ${view.progress.failed} con error` : ''}</p></div>
              </div>
              <button className={btnGhost} onClick={() => { setView(null); phaseRef.current = 'idle'; setPhase('idle'); }}>Nueva grabación</button>
            </div>
            {view.status === 'failed' && <p className="mt-3 text-xs leading-5 text-negative">{view.error}</p>}
            {active && <div className="mt-4 h-1.5 overflow-hidden rounded-full bg-secondary"><div className="h-full bg-primary transition-all" style={{ width: `${view.status === 'drafting' ? 90 : Math.max(5, (view.progress.transcribed / Math.max(1, view.progress.chunks)) * 80)}%` }} /></div>}
          </section>

          {(view.status === 'ready' || view.draft) && (
            <section className={`${card} p-5`}>
              <div className="flex flex-wrap items-center justify-between gap-2">
                <h2 className="flex items-center gap-2 text-sm font-semibold"><FileText size={15} />Borrador del acta</h2>
                <div className="flex flex-wrap gap-2">
                  <button className={btnGhost} disabled={busy || edited === null} onClick={() => { void act(() => api.saveDraft(view.id, draft), 'Cambios guardados.'); }}>Guardar cambios</button>
                  <button className={btnGhost} onClick={() => { void navigator.clipboard?.writeText(draft); notify('Borrador copiado.'); }}><Copy size={13} />Copiar</button>
                  <button className={btnGhost} onClick={() => downloadUrl(URL.createObjectURL(new Blob(['\ufeff' + draft], { type: 'text/markdown;charset=utf-8' })), 'borrador-acta.md')}><Download size={13} />Descargar</button>
                  <button className={btnGhost} disabled={busy || !!active || view.audio_deleted} onClick={() => { void act(() => api.regenerate(view.id)); }}><RefreshCw size={13} />Regenerar</button>
                </div>
              </div>
              <p className="mt-2 text-xs leading-5 text-muted-foreground">Revisa los datos marcados como [POR CONFIRMAR] y las marcas de tiempo [hh:mm:ss] para contrastar con el audio. Debe ser aprobado por la asamblea antes de ser acta oficial.</p>
              <textarea value={draft} onChange={e => setEdited(e.target.value)} aria-label="Borrador del acta" className="mt-4 h-[28rem] w-full resize-y rounded-xl border border-border bg-background p-4 font-mono text-xs leading-6 outline-none focus:border-primary" />
            </section>
          )}

          {view.transcript.length > 0 && (
            <details className={`${card} p-5`}>
              <summary className="cursor-pointer text-sm font-semibold">Transcripción completa ({view.transcript.length} fragmentos)</summary>
              <ul className="mt-4 space-y-2 text-xs leading-5">{view.transcript.map(t => <li key={t.t_ms} className="flex gap-3"><span className="w-16 shrink-0 tabular-nums text-muted-foreground">{t.ts}</span><span>{t.text}</span></li>)}</ul>
            </details>
          )}

          <section className={`${card} flex flex-wrap items-center justify-between gap-3 p-5`}>
            <p className="max-w-xl text-xs leading-5 text-muted-foreground">{view.audio_deleted ? 'El audio original fue eliminado del servidor. Se conservan la transcripción y el borrador.' : 'El audio original sigue almacenado. Elimínalo cuando el acta esté aprobada, según la política de retención de datos de tu copropiedad.'}</p>
            {!view.audio_deleted && <button className={btnGhost} disabled={busy || !!active} onClick={() => { if (window.confirm('¿Eliminar definitivamente el audio original? Esta acción no se puede deshacer.')) void act(() => api.deleteAudio(view.id), 'Audio eliminado.'); }}><Trash2 size={13} />Eliminar audio</button>}
          </section>
        </div>
      )}
    </div>
  );
}
