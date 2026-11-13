# Habita — Contexto completo del proyecto

Sistema web, en español, para sesiones de asamblea de copropietarios en Colombia (propiedad horizontal, Ley 675 de 2001).

## 1. Módulos

| Módulo | Estado | Descripción |
|---|---|---|
| Espacio de trabajo (`src/App.tsx`) | Hecho | Comunidad, propietarios, reuniones, votaciones, orden del día, documentos. Persistido en localStorage `habita-workspace-v1`. |
| Registro de asistencia (`src/Checkin.tsx`, `src/lib/checkin.ts`) | Hecho | Preregistro con QR, puesto de registro con cámara, indicadores. |
| Grabación y acta (`src/Recorder.tsx`, `src/lib/recorderApi.ts`, `backend-dotnet/`) | Prototipo en .NET 10 | Graba en segmentos, sube, transcribe y genera borrador de acta. |
| Login y roles (`src/Session.tsx`, `src/lib/auth.ts`, `backend-dotnet/`) | Implementado; falta probarlo con un tenant real | Entra External ID (MSAL en el frontend, JWT en la API), comunidades, miembros y roles. Ver sección 1.3. |

### 1.1 Registro de asistencia
- Pestañas: Puesto de registro, Preregistro y pases, Indicadores.
- Payload del QR: `HABITA|meetingId|ownerId|token`.
- Pase por enlace: `#pase=<base64url JSON>`; `main.tsx` muestra `PassView` si el hash existe.
- Lectura: `BarcodeDetector` si existe, si no `jsqr`. Generación: `qrcode-generator`.
- Registros por reunión en localStorage `habita-checkin-v1-<meetingId>`: `{ [ownerId]: { token, pre?, at?, via? } }`. El `token` es un secreto aleatorio por propietario que impide falsificar un QR.
- La impresión de pases escapa el HTML (`escapeHtml`, `safeDataImage`) porque se escribe con `document.write`.
- Limitación: los datos son por navegador (sin sincronización entre puestos).

### 1.2 Grabación, transcripción y acta
Flujo: consentimiento → grabar → subir segmentos → finalizar → transcribir → redactar → revisar y editar.

- Cliente: MediaRecorder con segmentos independientes de 60 s (el siguiente arranca antes de detener el anterior), cola persistente en IndexedDB (`habita-audio-v1`), Wake Lock, aviso `beforeunload`, recuperación de grabación interrumpida (`habita-rec-active`), marcadores automáticos (cambio de punto del orden del día, votación cerrada).
- La cola sube los fragmentos pendientes en paralelo. El servidor guarda cada fragmento por su número y lo reemplaza si llega de nuevo (por eso `FileAudioStore` usa `FileMode.Create`, no `CreateNew`).
- `recorderApi.ts` valida el id de grabación y los parámetros numéricos antes de armar la URL.
- Backend: transcripción por segmento (concurrencia 3, 3 reintentos con espera creciente). Las secciones se asignan por marcadores, usando el punto medio de cada segmento. Cada sección se redacta con el LLM (concurrencia 4) con un prompt estricto: sin invenciones, `[POR CONFIRMAR]`, citas `[hh:mm:ss]`. Encabezado, quórum y tabla de votos se generan de forma determinista (`ActaDrafting.RenderActa`).

### 1.3 Login y roles (propuesta v1, 12/11/2026)
Decisiones confirmadas:
- **Multi-comunidad:** Habita sirve a varias copropiedades; una persona puede tener roles distintos en cada una.
- **Identidad:** Microsoft Entra External ID (código al correo, sin contraseña; verificación en dos pasos para Administración y Revisoría).
- **Modelo híbrido de participación:** nadie queda excluido por no tener celular, internet o cuenta. El voto es de la unidad (coeficiente), todos los canales valen igual y cada unidad vota una sola vez por votación.

