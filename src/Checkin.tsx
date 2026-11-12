import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import jsQR from 'jsqr';
import { AlertTriangle, Camera, CameraOff, Check, Copy, Download, Printer, QrCode, ScanLine, Search, Send, Undo2, X, Zap } from 'lucide-react';
import { beep, downloadUrl, newToken, normalize, parsePayload, passLink, payloadOf, qrPng } from '@/lib/checkin';
import type { CheckRecord, PassData, Records, Via } from '@/lib/checkin';

type Owner = { id: number; name: string; unit: string; present: boolean };
type Meeting = { id: number; title: string; date: string; time: string; venue: string };
type Result = { kind: 'ok' | 'dup' | 'bad'; owner?: Owner; via?: Via; msg?: string };
type Tab = 'puesto' | 'preregistro' | 'indicadores';

const escapeHtml = (value: unknown) => String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c] as string));
const safeDataImage = (url: string) => (/^data:image\/png;base64,[A-Za-z0-9+/=]+$/.test(url) ? url : '');
const timeOf = (ms: number) => new Date(ms).toLocaleTimeString('es-CO', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const card = 'rounded-2xl border border-border bg-card';
const btn = 'inline-flex items-center justify-center gap-1.5 rounded-lg px-3 py-2 text-xs font-medium transition-opacity hover:opacity-85 disabled:opacity-40';
const btnPrimary = `${btn} bg-primary text-primary-foreground`;
const btnGhost = `${btn} border border-border bg-card`;

function Scanner({ onScan, onClose }: { onScan: (text: string) => void; onClose: () => void }) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const scanRef = useRef(onScan);
  const [error, setError] = useState('');
  useEffect(() => { scanRef.current = onScan; });
  useEffect(() => {
    let stopped = false;
    let raf = 0;
    let last = 0;
    let stream: MediaStream | undefined;
    const canvas = document.createElement('canvas');
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    const W = window as unknown as { BarcodeDetector?: new (o: { formats: string[] }) => { detect: (s: CanvasImageSource) => Promise<{ rawValue: string }[]> } };
    const detector = W.BarcodeDetector ? new W.BarcodeDetector({ formats: ['qr_code'] }) : null;
    const tick = async (t: number) => {
      if (stopped) return;
      const v = videoRef.current;
      if (v && v.readyState >= 2 && v.videoWidth && t - last > 100) {
        last = t;
        try {
          if (detector) {
            const found = await detector.detect(v);
            if (found[0]) scanRef.current(found[0].rawValue);
          } else if (ctx) {
            const w = Math.min(640, v.videoWidth);
            const h = Math.round((v.videoHeight * w) / v.videoWidth);
            canvas.width = w;
            canvas.height = h;
            ctx.drawImage(v, 0, 0, w, h);
            const img = ctx.getImageData(0, 0, w, h);
            const code = jsQR(img.data, w, h, { inversionAttempts: 'dontInvert' });
            if (code) scanRef.current(code.data);
          }
        } catch { /* frame no legible */ }
      }
      raf = requestAnimationFrame(t2 => { void tick(t2); });
    };
    navigator.mediaDevices?.getUserMedia({ video: { facingMode: 'environment' }, audio: false })
      .then(s => {
        if (stopped) { s.getTracks().forEach(tr => tr.stop()); return; }
        stream = s;
        const v = videoRef.current;
        if (v) { v.srcObject = s; void v.play(); }
        raf = requestAnimationFrame(t => { void tick(t); });
      })
      .catch(() => setError('No se pudo acceder a la cámara. Revisa el permiso del navegador o usa la búsqueda manual.'));
    if (!navigator.mediaDevices) setTimeout(() => setError('Este navegador no permite usar la cámara (requiere HTTPS).'), 0);
    return () => { stopped = true; cancelAnimationFrame(raf); stream?.getTracks().forEach(tr => tr.stop()); };
  }, []);
  return (
    <div className="relative overflow-hidden rounded-xl bg-black">
      {error ? <div className="flex aspect-video items-center justify-center p-6 text-center text-xs leading-5 text-white/80"><CameraOff className="mr-2 shrink-0" size={18} />{error}</div> : (
        <>
          <video ref={videoRef} playsInline muted className="aspect-video w-full object-cover" />
          <div className="pointer-events-none absolute inset-0 flex items-center justify-center"><div className="h-2/3 aspect-square rounded-2xl border-2 border-white/80 shadow-[0_0_0_999px_rgba(0,0,0,0.35)]" /></div>
        </>
      )}
      <button onClick={onClose} aria-label="Cerrar cámara" className="absolute right-2 top-2 flex h-8 w-8 items-center justify-center rounded-full bg-black/60 text-white"><X size={16} /></button>
    </div>
  );
}

