# CustomerMgnt — API reference สำหรับ Claude

ไฟล์นี้สรุป **ทุก endpoint / MCP tool ที่ CustomerMgnt เปิดให้ระบบอื่นเรียก** พร้อมสิทธิ์ที่ต้องใช้ ข้อมูลขาเข้า และรูปแบบข้อมูลที่คืนกลับ
สร้างจากโค้ดจริงใน `src/CustomerMgnt.Api/Controllers/*.cs`, `src/CustomerMgnt.Api/Program.cs`, `src/CustomerMgnt.Core/Domain/*.cs`,
`src/CustomerMgnt.Core/Security/Permissions.cs` และ `src/CustomerMgnt.AI/CustomerMgntAIFunctions.cs` — **ถ้าแก้ controller / DTO / สิทธิ์ ให้แก้ไฟล์นี้ด้วย**

> สำหรับ Claude ใน project อื่น: ใช้ไฟล์นี้เป็นสัญญา (contract) ตอนเขียน client ที่เรียก CustomerMgnt
> อย่าเดา endpoint หรือ field ที่ไม่มีในไฟล์นี้ อย่าส่งหรือเก็บ token, password, เลขผู้เสียภาษี หรือ secret ลง log
> catalog ที่สร้างอัตโนมัติอยู่ที่ `docs/api-catalog/api-catalog.md` (`dotnet run --project src/CustomerMgnt.Api -- --export-catalog docs/api-catalog`)

---

## 1. ภาพรวม

| หัวข้อ | รายละเอียด |
|---|---|
| Base URL | URL ของ CustomerMgnt.Api (compose: `http://127.0.0.1:8095`) — UI Blazor ถูก publish ไว้ใน `wwwroot` ของ API เดียวกัน |
| Version | REST path ปัจจุบันขึ้นต้นด้วย `api/` **ยังไม่มี version segment** (ดู §12 แผนย้ายไป `api/v1`) — `/mcp` และ `/health/*` ไม่มี version |
| รูปแบบ | JSON, property **camelCase**, enum ส่งเป็น **ข้อความ** (`"Active"`), `DateTimeOffset` ISO-8601, `DateOnly` = `yyyy-MM-dd`, `TimeOnly` = `HH:mm:ss` |
| Auth | `Authorization: Bearer <access_token ของ AuthServer>` — ตรวจด้วย `Common.AspNetCore` (`AddAuthServerAuthentication`, JWKS) |
| Deny by default | ทุก endpoint ต้อง login + มีสิทธิ์ตามตาราง ยกเว้น `POST api/session/login`, `api/webhooks/*` (ตรวจลายเซ็นแทน), `/health/*` |
| Rate limit | global: token bucket ต่อผู้ใช้ (หรือ IP ถ้าไม่ login) `RateLimit:PermitsPerSecond` ค่าเริ่มต้น 200/วินาที; policy `strict` 20 คำขอ/นาที ใช้กับ login และ webhook → เกินได้ **429** |
| ขนาด body | สูงสุด 16 MB (webhook 1 MB) |
| Concurrency | DTO ที่แก้ไขได้มี `version` (optimistic lock) — ส่ง `version` ล่าสุดกลับมาตอนแก้ไข ถ้าไม่ตรงได้ **409** |
| Tolerant reader | client ต้องไม่ล้มเมื่อเจอ field หรือ enum ค่าใหม่ |

### 1.1 สิทธิ์ (permission)

ชื่อสิทธิ์อ่านจาก claim `perm` / `permissions` (token ผู้ใช้) หรือ `scope` (client_credentials) ของ AuthServer — endpoint ที่ไม่ได้ระบุสิทธิ์ถูกปฏิเสธ

| permission | ใช้กับ |
|---|---|
| `customer.read` | ค้นหา/ดูลูกค้า, 360, ความสัมพันธ์, สัญญา, กิจกรรม, insights, catalog, profile ตัวเอง |
| `customer.write` | ลงทะเบียน/แก้ลูกค้า, identity, link ภายนอก, ความสัมพันธ์, สัญญา, profile ผู้อื่น |
| `chat.read` / `chat.reply` | ดูบทสนทนา omnichannel / ตอบและอัปเดตบทสนทนา |
| `activity.write` | สร้าง/ปิดกิจกรรม |
| `request.read` / `request.write` / `request.forward` | คำขอลูกค้า (Request/TOR/CR/Incident) ดู / สร้างและตัดสิน / ส่งต่อไประบบอื่น |
| `sales.read` / `sales.write` | pipeline โอกาสขาย |
| `sla.read` / `sla.manage` | คำนวณ/ประเมิน/ดูนโยบาย SLA / สร้างและแก้นโยบาย SLA |
| `analytics.read` | ML/analytics และ **MCP `/mcp`** |
| `data.ingest` | ส่ง engagement event, แก้ catalog, สั่ง retrain |