Roles en tres niveles:
- Plataforma: Superadministración Habita (único rol global, app role de Entra).
- Comunidad (permanentes, guardados en Azure SQL como persona × comunidad × rol): Administración, Registro, Consejo de administración, Revisoría fiscal, Copropietarios y apoderados.
- Asamblea (por reunión, vencen al aprobar el acta): Presidencia, Secretaría, Comisión de escrutinio y Comisión verificadora (opcionales).
- La representación (propietario, cotitular designado, representante legal, apoderado) es un vínculo unidad → persona por asamblea, con soporte; no es un rol.

Canales: registro con pase en el celular, pase impreso, búsqueda con cédula o a distancia; voto por celular (con cuenta o con código de 6 dígitos del puesto), tarjeta impresa con tres códigos, mesa asistida, votación nominal o a distancia.

Pendiente que decidan las administraciones y consejos: límite de poderes por persona, si la administración o la revisoría pueden recibir poderes, si la llegada del propietario suspende el poder, asistencia a distancia en la fase 1, tarjetas a mano alzada o en urna, y forma de entrega de los pases impresos.

#### Implementado en la API (12/11/2026)
- **Autenticación** (`Habita.Api/Authentication.cs`), según `Authentication:Mode`:
  - `EntraExternalId` (por defecto): JWT de Entra External ID validados con los metadatos del tenant (`Authority`) y la audiencia de la API (`Audience`, se acepta también `api://<audience>`). Si falta la configuración, la API no arranca.
  - `Development` (en `appsettings.Development.json`): toda solicitud entra como el usuario configurado en `Authentication:Development`, que es superadministración. Al arrancar se crea la comunidad "Conjunto Los Olivos" con ese usuario como administración. La API se niega a arrancar con este modo fuera del entorno Development.
- **Identidad:** claim `oid` como identificador de la cuenta; `email` y `name` para vincular invitaciones; el app role `Habita.SuperAdmin` (claim `roles`) marca la superadministración.
- **Invitaciones:** la administración agrega personas por correo y rol. La primera vez que alguien inicia sesión con ese correo, su cuenta queda vinculada (`People.ExternalId`). Una invitación ya vinculada no se reasigna a otra cuenta. Quien inicia sesión sin invitación no se guarda en la base.
- **Reglas** (`AccessService`): solo la superadministración crea comunidades; la administración (o la superadministración) gestiona miembros; no se puede quitar el último rol de administración; quien no pertenece a una comunidad recibe 404, y quien pertenece sin el rol necesario recibe 403. La superadministración no ve grabaciones.
- **Grabaciones:** todas las rutas exigen sesión (salvo `/api/health`). Crear, subir, finalizar, ver, regenerar, editar y borrar audio exige el rol de administración en la comunidad de la grabación; la Secretaría se agregará con las funciones de asamblea. El nombre de la comunidad del acta sale de la comunidad registrada.
- **Base de datos** (migración `AddAccessControl`): tablas `Communities`, `People` (índices únicos filtrados en `ExternalId` y `Email`) y `Memberships` (clave comunidad + persona + rol), y columna `Recordings.CommunityId`. Las grabaciones anteriores quedan con `CommunityId` vacío y responden 404.
- **Grabación en el frontend:** `Recorder.tsx` consulta `/api/me` y graba en la comunidad donde la persona tiene rol de administración.

#### Implementado en el frontend (13/11/2026)
- **MSAL** (`@azure/msal-browser` 5.27.0 y `@azure/msal-react` 5.7.4, versiones fijas). La configuración está en `src/lib/auth.ts` y se lee de las variables `VITE_ENTRA_*` (ver `.env.example`; los valores reales van en `.env.local`, excluido de git).
- **Dos modos:** con `VITE_ENTRA_CLIENT_ID`, `VITE_ENTRA_AUTHORITY` y `VITE_ENTRA_API_SCOPE` definidas, Habita pide iniciar sesión. Sin ellas no pide nada y usa la API en modo `Development`, como antes.
- **`src/Session.tsx`** (`AuthGate` en `main.tsx`):
  - Pantalla "Inicia sesión" con redirección a Entra (código de un solo uso al correo). Incluye un aviso para quien solo va a asistir: no necesita cuenta.
  - Después consulta `/api/me`. Si la cuenta no tiene comunidades ni es superadministración, muestra "Tu cuenta aún no tiene acceso" con la opción de usar otra cuenta.
  - Sin conexión no bloquea: tras 6 s sigue con lo guardado en el dispositivo.
  - Expone `useSession()` (nombre, correo, perfil, cerrar sesión). `App.tsx` lo usa en la barra lateral, la cabecera y el modal de la comunidad.
