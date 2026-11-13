import { createContext, useContext, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { EventType, InteractionStatus, InteractionType } from '@azure/msal-browser';
import { MsalProvider, useMsal } from '@azure/msal-react';
import { AlertTriangle, Building2, Loader2, LogIn, LogOut, QrCode } from 'lucide-react';
import { apiScopes, msal, SESSION_EXPIRED, sessionEvents } from '@/lib/auth';
import { api } from '@/lib/recorderApi';
import type { Me, Role } from '@/lib/recorderApi';

export type Session = {
  /** entra: sesión de Microsoft Entra External ID. development: sin inicio de sesión (API en modo Development). */
  mode: 'entra' | 'development';
  /** Perfil de /api/me; null si no se pudo consultar (por ejemplo, sin conexión). */
  me: Me | null;
  name: string;
  email: string | null;
  signOut: (() => void) | null;
};

const SessionContext = createContext<Session | null>(null);
export const useSession = () => useContext(SessionContext);

const ROLE_LABEL: Record<Role, string> = {
  administrator: 'Administración',
  registration_staff: 'Registro',
  council: 'Consejo de administración',
  fiscal_auditor: 'Revisoría fiscal',
  owner: 'Copropietario',
};

/** Roles de la persona en la comunidad indicada (o en la primera), para mostrarlos bajo su nombre. */
export function roleSummary(me: Me | null, communityName?: string) {
  if (!me) return '';
  const m = me.memberships.find(x => x.community_name === communityName) ?? me.memberships[0];
  if (m) return m.roles.map(r => ROLE_LABEL[r]).join(' · ');
  return me.is_super_admin ? 'Superadministración Habita' : '';
}

/** Iniciales para el avatar: primeras letras de las dos primeras palabras. */
export function userInitials(name: string) {
  return name.split(/\s+/).filter(w => /^\p{L}/u.test(w)).slice(0, 2).map(w => w[0].toUpperCase()).join('') || '·';
}

/**
 * Exige iniciar sesión con Entra External ID cuando está configurado. Sin configuración, deja pasar
 * (la API en modo Development identifica a todos como el usuario de desarrollo).
 */
export function AuthGate({ children }: Readonly<{ children: ReactNode }>) {
  if (!msal) return <ProfileGate mode="development" name="Modo desarrollo" email={null} signOut={null}>{children}</ProfileGate>;
  return <MsalProvider instance={msal}><EntraGate>{children}</EntraGate></MsalProvider>;
}

function EntraGate({ children }: Readonly<{ children: ReactNode }>) {
  const { instance, inProgress, accounts } = useMsal();
  const [loginError, setLoginError] = useState('');
  const [expired, setExpired] = useState(false);
  const account = instance.getActiveAccount() ?? accounts[0] ?? null;

  useEffect(() => { if (account && !instance.getActiveAccount()) instance.setActiveAccount(account); }, [account, instance]);

  useEffect(() => {
    // En MSAL 5 el inicio de sesión fallido llega como ACQUIRE_TOKEN_FAILURE de una redirección.
    const id = instance.addEventCallback(e => {
      if (e.eventType === EventType.ACQUIRE_TOKEN_FAILURE && e.interactionType === InteractionType.Redirect) setLoginError('No se pudo iniciar sesión. Intenta de nuevo; si el problema sigue, avisa a la administración.');
    });
    return () => { if (id) instance.removeEventCallback(id); };
  }, [instance]);

  useEffect(() => {
    const onExpired = () => setExpired(true);
    sessionEvents.addEventListener(SESSION_EXPIRED, onExpired);
    return () => sessionEvents.removeEventListener(SESSION_EXPIRED, onExpired);
  }, []);

  const login = () => {
    setLoginError('');
    instance.loginRedirect({ scopes: apiScopes }).catch(() => setLoginError('No se pudo abrir el inicio de sesión. Revisa la conexión e intenta de nuevo.'));
  };

  if (!account) return inProgress === InteractionStatus.None ? <LoginScreen onLogin={login} error={loginError} /> : <Splash text="Conectando con tu cuenta…" />;
  return (
    <ProfileGate mode="entra" name={account.name || account.username} email={account.username || null} signOut={() => { void instance.logoutRedirect({ account }); }}>
      {expired && <ExpiredBanner onLogin={login} />}
      {children}
    </ProfileGate>
  );
}

/** Consulta el perfil. Sin conexión no bloquea: el registro y la grabación funcionan con lo guardado en el dispositivo. */
function ProfileGate({ mode, name, email, signOut, children }: Readonly<{ mode: Session['mode']; name: string; email: string | null; signOut: (() => void) | null; children: ReactNode }>) {
  const [me, setMe] = useState<Me | null>(null);
  const [checked, setChecked] = useState(false);

  useEffect(() => {
    let alive = true;
    const giveUp = setTimeout(() => { if (alive) setChecked(true); }, 6000);
    api.me()
      .then(m => { if (alive) setMe(m); })
      .catch(() => undefined)
      .finally(() => { clearTimeout(giveUp); if (alive) setChecked(true); });
    return () => { alive = false; clearTimeout(giveUp); };
  }, []);

  if (!checked) return <Splash text="Cargando tu acceso…" />;
  if (me && me.memberships.length === 0 && !me.is_super_admin) return <NoAccess email={me.email ?? email} signOut={signOut} />;
  const session: Session = { mode, me, name: me?.display_name || name, email: me?.email ?? email, signOut };
  return <SessionContext.Provider value={session}>{children}</SessionContext.Provider>;
}

function Shell({ children }: Readonly<{ children: ReactNode }>) {
  return (
    <div className="flex min-h-screen items-center justify-center bg-sidebar px-4 py-10 text-sidebar-foreground">
      <div className="w-full max-w-[420px]">
        <p className="mb-8 flex items-center justify-center gap-2.5"><span className="flex h-8 w-8 items-center justify-center rounded-lg bg-accent text-primary"><Building2 size={21} strokeWidth={1.8} /></span><span className="text-[30px] font-semibold leading-none tracking-[-1.5px]">habita<span className="text-accent">.</span></span></p>
        <div className="rounded-2xl bg-card p-7 text-foreground shadow-xl">{children}</div>
      </div>
    </div>
  );
}

function Splash({ text }: Readonly<{ text: string }>) {
  return <Shell><p className="flex items-center justify-center gap-2.5 py-6 text-sm text-muted-foreground" role="status"><Loader2 size={16} className="animate-spin" />{text}</p></Shell>;
}

function LoginScreen({ onLogin, error }: Readonly<{ onLogin: () => void; error: string }>) {
  return (
    <Shell>
      <h1 className="text-[22px] font-semibold tracking-[-0.5px]">Inicia sesión</h1>
      <p className="mt-2 text-sm leading-6 text-muted-foreground">Entra con tu correo. Te enviaremos un código de un solo uso, así que no necesitas contraseña.</p>
      <button onClick={onLogin} className="mt-6 flex w-full items-center justify-center gap-2 rounded-xl bg-primary px-4 py-3 text-sm font-medium text-primary-foreground transition-opacity hover:opacity-90"><LogIn size={16} />Continuar con mi correo</button>
      {error && <p role="alert" className="mt-4 flex items-start gap-2 rounded-xl border border-negative/30 bg-negative/10 p-3 text-xs leading-5"><AlertTriangle size={14} className="mt-0.5 shrink-0 text-negative" />{error}</p>}
      <div className="mt-6 flex items-start gap-3 rounded-xl border border-hero-border bg-hero p-4 text-xs leading-5">
        <QrCode size={18} className="mt-0.5 shrink-0 text-primary" />
        <p><span className="font-medium">¿Solo vas a asistir a la asamblea?</span> No necesitas cuenta. En el puesto de registro muestra el pase que recibiste, en el celular o impreso, o da tu nombre y tu cédula.</p>
      </div>
      <p className="mt-5 text-center text-[11px] text-muted-foreground">Acceso protegido con Microsoft Entra External ID</p>
    </Shell>
  );
}

function NoAccess({ email, signOut }: Readonly<{ email: string | null; signOut: (() => void) | null }>) {
  return (
    <Shell>
      <h1 className="text-[22px] font-semibold tracking-[-0.5px]">Tu cuenta aún no tiene acceso</h1>
      <p className="mt-2 text-sm leading-6 text-muted-foreground">Iniciaste sesión{email ? <> como <span className="font-medium text-foreground">{email}</span></> : ''}, pero ninguna comunidad te ha invitado todavía. Pide a la administración de tu copropiedad que te agregue con este correo.</p>
      {signOut && <button onClick={signOut} className="mt-6 flex w-full items-center justify-center gap-2 rounded-xl border border-border px-4 py-3 text-sm font-medium transition-colors hover:bg-secondary"><LogOut size={16} />Usar otra cuenta</button>}
    </Shell>
  );
}

function ExpiredBanner({ onLogin }: Readonly<{ onLogin: () => void }>) {
  return (
    <div role="alert" className="fixed bottom-4 left-1/2 z-50 w-[min(92vw,520px)] -translate-x-1/2 rounded-xl border border-negative/30 bg-card p-4 text-xs leading-5 shadow-xl">
      <p className="flex items-start gap-2 font-medium"><AlertTriangle size={14} className="mt-0.5 shrink-0 text-negative" />Tu sesión expiró</p>
      <p className="mt-1 text-muted-foreground">Lo que no se haya enviado sigue guardado en este dispositivo y se enviará al volver a entrar. Si estás grabando, finaliza primero: al iniciar sesión la página se recarga.</p>
      <button onClick={onLogin} className="mt-3 inline-flex items-center gap-1.5 rounded-lg bg-primary px-3 py-2 text-xs font-medium text-primary-foreground"><LogIn size={13} />Iniciar sesión de nuevo</button>
    </div>
  );
}