service-to-service (เช่น IssueTracking) ใช้บัญชีบริการ/OAuth client ของ AuthServer ที่มีสิทธิ์เฉพาะที่ต้องใช้ เช่น `sla.read`

### 1.2 รูปแบบ error

ทุก error เป็น **ProblemDetails (RFC 9457)** พร้อม extension `code` และ `traceId`; validation มี `errors` (field → ข้อความ)

```json
{ "type": "https://httpstatuses.io/404", "title": "Not Found", "status": 404,
  "detail": "Customer 'C-0099' was not found.", "code": "not_found", "traceId": "00-..." }
```

| status | เมื่อไร |
|---|---|
| 400 | ข้อมูลไม่ถูกต้อง (`errors` ระบุ field) |
| 401 | ไม่มี/ token ไม่ถูกต้อง, ลายเซ็น webhook ผิด |
| 403 | ไม่มีสิทธิ์ หรือไม่ใช่เจ้าของข้อมูลที่ต้องเป็นเจ้าของ |
| 404 | ไม่พบข้อมูล |
| 409 | `version` ไม่ตรง (มีคนแก้ก่อน) หรือข้อมูลซ้ำ (เช่น code / เลขผู้เสียภาษีซ้ำ) |
| 429 | เกิน rate limit |
| 503 | ระบบที่พึ่งพา (ProjectMgnt / IssueTracking / MessageHub ฯลฯ) ใช้ไม่ได้ |
| 500 | ข้อผิดพลาดภายใน (ใช้ `traceId` ติดตาม) |

### 1.3 ข้อมูลส่วนบุคคล

- เลขผู้เสียภาษี/บัตรประชาชน **ไม่ถูกส่งออกทาง API** มีแต่ `taxIdMasked`; เก็บแบบเข้ารหัส PQC hybrid + blind index (ตรวจซ้ำโดยไม่ถอดรหัส)
- อย่าเก็บ `email` / `phone` ที่ได้จาก API ลง log ของระบบอื่น

---

## 2. Session — `api/session`

| Method | Route | สิทธิ์ | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| POST | `/api/session/login` | anonymous, rate limit `strict` | `LoginRequest { login, password }` | `SessionTokenDTO { accessToken, expiresAt }` / 401 |
| GET | `/api/session/me` | login | – | `SessionUserDTO { subject, userName, roles[], permissions[] }` |

> ⚠️ ปัจจุบัน `login` ส่งต่อไป AuthServer ที่ path `api/auth/login` แบบเดิม ซึ่ง AuthServer ย้ายเป็น `api/v1/auth/login` แล้ว
> ระบบอื่น **ควร login กับ AuthServer โดยตรง** แล้วส่ง token มาที่ CustomerMgnt แทนการใช้ endpoint นี้

---

## 3. ลูกค้า — `api/customers`, `api/relationships`, `api/contracts`, `api/products`

| Method | Route | สิทธิ์ | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| GET | `/api/customers` | customer.read | query `CustomerSearchCriteria` | `PagedResultDTO<CustomerDTO>` |
| POST | `/api/customers` | customer.write | `RegisterCustomerRequest` | `CustomerDTO` (201) / 409 ซ้ำ |
| GET | `/api/customers/{id}` | customer.read | – | `CustomerDTO` |
| GET | `/api/customers/by-code/{code}` | customer.read | – | `CustomerDTO` |
| PUT | `/api/customers/{id}` | customer.write | `UpdateCustomerRequest` (ต้องมี `version`) | `CustomerDTO` |
| GET | `/api/customers/{id}/360` | customer.read | – | `Customer360DTO` |
| POST | `/api/customers/{id}/identities` | customer.write | `ChannelIdentityRequest` | `ChannelIdentityDTO` |
| GET | `/api/customers/{id}/relationships?activeOn=` | customer.read | – | `PartyRelationshipDTO[]` |
| POST | `/api/customers/{id}/links` | customer.write | `ExternalLinkRequest` | `ExternalLinkDTO` |
| GET | `/api/customers/{id}/projects` | customer.read | – | `ProjectPortfolioDTO` (ดึงจาก ProjectMgnt) |
| GET | `/api/customers/{id}/contracts` | customer.read | – | `ContractDTO[]` |
| GET | `/api/customers/{customerId}/agenda` | customer.read | – | `CustomerAgendaDTO` (ประชุมจาก MessageHub + กิจกรรมค้าง) |
| POST | `/api/relationships` | customer.write | `CreateRelationshipRequest` | `PartyRelationshipDTO` |
| POST | `/api/relationships/{id}/end` | customer.write | `EndRelationshipRequest { validTo }` | `PartyRelationshipDTO` |
| POST | `/api/contracts` | customer.write | `ContractRequest` | `ContractDTO` |
| GET | `/api/products/{code}/customers` | customer.read | – | `ProductCustomerDTO[]` (ลูกค้าที่มีสัญญาของสินค้านี้ — ใช้โดย ProductMgnt) |