- **Pase del copropietario:** el enlace `#pase=...` se abre sin iniciar sesión.
- **Token para la API:** `recorderApi.ts` agrega `Authorization: Bearer` con `acquireTokenSilent`. Nunca redirige por su cuenta, para no cortar una grabación. Si la sesión expiró, muestra un aviso con "Iniciar sesión de nuevo"; los fragmentos pendientes siguen en IndexedDB y se reintentan. Un fallo de red se reintenta solo.
- **Caché de MSAL en `sessionStorage`:** la sesión dura lo que la pestaña. El personal de registro debe iniciar sesión antes de la asamblea; si luego se cae internet, sigue con el token guardado mientras sea válido.

## 2. Stack

**Frontend:** React 19, TypeScript 6, Vite 8, Tailwind 4, lucide-react, MSAL 5 (`@azure/msal-browser`, `@azure/msal-react`), eslint 10 (react-hooks 7). `node_modules` no está en git: se instala con `npm ci` a partir de `package-lock.json`.
**Backend (`backend-dotnet/`):** .NET 10 LTS, ASP.NET Core Minimal APIs, EF Core 10 sobre SQL Server (LocalDB en desarrollo, Azure SQL en producción), SDK oficial `OpenAI` 2.15 contra la API v1 de Azure OpenAI, `BackgroundService` + `Channel` como cola, Swagger UI (Swashbuckle) sobre el OpenAPI nativo, xUnit.

El prototipo original en FastAPI (`backend/`) y su lanzador `start.sh` se eliminaron; siguen en el historial de git (antes del commit `70a0371`).

### Arquitectura hexagonal del backend .NET
```
backend-dotnet/
  Habita.sln · Directory.Build.props (net10.0) · Directory.Packages.props (versiones centralizadas)
  nuget.config (solo nuget.org) · dotnet-tools.json (dotnet-ef)
  src/Habita.Domain/          Recording, Chunk, Marker, MeetingContext, DomainException. Sin dependencias.
                              Access.cs: Community, Person, Membership, CommunityRole
  src/Habita.Application/     Ports.cs (IRecordingRepository, IAccessRepository, IAudioStore, ITranscriber, ITextGenerator, IJobQueue)
                              RecordingService (casos de uso y procesamiento), RecordingAi (agrupa transcriptor y redactor)
                              AccessService (perfil, comunidades, miembros, verificación de roles), Caller
                              ActaDrafting (secciones, prompts, plantilla del acta)
  src/Habita.Infrastructure/  Persistence/ (HabitaDbContext, EfRecordingRepository, EfAccessRepository, Migrations/)
                              Storage/FileAudioStore · Ai/ (AzureOpenAiAdapter, DemoAiAdapter, UnconfiguredAiAdapter)
                              Queue/ (ChannelJobQueue, RecordingWorker) · DependencyInjection.cs
  src/Habita.Api/             Program.cs, Authentication.cs (Entra External ID y modo Development)
                              RecordingEndpoints.cs (rutas + ApiExceptionHandler), AccessEndpoints.cs
                              Contracts.cs y AccessContracts.cs (DTO snake_case)
  tests/Habita.Tests/         41 casos: flujo HTTP con SQLite e IA falsa, acceso con JWT firmados con clave de prueba,
                              adaptador Azure contra servidor falso, dominio
```

