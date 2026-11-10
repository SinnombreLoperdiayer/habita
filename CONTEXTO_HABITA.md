# Habita — Contexto completo del proyecto

Sistema web, en español, para sesiones de asamblea de copropietarios en Colombia (propiedad horizontal, Ley 675 de 2001).

## 1. Módulos

| Módulo | Estado | Descripción |
|---|---|---|
| Espacio de trabajo (`src/App.tsx`) | Hecho | Comunidad, propietarios, reuniones, votaciones, orden del día, documentos. Persistido en localStorage `habita-workspace-v1`. |
| Registro de asistencia (`src/Checkin.tsx`, `src/lib/checkin.ts`) | Hecho | Preregistro con QR, puesto de registro con cámara, indicadores. |
| Grabación y acta (`src/Recorder.tsx`, `src/lib/recorderApi.ts`, `backend-dotnet/`) | Prototipo en .NET 10 | Graba en segmentos, sube, transcribe y genera borrador de acta. |

### 1.1 Registro de asistencia
- Pestañas: Puesto de registro, Preregistro y pases, Indicadores.
- Payload del QR: `HABITA|meetingId|ownerId|token`.
- Pase por enlace: `#pase=<base64url JSON>`; `main.tsx` muestra `PassView` si el hash existe.
- Lectura: `BarcodeDetector` si existe, si no `jsqr`. Generación: `qrcode-generator`.
- Registros por reunión en localStorage `habita-checkin-v1-<meetingId>`: `{ [ownerId]: { token, pre?, at?, via? } }`. El `token` es un secreto aleatorio por propietario que impide falsificar un QR.
- Limitación: los datos son por navegador (sin sincronización entre puestos).

### 1.2 Grabación, transcripción y acta
Flujo: consentimiento → grabar → subir segmentos → finalizar → transcribir → redactar → revisar y editar.

- Cliente: MediaRecorder con segmentos independientes de 60 s (el siguiente arranca antes de detener el anterior), cola persistente en IndexedDB (`habita-audio-v1`), Wake Lock, aviso `beforeunload`, recuperación de grabación interrumpida (`habita-rec-active`), marcadores automáticos (cambio de punto del orden del día, votación cerrada).
- Backend: transcripción por segmento (concurrencia 3, 3 reintentos con espera creciente). Las secciones se asignan por marcadores, usando el punto medio de cada segmento. Cada sección se redacta con el LLM (concurrencia 4) con un prompt estricto: sin invenciones, `[POR CONFIRMAR]`, citas `[hh:mm:ss]`. Encabezado, quórum y tabla de votos se generan de forma determinista (`ActaDrafting.RenderActa`).

## 2. Stack

**Frontend:** React 19, TypeScript 6, Vite 8, Tailwind 4, lucide-react, eslint 10 (react-hooks 7). `node_modules` está instalado para Windows.
**Backend (`backend-dotnet/`, el vigente):** .NET 10 LTS, ASP.NET Core Minimal APIs, EF Core 10 sobre SQL Server (LocalDB en desarrollo, Azure SQL en producción), SDK oficial `OpenAI` 2.15 contra la API v1 de Azure OpenAI, `BackgroundService` + `Channel` como cola, xUnit.
**Backend anterior (`backend/`, FastAPI):** prototipo original en Python. Se conserva como referencia hasta retirarlo; ya no es el que usa el frontend.

### Arquitectura hexagonal del backend .NET
```
backend-dotnet/
  Habita.sln · Directory.Build.props (net10.0) · Directory.Packages.props (versiones centralizadas)
  nuget.config (solo nuget.org) · dotnet-tools.json (dotnet-ef)
  src/Habita.Domain/          Recording, Chunk, Marker, MeetingContext, DomainException. Sin dependencias.
  src/Habita.Application/     Ports.cs (IRecordingRepository, IAudioStore, ITranscriber, ITextGenerator, IJobQueue)
                              RecordingService (casos de uso y procesamiento), ActaDrafting (secciones, prompts, plantilla del acta)
  src/Habita.Infrastructure/  Persistence/ (HabitaDbContext, EfRecordingRepository, Migrations/)
                              Storage/FileAudioStore · Ai/ (AzureOpenAiAdapter, DemoAiAdapter, UnconfiguredAiAdapter)
                              Queue/ (ChannelJobQueue, RecordingWorker) · DependencyInjection.cs
  src/Habita.Api/             Program.cs, RecordingEndpoints.cs (rutas + ApiExceptionHandler), Contracts.cs (DTO snake_case)
  tests/Habita.Tests/         22 pruebas: flujo HTTP con SQLite e IA falsa, adaptador Azure contra servidor falso, dominio
```