```jsonc
// CustomerSearchCriteria (query string)
?text=&kind=Person|Juristic&status=Prospect|Active|Inactive|Churned&ownerSubject=&segment=&page=1&pageSize=20

// PagedResultDTO<T> (Common)
{ "items": [ ... ], "total": 135, "page": 1, "pageSize": 20, "totalPages": 7 }

// CustomerDTO
{ "id": 12, "code": "C-0012", "kind": "Person", "displayName": "สมชาย ใจดี", "legalName": null, "firstName": "สมชาย", "lastName": "ใจดี",
  "taxIdMasked": "1-2345-xxxxx-xx-1", "email": "...", "phone": "...", "industry": null, "employeeCount": null, "annualRevenue": null,
  "website": null, "address": null, "province": "กรุงเทพมหานคร", "country": "TH", "timeZoneId": "Asia/Bangkok",
  "status": "Active", "ownerSubject": "u-1", "tags": "vip", "segment": "S2", "marketingConsent": true,
  "createdAt": "...", "updatedAt": "...", "version": 3 }

// RegisterCustomerRequest
{ "kind": "Person", "code": null /* ว่าง = ระบบออกให้ */, "firstName": "", "lastName": "", "legalName": null, "displayName": null,
  "taxId": "1234567890121", "email": null, "phone": null, "industry": null, "employeeCount": null, "annualRevenue": null,
  "website": null, "address": null, "province": null, "country": "TH", "timeZoneId": "Asia/Bangkok",
  "status": "Prospect", "ownerSubject": null, "tags": null, "marketingConsent": false }

// UpdateCustomerRequest — ส่งเฉพาะ field ที่จะแก้ (null = ไม่เปลี่ยน)
{ "version": 3, "displayName": null, "email": null, "phone": null, "industry": null, "employeeCount": null, "annualRevenue": null,
  "address": null, "status": null, "ownerSubject": null, "tags": null, "marketingConsent": null }

// Customer360DTO
{ "customer": CustomerDTO, "relationships": PartyRelationshipDTO[], "identities": ChannelIdentityDTO[], "openConversations": 1,
  "overdueActivities": 0, "openRequests": 2, "openOpportunities": OpportunityDTO[], "links": ExternalLinkDTO[], "slaPolicyName": "Gold" }

// ChannelIdentityRequest / ChannelIdentityDTO
{ "channel": "Line", "externalId": "Uxxxx", "displayName": null }   // DTO เพิ่ม id, customerId, verified

// ExternalLinkRequest / ExternalLinkDTO
{ "system": "IssueTracking|ProjectMgnt|RequirementMgnt|MessageHub", "entityType": "issue (≤40)", "externalId": "123 (≤64)", "title": null }
// DTO เพิ่ม id, customerId, customerRequestId, createdAt

// CreateRelationshipRequest / PartyRelationshipDTO
{ "personId": 1, "organizationId": 2, "position": "CTO", "department": null, "isPrimaryContact": true, "isDecisionMaker": true,
  "validFrom": "2026-01-01", "validTo": null }   // DTO เพิ่ม id, personName, organizationName

// ContractRequest / ContractDTO
{ "customerId": 2, "contractNo": "CT-001", "title": "", "startDate": "2026-01-01", "endDate": "2026-12-31", "annualValue": 1200000,
  "slaPolicyId": null, "productCode": null }    // DTO เพิ่ม id, status (Active|Renewed|Expired|Cancelled), renewalCount

// ProductCustomerDTO
{ "customerCode": "C-0002", "customerName": "", "contractNo": "CT-001", "contractEnd": "2026-12-31" }

// ProjectPortfolioDTO
{ "customerCode": "", "active": ExternalProjectDTO[], "closed": ExternalProjectDTO[], "averageProgress": 0.42, "atRisk": 1 }
// ExternalProjectDTO { id, code, name, phase, actualProgress, plannedProgress, health, startDate, forecastEndDate }

// CustomerAgendaDTO
{ "customerCode": "", "upcomingMeetings": ExternalMeetingDTO[], "pendingAgendaItems": ExternalAgendaItemDTO[],
  "openActivities": ActivityDTO[], "overdueCount": 0 }
// ExternalMeetingDTO { id (Guid), title, startUtc, endUtc, status, agenda[] } / ExternalAgendaItemDTO { id, title, status, conclusion, ownerUserId }
```