Detalles:
- Contrato HTTP en JSON snake_case, el que espera `src/lib/recorderApi.ts`.
- Los errores salen como ProblemDetails con `detail`: 422 regla de negocio, 404 no encontrada, 403 rol sin permiso, 400 formato inválido. Sin sesión válida responde 401 (sin cuerpo).
- Tablas `Recordings` y `Chunks` (FK con borrado en cascada). `Markers` y `Context` se guardan como JSON en `nvarchar(max)`, consultables con `JSON_VALUE`.
- El repositorio crea un `DbContext` corto por operación (`IDbContextFactory`), así que el worker puede escribir en paralelo.
- `Program` es `public partial class` con constructor `protected`; no puede ser `static` porque las pruebas usan `WebApplicationFactory<Program>`.

### API
Todas las rutas exigen sesión salvo `/api/health`.

Grabaciones (rol de administración en la comunidad):
- `GET /api/health` (sin sesión)
- `POST /api/recordings` (cuerpo con `community_id`, `meeting_id`, `title`, `consent_by`, `context`)
- `PUT /api/recordings/{id}/chunks/{seq}?start_ms&duration_ms` (cuerpo binario)
- `POST /api/recordings/{id}/finish`
- `GET /api/recordings/{id}`
- `POST /api/recordings/{id}/regenerate`
- `PUT /api/recordings/{id}/draft`
- `DELETE /api/recordings/{id}/audio`

Acceso:
- `GET /api/me`: perfil, si es superadministración y comunidades con sus roles. Vincula la invitación pendiente del correo.
- `POST /api/communities` (superadministración): `{ name, admin_email }`.
- `GET /api/communities/{id}/members` (administración o superadministración).
- `POST /api/communities/{id}/members` (administración o superadministración): `{ email, role, display_name? }`.
- `DELETE /api/communities/{id}/members/{personId}/roles/{role}` (administración o superadministración).

Roles en el contrato: `administrator`, `registration_staff`, `council`, `fiscal_auditor`, `owner`.

### Selección del proveedor de IA (backend .NET)
Configuración `Ai:Provider`:
- `auto` (por defecto): Azure OpenAI si existen `AzureOpenAI:Endpoint` y `AzureOpenAI:ApiKey`; si no, `none` (la interfaz muestra una advertencia).
- `azure`: exige Azure; si falta la configuración, la API no arranca.
- `demo`: transcripción y redacción SIMULADAS, sin servicios externos. Solo para probar la interfaz.
- `none`: se graba pero no se transcribe.

Azure se llama con la API v1 (`{Endpoint}/openai/v1/`), sin `api-version`. `AzureOpenAI:TranscribeDeployment` (por defecto `gpt-4o-transcribe`) y `AzureOpenAI:ChatDeployment` (por defecto `gpt-4o`) son nombres de deployments, no de modelos.

## 3. Arquitectura de producción recomendada (.NET)
- **Monolito modular con arquitectura hexagonal, más un worker.** No hacen falta microservicios todavía.
- Azure SQL (consultable desde SSMS, con Row-Level Security multi-tenant).
- Azure Blob Storage con subida directa por SAS.
- Azure Service Bus en lugar de Kafka.
- Azure SignalR para sincronizar los puestos de registro.
- Azure Container Apps, Key Vault, Entra ID, Application Insights.
- Patrón Outbox para publicar eventos de forma fiable.
- Azure OpenAI en lugar de OpenAI directo, por residencia de datos (Ley 1581 de 2012).
- Redis y Mongo/Postgres no son necesarios en esta etapa.

Correspondencia del prototipo con producción:

| Backend .NET actual | Producción |
|---|---|
| SQL Server LocalDB (EF Core) | Azure SQL (mismo proveedor, solo cambia la cadena de conexión) |
| `FileAudioStore` (disco) | Blob Storage (nuevo adaptador de `IAudioStore`) |
| `ChannelJobQueue` + `RecordingWorker` en proceso | Service Bus + worker separado (nuevo adaptador de `IJobQueue`) |
| Clave de API de Azure OpenAI | Identidad administrada (`AuthenticationPolicy` del SDK) |
| Modo `Development` de autenticación | Entra External ID (JWT), ya implementado en la API |

