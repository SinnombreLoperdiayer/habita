# Habita — Contexto completo del proyecto

Sistema web, en español, para sesiones de asamblea de copropietarios en Colombia (propiedad horizontal, Ley 675 de 2001).

## 1. Módulos

| Módulo | Estado | Descripción |
|---|---|---|
| Espacio de trabajo (`src/App.tsx`) | Hecho | Comunidad, propietarios, reuniones, votaciones, orden del día, documentos. Persistido en localStorage `habita-workspace-v1`. |
| Registro de asistencia (`src/Checkin.tsx`, `src/lib/checkin.ts`) | Hecho | Preregistro con QR, puesto de registro con cámara, indicadores. |
| Grabación y acta (`src/Recorder.tsx`, `src/lib/recorderApi.ts`, `backend-dotnet/`) | Prototipo en .NET 10 | Graba en segmentos, sube, transcribe y genera borrador de acta. |
| Login y roles | En diseño | Ver sección 1.3. |

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

## 2. Stack

**Frontend:** React 19, TypeScript 6, Vite 8, Tailwind 4, lucide-react, eslint 10 (react-hooks 7). `node_modules` no está en git: se instala con `npm ci` a partir de `package-lock.json`.
**Backend (`backend-dotnet/`):** .NET 10 LTS, ASP.NET Core Minimal APIs, EF Core 10 sobre SQL Server (LocalDB en desarrollo, Azure SQL en producción), SDK oficial `OpenAI` 2.15 contra la API v1 de Azure OpenAI, `BackgroundService` + `Channel` como cola, Swagger UI (Swashbuckle) sobre el OpenAPI nativo, xUnit.

El prototipo original en FastAPI (`backend/`) y su lanzador `start.sh` se eliminaron; siguen en el historial de git (antes del commit `70a0371`).

### Arquitectura hexagonal del backend .NET
```
backend-dotnet/
  Habita.sln · Directory.Build.props (net10.0) · Directory.Packages.props (versiones centralizadas)
  nuget.config (solo nuget.org) · dotnet-tools.json (dotnet-ef)
  src/Habita.Domain/          Recording, Chunk, Marker, MeetingContext, DomainException. Sin dependencias.
  src/Habita.Application/     Ports.cs (IRecordingRepository, IAudioStore, ITranscriber, ITextGenerator, IJobQueue)
                              RecordingService (casos de uso y procesamiento), RecordingAi (agrupa transcriptor y redactor)
                              ActaDrafting (secciones, prompts, plantilla del acta)
  src/Habita.Infrastructure/  Persistence/ (HabitaDbContext, EfRecordingRepository, Migrations/)
                              Storage/FileAudioStore · Ai/ (AzureOpenAiAdapter, DemoAiAdapter, UnconfiguredAiAdapter)
                              Queue/ (ChannelJobQueue, RecordingWorker) · DependencyInjection.cs
  src/Habita.Api/             Program.cs, RecordingEndpoints.cs (rutas + ApiExceptionHandler), Contracts.cs (DTO snake_case)
  tests/Habita.Tests/         23 casos: flujo HTTP con SQLite e IA falsa, adaptador Azure contra servidor falso, dominio
```

Detalles:
- Contrato HTTP en JSON snake_case, el que espera `src/lib/recorderApi.ts`.
- Los errores salen como ProblemDetails con `detail`: 422 regla de negocio, 404 no encontrada, 400 formato inválido.
- Tablas `Recordings` y `Chunks` (FK con borrado en cascada). `Markers` y `Context` se guardan como JSON en `nvarchar(max)`, consultables con `JSON_VALUE`.
- El repositorio crea un `DbContext` corto por operación (`IDbContextFactory`), así que el worker puede escribir en paralelo.
- `Program` es `public partial class` con constructor `protected`; no puede ser `static` porque las pruebas usan `WebApplicationFactory<Program>`.

### API
- `GET /api/health`
- `POST /api/recordings`
- `PUT /api/recordings/{id}/chunks/{seq}?start_ms&duration_ms` (cuerpo binario)
- `POST /api/recordings/{id}/finish`
- `GET /api/recordings/{id}`
- `POST /api/recordings/{id}/regenerate`
- `PUT /api/recordings/{id}/draft`
- `DELETE /api/recordings/{id}/audio`

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
| Sin autenticación | Entra ID (JWT) con roles (en diseño) |

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
- No hay autenticación en la API.
- Sin diarización (identificación de hablantes).
- La asignación de segmentos a secciones usa el punto medio, así que puede errar en los límites.
- El quórum cuenta unidades, no coeficientes.
- Los datos de registro están en localStorage de cada navegador.
- Los flujos de micrófono y cámara no se probaron en un navegador real.

## 8. Pendientes
1. Login con roles (en diseño).
2. Probar con Azure OpenAI real.
3. Módulo de poderes y coeficientes (Ley 675).
4. Sincronización entre varios puestos de registro.
5. Regeneración de tokens de check-in faltantes.
6. Completar la arquitectura de producción en .NET (sección 3): Blob Storage, Service Bus con worker separado, Entra ID, Key Vault.
7. Configuración de publicación.