---

## 4. กิจกรรม — `api/activities`

| Method | Route | สิทธิ์ | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| POST | `/api/activities` | activity.write | `CreateActivityRequest` | `ActivityDTO` (Meeting + attendees → สร้างประชุมใน MessageHub) |
| POST | `/api/activities/{id}/complete` | activity.write | `CompleteActivityRequest` | `CompleteActivityResultDTO { completed, followUp? }` |
| GET | `/api/activities/mine?days=7` | customer.read | – | `ActivityDTO[]` |
| GET | `/api/activities/customer/{customerId}?openOnly=false` | customer.read | – | `ActivityDTO[]` |

```jsonc
// CreateActivityRequest
{ "customerId": 1, "opportunityId": null, "kind": "Call|Meeting|Email|Chat|Task|Note|Visit", "subject": "", "notes": null,
  "ownerSubject": null, "dueAt": "...", "durationMinutes": null, "remindBeforeMinutes": 30, "status": "Planned",
  "meetingAttendees": [ "<MessageHub user Guid>" ], "meetingAgenda": [ "หัวข้อ" ] }
// CompleteActivityRequest { outcome, notes, followUpInDays }
// ActivityDTO { id, customerId, opportunityId, kind, subject, notes, status (Planned|Done|Cancelled), ownerSubject, dueAt, durationMinutes,
//               remindBeforeMinutes, reminderSentAt, completedAt, outcome, messageHubMeetingId, createdAt }
```

---

## 5. คำขอลูกค้า — `api/requests`

| Method | Route | สิทธิ์ | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| POST | `/api/requests` | request.write | `CreateCustomerRequestRequest` | `CustomerRequestDTO` (201) |
| GET | `/api/requests?customerId=&status=` | request.read | – | `CustomerRequestDTO[]` |
| GET | `/api/requests/{id}` | request.read | – | `CustomerRequestDTO` |
| POST | `/api/requests/{id}/decision` | request.write | `RequestDecisionRequest { version, status, comment }` | `CustomerRequestDTO` |
| POST | `/api/requests/{id}/forward` | request.forward | `ForwardRequestOptions` | `ForwardResultDTO` (สร้างงานใน ProjectMgnt / RequirementMgnt / IssueTracking พร้อมลายเซ็น PQC) |
| GET | `/api/requests/{id}/verify-signature` | request.read | – | `SignatureCheckDTO { requestId, valid }` |

```jsonc
// CreateCustomerRequestRequest — Incident ต้องมี severityCode ที่อยู่ในนโยบาย SLA ของลูกค้า
{ "customerId": 1, "contactPersonId": null, "kind": "Request|Tor|ChangeRequest|Incident", "title": "", "description": null,
  "severityCode": null, "budget": null, "desiredDate": null,
  "items": [ { "itemNo": "1.1", "text": "", "priority": "Must", "mandatory": true } ] }

// CustomerRequestDTO
{ "id": 1, "requestNo": "REQ-2026-0001", "customerId": 1, "contactPersonId": null, "kind": "Tor", "title": "", "description": null,
  "status": "New|UnderReview|Approved|Forwarded|Rejected|Closed", "severityCode": null, "budget": null, "desiredDate": null,
  "ownerSubject": "", "items": [ { "id": 1, "requestId": 1, "itemNo": "1.1", "text": "", "priority": "", "mandatory": true, "requirementKey": null } ],
  "forwardSignature": null, "forwardedAt": null, "createdAt": "...", "updatedAt": "...", "version": 1 }

// ForwardRequestOptions
{ "toProjectMgnt": true, "toRequirementMgnt": true, "toIssueTracking": false, "projectCode": null, "projectManagerId": null,
  "methodology": "Waterfall", "requirementStandard": "IEEE830", "requirementModuleId": null, "issueTrackingProjectId": null, "issueTypeId": 1 }

// ForwardResultDTO { requestId, links: ExternalLinkDTO[], signature, warnings: string[] }
```