## 4. Marco legal
- Ley 1581 de 2012: la voz es dato personal, así que se requiere consentimiento informado y política de retención. El audio se puede borrar desde la interfaz.
- Ley 675 de 2001: el quórum y las mayorías se calculan por coeficientes de copropiedad. **No implementado:** hoy se cuentan unidades.

## 5. Seguridad
- El código se escanea con **Veracode** (remediaciones en el commit `5d22386`). Algunos patrones existen para satisfacer el escáner y no deben "simplificarse":
  - `AzureOpenAiOptions.V1Endpoint()` arma `/openai/v1` con `string.Join`.
  - `FileAudioStore.SaveAsync` valida varias veces que la ruta no salga de la carpeta raíz.
  - `RecordingAi` reduce los parámetros del constructor de `RecordingService`.
- Al aplicar una remediación, comprueba que no cambie el comportamiento y corre `dotnet test`.
- `.github/agents/security-reviewer.agent.md` define un agente revisor de seguridad (solo lectura).

## 6. Ejecución local (Windows)

Requisitos: SDK de .NET 10, SQL Server LocalDB (incluido con Visual Studio o SQL Server Express) y Node 20+.

```powershell
# Terminal 1 — API .NET en http://localhost:8100 (crea la base Habita en LocalDB y aplica las migraciones)
cd backend-dotnet
dotnet run --project src/Habita.Api                          # sin Azure: proveedor "none"
dotnet run --project src/Habita.Api -- --Ai:Provider=demo    # modo demostración

# Terminal 2 — frontend en http://localhost:5173 (el proxy /api apunta al puerto 8100)
npm ci
npm run dev
```

- Swagger UI (solo en Development): `http://localhost:8100/swagger`. Con el perfil `http` de `launchSettings.json` se abre solo. El documento OpenAPI está en `http://localhost:8100/openapi/v1.json`.
- En Development la API usa el modo de autenticación `Development`: no pide token y entra como la superadministración de desarrollo, con la comunidad "Conjunto Los Olivos" ya creada.

### Configurar Microsoft Entra External ID
1. En el centro de administración de Microsoft Entra, crea un **tenant externo** (External ID). Anota el id del tenant y su subdominio (`<subdominio>.ciamlogin.com`).
2. Registra la app **Habita API**:
   - Anota el *Application (client) ID*: es `Audience`.
   - En el manifiesto, `requestedAccessTokenVersion: 2`, para que los tokens usen el emisor v2.
   - *Expose an API*: Application ID URI `api://<client-id>` y un scope `access_as_user`.
   - *App roles*: `Habita.SuperAdmin` (miembros: usuarios y grupos).
   - *Token configuration*: claim opcional `email` en el **access token** (sin él no se pueden vincular las invitaciones).
3. En *Enterprise applications → Habita API → Users and groups*, asigna `Habita.SuperAdmin` al equipo de Habita.
4. Crea un flujo de usuario de registro e inicio de sesión con **código de un solo uso por correo**.
5. Registra la app **Habita Web** como *Single-page application*, con URI de redirección `http://localhost:5173/` (con la barra final; en producción, la URL publicada), permiso delegado al scope `access_as_user` de Habita API (con consentimiento de administrador) y asociada al flujo de usuario.
6. Configura la API con user-secrets y arráncala en modo Entra:
```powershell
cd backend-dotnet
dotnet user-secrets --project src/Habita.Api set "Authentication:EntraExternalId:Authority" "https://<subdominio>.ciamlogin.com/<id-del-tenant>/v2.0"
dotnet user-secrets --project src/Habita.Api set "Authentication:EntraExternalId:Audience" "<client-id de Habita API>"
dotnet run --project src/Habita.Api -- --Authentication:Mode=EntraExternalId
```
En producción: variables `Authentication__EntraExternalId__Authority` y `Authentication__EntraExternalId__Audience`, o Key Vault. La primera comunidad la crea la superadministración con `POST /api/communities`.
7. Configura el frontend: copia `.env.example` como `.env.local` y completa los valores. Reinicia `npm run dev`, porque Vite lee las variables al arrancar.
```ini
VITE_ENTRA_CLIENT_ID=<client-id de Habita Web>
VITE_ENTRA_AUTHORITY=https://<subdominio>.ciamlogin.com/
VITE_ENTRA_API_SCOPE=api://<client-id de Habita API>/access_as_user
```
Para volver al modo sin inicio de sesión, borra o vacía `.env.local` y arranca la API en Development sin `--Authentication:Mode=EntraExternalId`.

