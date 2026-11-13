/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Application (client) ID de la app Habita Web (SPA) en Entra External ID. */
  readonly VITE_ENTRA_CLIENT_ID?: string;
  /** https://<subdominio>.ciamlogin.com/ */
  readonly VITE_ENTRA_AUTHORITY?: string;
  /** api://<client-id de Habita API>/access_as_user */
  readonly VITE_ENTRA_API_SCOPE?: string;
  /** Opcional. Por defecto, el origen de la página con "/" al final. */
  readonly VITE_ENTRA_REDIRECT_URI?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