Detalles:
- El contrato HTTP es idéntico al de FastAPI (JSON snake_case, mismos códigos), así que el frontend no cambió.
- Los errores salen como ProblemDetails con `detail`: 422 regla de negocio, 404 no encontrada, 400 formato inválido.
- Tablas `Recordings` y `Chunks` (FK con borrado en cascada). `Markers` y `Context` se guardan como JSON en `nvarchar(max)`, consultables con `JSON_VALUE`.
- El repositorio crea un `DbContext` corto por operación (`IDbContextFactory`), así que el worker puede escribir en paralelo.

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
| Sin autenticación | Entra ID (JWT) |

## 4. Marco legal
- Ley 1581 de 2012: la voz es dato personal, así que se requiere consentimiento informado y política de retención. El audio se puede borrar desde la interfaz.
- Ley 675 de 2001: el quórum y las mayorías se calculan por coeficientes de copropiedad. **No implementado:** hoy se cuentan unidades.

## 5. Ejecución local (Windows)

Requisitos: SDK de .NET 10, SQL Server LocalDB (incluido con Visual Studio o SQL Server Express) y Node 20+.

```powershell
# Terminal 1 — API .NET en http://localhost:8100 (crea la base Habita en LocalDB y aplica las migraciones)
cd backend-dotnet
dotnet run --project src/Habita.Api                          # sin Azure: proveedor "none"
dotnet run --project src/Habita.Api -- --Ai:Provider=demo    # modo demostración

# Terminal 2 — frontend en http://localhost:5173 (el proxy /api apunta al puerto 8100)
npm install
npm run dev
```

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
- Audio local: `backend-dotnet/src/Habita.Api/data/audio/`.
- Sin SQL Server (Linux o Mac): `--Database:Provider=Sqlite --ConnectionStrings:Habita="Data Source=habita.db" --Database:Initialize=EnsureCreated`.
- OpenAPI en Development: `http://localhost:8100/openapi/v1.json`.

Pruebas del backend: `cd backend-dotnet && dotnet test`.
Comprobaciones del frontend: `npx tsc -p tsconfig.app.json --noEmit`, `npm run lint`, `npm run build`.

Nota: `start.sh` es el lanzador del antiguo entorno Workshop (FastAPI) y no sirve en local. El micrófono y la cámara requieren `localhost` o HTTPS.

## 6. Limitaciones conocidas
- La transcripción real con Azure no está verificada: faltan credenciales y solo se probó contra un servidor Azure falso (rutas v1, credencial y parámetros).
- No hay autenticación en la API.
- Sin diarización (identificación de hablantes).
- La asignación de segmentos a secciones usa el punto medio, así que puede errar en los límites.
- El quórum cuenta unidades, no coeficientes.
- Los datos de registro están en localStorage de cada navegador.
- Los flujos de micrófono y cámara no se probaron en un navegador real.

## 7. Pendientes
1. Probar con Azure OpenAI real.
2. Autenticación (Entra ID o clave de API).
3. Módulo de poderes y coeficientes (Ley 675).
4. Sincronización entre varios puestos de registro.
5. Regeneración de tokens de check-in faltantes.
6. Completar la arquitectura de producción en .NET (sección 3): Blob Storage, Service Bus con worker separado, Entra ID, Key Vault. El backend ya está en .NET 10.
7. Configuración de publicación.
8. Retirar el backend FastAPI (`backend/`) y `start.sh`.