function PassModal({ data, onClose }: { data: PassData; onClose: () => void }) {
  const link = useMemo(() => passLink(data), [data]);
  const png = useMemo(() => qrPng(payloadOf(data.i, data.o, data.k), 420), [data]);
  const [copied, setCopied] = useState(false);
  const text = `Hola ${data.n.split(' ')[0]}, este es tu pase de ingreso para ${data.m} (${data.d}, ${data.t} h, ${data.v}). Preséntalo en la entrada para registrarte en segundos: ${link}`;
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div className="w-full max-w-sm rounded-2xl bg-card p-6 text-center" onClick={e => e.stopPropagation()}>
        <div className="flex justify-between"><span className="text-xs font-medium text-muted-foreground">Pase de ingreso</span><button onClick={onClose} aria-label="Cerrar"><X size={16} /></button></div>
        <img src={png} alt={`Código QR de ${data.n}`} className="mx-auto my-4 w-56 rounded-xl border border-border" />
        <p className="font-semibold">{data.n}</p>
        <p className="text-xs text-muted-foreground">{data.u} · Código {data.k}</p>
        <div className="mt-5 grid grid-cols-2 gap-2">
          <a href={`https://wa.me/?text=${encodeURIComponent(text)}`} target="_blank" rel="noreferrer" className={btnPrimary}><Send size={13} />WhatsApp</a>
          <button className={btnGhost} onClick={() => { void navigator.clipboard?.writeText(link); setCopied(true); setTimeout(() => setCopied(false), 2000); }}>{copied ? <Check size={13} /> : <Copy size={13} />}{copied ? 'Copiado' : 'Copiar enlace'}</button>
          <button className={`${btnGhost} col-span-2`} onClick={() => downloadUrl(png, `pase-${data.u}.png`)}><Download size={13} />Descargar QR</button>
        </div>
      </div>
    </div>
  );
}

export function PassView({ data }: { data: PassData }) {
  const png = useMemo(() => qrPng(payloadOf(data.i, data.o, data.k), 480), [data]);
  return (
    <main className="flex min-h-screen items-center justify-center bg-background p-5 text-foreground">
      <div className="w-full max-w-sm overflow-hidden rounded-3xl border border-border bg-card text-center shadow-sm">
        <div className="bg-primary px-6 py-5 text-primary-foreground"><p className="text-xs opacity-80">{data.c}</p><h1 className="mt-1 text-base font-semibold">{data.m}</h1><p className="mt-1 text-xs opacity-80">{data.d} · {data.t} h · {data.v}</p></div>
        <div className="p-6">
          <img src={png} alt="Código QR de ingreso" className="mx-auto w-64 max-w-full rounded-2xl border border-border" />
          <p className="mt-4 text-lg font-semibold">{data.n}</p>
          <p className="text-sm text-muted-foreground">{data.u}</p>
          <p className="mt-4 rounded-xl bg-hero px-4 py-3 text-xs leading-5 text-muted-foreground">Muestra este código en la entrada. Sube el brillo de la pantalla. Si no puedes escanearlo, dicta tu unidad ({data.u}).</p>
          <button className={`${btnGhost} mt-4 w-full`} onClick={() => downloadUrl(png, `pase-${data.u}.png`)}><Download size={13} />Guardar en el teléfono</button>
        </div>
      </div>
    </main>
  );
}

