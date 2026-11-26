# CustomerMgnt

ระบบบริหารลูกค้า (Customer Management) บน **.NET 10** สำหรับ ecosystem ของ mrpichet
(AuthServer, LogServer, ProjectMgnt, IssueTracking, RequirementMgnt, MessageHub) โดยใช้ไลบรารีกลางจาก [`external/common`](https://github.com/mrpichet/common)

- **business layer ชุดเดียว** (`CustomerMgnt.Core`, pure C#) เปิดให้ใช้ 3 ทาง:
  REST Web API, **MCP server** (HTTP `/mcp` และ stdio) และ **AIFunction** ของ Microsoft.Extensions.AI
- ฐานข้อมูล **PostgreSQL** (ค่าเริ่มต้น) หรือ **SQL Server 2022+** ผ่าน Dapper
- ML แบบ pure C# (ไม่ใช้ ML.NET) ใช้ SIMD (`Vector<T>`) และ `Parallel` เมื่อข้อมูลมากพอ
- UI แยกเป็น **Blazor WebAssembly + DevExpress 26.1** โฮสต์โดย API ตัวเดียวกัน
- ความปลอดภัยตั้งแต่การออกแบบ: deny-by-default ตามสิทธิ์ของ AuthServer, เข้ารหัสข้อมูลส่วนบุคคลแบบ PQC (ML-KEM/ML-DSA ผ่าน Common), container แบบ distroless/read-only

## สารบัญ

1. [ความสามารถ](#ความสามารถ)
2. [โครงสร้างโซลูชัน](#โครงสร้างโซลูชัน)
3. [เริ่มใช้งานบนเครื่อง (Development)](#เริ่มใช้งานบนเครื่อง-development)
4. [ฐานข้อมูล: PostgreSQL หรือ SQL Server](#ฐานข้อมูล-postgresql-หรือ-sql-server)
5. [Docker / docker compose (hardened)](#docker--docker-compose-hardened)
6. [ตัวอย่าง cURL](#ตัวอย่าง-curl)
7. [AI: Microsoft.Extensions.AI และ MCP](#ai-microsoftextensionsai-และ-mcp)
8. [SLA และการเชื่อมกับ IssueTracking](#sla-และการเชื่อมกับ-issuetracking)
9. [สิทธิ์ (permissions)](#สิทธิ์-permissions)
10. [การทดสอบ](#การทดสอบ)
11. [ความปลอดภัย](#ความปลอดภัย)
12. [API catalog](#api-catalog)

## ความสามารถ

| กลุ่ม | รายละเอียด |
|---|---|
| ทะเบียนลูกค้า | บุคคล / นิติบุคคล, เลขผู้เสียภาษีเข้ารหัส + ตรวจซ้ำด้วย blind index, ค้นหาภาษาไทย, มุมมอง 360° |
| ความสัมพันธ์ | บุคคล ↔ บริษัท พร้อมตำแหน่ง แผนก ผู้ติดต่อหลัก/ผู้ตัดสินใจ และช่วงเวลา (เริ่ม–สิ้นสุด) |
| Omnichannel | ศูนย์รวมแชท LINE OA (ตรวจ `X-Line-Signature`), อีเมล, Facebook/เว็บแชทผ่าน webhook กลาง; ตอบกลับผ่าน MessageHub; วิเคราะห์อารมณ์ทุกข้อความ |
| กิจกรรม | นัดหมาย/โทร/งาน, ตั้งเตือน, สร้างการประชุมใน MessageHub, วาระและงานค้าง (agenda / pending) |
| คำร้อง / TOR | state machine, ส่งต่อแบบ idempotent ไป ProjectMgnt (สร้างโครงการ) และ RequirementMgnt (TOR → requirement), incident ไป IssueTracking พร้อม severity ของลูกค้า, ลายเซ็นดิจิทัล PQC |
| SLA | นโยบาย SLA แยกตามลูกค้า, severity ที่ลูกค้ากำหนดเอง, เวลาทำการ/วันหยุด, compliance (Met/Breached/AtRisk/OnTrack) |
| ภาพรวม | อายุ issue ตาม severity, portfolio โครงการของลูกค้า, pipeline / leads |
| สมาชิกและโปรโมชัน | สมัคร/ยืนยัน OTP, Tier, คะแนนแบบ lot (FEFO, หมดอายุ, คืนตามสัดส่วนสะสม), แคมเปญ 7 แบบ + คูปอง + Segment, POS checkout ผ่าน API (quote → reserve → commit, idempotent), อนุมัติแบบ maker-checker, PDPA — Back Office และ Member Portal ที่ `/membership/` (สัญญา API: `Claude.MD` ส่วนที่ 2) |
| ML | lead scoring, sales forecast (Holt-Winters), next best action, sentiment, dynamic segmentation (k-means++), recommendation (item CF), send-time optimization, churn prediction, MA renewal + upsell, seasonal budget forecast, คะแนนความเสี่ยงลูกค้า |

## โครงสร้างโซลูชัน

```
CustomerMgnt.slnx
├─ src/
│  ├─ CustomerMgnt.Core          business layer (pure C#): *Service, *DTO, I*DAO, ML, SLA, security
│  ├─ CustomerMgnt.Data          *ADO (Dapper) สำหรับ PostgreSQL / SQL Server + in-memory, migration scripts (Sql/postgres, Sql/sqlserver)
│  ├─ CustomerMgnt.Integrations  gateway ไป ProjectMgnt / IssueTracking / RequirementMgnt / MessageHub (ส่งต่อ bearer token ของผู้เรียก)
│  ├─ CustomerMgnt.AI            AIFunction ของ business layer → ใช้ได้ทั้ง IChatClient และ MCP
│  ├─ CustomerMgnt.Api           REST API + MCP over HTTP (/mcp) + โฮสต์ UI
│  ├─ CustomerMgnt.Mcp           MCP server แบบ stdio (Claude Desktop / IDE)
│  ├─ CustomerMgnt.Web           Blazor WebAssembly + DevExpress 26.1
│  └─ CustomerMgnt.Membership.Web Blazor WebAssembly ระบบสมาชิก (Back Office + Member Portal) ด้วย webcommon → /membership/
├─ samples/CustomerMgnt.AiSample  ตัวอย่าง IChatClient + UseFunctionInvocation (รันได้โดยไม่ต้องมี API key)
├─ tests/CustomerMgnt.Tests       MSTest (unit + API end-to-end + integration กับฐานข้อมูลจริง)
├─ deploy/                        Dockerfile, docker-compose (Postgres / SQL Server), generate-secrets.sh
├─ docs/api-catalog/              API catalog (REST + AI tools) ที่ export จากโค้ด
├─ external/common                git submodule: mrpichet/common
└─ external/webcommon             git submodule: mrpichet/webcommon (Blazor UI กลาง)
```

หลักการตั้งชื่อ: `*DTO` (ข้อมูลส่งต่อ), `I*DAO` (สัญญาการเข้าถึงข้อมูลใน Core), `*ADO` (Dapper implementation ใน Data), `*Service` (business logic, pure C#)
controller และ MCP tool เป็นเพียงตัวแปลง HTTP/JSON-RPC ↔ service จึงมี logic และการตรวจสิทธิ์ชุดเดียวกัน

## เริ่มใช้งานบนเครื่อง (Development)

ต้องมี .NET SDK 10.0.100+

```bash
git clone --recurse-submodules https://github.com/mrpichet/customerMgnt.git
cd customerMgnt

dotnet build CustomerMgnt.slnx
dotnet run --project src/CustomerMgnt.Api -p:IncludeWebUi=true   # http://localhost:5180 (มี UI)
dotnet run --project src/CustomerMgnt.Api                        # API อย่างเดียว ไม่ต้องใช้ DevExpress
```

- โหมด Development ใช้ **ฐานข้อมูลในหน่วยความจำ** และคีย์เข้ารหัสชั่วคราว (`appsettings.Development.json`) จึงรันได้ทันที
  นอก Development แอปจะไม่ยอมเริ่มถ้าไม่มี `Database:ConnectionString` (กันข้อมูลหายเพราะ deploy ผิดค่า)
- การเข้าสู่ระบบใช้ AuthServer: ตั้ง `AuthServer:Authority` ให้ชี้ไป AuthServer ของคุณ แล้ว UI จะ login ผ่าน `/api/session/login` (ส่งต่อไป AuthServer `api/v1/auth/login`, รองรับ OTP ของผู้ใช้ที่เปิด MFA)
- OpenAPI (เฉพาะ Development): `GET /openapi/v1.json`
- ระบบสมาชิก: Back Office `http://localhost:5180/membership/` และ Member Portal `/membership/portal/` (ไม่ต้องใช้ DevExpress; ปิดด้วย `-p:IncludeMembershipUi=false`)
  ตอนพัฒนาตั้ง `Membership__OtpFixedCodeForDevelopment=123456` เพื่อใช้ OTP คงที่โดยไม่ส่ง SMS (ใช้ได้เฉพาะ Development)
  login ของสมาชิกด้วย Google/Facebook ตั้งที่ `src/CustomerMgnt.Membership.Web/wwwroot/appsettings.json` (`Portal:GoogleClientId`, `Portal:FacebookAppId`)
- DevExpress: build ได้โดยไม่มี license (มี warning DX1000/DX1001 และแถบ evaluation บน UI) — ใส่ license key ตามวิธีของ DevExpress
  (`DevExpress_License.txt` หรือตัวแปร `DevExpress_License`) ห้าม commit license ลง repo

ตั้งค่าที่ใช้บ่อย (ใส่ใน user-secrets หรือ environment variable เช่น `Integrations__IssueTracking__BaseUrl`):

| คีย์ | ความหมาย |
|---|---|
| `Database:Provider` | `Postgres` (ค่าเริ่มต้น) หรือ `SqlServer` |
| `Database:ConnectionString` | connection string |
| `AuthServer:Authority` / `AuthServer:Audience` | AuthServer ที่ออก JWT |
| `AuthServer:LoginClientId` | OAuth client ของหน้าจอหลักใน AuthServer (ค่าเริ่มต้น `customermgnt-web`) ที่ `/api/session/login` ใช้ |
| `Integrations:{IssueTracking,ProjectMgnt,RequirementMgnt,MessageHub}:BaseUrl` | ปล่อยว่าง = ปิดการเชื่อมระบบนั้น |
| `Integrations:ServiceAccount:*` | บัญชีบริการสำหรับงานเบื้องหลัง (เตือนนัดหมาย, ฝึกโมเดล) ที่ไม่มี token ของผู้ใช้ |
| `Privacy:BlindIndexKey` | คีย์ HMAC (base64 ≥ 32 byte) สำหรับตรวจซ้ำเลขผู้เสียภาษี/อีเมล — ห้ามเปลี่ยนหลังมีข้อมูล |
| `Pqc:*` | คีย์ ML-DSA / ML-KEM / ECDSA / ECDH / HMAC ของ Common |
| `Webhooks:LineChannelSecret` / `Webhooks:InboundSecret` | secret สำหรับตรวจลายเซ็น webhook |
| `Compute:UseSimd` / `Compute:UseParallel` | เปิด/ปิด SIMD และ parallel ของงาน ML |

## ฐานข้อมูล: PostgreSQL หรือ SQL Server

เลือกด้วย `Database:Provider` — schema แยกสคริปต์ตาม provider (`src/CustomerMgnt.Data/Sql/postgres`, `Sql/sqlserver`) และ migration รันอัตโนมัติเมื่อ `Database:RunMigrations=true` (ล็อกด้วย advisory lock / `sp_getapplock` กันหลาย instance รันพร้อมกัน)

```jsonc
// PostgreSQL 14+
"Database": { "Provider": "Postgres",  "ConnectionString": "Host=db;Database=customermgnt;Username=customermgnt;Password=..." }
// SQL Server 2022+ (ใช้ IS DISTINCT FROM, OPENJSON)
"Database": { "Provider": "SqlServer", "ConnectionString": "Server=db,1433;Database=customermgnt;User Id=customermgnt;Password=...;Encrypt=True" }
```

ADO ทุกตัวใช้ SQL ที่ทำงานได้ทั้งสองฐาน และใช้ตัวช่วย dialect เฉพาะจุดที่ต่างกัน (`RETURNING` ↔ `OUTPUT INSERTED`, `LIMIT` ↔ `OFFSET/FETCH`, `= ANY(@arr)` ↔ `OPENJSON`, upsert ↔ `MERGE WITH (HOLDLOCK)`)

## Docker / docker compose (hardened)

```bash
./deploy/generate-secrets.sh                                    # สร้าง deploy/secrets/*.txt + deploy/.env (ไม่เขียนทับของเดิม)
docker compose -f deploy/docker-compose.yml up -d --build       # API + PostgreSQL 17 → http://127.0.0.1:8095

# ใช้ SQL Server 2022 แทน PostgreSQL
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.sqlserver.yml up -d --build

# รัน unit test ใน container
docker build -f deploy/Dockerfile --target test .

# ใส่ DevExpress license ตอน build (ส่งเป็น BuildKit secret — ไม่อยู่ใน layer ของ image)
DEVEXPRESS_LICENSE="<key>" ./deploy/generate-secrets.sh   # หรือเขียนลง deploy/secrets/devexpress_license.txt เอง
```

สิ่งที่ทำไว้:

- image: build ด้วย `sdk:10.0-resolute` (OpenSSL 3.5 → PQC ใช้งานได้จริง), runtime `aspnet:10.0-resolute-chiseled-extra` (distroless, ไม่มี shell, non-root uid 1654), ไฟล์แอป read-only `0555`, ไม่มี pdb, HEALTHCHECK ผ่าน `--health-probe` (ไม่ต้องมี curl)
- compose: `read_only`, `cap_drop: [ALL]`, `no-new-privileges`, จำกัด CPU/RAM/PIDs, tmpfs สำหรับ `/tmp`, เปิด port เฉพาะ `127.0.0.1`, ฐานข้อมูลอยู่ใน network `internal` (ไม่ออก internet และไม่เปิด port), secret เป็นไฟล์ (`/run/secrets/<Config__Key>`) ไม่อยู่ใน env หรือ image
- SQL Server: แอปใช้ login `customermgnt` ของตัวเอง (สร้างโดย `sqlserver-init`) ไม่ใช้ `sa`; คอนเทนเนอร์ได้ capability เดียวคือ `NET_BIND_SERVICE` ที่ `sqlservr` ต้องใช้
- production: วางหลัง reverse proxy ที่ทำ TLS และตั้ง `AUTHSERVER_AUTHORITY`, `AUTHSERVER_JWKS_URI`, `LOGSERVER_URLS`, `ISSUETRACKING_URL`, `PROJECTMGNT_URL`, `REQUIREMENTMGNT_URL`, `MESSAGEHUB_URL`, `SERVICE_ACCOUNT_LOGIN`
- หมายเหตุ: log `libgssapi_krb5.so.2: cannot open shared object file` ตอนเริ่มมาจาก Npgsql ที่ลองโหลด Kerberos (image chiseled ไม่มี) — ไม่กระทบการใช้รหัสผ่านแบบ SCRAM

## ตัวอย่าง cURL

ทุก endpoint (ยกเว้น login, webhook และ health) ต้องมี `Authorization: Bearer <token>` จาก AuthServer และสิทธิ์ (`perm`) ตามตาราง [API catalog](docs/api-catalog/api-catalog.md)
ข้อผิดพลาดตอบเป็น `application/problem+json` (RFC 9457) เช่น 400 validation, 401, 403, 404, 409 (ข้อมูลซ้ำ/version ไม่ตรง)

```bash
BASE=http://localhost:5180

# 1) เข้าสู่ระบบ (ส่งต่อไป AuthServer) แล้วเก็บ token
TOKEN=$(curl -s -X POST $BASE/api/session/login -H 'Content-Type: application/json' \
  -d '{"login":"alice","password":"***"}' | jq -r .accessToken)
AUTH="Authorization: Bearer $TOKEN"

# 2) ลงทะเบียนนิติบุคคล และบุคคล
curl -s -X POST $BASE/api/customers -H "$AUTH" -H 'Content-Type: application/json' -d '{
  "kind":"Juristic","legalName":"บริษัท ตัวอย่าง จำกัด","taxId":"0105551234567",
  "country":"TH","timeZoneId":"Asia/Bangkok","status":"Active","industry":"Manufacturing"}'
curl -s -X POST $BASE/api/customers -H "$AUTH" -H 'Content-Type: application/json' -d '{
  "kind":"Person","firstName":"สมชาย","lastName":"ใจดี","email":"somchai@example.com",
  "country":"TH","timeZoneId":"Asia/Bangkok","status":"Active","marketingConsent":true}'

# 3) ผูกบุคคล (id 2) กับบริษัท (id 1) พร้อมตำแหน่งและช่วงเวลา
curl -s -X POST $BASE/api/relationships -H "$AUTH" -H 'Content-Type: application/json' -d '{
  "personId":2,"organizationId":1,"position":"IT Manager","isPrimaryContact":true,
  "isDecisionMaker":true,"validFrom":"2026-01-01"}'

# 4) ค้นหา และดูภาพรวม 360°
curl -s "$BASE/api/customers?text=ตัวอย่าง&page=1&pageSize=20" -H "$AUTH"
curl -s $BASE/api/customers/1/360 -H "$AUTH"

# 5) คำร้อง/TOR → ส่งต่อไป ProjectMgnt + RequirementMgnt (เรียกซ้ำได้ ไม่สร้างซ้ำ)
curl -s -X POST $BASE/api/requests -H "$AUTH" -H 'Content-Type: application/json' -d '{
  "customerId":1,"kind":"Tor","title":"ระบบ ERP ระยะที่ 2","budget":2500000,
  "items":[{"itemNo":"1.1","text":"รองรับการออกใบกำกับภาษีอิเล็กทรอนิกส์","priority":"High","mandatory":true}]}'
curl -s -X POST $BASE/api/requests/1/forward -H "$AUTH" -H 'Content-Type: application/json' -d '{
  "toProjectMgnt":true,"toRequirementMgnt":true,"projectCode":"ERP2","projectManagerId":7}'

# 6) SLA: กำหนดเวลาตอบสนอง/แก้ไขของลูกค้าตาม severity (IssueTracking เรียก endpoint นี้)
curl -s -X POST $BASE/api/sla/calculate -H "$AUTH" -H 'Content-Type: application/json' -d '{
  "customerCode":"CUST-000001","severityCode":"S1","startedAt":"2026-11-23T09:30:00+07:00"}'

# 7) อายุ issue ตาม severity, pipeline, งานค้างของฉัน
curl -s "$BASE/api/insights/issues/aging?customerId=1" -H "$AUTH"
curl -s $BASE/api/opportunities/summary -H "$AUTH"
curl -s "$BASE/api/activities/mine?days=7" -H "$AUTH"

# 8) ML
curl -s "$BASE/api/analytics/leads?top=20" -H "$AUTH"
curl -s "$BASE/api/analytics/churn?minProbability=0.5" -H "$AUTH"
curl -s "$BASE/api/analytics/forecast/sales?horizonMonths=6" -H "$AUTH"
curl -s $BASE/api/analytics/nba/1 -H "$AUTH"
curl -s -X POST $BASE/api/analytics/sentiment -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"text":"ระบบล่มอีกแล้ว ช้ามาก ไม่พอใจเลย"}'

# 9) webhook กลาง (อีเมล/เว็บแชท) — ลงลายเซ็น HMAC-SHA256 ของ body ด้วย Webhooks:InboundSecret
BODY='{"channel":"Email","externalUserId":"somchai@example.com","subject":"สอบถาม","body":"ขอใบเสนอราคา"}'
SIG=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$INBOUND_SECRET" -hex | sed 's/^.* //')
curl -s -X POST $BASE/api/webhooks/inbound -H 'Content-Type: application/json' -H "X-Signature: sha256=$SIG" -d "$BODY"
```

## AI: Microsoft.Extensions.AI และ MCP

ฟังก์ชันของ business layer ถูกห่อเป็น `AIFunction` ชุดเดียว (`CustomerMgnt.AI`) แล้วนำไปใช้ทั้งกับ `IChatClient` และ MCP server
ทุก tool เรียกผ่าน service ตัวเดียวกับ REST จึงตรวจสิทธิ์ของผู้ใช้เหมือนกัน (ผ่าน HTTP = ผู้ใช้จาก JWT, stdio = ตัวตนใน `Mcp:Identity`)

tools หลัก: `calculate_customer_risk`, `assess_portfolio_risk`, `predict_churn`, `score_leads`, `forecast_renewals`, `forecast_sales`,
`forecast_seasonal_budget`, `recommend_next_best_action`, `recommend_products`, `get_best_send_time`, `segment_customers`, `analyze_sentiment`,
`search_customers`, `get_customer_360`, `get_issue_aging`, `get_sla_compliance`, `get_sla_policy`, `calculate_sla_targets`, `get_project_portfolio`,
`get_pipeline_summary`, `get_customer_agenda`, `list_customer_requests`, `create_follow_up`, `create_customer_request` (+ tools กลางของ Common เช่น risk matrix)

### Microsoft.Extensions.AI (IChatClient)

```bash
dotnet run --project samples/CustomerMgnt.AiSample   # ใช้โมเดลจำลอง ไม่ต้องมี API key
```

```csharp
// ลงทะเบียน business layer + tools (ดู samples/CustomerMgnt.AiSample/Program.cs)
services.AddCustomerMgntCore(config).AddCustomerMgntData(config).AddCustomerMgntIntegrations(config);
services.AddSingleton<IAiUserContextAccessor, ConfiguredAiUserContextAccessor>(); // หรือผู้ใช้จาก HTTP
services.AddCustomerMgntAiTools();

// ใช้กับ IChatClient ของผู้ให้บริการใดก็ได้ (Azure OpenAI, Ollama, Anthropic ฯลฯ)
IChatClient client = new ChatClientBuilder(innerClient).UseFunctionInvocation().Build();
var tools = sp.GetRequiredService<CustomerMgntAiTools>();
ChatResponse answer = await client.GetResponseAsync(
    "ลูกค้า CUST-000001 เสี่ยงจะเลิกใช้บริการไหม และควรทำอะไรต่อ?",
    new ChatOptions { Tools = tools.AsTools() });

// หรือเรียก tool ตรง ๆ
object? sla = await tools.Functions["calculate_sla_targets"].InvokeAsync(new AIFunctionArguments
{
    ["customerCode"] = "CUST-000001", ["severityCode"] = "S1",
});
```

### MCP over HTTP (`/mcp`)

ใช้ token ของ AuthServer (ต้องมีสิทธิ์ `analytics.read`; tool แต่ละตัวตรวจสิทธิ์เพิ่มเติมของตัวเอง)

```bash
curl -s -X POST $BASE/mcp -H "$AUTH" -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_sentiment","arguments":{"text":"บริการดีมาก ขอบคุณครับ"}}}'
```

### MCP stdio (Claude Desktop / VS Code)

```json
{
  "mcpServers": {
    "customermgnt": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/customerMgnt/src/CustomerMgnt.Mcp"],
      "env": {
        "Database__ConnectionString": "Host=localhost;Database=customermgnt;Username=readonly;Password=...",
        "Privacy__BlindIndexKey": "<base64>",
        "Mcp__Identity__Subject": "ai-desk"
      }
    }
  }
}
```

ค่าเริ่มต้นของ stdio มีเฉพาะสิทธิ์อ่าน (`customer.read`, `sales.read`, `sla.read`, `request.read`, `chat.read`, `analytics.read`) — เพิ่ม `activity.write` / `request.write` ใน `Mcp:Identity:Permissions` เมื่อต้องการให้ AI สร้างงานติดตามหรือคำร้องได้
โหมดทดลองไม่มีฐานข้อมูล: ตั้ง `DOTNET_ENVIRONMENT=Development` (ข้อมูลในหน่วยความจำ)

## SLA และการเชื่อมกับ IssueTracking

- นโยบาย SLA ผูกกับลูกค้าได้ (ไม่ผูก = นโยบายมาตรฐาน) มี severity ที่ลูกค้ากำหนดเอง: รหัส, ลำดับ 1–5, เวลาตอบสนอง/แก้ไข (นาที), นับเฉพาะเวลาทำการหรือ 24×7, priority ที่ map ไป IssueTracking
- เวลาทำการ: เขตเวลา, ช่วงเวลา, วันทำงาน (bitmask: bit0 = จันทร์ … bit6 = อาทิตย์; 31 = จันทร์–ศุกร์), วันหยุด
- CustomerMgnt เป็นแหล่งข้อมูลหลักของ SLA: IssueTracking เก็บ customer code + severity + เวลาครบกำหนด และเรียก `POST /api/sla/calculate` ตอนสร้าง issue
  (การแก้ไขฝั่ง IssueTracking อยู่ใน PR แยกของ mrpichet/issuetracking)
- `GET /api/sla/customers/{id}/compliance` สรุป Met / Breached / AtRisk / OnTrack จาก issue จริงใน IssueTracking

## สิทธิ์ (permissions)

สิทธิ์มาจาก claim `perm` (หรือ `scope`) ใน JWT ของ AuthServer — endpoint ที่ไม่ได้ระบุจะถูกปฏิเสธโดยค่าเริ่มต้น

| สิทธิ์ | ใช้กับ |
|---|---|
| `customer.read` / `customer.write` | ทะเบียนลูกค้า ความสัมพันธ์ สัญญา |
| `chat.read` / `chat.reply` | ศูนย์รวมแชท |
| `activity.write` | กิจกรรม นัดหมาย |
| `request.read` / `request.write` / `request.forward` | คำร้อง/TOR และการส่งต่อ |
| `sales.read` / `sales.write` | leads / pipeline |
| `sla.read` / `sla.manage` | นโยบาย SLA |
| `analytics.read` | ML, AI tools, MCP |
| `data.ingest` | นำเข้าเหตุการณ์/แคตตาล็อก, ฝึกโมเดลใหม่ |

## การทดสอบ

```bash
dotnet test --project tests/CustomerMgnt.Tests
```

- unit test ของ service (SLA, workflow คำร้อง, ML: SIMD เทียบ scalar, k-means, Holt-Winters, NBA, sentiment) ใช้ข้อมูลในหน่วยความจำและ `FakeTimeProvider`
- API end-to-end ผ่าน `WebApplicationFactory` (401/403/400 ProblemDetails, webhook ไม่มีลายเซ็น → 401 ฯลฯ)
- ระบบสมาชิก (`tests/CustomerMgnt.Tests/Membership`): สูตรคะแนนตามตัวอย่างใน spec (132 คะแนน, คืนสินค้า 30 + 102), promotion engine, กฎสถานะ/Tier/Segment และ end-to-end: กฎคะแนน → อนุมัติ (ห้ามอนุมัติเอง) → สมัครสมาชิก → POS checkout → commit ซ้ำด้วย key เดิม → คืนสินค้า
- integration test กับฐานข้อมูลจริง (ข้ามอัตโนมัติถ้าไม่ตั้งตัวแปร):

```bash
export CUSTOMERMGNT_TEST_POSTGRES="Host=localhost;Database=customermgnt_test;Username=postgres;Password=..."
export CUSTOMERMGNT_TEST_SQLSERVER="Server=localhost,1433;Database=customermgnt_test;User Id=sa;Password=...;TrustServerCertificate=True"
dotnet test --project tests/CustomerMgnt.Tests
```

## ความปลอดภัย

- **Authentication/authorization**: JWT จาก AuthServer, policy ตามสิทธิ์ราย endpoint, fallback policy บังคับ login; service ตรวจสิทธิ์ซ้ำอีกชั้น (ใช้ได้กับ MCP/AI ด้วย)
- **ข้อมูลส่วนบุคคล**: เลขผู้เสียภาษี/บัตรประชาชนเข้ารหัสแบบ hybrid PQC (ML-KEM + ECDH) และค้นหา/ตรวจซ้ำด้วย blind index (HMAC); แสดงผลแบบ mask
- **Webhook**: ตรวจ HMAC-SHA256 แบบ constant-time (LINE `X-Line-Signature`, ระบบอื่น `X-Signature: sha256=`), จำกัดขนาด body
- **คำร้อง/TOR**: ลงลายเซ็นดิจิทัล (ML-DSA) และตรวจสอบได้ที่ `GET /api/requests/{id}/verify-signature`
- **Hardening**: rate limit (login แบบเข้ม), security headers + CSP ของ UI, ProblemDetails ไม่เปิดเผย stack trace, SQL ทุกคำสั่งใช้ parameter (Dapper), NuGet audit ทุกระดับ
- **ไม่เก็บ secret ใน repo**: ใช้ user-secrets / environment / Docker secrets (`deploy/secrets/` อยู่ใน `.gitignore`)

## API catalog

[`docs/api-catalog/api-catalog.md`](docs/api-catalog/api-catalog.md) และ `api-catalog.json` (REST + AI tools พร้อม JSON schema) สร้างจากโค้ดด้วย

```bash
dotnet run --project src/CustomerMgnt.Api -- --export-catalog docs/api-catalog
```

## API ของระบบอื่นที่ใช้

| ระบบ | ใช้ทำอะไร |
|---|---|
| AuthServer | login (`/api/auth/login`), JWKS สำหรับตรวจ JWT, บัญชีบริการ |
| ProjectMgnt | สร้างโครงการจากคำร้อง, อ่านสถานะ/ความคืบหน้าโครงการของลูกค้า |
| RequirementMgnt | สร้างโครงการ requirement และนำเข้ารายการ TOR |
| IssueTracking | สร้าง incident พร้อม severity ของลูกค้า, อ่าน issue สำหรับ aging และ SLA compliance |
| MessageHub | ส่งข้อความตอบกลับ (LINE/อีเมล/SMS), สร้างการประชุม, เตือนนัดหมาย, วาระประชุม |
| LogServer | รับ log แบบมีโครงสร้าง (ผ่าน Common.Logging) |