Azure OpenAI (secretos fuera del repositorio, con user-secrets):
```powershell
cd backend-dotnet
dotnet user-secrets --project src/Habita.Api set "AzureOpenAI:Endpoint" "https://<recurso>.openai.azure.com"
dotnet user-secrets --project src/Habita.Api set "AzureOpenAI:ApiKey" "<clave>"
dotnet user-secrets --project src/Habita.Api set "AzureOpenAI:TranscribeDeployment" "gpt-4o-transcribe"
dotnet user-secrets --project src/Habita.Api set "AzureOpenAI:ChatDeployment" "gpt-4o"
```
Comprueba con `curl http://localhost:8100/api/health`, que debe responder `"ai_provider":"azure-openai"`. En producción se usan variables de entorno (`AzureOpenAI__ApiKey`, `ConnectionStrings__Habita`) o Key Vault.

- Base de datos: `Server=(localdb)\MSSQLLocalDB;Database=Habita` (ábrela con SSMS). En Development las migraciones se aplican al arrancar (`Database:Initialize=Migrate`). En producción se aplica un script idempotente: `dotnet dotnet-ef migrations script --idempotent --project src/Habita.Infrastructure --startup-project src/Habita.Api`.
- Nueva migración: `dotnet dotnet-ef migrations add <Nombre> --project src/Habita.Infrastructure --startup-project src/Habita.Api --output-dir Persistence/Migrations -- --environment Development`.
- Audio local: `backend-dotnet/src/Habita.Api/data/audio/` (excluido de git).
- Sin SQL Server (Linux o Mac): `--Database:Provider=Sqlite --ConnectionStrings:Habita="Data Source=habita.db" --Database:Initialize=EnsureCreated`.

Pruebas del backend: `cd backend-dotnet && dotnet test`.
Comprobaciones del frontend: `npx tsc -p tsconfig.app.json --noEmit`, `npm run lint`, `npm run build`.

El micrófono y la cámara requieren `localhost` o HTTPS.

Finales de línea: `.gitattributes` (`* text=auto`) hace que git guarde LF; cada sistema usa los suyos al hacer checkout.

## 7. Limitaciones conocidas
- La transcripción real con Azure no está verificada: faltan credenciales y solo se probó contra un servidor Azure falso (rutas v1, credencial y parámetros).
- El inicio de sesión con un tenant real de Entra External ID no está verificado (aún no existe el tenant). La API se probó con tokens firmados con una clave de prueba. Del frontend se verificaron, en Edge, la pantalla de inicio de sesión con variables ficticias, el pase sin sesión y el modo desarrollo con la API real; la redirección a Entra y el token no.
- Las funciones de asamblea (Presidencia, Secretaría) y la representación (poderes) aún no existen; por ahora solo la administración graba.
- Sin diarización (identificación de hablantes).
- La asignación de segmentos a secciones usa el punto medio, así que puede errar en los límites.
- El quórum cuenta unidades, no coeficientes.
- Los datos de registro están en localStorage de cada navegador.
- Los flujos de micrófono y cámara no se probaron en un navegador real.

## 8. Pendientes
1. Crear el tenant de Entra External ID (sección "Configurar Microsoft Entra External ID") y probar el inicio de sesión de punta a punta.
2. Funciones de asamblea (Presidencia, Secretaría) y representación (poderes) en la API.
3. Probar con Azure OpenAI real.
4. Módulo de poderes y coeficientes (Ley 675).
5. Sincronización entre varios puestos de registro.
6. Regeneración de tokens de check-in faltantes.
7. Completar la arquitectura de producción en .NET (sección 3): Blob Storage, Service Bus con worker separado, Entra ID, Key Vault.
8. Configuración de publicación.