---

## 6. โอกาสขาย — `api/opportunities`

| Method | Route | สิทธิ์ | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| POST | `/api/opportunities` | sales.write | `CreateOpportunityRequest` | `OpportunityDTO` |
| GET | `/api/opportunities?customerId=&openOnly=true&mine=false` | sales.read | – | `OpportunityDTO[]` |
| POST | `/api/opportunities/{id}/stage` | sales.write | `MoveStageRequest { version, stage, amount?, lostReason? }` | `OpportunityDTO` |
| GET | `/api/opportunities/summary` | sales.read | – | `PipelineSummaryDTO` |

```jsonc
// CreateOpportunityRequest { customerId, title, amount, probability?, expectedCloseDate, source?, ownerSubject? }
// OpportunityDTO { id, customerId, title, stage (New|Contacted|Qualified|Proposal|Negotiation|Won|Lost), amount, probability, score,
//                  expectedCloseDate, source, ownerSubject, lostReason, stageChangedAt, closedAt, createdAt, version, customerName }
// PipelineSummaryDTO { stages: [ { stage, count, amount, weightedAmount, averageDaysInStage } ], totalOpenAmount, weightedOpenAmount, winRate }
```

---

## 7. SLA — `api/sla` (CustomerMgnt เป็นแหล่งข้อมูลหลักของ SLA และระดับความรุนแรงที่ลูกค้ากำหนด)

| Method | Route | สิทธิ์ | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| POST | `/api/sla/calculate` | sla.read | `SlaCalculateRequest` | `SlaTargetDTO` / 400·404 ไม่มีลูกค้าหรือ severity นี้ |
| POST | `/api/sla/evaluate` | sla.read | `SlaEvaluateRequest` | `SlaEvaluationDTO` |
| GET | `/api/sla/customers/{code}/policy` | sla.read | – | `SlaPolicyDTO` (นโยบายที่มีผล ณ วันนี้) / 404 |
| GET | `/api/sla/customers/{id}/compliance` | sla.read | – | `SlaComplianceDTO` (ดึง issue จาก IssueTracking) |
| GET | `/api/sla/policies?customerId=` | sla.read | – | `SlaPolicyDTO[]` |
| POST | `/api/sla/policies` | sla.manage | `SlaPolicyRequest` | `SlaPolicyDTO` |
| PUT | `/api/sla/policies/{id}` | sla.manage | `SlaPolicyRequest` (ต้องมี `version`) | `SlaPolicyDTO` |

```jsonc
// SlaCalculateRequest  { "customerCode": "C-0002", "severityCode": "S1", "startedAt": "2026-11-25T09:00:00+07:00" /* null = ตอนนี้ */ }
// SlaTargetDTO
{ "customerCode": "C-0002", "policyId": 3, "policyName": "Gold", "severityCode": "S1", "severityName": "Critical", "severityRank": 1,
  "issuePriority": "Urgent", "startedAt": "...", "responseDueAt": "...", "resolutionDueAt": "...", "businessHoursOnly": true }

// SlaEvaluateRequest { customerCode, severityCode, startedAt, firstRespondedAt?, resolvedAt? }
// SlaEvaluationDTO { target: SlaTargetDTO, responseState, resolutionState (NotApplicable|OnTrack|AtRisk|Breached|Met),
//                    resolutionElapsedRatio, minutesToResolutionDue }
// SlaComplianceDTO { customerCode, total, met, breached, atRisk, onTrack, compliancePercent, bySeverity: { "S1": { total, breached } } }

// SlaPolicyRequest — customerId null = นโยบายเริ่มต้นของทุกลูกค้า; workingDaysMask bit0=อาทิตย์ … bit6=เสาร์
{ "customerId": 2, "version": null, "name": "Gold", "description": null, "validFrom": "2026-01-01", "validTo": null,
  "timeZoneId": "Asia/Bangkok", "businessStart": "08:30:00", "businessEnd": "17:30:00", "workingDaysMask": 62,
  "holidays": [ "2026-12-31" ], "atRiskThreshold": 0.8,
  "levels": [ { "code": "S1", "name": "Critical", "description": null, "rank": 1, "responseMinutes": 30, "resolutionMinutes": 240,
                "businessHoursOnly": false, "issuePriority": "Urgent" } ] }
// SlaPolicyDTO = เหมือนข้างบน + id, isActive, holidays (string), levels[].id, levels[].policyId
```