export default function Checkin({ owners, meeting, community, onSetPresent, notify }: { owners: Owner[]; meeting: Meeting; community: string; onSetPresent: (id: number, present: boolean) => void; notify: (msg: string) => void }) {
  const storageKey = `habita-checkin-v1-${meeting.id}`;
  const [records, setRecords] = useState<Records>(() => {
    let saved: Records = {};
    try { saved = JSON.parse(localStorage.getItem(storageKey) ?? '{}') as Records; } catch { /* vacío */ }
    const out: Records = {};
    owners.forEach(o => { out[o.id] = saved[o.id] ?? { token: newToken() }; });
    return out;
  });
  const [tab, setTab] = useState<Tab>('puesto');
  const [query, setQuery] = useState('');
  const [scanning, setScanning] = useState(false);
  const [result, setResult] = useState<Result | null>(null);
  const [passFor, setPassFor] = useState<Owner | null>(null);
  const [listQuery, setListQuery] = useState('');
  const [now, setNow] = useState(() => Date.now());
  const [manualSec, setManualSec] = useState(60);
  const [qrSec, setQrSec] = useState(6);
  const lastScan = useRef({ text: '', at: 0 });
  const searchRef = useRef<HTMLInputElement>(null);

  useEffect(() => { localStorage.setItem(storageKey, JSON.stringify(records)); }, [records, storageKey]);
  useEffect(() => { const t = setInterval(() => setNow(Date.now()), 15000); return () => clearInterval(t); }, []);

  const byId = useMemo(() => new Map(owners.map(o => [o.id, o])), [owners]);
  const passOf = useCallback((o: Owner): PassData => ({ c: community, m: meeting.title, d: meeting.date, t: meeting.time, v: meeting.venue, n: o.name, u: o.unit, o: o.id, i: meeting.id, k: records[o.id]?.token ?? '' }), [community, meeting, records]);

  const register = useCallback((o: Owner, via: Via) => {
    if (o.present) { setResult({ kind: 'dup', owner: o }); beep(false); return; }
    setRecords(r => ({ ...r, [o.id]: { ...r[o.id], at: Date.now(), via } }));
    onSetPresent(o.id, true);
    setResult({ kind: 'ok', owner: o, via });
    beep(true);
  }, [onSetPresent]);

  const handleScan = useCallback((text: string) => {
    const t = Date.now();
    if (lastScan.current.text === text && t - lastScan.current.at < 3000) return;
    lastScan.current = { text, at: t };
    const p = parsePayload(text);
    const owner = p ? byId.get(p.ownerId) : undefined;
    if (!p || !owner || p.meetingId !== meeting.id || records[owner.id]?.token !== p.token) {
      setResult({ kind: 'bad', msg: 'Código no válido para esta asamblea' });
      beep(false);
      return;
    }
    register(owner, 'QR');
  }, [byId, meeting.id, records, register]);

  const undo = (o: Owner) => {
    setRecords(r => ({ ...r, [o.id]: { token: r[o.id].token, pre: r[o.id].pre } }));
    onSetPresent(o.id, false);
    setResult(null);
    notify(`Registro de ${o.name} revertido.`);
  };

  const matches = useMemo(() => {
    const q = normalize(query);
    if (!q || q.startsWith('habita|')) return [];
    return owners.filter(o => normalize(`${o.name} ${o.unit}`).includes(q)).sort((a, b) => Number(a.present) - Number(b.present)).slice(0, 6);
  }, [owners, query]);

  const submitSearch = () => {
    if (query.trim().toUpperCase().startsWith('HABITA|')) { handleScan(query.trim()); setQuery(''); return; }
    if (matches.length === 1) { register(matches[0], 'Búsqueda'); setQuery(''); }
  };

  const simulate = () => {
    const candidates = owners.filter(o => !o.present && records[o.id]?.pre);
    if (!candidates.length) { notify('No hay pases enviados pendientes. Genera pases en la pestaña Preregistro.'); return; }
    const o = candidates[Math.floor(Math.random() * candidates.length)];
    lastScan.current = { text: '', at: 0 };
    handleScan(payloadOf(meeting.id, o.id, records[o.id].token));
  };

  const generateAll = () => {
    const t = Date.now();
    setRecords(r => {
      const next = { ...r };
      owners.forEach(o => { if (!o.present && !next[o.id].pre) next[o.id] = { ...next[o.id], pre: t }; });
      return next;
    });
    notify('Pases generados para todos los copropietarios pendientes.');
  };

  const exportCsv = () => {
    const rows = ['Copropietario,Unidad,Código,Enlace del pase', ...owners.map(o => `"${o.name}","${o.unit}",${records[o.id].token},"${passLink(passOf(o))}"`)];
    downloadUrl(URL.createObjectURL(new Blob(['\ufeff' + rows.join('\n')], { type: 'text/csv;charset=utf-8' })), 'pases-asamblea.csv');
  };

  const printCards = () => {
    const pending = owners.filter(o => !o.present);
    const cards = pending.map(o => `<div class="c"><img src="${escapeHtml(safeDataImage(qrPng(payloadOf(meeting.id, o.id, records[o.id].token), 240)))}"/><b>${escapeHtml(o.name)}</b><span>${escapeHtml(o.unit)}</span><small>${escapeHtml(records[o.id].token)}</small></div>`).join('');
    const w = window.open('', '_blank');
    if (!w) { notify('El navegador bloqueó la ventana de impresión.'); return; }
    w.document.write(`<title>Pases · ${escapeHtml(meeting.title)}</title><style>body{font-family:system-ui;margin:16px}h1{font-size:14px}.g{display:grid;grid-template-columns:repeat(4,1fr);gap:10px}.c{border:1px dashed #999;border-radius:8px;padding:8px;text-align:center;break-inside:avoid;font-size:11px}.c img{width:100%}.c b,.c span,.c small{display:block}</style><h1>${escapeHtml(community)} · ${escapeHtml(meeting.title)}</h1><div class="g">${cards}</div>`);
    w.document.close();
    w.focus();
    setTimeout(() => w.print(), 400);
  };

  const total = owners.length;
  const present = owners.filter(o => o.present).length;
  const pre = owners.filter(o => records[o.id]?.pre).length;
  const preAttended = owners.filter(o => records[o.id]?.pre && o.present).length;
  const log = owners.map(o => ({ o, r: records[o.id] as CheckRecord })).filter(x => x.r?.at).sort((a, b) => (b.r.at ?? 0) - (a.r.at ?? 0));
  const byVia = (v: Via) => log.filter(x => x.r.via === v).length;
  const earlier = present - log.length;
  const last5 = log.filter(x => now - (x.r.at ?? 0) < 5 * 60000).length;
  const buckets = Array.from({ length: 15 }, (_, i) => log.filter(x => { const age = Math.floor((now - (x.r.at ?? 0)) / 60000); return age === 14 - i; }).length);
  const maxBucket = Math.max(1, ...buckets);
  const qrCount = byVia('QR');
  const savedMin = Math.max(0, Math.round((qrCount * (manualSec - qrSec)) / 60));
  const listed = owners.filter(o => normalize(`${o.name} ${o.unit}`).includes(normalize(listQuery))).slice(0, 40);

  const banner = result && (
    <div className={`mt-4 flex items-center gap-3 rounded-xl border p-4 ${result.kind === 'ok' ? 'border-positive/30 bg-positive-soft' : result.kind === 'dup' ? 'border-border bg-neutral-soft' : 'border-negative/30 bg-negative/10'}`} role="status">
      {result.kind === 'ok' ? <Check className="shrink-0 text-positive" size={26} /> : <AlertTriangle className={`shrink-0 ${result.kind === 'bad' ? 'text-negative' : 'text-muted-foreground'}`} size={26} />}
      <div className="min-w-0 flex-1">
        <p className="truncate text-sm font-semibold">{result.owner ? result.owner.name : result.msg}</p>
        <p className="text-xs text-muted-foreground">{result.kind === 'ok' ? `${result.owner?.unit} · Registrado por ${result.via}` : result.kind === 'dup' ? `${result.owner?.unit} · Ya estaba registrado` : 'Verifica el pase o busca al copropietario por nombre o unidad.'}</p>
      </div>
      {result.kind === 'ok' && result.owner && <button className={btnGhost} onClick={() => undo(result.owner!)}><Undo2 size={13} />Deshacer</button>}
    </div>
  );

  return (
    <div>
      <div className="mb-5 grid grid-cols-2 gap-3 lg:grid-cols-4">
        {[['Asistentes', `${present}/${total}`, `${Math.round((present / total) * 100)}% de unidades`], ['Con pase enviado', String(pre), `${Math.round((pre / total) * 100)}% del conjunto`], ['Registro por QR', String(qrCount), 'en esta sesión'], ['Últimos 5 min', String(last5), 'registros']].map(([l, v, s]) => (
          <div key={l} className={`${card} p-4`}><p className="text-xs text-muted-foreground">{l}</p><p className="mt-1 text-2xl font-semibold">{v}</p><p className="text-xs text-muted-foreground">{s}</p></div>
        ))}
      </div>

      <div className="mb-5 flex gap-1 rounded-xl border border-border bg-card p-1 text-xs font-medium sm:inline-flex" role="tablist">
        {([['puesto', 'Puesto de registro'], ['preregistro', 'Preregistro y pases'], ['indicadores', 'Indicadores']] as [Tab, string][]).map(([k, l]) => (
          <button key={k} role="tab" aria-selected={tab === k} onClick={() => setTab(k)} className={`flex-1 rounded-lg px-4 py-2 ${tab === k ? 'bg-primary text-primary-foreground' : 'text-muted-foreground'}`}>{l}</button>
        ))}
      </div>

      {tab === 'puesto' && (
        <div className="grid gap-5 xl:grid-cols-[1.3fr_1fr]">
          <section className={`${card} p-5`}>
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div><h2 className="text-sm font-semibold">Registro rápido</h2><p className="mt-1 text-xs text-muted-foreground">Escanea el QR del pase o escribe nombre o unidad y presiona Enter.</p></div>
              <div className="flex gap-2">
                <button className={btnGhost} onClick={simulate}><Zap size={13} />Simular escaneo</button>
                <button className={btnPrimary} onClick={() => setScanning(s => !s)}>{scanning ? <CameraOff size={13} /> : <Camera size={13} />}{scanning ? 'Apagar cámara' : 'Escanear QR'}</button>
              </div>
            </div>
            {scanning && <div className="mt-4"><Scanner onScan={handleScan} onClose={() => setScanning(false)} /></div>}
            <div className="relative mt-4">
              <Search className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground" size={16} />
              <input ref={searchRef} autoFocus value={query} onChange={e => setQuery(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') submitSearch(); }} placeholder="Nombre, unidad (A-101) o lector de códigos…" aria-label="Buscar copropietario" className="w-full rounded-xl border border-border bg-background py-3 pl-10 pr-3 text-sm outline-none focus:border-primary" />
            </div>
            {matches.length > 0 && (
              <ul className="mt-2 divide-y divide-border rounded-xl border border-border">
                {matches.map(o => (
                  <li key={o.id} className="flex items-center justify-between gap-3 p-3">
                    <div className="min-w-0"><p className="truncate text-sm font-medium">{o.name}</p><p className="text-xs text-muted-foreground">{o.unit}{records[o.id]?.pre ? ' · con pase' : ''}</p></div>
                    {o.present ? <span className="text-xs font-medium text-positive">Presente</span> : <button className={btnPrimary} onClick={() => { register(o, 'Búsqueda'); setQuery(''); searchRef.current?.focus(); }}>Registrar</button>}
                  </li>
                ))}
              </ul>
            )}
            {query && !matches.length && !query.toUpperCase().startsWith('HABITA|') && <p className="mt-2 text-xs text-muted-foreground">Sin resultados.</p>}
            {banner}
          </section>

          <section className={`${card} p-5`}>
            <h2 className="text-sm font-semibold">Últimos registros</h2>
            {log.length === 0 ? <p className="mt-4 text-xs leading-5 text-muted-foreground">Aún no hay registros en esta sesión.{earlier > 0 ? ` ${earlier} unidades ya figuraban como presentes.` : ''}</p> : (
              <ul className="mt-3 divide-y divide-border">
                {log.slice(0, 8).map(({ o, r }) => (
                  <li key={o.id} className="flex items-center justify-between gap-2 py-2.5">
                    <div className="min-w-0"><p className="truncate text-sm">{o.name}</p><p className="text-xs text-muted-foreground">{o.unit} · {r.via}</p></div>
                    <div className="flex items-center gap-2"><span className="text-xs tabular-nums text-muted-foreground">{timeOf(r.at!)}</span><button aria-label={`Deshacer registro de ${o.name}`} onClick={() => undo(o)} className="text-muted-foreground"><Undo2 size={13} /></button></div>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </div>
      )}

      {tab === 'preregistro' && (
        <section className={card}>
          <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border p-5">
            <div className="max-w-xl"><h2 className="text-sm font-semibold">Pases con código QR</h2><p className="mt-1 text-xs leading-5 text-muted-foreground">Cada copropietario recibe un pase personal por WhatsApp o correo (enlace o imagen QR). El código solo contiene un identificador, no datos personales, y se valida en la entrada.</p></div>
            <div className="flex flex-wrap gap-2">
              <button className={btnPrimary} onClick={generateAll}><QrCode size={13} />Generar para todos</button>
              <button className={btnGhost} onClick={exportCsv}><Download size={13} />CSV de enlaces</button>
              <button className={btnGhost} onClick={printCards}><Printer size={13} />Imprimir pases</button>
            </div>
          </div>
          <div className="p-5 pb-3"><input value={listQuery} onChange={e => setListQuery(e.target.value)} placeholder="Filtrar por nombre o unidad" aria-label="Filtrar" className="w-full rounded-xl border border-border bg-background px-3 py-2 text-sm outline-none focus:border-primary sm:max-w-xs" /></div>
          <ul className="max-h-[28rem] divide-y divide-border overflow-y-auto px-5 pb-3">
            {listed.map(o => {
              const r = records[o.id];
              const status = o.present ? 'Asistió' : r?.pre ? 'Pase enviado' : 'Sin pase';
              return (
                <li key={o.id} className="flex flex-wrap items-center justify-between gap-2 py-2.5">
                  <div className="min-w-0"><p className="truncate text-sm font-medium">{o.name}</p><p className="text-xs text-muted-foreground">{o.unit}</p></div>
                  <div className="flex items-center gap-2">
                    <span className={`rounded-full px-2.5 py-1 text-xs ${o.present ? 'bg-positive-soft text-positive' : r?.pre ? 'bg-hero text-foreground' : 'bg-neutral-soft text-muted-foreground'}`}>{status}</span>
                    <button className={btnGhost} onClick={() => { if (!r.pre) setRecords(rs => ({ ...rs, [o.id]: { ...rs[o.id], pre: Date.now() } })); setPassFor(o); }}><ScanLine size={13} />{r?.pre ? 'Ver pase' : 'Enviar pase'}</button>
                  </div>
                </li>
              );
            })}
          </ul>
          {listed.length === 40 && <p className="px-5 pb-4 text-xs text-muted-foreground">Mostrando 40 resultados. Usa el filtro para acotar.</p>}
        </section>
      )}

      {tab === 'indicadores' && (
        <div className="grid gap-5 xl:grid-cols-2">
          <section className={`${card} p-5`}>
            <h2 className="text-sm font-semibold">Ritmo de registro</h2>
            <p className="mt-1 text-xs text-muted-foreground">Registros por minuto, últimos 15 minutos.</p>
            <div className="mt-5 flex h-32 items-end gap-1.5" aria-hidden="true">{buckets.map((b, i) => <div key={i} className="flex-1 rounded-t bg-primary/80" style={{ height: `${Math.max(3, (b / maxBucket) * 100)}%`, opacity: b ? 1 : 0.2 }} title={`${b}`} />)}</div>
            <div className="mt-1 flex justify-between text-[10px] text-muted-foreground"><span>−15 min</span><span>ahora</span></div>
            <dl className="mt-5 grid grid-cols-2 gap-3 text-xs">
              {([['Por QR', byVia('QR')], ['Por búsqueda', byVia('Búsqueda')], ['Manual', byVia('Manual')], ['Previos a la sesión', Math.max(0, earlier)]] as [string, number][]).map(([l, v]) => <div key={l} className="rounded-xl bg-hero p-3"><dt className="text-muted-foreground">{l}</dt><dd className="mt-0.5 text-lg font-semibold">{v}</dd></div>)}
            </dl>
          </section>
          <section className={`${card} p-5`}>
            <h2 className="text-sm font-semibold">Tiempo ahorrado (estimado)</h2>
            <p className="mt-1 text-xs leading-5 text-muted-foreground">Compara el registro por QR contra planilla en papel. Ajusta los segundos según lo medido en tu conjunto.</p>
            <div className="mt-4 grid grid-cols-2 gap-3 text-xs">
              <label className="space-y-1"><span className="text-muted-foreground">Planilla (seg./persona)</span><input type="number" min={1} value={manualSec} onChange={e => setManualSec(Math.max(1, Number(e.target.value)))} className="w-full rounded-lg border border-border bg-background px-3 py-2 text-sm" /></label>
              <label className="space-y-1"><span className="text-muted-foreground">QR (seg./persona)</span><input type="number" min={1} value={qrSec} onChange={e => setQrSec(Math.max(1, Number(e.target.value)))} className="w-full rounded-lg border border-border bg-background px-3 py-2 text-sm" /></label>
            </div>
            <p className="mt-5 text-3xl font-semibold">{savedMin} min</p>
            <p className="text-xs text-muted-foreground">ahorrados con {qrCount} registros por QR. Para {total} unidades, el registro pasaría de ≈{Math.round((total * manualSec) / 60)} min a ≈{Math.round((total * qrSec) / 60)} min en una sola fila.</p>
            <p className="mt-5 rounded-xl bg-hero p-3 text-xs leading-5 text-muted-foreground">Conversión de pases a asistencia: {pre ? Math.round((preAttended / pre) * 100) : 0}% ({preAttended} de {pre}). Un mayor preregistro reduce la fila el día de la asamblea.</p>
          </section>
          <p className="text-xs leading-5 text-muted-foreground xl:col-span-2">Nota: esta demostración cuenta unidades. En una asamblea real el quórum se calcula por coeficientes de copropiedad (Ley 675 de 2001) y los poderes de representación deben verificarse en la mesa de registro.</p>
        </div>
      )}

      {passFor && <PassModal data={passOf(passFor)} onClose={() => setPassFor(null)} />}
    </div>
  );
}
