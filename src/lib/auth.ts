import { BrowserCacheLocation, InteractionRequiredAuthError, PublicClientApplication } from '@azure/msal-browser';

// Configuración de Microsoft Entra External ID (variables VITE_ENTRA_* en .env.local; ver .env.example).
const env = import.meta.env;
const clientId = env.VITE_ENTRA_CLIENT_ID?.trim() ?? '';
const authority = env.VITE_ENTRA_AUTHORITY?.trim() ?? '';
const apiScope = env.VITE_ENTRA_API_SCOPE?.trim() ?? '';
const redirectUri = env.VITE_ENTRA_REDIRECT_URI?.trim() || `${window.location.origin}/`;

/** Con las tres variables, Habita pide iniciar sesión con Entra External ID. Sin ellas, usa la API en modo Development. */
export const entraConfigured = clientId !== '' && authority !== '' && apiScope !== '';

/** Permiso de la API (api://<client-id-de-la-API>/access_as_user) que se pide al iniciar sesión y en cada token. */
export const apiScopes = apiScope ? [apiScope] : [];

function hostOf(url: string) {
  try { return [new URL(url).host]; } catch { return []; }
}

export const msal = entraConfigured
  ? new PublicClientApplication({
      auth: { clientId, authority, knownAuthorities: hostOf(authority), redirectUri, postLogoutRedirectUri: redirectUri },
      // sessionStorage: la sesión dura lo que la pestaña. Si se cae internet, sigue funcionando con el token guardado.
      cache: { cacheLocation: BrowserCacheLocation.SessionStorage },
    })
  : null;

/** Avisa a la interfaz que hay que volver a iniciar sesión (escucha el evento SESSION_EXPIRED). */
export const sessionEvents = new EventTarget();
export const SESSION_EXPIRED = 'expired';
export const notifySessionExpired = () => { sessionEvents.dispatchEvent(new Event(SESSION_EXPIRED)); };

export class TokenError extends Error {
  kind: 'expired' | 'offline';
  constructor(kind: 'expired' | 'offline', message: string) { super(message); this.kind = kind; }
}

/**
 * Token de acceso para la API, o null si no hay Entra configurado (modo desarrollo).
 * No redirige por su cuenta: si la sesión expiró lo avisa, para no interrumpir una grabación en curso.
 */
export async function getApiToken(): Promise<string | null> {
  if (!msal) return null;
  const account = msal.getActiveAccount() ?? msal.getAllAccounts()[0];
  if (!account) {
    notifySessionExpired();
    throw new TokenError('expired', 'No hay una sesión iniciada. Vuelve a iniciar sesión.');
  }
  try {
    return (await msal.acquireTokenSilent({ scopes: apiScopes, account })).accessToken;
  } catch (e) {
    if (e instanceof InteractionRequiredAuthError) {
      notifySessionExpired();
      throw new TokenError('expired', 'Tu sesión expiró. Vuelve a iniciar sesión; lo que no se haya enviado sigue guardado en este dispositivo.');
    }
    throw new TokenError('offline', 'No se pudo renovar el acceso. Revisa la conexión; se reintentará solo.');
  }
}