---

## 8. Insights — `api/insights`

| Method | Route | สิทธิ์ | คืนค่า |
|---|---|---|---|
| GET | `/api/insights/issues/aging?customerId=` | customer.read | `IssueAgingSummaryDTO` (ดึงจาก IssueTracking) |

```jsonc
// IssueAgingSummaryDTO { customerCode, openIssues, buckets: [ { label, minDays, maxDays, bySeverity: { "S1": 2 }, total } ],
//                        slaBreached, slaAtRisk, averageAgeDays, oldest: ExternalIssueDTO[] }
// ExternalIssueDTO { id, projectId, projectKey, subject, statusName, isClosed, priority, customerCode, severityCode, severityRank,
//                    responseDueAt, resolutionDueAt, firstRespondedAt, assigneeName, createdAt, closedAt }
```

---

## 9. บทสนทนา omnichannel — `api/conversations`, `api/webhooks`

| Method | Route | สิทธิ์ | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| GET | `/api/conversations?status=&customerId=&needsAttentionOnly=false&take=100` | chat.read | – | `ConversationDTO[]` |
| GET | `/api/conversations/{id}/messages?take=200` | chat.read | – | `ChatMessageDTO[]` |
| POST | `/api/conversations/{id}/reply` | chat.reply | `ReplyRequest { body, resolve }` | `ChatMessageDTO` (ส่งออกผ่าน MessageHub) |
| PATCH | `/api/conversations/{id}` | chat.reply | `ConversationUpdateRequest { assigneeSubject?, customerId?, status? }` | `ConversationDTO` |
| POST | `/api/webhooks/line` | anonymous + header `X-Line-Signature` (HMAC-SHA256 base64 ด้วย `Webhooks:LineChannelSecret`), rate limit `strict` | body ดิบของ LINE | `{ accepted }` / 401 |
| POST | `/api/webhooks/inbound` | anonymous + header `X-Signature: sha256=<hex HMAC>` ด้วย `Webhooks:InboundSecret`, rate limit `strict` | `InboundMessageRequest` | `{ accepted }` / 401 |

```jsonc
// InboundMessageRequest { channel, externalUserId, displayName?, subject?, body, externalMessageId? /* กันซ้ำ */, sentAt? }
// ConversationDTO { id, customerId, channel, externalUserId, subject, status (Open|Pending|Resolved), assigneeSubject, lastSentiment,
//                   needsAttention, lastMessageAt, firstResponseAt, createdAt, customerName }
// ChatMessageDTO { id, conversationId, direction (Inbound|Outbound), body, externalMessageId, senderSubject, sentiment, sentimentLabel, sentAt }
// ChannelKind: Line | Email | Facebook | Instagram | WebChat | Phone | Sms | Teams | WhatsApp | Telegram | Review
```

---

## 10. Analytics / ML — `api/analytics`, `api/ingest`, `api/ai`

| Method | Route | สิทธิ์ | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| GET | `/api/analytics/leads?customerId=&top=100` | analytics.read | – | `LeadScoreDTO[]` |
| GET | `/api/analytics/churn?customerId=&minProbability=0&top=100` | analytics.read | – | `ChurnRiskDTO[]` |
| GET | `/api/analytics/renewals?withinDays=180` | analytics.read | – | `RenewalSummaryDTO` |
| GET | `/api/analytics/forecast/sales?horizonMonths=6&customerId=&includePipeline=true` | analytics.read | – | `ForecastDTO` |
| GET | `/api/analytics/forecast/budget/{customerId}?horizonMonths=12` | analytics.read | – | `ForecastDTO` |
| GET | `/api/analytics/nba/{customerId}?max=3` | analytics.read | – | `NextBestActionDTO[]` |
| GET | `/api/analytics/recommendations/{customerId}?top=5&kind=` | analytics.read | – | `RecommendationDTO[]` |
| GET | `/api/analytics/send-time/{customerId}?channel=Email` | analytics.read | – | `SendTimeDTO` |
| GET | `/api/analytics/segments?segments=` | analytics.read | – | `SegmentationResultDTO` (คำนวณ ไม่บันทึก) |
| POST | `/api/analytics/segments?segments=` | analytics.read | – | `SegmentationResultDTO` (บันทึก `segment` ลงลูกค้า) |
| POST | `/api/analytics/sentiment` | analytics.read | `SentimentRequest { text (1-8000) }` | `SentimentDTO` |
| GET | `/api/analytics/risk/{customerId}` | analytics.read | – | `CustomerRiskDTO` |
| GET | `/api/analytics/risk/portfolio?top=20` | analytics.read | – | `CustomerRiskPortfolioDTO` |
| GET | `/api/analytics/models` | analytics.read | – | `ModelInfoDTO[] { name, samples, logLoss, trainedAt }` |
| POST | `/api/analytics/models/retrain` | data.ingest | – | 204 (ล้างโมเดลเดิม ฝึกใหม่ตอนเรียกครั้งถัดไป) |
| POST | `/api/ingest/events` | data.ingest | `IngestEventsRequest { events: EngagementEventRequest[1..10000] }` | `IngestResultDTO { accepted }` |
| GET | `/api/ingest/catalog` | customer.read | – | `CatalogItemDTO[]` |
| PUT | `/api/ingest/catalog/{code}` | data.ingest | `CatalogItemRequest { name, kind, category?, price, isActive }` | `CatalogItemDTO` |
| GET | `/api/ai/tools` | analytics.read | – | `AiToolInfoDTO[] { name, description, parameters (JSON schema) }` |

```jsonc
// EngagementEventRequest { customerId, kind, channel?, itemCode?, value, occurredAt? }
//   kind: EmailSent | EmailOpen | EmailClick | WebVisit | Purchase | Complaint | SupportTicket | ContentView | ProductView | MessageOpen
// FactorDTO { feature, value, contribution }
// LeadScoreDTO { opportunityId, customerId, title, score, grade, topFactors: FactorDTO[] }
// ChurnRiskDTO { customerId, customerName, probability, level, signals: FactorDTO[], recommendedAction }
// RenewalSummaryDTO { from, to, contracts: [ { contractId, customerId, contractNo, endDate, renewalProbability, expectedRenewalValue,
//                     expansionProbability, suggestedUpsell, factors } ], totalAtStake, expectedRenewal }
// ForecastDTO { method, history: ForecastPointDTO[], points: ForecastPointDTO[], seasonalIndex: { "1": 1.05 }, mape }
//   ForecastPointDTO { period, forecast, lower, upper, pipelineContribution }
// NextBestActionDTO { customerId, action, reason, priority, suggestedAt, channel, offer }
// RecommendationDTO { itemCode, name, kind (Product|Service|Content|Promotion), score, reason }
// SendTimeDTO { customerId, channel, bestDayOfWeek, bestHourLocal, nextSendAt, expectedOpenRate, observations }
// SegmentationResultDTO { segments: [ { name, size, centroid: { feature: value }, customerIds[] } ], iterations, inertia, computedAt }
// SentimentDTO { score, label (Negative|Neutral|Positive), confidence, signals[], needsAttention }
// CustomerRiskDTO { customerId, customerCode, result: RiskResultDTO (Common), factors: RiskFactorDTO[] (Common), warnings[] }
// CustomerRiskPortfolioDTO { customers: CustomerRiskDTO[], summary: RiskSummaryDTO (Common) }
// CatalogItemDTO { code, name, kind, category, price, isActive }
```

---

## 11. Profile ผู้ใช้ — `api/me`, `api/users`

| Method | Route | สิทธิ์ | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| GET | `/api/me/profile` | customer.read | – | `UserProfileDTO { subject, displayName, messageHubUserId }` |
| PUT | `/api/me/profile` | customer.read | `ProfileRequest { displayName, messageHubUserId? }` | `UserProfileDTO` |
| PUT | `/api/users/{subject}/profile` | customer.write | `ProfileRequest` | `UserProfileDTO` |

---

## 12. MCP — `/mcp` (สิทธิ์ `analytics.read` + สิทธิ์ของ tool แต่ละตัว)

MCP over HTTP (ModelContextProtocol SDK) ใช้ Bearer token ของ AuthServer เหมือน REST — รายชื่อและ JSON schema จริงดูได้ที่ `GET /api/ai/tools`
(โปรเจกต์ `CustomerMgnt.Mcp` เป็น MCP แบบ stdio สำหรับ desktop client ใช้ tool ชุดเดียวกัน)

| tool | อ่านอย่างเดียว | ทำอะไร |
|---|---|---|
| `calculate_customer_risk` | ✓ | คะแนนความเสี่ยงของลูกค้า |
| `assess_portfolio_risk` | ✓ | ความเสี่ยงทั้งพอร์ต |
| `predict_churn` | ✓ | โอกาสเลิกใช้บริการ |
| `score_leads` | ✓ | คะแนนโอกาสขาย |
| `forecast_renewals` | ✓ | พยากรณ์การต่อสัญญา |
| `forecast_sales` | ✓ | พยากรณ์ยอดขาย (Holt-Winters) |
| `forecast_seasonal_budget` | ✓ | พยากรณ์งบประมาณตามฤดูกาลของลูกค้า |
| `recommend_next_best_action` | ✓ | สิ่งที่ควรทำต่อกับลูกค้า |
| `recommend_products` | ✓ | แนะนำสินค้า/บริการ |
| `get_best_send_time` | ✓ | เวลาส่งข้อความที่เหมาะ |
| `segment_customers` | ✓ | แบ่งกลุ่มลูกค้า (k-means) |
| `analyze_sentiment` | ✓ | วิเคราะห์อารมณ์ข้อความ |
| `search_customers` | ✓ | ค้นหาลูกค้า |
| `get_customer_360` | ✓ | ข้อมูลลูกค้าแบบ 360 |
| `get_issue_aging` | ✓ | อายุ issue ของลูกค้า |
| `get_sla_compliance` | ✓ | ผลการทำตาม SLA |
| `get_sla_policy` | ✓ | นโยบาย SLA ของลูกค้า |
| `calculate_sla_targets` | ✓ | คำนวณ due date ของ SLA |
| `get_project_portfolio` | ✓ | โครงการของลูกค้า |
| `get_pipeline_summary` | ✓ | สรุป pipeline |
| `get_customer_agenda` | ✓ | นัดหมาย/วาระของลูกค้า |
| `list_customer_requests` | ✓ | รายการคำขอลูกค้า |
| `create_follow_up` | ✗ | สร้างกิจกรรมติดตาม (ต้องมี `activity.write`) |
| `create_customer_request` | ✗ | สร้างคำขอลูกค้า ไม่ส่งต่อ (ต้องมี `request.write`) |

---

## 13. Health และเครื่องมือ

| Route | หมายเหตุ |
|---|---|
| `GET /health/live`, `GET /health/ready` | anonymous (จาก `AddCommonWebDefaults`) |
| `CustomerMgnt.Api --health-probe <url>` | ใช้เป็น HEALTHCHECK ใน container chiseled |
| `CustomerMgnt.Api --export-catalog <dir>` | เขียน API catalog แล้วจบ |

---

## 14. ระบบที่ CustomerMgnt เรียกออกไป

| ระบบ | ใช้ทำอะไร | สัญญา |
|---|---|---|
| AuthServer | JWKS ตรวจ token, login ของ UI, บัญชีบริการ | `mrpichet/authserver` CLAUDE.md |
| IssueTracking | aging / SLA compliance, สร้าง issue ตอน forward | ส่งต่อ token ของผู้เรียก |
| ProjectMgnt / RequirementMgnt | portfolio, สร้างโครงการ/requirement ตอน forward | ส่งต่อ token ของผู้เรียก |
| MessageHub | ตอบแชต omnichannel, ประชุม | `mrpichet/messagehub` Claude.MD |
| LogServer | ส่ง log | `mrpichet/logserver` Claude.MD |

ทุกการเรียกออกส่งต่อ **AuthServer token ของผู้เรียก** เท่านั้น (ไม่ส่ง PQC token) ถ้าระบบปลายทางใช้ไม่ได้จะได้ 503 หรือข้อมูลส่วนนั้นว่าง

---

## 15. แผนการเปลี่ยนเวอร์ชัน

- path ปัจจุบัน (`api/...`) ถือเป็น v1 โดยปริยาย การเปลี่ยนที่ไม่ทำให้ client เดิมพัง (เพิ่ม field / enum / endpoint) ทำได้ใน path เดิม
- เมื่อย้ายไปรูปแบบ `api/v1/...` ตาม `Common.AspNetCore.Versioning` จะคง path เดิมไว้ช่วงหนึ่งพร้อม header `Deprecation` / `Sunset` / `Link` แล้วจึงคืน 410
- การเปลี่ยนที่ทำให้ client พัง (ลบ/เปลี่ยนชนิด field, เปลี่ยนความหมาย) ต้องขึ้นเวอร์ชันใหม่ และแก้ไฟล์นี้พร้อมกัน
