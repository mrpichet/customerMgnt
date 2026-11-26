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
| POST | `/api/session/login` | anonymous, rate limit `strict` | `LoginRequest { login, password, otpCode? }` | `SessionTokenDTO { accessToken, expiresAt, passwordChangeRequired, mfaEnrollmentRequired }` / 401 (`code` = `mfa_required` · `invalid_otp` เมื่อรหัสผ่านถูกแต่ต้องใช้ OTP — ส่งซ้ำพร้อม `otpCode`) |
| GET | `/api/session/me` | login | – | `SessionUserDTO { subject, userName, roles[], permissions[] }` |

> `login` ส่งต่อไป AuthServer `POST api/v1/auth/login` ด้วย client `AuthServer:LoginClientId` (ค่าเริ่มต้น `customermgnt-web`) และ identity source `AuthServer:LoginSource` (ค่าเริ่มต้น `Local`)
> endpoint นี้มีไว้ให้หน้าจอหลักของ CustomerMgnt — ระบบอื่น **ควร login กับ AuthServer โดยตรง** แล้วส่ง token มาที่ CustomerMgnt

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

---

# ส่วนที่ 2 — ระบบสมาชิกและโปรโมชัน (Membership & Promotion)

> **สถานะ: implement แล้ว** (2026-11-26) — ตรงกับโค้ดใน `src/CustomerMgnt.Api/Controllers/{MembershipControllers,MembershipOperationsControllers,PromotionControllers}.cs` และ `docs/api-catalog` (สร้างด้วย `--export-catalog`)
> หน้าจอ: Blazor WASM `src/CustomerMgnt.Membership.Web` (Back Office + Member Portal ใช้ submodule `external/webcommon`) โฮสต์โดย API ที่ `/membership/` — ดู §27
> ที่มาของกฎและสูตร: เอกสาร spec สูตรคำนวณ (FEFO lot, การคืนตามสัดส่วนสะสม, ลำดับโปรโมชัน) — สูตรอยู่ใน `MemberMgnt` service ของ `CustomerMgnt.Core`

## 16. หลักการร่วมของ Membership API

| หัวข้อ | รายละเอียด |
|---|---|
| Version | ทุก path ใหม่เป็น `api/v1/...` ตั้งแต่แรก (Common `ApiVersionRouteConvention`) — breaking change → `api/v2` คู่กับ v1 พร้อม header `Deprecation` / `Sunset` / `Link` |
| ผู้เรียก 3 กลุ่ม | (1) เจ้าหน้าที่ Back Office: token ผู้ใช้ AuthServer ที่มีสิทธิ์ตาม §17 (2) สมาชิก: token จาก `POST /api/v1/auth/external` (Google/Facebook) เรียกได้เฉพาะ `api/v1/me/*` ของตัวเอง (3) POS / ระบบต้นทาง: `client_credentials` ที่มี scope `pos.*` / `integration.*` |
| ระบบต้นทาง | `sourceSystem` = claim `client_id` ของ token (ไม่รับจาก body) — ใช้แยก idempotency key, transaction ID, rate limit และ audit |
| `X-Request-Id` | ส่งได้ทุกคำขอ (≤64, ไม่ส่ง = ระบบออกให้) คืนกลับใน response header และบันทึกใน log/audit/outbox |
| `Idempotency-Key` | **บังคับ** สำหรับทุก POST ที่เปลี่ยนข้อมูลจาก POS / integration (≤100) — เก็บ 7 วันต่อ (sourceSystem, key): ส่งซ้ำ body เดิม = ได้ response เดิม (header `Idempotent-Replayed: true`), body ต่าง = **422** `idempotency_key_reused`, คำขอเดิมยังทำอยู่ = **409** `request_in_progress` |
| Optimistic lock | DTO ที่แก้ได้มี `version` ต้องส่งกลับตอนแก้ ไม่ตรง = 409 `version_conflict` |
| Rate limit | `pos`: 50 คำขอ/วินาที ต่อ client (token bucket); `member-portal`: 60 คำขอ/นาที ต่อผู้ใช้; OTP: 3 ครั้ง/10 นาที ต่อเบอร์ → 429 + `Retry-After` |
| Error | ProblemDetails (§1.2) + `code` ตาราง §25 |
| PII | เบอร์/อีเมล/วันเกิด ส่งออกแบบ mask (`081-xxx-5678`, `s***@mail.com`) เว้นแต่ผู้เรียกมี `member.pii` — ไม่เคยส่งเลขบัตรประชาชน |
| เงิน / คะแนน | เงินเป็น `decimal` 2 ตำแหน่ง (บาท), คะแนนเป็น `int` |
| Tolerant reader | client ต้อง ignore field และค่า enum ที่ไม่รู้จัก |

## 17. สิทธิ์ (Application `customermgnt` ใน AuthServer)

| permission / scope | บทบาทที่แนะนำ | ใช้กับ |
|---|---|---|
| `member.read` | Member Admin, CS, Promotion Manager, Approver, Analyst, System Admin | ค้นหา/ดูสมาชิก (mask) |
| `member.write` | Member Admin, CS | สมัครแทน, แก้ข้อมูล, ผูก Customer |
| `member.pii` | Member Admin | ดูข้อมูลส่วนบุคคลเต็ม (บันทึก audit ทุกครั้ง) |
| `member.status` | Member Admin | เปลี่ยนสถานะสมาชิก |
| `member.merge` / `member.merge.approve` | Member Admin | ยื่น / อนุมัติรวมบัญชี |
| `member.verify.override` | Member Admin | ยืนยันสมาชิกแทน OTP (ตรวจบัตรต่อหน้า) |
| `member.export` | Member Admin, Analyst | ส่งออกข้อมูลสมาชิก |
| `tier.manage` | Member Admin | จัดการ Tier |
| `points.read` | ทุกบทบาทเจ้าหน้าที่ | ดู ledger / lot |
| `points.adjust` / `points.adjust.approve` | CS, Member Admin / Member Admin | ยื่น / อนุมัติปรับคะแนน |
| `points.rule.manage` / `points.rule.approve` | Promotion Manager / Approver | กฎการได้คะแนน |
| `promotion.read` / `promotion.write` / `promotion.approve` | — | แคมเปญ (approve ต้องไม่ใช่ผู้สร้าง) |
| `coupon.read` / `coupon.write` | CS / Promotion Manager | คูปอง |
| `segment.read` / `segment.write` | Analyst / Promotion Manager | Segment |
| `campaign.message.send` | Promotion Manager | ส่งข้อความผ่าน MessageHub |
| `txn.read` | CS, Analyst | ประวัติการซื้อ |
| `report.read` / `report.export` | Analyst, Promotion Manager, Member Admin | รายงาน / ส่งออก |
| `settings.manage` | System Admin | กฎธุรกิจ, consent, ระบบภายนอก |
| `audit.read` | System Admin | audit log |
| `privacy.manage` | Member Admin | คำขอสิทธิ์ PDPA |
| `pos.lookup` (scope) | client ของ POS | ค้นหาสมาชิก |
| `pos.checkout` (scope) | client ของ POS | quote / reserve / commit / release |
| `pos.transaction` (scope) | client ของ POS | บันทึกการซื้อ ยกเลิก คืน |
| `integration.read` (scope) | ระบบอื่น | อ่านสมาชิก/คะแนนแบบ mask |

สมาชิก (Portal) **ไม่ต้องมี** permission ใด ๆ — `api/v1/me/*` ตรวจว่า `sub` ของ token ผูกกับสมาชิก

## 18. สมาชิก — `api/v1/members`

| Method | Route | สิทธิ์ | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| GET | `/api/v1/members` | member.read | query `MemberSearchCriteria` | `PagedResultDTO<MemberSummaryDTO>` |
| POST | `/api/v1/members` | member.write | `RegisterMemberRequest` (สมัครโดยเจ้าหน้าที่) | 201 `MemberRegistrationResultDTO { member: MemberDTO, challenge: { challengeId, expiresAt } }` (`PendingVerification` + ส่ง OTP) / 409 `duplicate_member` / 503 `dependency_unavailable` (ส่ง SMS ไม่ได้) |
| GET | `/api/v1/members/{id}` | member.read | – | `MemberDTO` |
| GET | `/api/v1/members/by-no/{memberNo}` | member.read | – | `MemberDTO` |
| PUT | `/api/v1/members/{id}` | member.write | `UpdateMemberRequest` (`version`) | `UpdateMemberResultDTO { member, challenge }` — 200 / 202 เมื่อเปลี่ยนเบอร์ (ยืนยันด้วย `/verify`) |
| POST | `/api/v1/members/{id}/reveal` | member.pii | `{ fields: ["phone","email","birthDate"], reason }` | `MemberPiiDTO` (บันทึก audit) |
| POST | `/api/v1/members/{id}/otp` | member.write | – | 202 `{ challengeId, expiresAt }` (ส่ง OTP ใหม่) |
| POST | `/api/v1/members/{id}/verify` | member.write | `{ challengeId, code }` | `MemberDTO` (`Active`) |
| POST | `/api/v1/members/{id}/verify-override` | member.verify.override | `{ reason, documentRef }` | `MemberDTO` |
| PUT | `/api/v1/members/{id}/status` | member.status | `{ version, status, reason }` | `MemberDTO` / 409 `invalid_transition` |
| PUT | `/api/v1/members/{id}/customer` | member.write | `{ version, customerCode? }` (null = ให้ระบบสร้าง Customer ใหม่) | `MemberDTO` |
| GET | `/api/v1/members/{id}/tier-history` | member.read | – | `TierHistoryDTO[]` |
| GET | `/api/v1/members/{id}/consents` | member.read | – | `MemberConsentDTO[]` |
| GET | `/api/v1/members/{id}/audit` | audit.read | – | `AuditEntryDTO[]` (ของสมาชิกคนนี้) |
| POST | `/api/v1/members/duplicates/check` | member.write | `{ phone?, email? }` | `DuplicateCheckDTO { exists, memberId?, memberNoMasked? }` |
| POST | `/api/v1/members/merges` | member.merge | `MergeRequest { primaryId, secondaryId, fieldChoices, reason }` | 202 `ApprovalRequestDTO` |
| GET | `/api/v1/members/merges/preview?primaryId=&secondaryId=` | member.merge | – | `MergePreviewDTO` (เทียบ field, คะแนน, คูปองที่จะย้าย) |

```jsonc
// MemberSearchCriteria (query)
?text=&memberNo=&phone=&email=&status=PendingVerification|Active|Suspended|Inactive|Closed&tierCode=&segmentId=&page=1&pageSize=20
// phone / email ค้นหาด้วย blind index แบบตรงทั้งค่า (ไม่ค้นบางส่วน)

// MemberSummaryDTO
{ "id": 1, "memberNo": "M2600001", "displayName": "สมชาย ใ.", "phoneMasked": "081-xxx-5678", "status": "Active",
  "tierCode": "GOLD", "pointsAvailable": 1320, "customerCode": "C-0012", "createdAt": "..." }

// MemberDTO
{ "id": 1, "memberNo": "M2600001", "firstName": "สมชาย", "lastName": "ใจดี", "displayName": "สมชาย ใจดี",
  "phoneMasked": "081-xxx-5678", "emailMasked": "s***@mail.com", "birthDateMasked": "xxxx-05-14", "gender": "Male|Female|Other|Unspecified",
  "province": "กรุงเทพมหานคร", "status": "Active", "statusReason": null, "registeredChannel": "Portal|BackOffice|Pos|Import",
  "tier": { "code": "GOLD", "name": "Gold", "since": "2026-01-01", "reviewAt": "2027-01-01" },
  "points": PointBalanceDTO, "customerCode": "C-0012", "hasPortalAccount": true,
  "marketingConsent": true, "createdAt": "...", "updatedAt": "...", "version": 4 }

// RegisterMemberRequest (เจ้าหน้าที่) / me/registration (สมาชิก) ใช้ field ชุดเดียวกัน
{ "firstName": "", "lastName": "", "phone": "0812345678", "email": null, "birthDate": "1990-05-14", "gender": "Unspecified",
  "province": null, "consentVersionId": 3, "marketingConsent": false, "customerCode": null /* null = สร้าง Customer ใหม่ */ }

// UpdateMemberRequest — null = ไม่เปลี่ยน; เปลี่ยนเบอร์ต้องยืนยัน OTP ของเบอร์ใหม่ (ได้ 202 + challengeId)
{ "version": 4, "firstName": null, "lastName": null, "phone": null, "email": null, "birthDate": null, "gender": null, "province": null }
```

## 19. Tier — `api/v1/tiers`

| Method | Route | สิทธิ์ | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| GET | `/api/v1/tiers` | member.read | – | `TierDTO[]` (เรียงตาม rank) |
| GET | `/api/v1/tiers/{id}` | member.read | – | `TierDTO` |
| POST | `/api/v1/tiers` | tier.manage | `TierRequest` | 201 `TierDTO` |
| PUT | `/api/v1/tiers/{id}` | tier.manage | `TierRequest` (`version`) | `TierDTO` |
| POST | `/api/v1/tiers/evaluate` | tier.manage | `{ memberId? }` (ว่าง = ทั้งหมด, งานเบื้องหลัง) | 202 `JobDTO` |

```jsonc
// TierRequest / TierDTO
{ "code": "GOLD", "name": "Gold", "rank": 2, "upgradeThreshold": 30000, "retainThreshold": 20000,
  "pointMultiplier": 1.5, "benefits": ["ส่วนลดวันเกิด 10%"], "isActive": true, "version": 1 }
```

## 20. คะแนน — `api/v1/points`

| Method | Route | สิทธิ์ | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| GET | `/api/v1/points/balances/{memberId}` | points.read | – | `PointBalanceDTO` |
| GET | `/api/v1/points/ledger` | points.read | query `memberId?, type?, from?, to?, transactionId?, page, pageSize` | `PagedResultDTO<PointLedgerDTO>` |
| GET | `/api/v1/points/lots/{memberId}?activeOnly=true` | points.read | – | `PointLotDTO[]` |
| GET | `/api/v1/points/rules` | points.read | `?activeOnly=` | `EarnRuleDTO[]` |
| POST | `/api/v1/points/rules` | points.rule.manage | `EarnRuleRequest` | 201 `EarnRuleDTO` (`PendingApproval`) |
| PUT | `/api/v1/points/rules/{id}` | points.rule.manage | `EarnRuleRequest` (`version`) | `EarnRuleDTO` (แก้ field ที่ต้องอนุมัติ → `PendingApproval`) |
| POST | `/api/v1/points/adjustments` | points.adjust | `AdjustmentRequest` | 201 `AdjustmentDTO` (`Applied` เมื่ออยู่ในวงเงิน / `PendingApproval`) |
| GET | `/api/v1/points/adjustments` | points.read | `?status=&memberId=` | `AdjustmentDTO[]` |

```jsonc
// PointBalanceDTO
{ "memberId": 1, "available": 1320, "reserved": 0, "debt": 0, "expiringSoon": [ { "expiresAt": "2026-12-31T16:59:59Z", "points": 200 } ],
  "lifetimeEarned": 5400, "version": 17 }

// PointLedgerDTO — append-only
{ "id": 901, "memberId": 1, "type": "Earn|Redeem|Expire|Reverse|Adjustment", "points": 132, "balanceAfter": 1320,
  "sourceSystem": "pos-bkk", "transactionId": "T-0001", "reference": "rule:2", "reason": null, "actor": "client:pos-bkk",
  "requestId": "r-1", "createdAt": "..." }

// PointLotDTO { id, memberId, points, remaining, earnedAt, expiresAt, sourceLedgerId }

// EarnRuleRequest / EarnRuleDTO
{ "code": "BASE", "name": "25 บาท = 1 คะแนน", "spendPerPoint": 25, "minSpend": 0, "maxPointsPerTransaction": null,
  "includeCategories": [], "excludeItemCodes": ["GIFTCARD"], "branches": [], "channels": [], "startAt": "...", "endAt": null,
  "status": "Draft|PendingApproval|Active|Inactive", "version": 1 }

// AdjustmentRequest
{ "memberId": 1, "points": -150 /* บวก = เพิ่ม, ลบ = ลด */, "reason": "ชดเชยคะแนนจากบิลตกหล่น", "attachmentRef": null }
// AdjustmentDTO = AdjustmentRequest + id, status (Applied|PendingApproval|Rejected), approvalRequestId, ledgerId, requestedBy, createdAt
```

## 21. แคมเปญ / คูปอง / Segment — `api/v1/promotions`, `api/v1/coupons`, `api/v1/segments`

| Method | Route | สิทธิ์ | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| GET | `/api/v1/promotions` | promotion.read | `?status=&from=&to=&rewardType=&text=&page=&pageSize=` | `PagedResultDTO<CampaignSummaryDTO>` |
| POST | `/api/v1/promotions` | promotion.write | `CampaignRequest` | 201 `CampaignDTO` (`Draft`) |
| GET | `/api/v1/promotions/{id}` | promotion.read | – | `CampaignDTO` (รวม `usage`, `approvals`) |
| PUT | `/api/v1/promotions/{id}` | promotion.write | `CampaignRequest` (`version`) | `CampaignDTO` — แก้ field ที่ต้องอนุมัติตอน Approved/Scheduled → กลับเป็น `PendingApproval` |
| POST | `/api/v1/promotions/{id}/submit` | promotion.write | `{ version, comment? }` | `CampaignDTO` (`PendingApproval`) |
| POST | `/api/v1/promotions/{id}/pause` · `/resume` · `/cancel` | promotion.write | `{ version, reason }` | `CampaignDTO` / 409 `invalid_transition` |
| POST | `/api/v1/promotions/simulate` | promotion.read | `QuoteRequest` + `campaignIds?` (รวม Draft ได้) | `QuoteDTO` (ไม่บันทึกอะไร) |
| GET | `/api/v1/coupons/batches` | coupon.read | `?campaignId=` | `CouponBatchDTO[]` |
| POST | `/api/v1/coupons/batches` | coupon.write | `CouponBatchRequest` | 202 `JobDTO` (สร้างรหัสเบื้องหลัง) |
| GET | `/api/v1/coupons` | coupon.read | `?code=&batchId=&memberId=&status=&page=&pageSize=` | `PagedResultDTO<CouponDTO>` |
| GET | `/api/v1/coupons/{code}` | coupon.read | – | `CouponDTO` + `history: CouponEventDTO[]` |
| POST | `/api/v1/coupons/{code}/cancel` | coupon.write | `{ reason }` | `CouponDTO` |
| POST | `/api/v1/coupons/issue` | coupon.write | `{ campaignId, memberIds? , segmentId? }` | 202 `JobDTO` |
| GET | `/api/v1/segments` | segment.read | – | `SegmentDTO[]` |
| POST | `/api/v1/segments` | segment.write | `SegmentRequest` | 201 `SegmentDTO` |
| GET | `/api/v1/segments/{id}` | segment.read | – | `SegmentDTO` |
| PUT | `/api/v1/segments/{id}` | segment.write | `SegmentRequest` (`version`) | `SegmentDTO` |
| POST | `/api/v1/segments/preview` | segment.read | `{ rule }` | `{ count, sample: MemberSummaryDTO[10] }` |
| POST | `/api/v1/segments/{id}/refresh` | segment.write | – | 202 `JobDTO` |
| GET | `/api/v1/segments/{id}/members` | segment.read | `?page=&pageSize=` | `PagedResultDTO<MemberSummaryDTO>` |

```jsonc
// CampaignRequest
{ "code": "XMAS26", "name": "", "description": null, "startAt": "...", "endAt": "...",
  "rewardType": "PercentDiscount|AmountDiscount|BuyXGetY|SpendThreshold|BonusPoints|PointMultiplier|IssueCoupon",
  "reward": { "percent": 10, "amount": null, "cap": 500, "buyQty": null, "getQty": null, "threshold": null, "bonusPoints": null, "multiplier": null, "couponBatchId": null },
  "level": "Line|Bill", "requiresCoupon": false,
  "conditions": { "minSpend": 1000, "itemCodes": [], "categories": [], "branches": [], "channels": ["Pos","Online"], "daysOfWeek": [], "timeFrom": null, "timeTo": null },
  "targets": { "tierCodes": ["GOLD"], "segmentIds": [], "allMembers": false },
  "stackMode": "Exclusive|Stackable|Group", "exclusionGroup": null, "priority": 100,
  "limits": { "perMemberTotal": null, "perMemberPerDay": 1, "totalUses": null, "budget": 200000 },
  "version": null }

// CampaignDTO = CampaignRequest + id, status (Draft|PendingApproval|Approved|Scheduled|Active|Paused|Ended|Rejected|Cancelled),
//   usage { uses, reserved, budgetUsed }, approvalRequestId, createdBy, createdAt, updatedAt

// CouponBatchRequest { campaignId, quantity (1-1,000,000), codePrefix, codeLength (8-16), expiresAt, perMember? }
// CouponDTO { code, batchId, campaignId, memberId?, status (Available|Reserved|Redeemed|Expired|Cancelled|Reversed),
//             reservationId?, redeemedTransactionId?, expiresAt, version }

// SegmentRequest / SegmentDTO
{ "name": "Gold กรุงเทพฯ ซื้อบ่อย", "description": null,
  "rule": { "op": "And", "items": [ { "field": "tierCode", "operator": "In", "value": ["GOLD"] },
                                    { "field": "province", "operator": "Eq", "value": "กรุงเทพมหานคร" },
                                    { "field": "frequency12m", "operator": "Gte", "value": 6 } ] },
  "refreshDaily": true, "version": 1 }   // DTO เพิ่ม id, memberCount, refreshedAt
// field ที่ใช้ได้: tierCode, status, ageYears, gender, province, pointsAvailable, spend12m, frequency12m, recencyDays, rfmR, rfmF, rfmM, marketingConsent
// operator: Eq, Ne, In, NotIn, Gt, Gte, Lt, Lte, Between — แปลงเป็น SQL แบบ parameterized เท่านั้น
```

## 22. POS / ระบบต้นทาง — `api/v1/pos` (client_credentials, ต้องมี `Idempotency-Key` ทุก POST ยกเว้น `quote`)

| Method | Route | scope | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| GET | `/api/v1/pos/members/lookup?phone=` \| `?memberNo=` \| `?couponCode=` | pos.lookup | – | `PosMemberDTO` / 404 |
| POST | `/api/v1/pos/checkout/quote` | pos.checkout | `QuoteRequest` | `QuoteDTO` (ไม่จองอะไร ไม่ต้องมี `Idempotency-Key`) |
| POST | `/api/v1/pos/checkout/reserve` | pos.checkout | `QuoteRequest` + `quoteId?` | 201 `ReservationDTO` / 409 `quote_changed` (ผลต่างจาก quote เดิม — quote ใหม่อยู่ใน extension `payload` ของ ProblemDetails) |
| POST | `/api/v1/pos/checkout/{reservationId}/commit` | pos.checkout | `CommitRequest` | `CheckoutResultDTO` / 410 `reservation_expired` |
| POST | `/api/v1/pos/checkout/{reservationId}/release` | pos.checkout | `{ reason }` | 204 (release ซ้ำ = 204) |
| POST | `/api/v1/pos/transactions` | pos.transaction | `SalesTransactionRequest` (ขายที่ไม่ได้ผ่าน checkout เช่น ตอน offline — ได้**คะแนนฐาน**อย่างเดียว ไม่คิดโปรโมชัน/คะแนนพิเศษ ภายใน `pos.lateSubmitDays`) | 201 `TransactionResultDTO` / 200 เมื่อ transactionId เคยบันทึกแล้ว |
| GET | `/api/v1/pos/transactions/{transactionId}` | pos.transaction | – | `TransactionResultDTO` |
| POST | `/api/v1/pos/transactions/{transactionId}/cancel` | pos.transaction | `{ reason }` | `TransactionResultDTO` |
| POST | `/api/v1/pos/transactions/{transactionId}/returns` | pos.transaction | `ReturnRequest` | 200 `ReturnResultDTO` / **202** `{ status: "PendingOriginal" }` เมื่อยังไม่พบรายการขาย (มาผิดลำดับ) |

```jsonc
// PosMemberDTO — ข้อมูลน้อยที่สุดที่แคชเชียร์ต้องใช้
{ "memberNo": "M2600001", "displayName": "สมชาย ใ.", "status": "Active", "tierCode": "GOLD", "pointsAvailable": 1320,
  "pointValue": 0.25, "maxPointsPayPercent": 50, "coupons": [ { "code": "XMAS-8K2Q", "name": "ลด 10%", "expiresAt": "..." } ] }

// QuoteRequest
{ "memberNo": "M2600001", "branchCode": "BKK01", "channel": "Pos", "occurredAt": "...",
  "lines": [ { "lineNo": 1, "itemCode": "SKU-1", "category": "FOOD", "qty": 2, "unitPrice": 450.00, "lineDiscount": 0 } ],
  "billDiscount": 100.00, "couponCodes": ["XMAS-8K2Q"], "pointsToRedeem": 200 }

// QuoteDTO
{ "quoteId": "q_...", "subtotal": 2350.00, "lineDiscounts": 0, "promotionDiscount": 235.00, "couponDiscount": 0, "billDiscount": 100.00,
  "pointsRedeemed": 200, "pointsTender": 50.00, "netPayable": 1965.00, "pointsToEarn": 117,   // GOLD ×1.5: floor(1,965/25)=78 → 117
  "benefits": [ { "campaignId": 7, "campaignCode": "XMAS26", "kind": "PercentDiscount", "amount": 235.00, "lineNos": [1], "couponCode": null } ],
  "rejectedCoupons": [ { "code": "OLD-1", "reason": "coupon_expired" } ],
  "lines": [ { "lineNo": 1, "linePaid": 1965.00, "eligible": true } ],
  "expiresAt": "..." }   // quote ใช้อ้างอิงได้ 15 นาที

// ReservationDTO
{ "reservationId": "rsv_...", "expiresAt": "...", "quote": QuoteDTO }

// CommitRequest — transactionId ต้องไม่ซ้ำต่อ sourceSystem
{ "transactionId": "T-0001", "receiptNo": "R-889", "paidAt": "...", "paidAmount": 1965.00 }

// CheckoutResultDTO
{ "transactionId": "T-0001", "memberNo": "M2600001", "pointsEarned": 117, "pointsRedeemed": 200,
  "balance": PointBalanceDTO, "couponsRedeemed": ["XMAS-8K2Q"], "couponsIssued": [], "tierChanged": null }

// SalesTransactionRequest = QuoteRequest (ไม่มี couponCodes / pointsToRedeem) + transactionId, receiptNo, paidAt, paidAmount, offline: true

// ReturnRequest
{ "returnId": "RT-01", "occurredAt": "...", "lines": [ { "lineNo": 1, "qty": 1 } ], "reason": null }
// ReturnResultDTO { returnId, transactionId, pointsReversed, pointsRestored, couponsRestored[], balance: PointBalanceDTO }
```

ลำดับที่ POS ต้องทำ: `lookup` → `quote` (กี่ครั้งก็ได้) → `reserve` → ชำระเงิน → `commit` หรือ `release`
- reservation ที่ไม่ commit ภายใน `pos.reserveTtlMinutes` (15 นาที) ถูกปล่อยอัตโนมัติ → commit หลังจากนั้นได้ 410
- commit / release / returns ที่ส่งซ้ำด้วย `Idempotency-Key` เดิมได้ผลเดิม; network หลุดหลัง commit ให้ส่ง commit เดิมซ้ำ (ห้ามสร้าง key ใหม่)
- ติดต่อไม่ได้ (timeout, 5xx, circuit เปิด): ขายต่อได้โดยไม่ใช้สิทธิ์สมาชิก แล้วส่ง `POST /pos/transactions` ภายหลัง (`offline: true`)
- retry ฝั่ง POS: exponential backoff + jitter (เริ่ม 200 ms สูงสุด 5 ครั้ง) เฉพาะ 408/429/5xx และ network error — 4xx อื่นห้าม retry

## 23. Portal ของสมาชิก — `api/v1/me` (token สมาชิกจาก AuthServer)

| Method | Route | ขาเข้า | คืนค่า |
|---|---|---|---|
| GET | `/api/v1/me/member` | – | `MemberDTO` (ข้อมูลตัวเองไม่ mask) / 404 `not_registered` |
| GET | `/api/v1/me/consents/current` | – | `ConsentVersionDTO` ฉบับปัจจุบัน / 204 ยังไม่ประกาศ (ใช้ตอนสมัคร ยังไม่ต้องเป็นสมาชิก) |
| POST | `/api/v1/me/registration` | `RegisterMemberRequest` (ไม่มี customerCode) | 202 `{ registrationId, challengeId, expiresAt }` |
| POST | `/api/v1/me/registration/verify` | `{ registrationId, challengeId, code }` | 201 `MemberDTO` (ผูก `sub` กับสมาชิก; เบอร์ตรงสมาชิกที่เจ้าหน้าที่สมัครให้ = ผูกกับคนเดิม) |
| PUT | `/api/v1/me/member` | `UpdateMemberRequest` | `UpdateMemberResultDTO { member, challenge }` — 200 / 202 เมื่อเปลี่ยนเบอร์ |
| POST | `/api/v1/me/member/phone/verify` | `{ challengeId, code }` | `MemberDTO` (ยืนยัน OTP ของเบอร์ใหม่) |
| GET | `/api/v1/me/points` | – | `PointBalanceDTO` |
| GET | `/api/v1/me/points/ledger?page=&pageSize=` | – | `PagedResultDTO<PointLedgerDTO>` |
| GET | `/api/v1/me/rewards` | – | `RewardDTO[]` (ของรางวัลที่แลกด้วยคะแนนได้) |
| POST | `/api/v1/me/rewards/{rewardId}/redeem` | header `Idempotency-Key` | 201 `CouponDTO` (reserve → ออกคูปอง → commit; ล้มที่ขั้นใด = release) |
| GET | `/api/v1/me/coupons?status=` | – | `CouponDTO[]` + `qrPayload` |
| GET | `/api/v1/me/promotions` | – | `CampaignSummaryDTO[]` (ที่ตัวเองมีสิทธิ์) |
| GET | `/api/v1/me/purchases?page=&pageSize=` | – | `PagedResultDTO<TransactionSummaryDTO>` |
| GET | `/api/v1/me/consents` | – | `MemberConsentDTO[]` + ฉบับปัจจุบัน |
| PUT | `/api/v1/me/consents` | `{ consentVersionId, accepted, marketingChannels: ["Sms","Email","Line"] }` | `MemberConsentDTO[]` |
| POST | `/api/v1/me/privacy-requests` | `{ type: "Access|Rectify|Erase|Close", detail }` | 201 `PrivacyRequestDTO` |
| GET | `/api/v1/me/privacy-requests` | – | `PrivacyRequestDTO[]` |

## 24. ธุรกรรม / อนุมัติ / รายงาน / ตั้งค่า / Audit / ข้อความ

| Method | Route | สิทธิ์ | ขาเข้า | คืนค่า |
|---|---|---|---|---|
| GET | `/api/v1/transactions` | txn.read | `?memberId=&transactionId=&sourceSystem=&branchCode=&from=&to=&status=&page=&pageSize=` | `PagedResultDTO<TransactionSummaryDTO>` |
| GET | `/api/v1/transactions/{id}` | txn.read | – | `TransactionDetailDTO` (lines, benefits, points, returns) |
| GET | `/api/v1/approvals` | (ผู้มีสิทธิ์ approve ของเรื่องนั้น) | `?status=Pending&type=Campaign|Adjustment|Merge|EarnRule&mine=` | `ApprovalRequestDTO[]` |
| GET | `/api/v1/approvals/pending-count` | ผู้ใช้ที่ login | – | `int` จำนวนคำขอที่รอฉันพิจารณา (badge เมนู) |
| GET | `/api/v1/approvals/{id}` | เดียวกัน | – | `ApprovalRequestDTO` (before/after แบบ mask) |
| POST | `/api/v1/approvals/{id}/decision` | `*.approve` ของเรื่องนั้น | `{ decision: "Approve|Reject", comment }` (Reject ต้องมี comment) | `ApprovalRequestDTO` / 403 `self_approval` |
| POST | `/api/v1/approvals/{id}/cancel` | ผู้ยื่น | `{ comment? }` | `ApprovalRequestDTO` |
| GET | `/api/v1/reports/members` · `/points` · `/promotions` · `/transactions` | report.read | query ช่วงวัน + ตัวกรอง | `ReportDTO { columns, rows, totals }` (≤ 5,000 แถว) |
| POST | `/api/v1/reports/exports` | report.export (+ member.export ถ้ามีข้อมูลสมาชิก) | `{ report, filters, format: "Xlsx|Csv" }` | 202 `ExportJobDTO` (บันทึกผู้ส่งออกและตัวกรอง) |
| GET | `/api/v1/reports/exports` | report.export | – | `ExportJobDTO[]` (ของตัวเอง) |
| GET | `/api/v1/reports/exports/{id}/file` | report.export (เจ้าของงาน) | – | ไฟล์ (หมดอายุ 24 ชม.) |
| GET | `/api/v1/dashboard` | member.read | – | `DashboardDTO` (ตัวเลขตามสิทธิ์ของผู้เรียก) |
| POST | `/api/v1/campaign-messages` | campaign.message.send | `{ segmentId? , memberIds?, channel, templateCode, variables, scheduledAt? }` | 202 `MessageJobDTO` (ส่งเฉพาะคนที่ยินยอมช่องทางนั้น ผ่าน MessageHub `api/v1/messages/bulk`) — ช่องทาง `Line` ยังไม่มี LINE user id ของสมาชิก จึงข้ามผู้รับทุกคน (นับเป็น skipped) |
| GET | `/api/v1/campaign-messages` | campaign.message.send | – | `MessageJobDTO[]` (สถานะส่ง) |
| GET | `/api/v1/settings/rules` | settings.manage | – | `BusinessRulesDTO` (ทุก key ในหัวข้อ 1 ของ spec) |
| PUT | `/api/v1/settings/rules` | settings.manage | `BusinessRulesDTO` (`version`) | `BusinessRulesDTO` |
| GET/POST | `/api/v1/settings/consents` | settings.manage | `ConsentVersionRequest { title, body, effectiveAt }` | `ConsentVersionDTO[]` / 201 |
| POST | `/api/v1/settings/consents/{id}/publish` | settings.manage | – | `ConsentVersionDTO` |
| GET/PUT | `/api/v1/settings/integrations` | settings.manage | `IntegrationClientDTO { clientId, name, enabled, rateLimitPerSecond, alertOnErrorRate }` | `IntegrationClientDTO[]` |
| GET | `/api/v1/settings/roles` | settings.manage | – | บทบาท/สิทธิ์ของ Application `customermgnt` (อ่านจาก AuthServer) |
| GET | `/api/v1/audit` | audit.read | `?actor=&action=&target=&from=&to=&page=&pageSize=` | `PagedResultDTO<AuditEntryDTO>` |
| GET | `/api/v1/privacy-requests` | privacy.manage | `?status=` | `PrivacyRequestDTO[]` |
| POST | `/api/v1/privacy-requests/{id}/complete` | privacy.manage | `{ outcome, note }` | `PrivacyRequestDTO` |

```jsonc
// ApprovalRequestDTO
{ "id": 31, "type": "Campaign", "entityId": 7, "summary": "แคมเปญ XMAS26", "levelsRequired": 1, "levelsApproved": 0,
  "status": "Pending|Approved|Rejected|Cancelled", "beforeJson": null, "afterJson": "{...}",
  "requestedBy": "u-1", "requestedAt": "...", "steps": [ { "level": 1, "decidedBy": null, "decision": null, "comment": null, "decidedAt": null } ] }

// AuditEntryDTO { id, at, actor, actorType (User|Client|System), action, target, requestId, ip, beforeMasked, afterMasked, hash }
// ExportJobDTO { id, report, format, filters, status (Queued|Running|Done|Failed|Expired), rowCount, requestedBy, requestedAt, expiresAt }
```

## 25. รหัส error (`code` ใน ProblemDetails)

| status | code | ความหมาย |
|---|---|---|
| 400 | `validation_failed` | field ไม่ถูกต้อง (`errors`) |
| 400 | `idempotency_key_required` | POST จาก POS / integration ไม่มี `Idempotency-Key` |
| 404 | `not_found` · `not_registered` | ไม่พบ / token สมาชิกยังไม่ผูกกับสมาชิก |
| 409 | `duplicate_member` | เบอร์หรืออีเมลซ้ำกับสมาชิกที่ยังไม่ Closed |
| 409 | `version_conflict` · `invalid_transition` | มีคนแก้ก่อน / เปลี่ยนสถานะไม่ได้จากสถานะปัจจุบัน |
| 409 | `insufficient_points` · `limit_reached` · `budget_exhausted` | คะแนนไม่พอ / เกินจำนวนสิทธิ์ / งบหมด |
| 409 | `coupon_unavailable` · `coupon_expired` · `coupon_not_owned` | คูปองใช้ไม่ได้ |
| 409 | `quote_changed` · `request_in_progress` · `duplicate_transaction` | ผลคำนวณเปลี่ยน / คำขอเดิมยังทำงาน / transactionId ซ้ำแต่ข้อมูลต่าง |
| 403 | `self_approval` · `access_denied` | อนุมัติงานตัวเอง / ไม่มีสิทธิ์ |
| 410 | `reservation_expired` · `late_submission` | การจองหมดอายุ / ส่งรายการย้อนหลังเกินกำหนด |
| 422 | `idempotency_key_reused` | key เดิมแต่ body ต่าง |
| 422 | `otp_invalid` · `otp_expired` · `otp_locked` | OTP ผิด / หมดอายุ / ผิดเกินจำนวนครั้ง |
| 429 | `rate_limited` | เกิน rate limit (`Retry-After`) |
| 503 | `dependency_unavailable` | MessageHub / CustomerMgnt ภายนอกใช้ไม่ได้ (การขายและคะแนนไม่ขึ้นกับระบบเหล่านี้) |

## 26. งานเบื้องหลังและการสื่อสารกับระบบอื่น

| งาน | รอบ | หมายเหตุ |
|---|---|---|
| ปล่อย reservation หมดอายุ | ทุก 1 นาที | คืนคะแนน คูปอง สิทธิ์ และงบ |
| ตัดคะแนนหมดอายุ | ทุกวัน 00:15 (Asia/Bangkok) | idempotency key `expire:{lotId}` |
| แจ้งเตือนคะแนนใกล้หมดอายุ | ทุกวัน 09:00 | ผ่าน outbox → MessageHub |
| เปลี่ยนสถานะแคมเปญตามเวลา | ทุก 1 นาที | Scheduled → Active → Ended, งบหมด → Ended |
| ประเมิน Tier ครบรอบ | ทุกวัน 01:00 | ขึ้น Tier ทำทันทีตอน commit |
| คำนวณ Segment | ทุกวัน 02:00 และเมื่อสั่ง | |
| ประมวลผลคืนสินค้าที่มาก่อนรายการขาย | ทุก 5 นาที | ค้างเกิน 7 วันแจ้ง alert ผ่าน LogServer |
| ส่ง outbox | ต่อเนื่อง | retry แบบ exponential backoff + jitter (สูงสุด 10 ครั้ง / 24 ชม.) แล้วย้ายไป dead-letter + alert |
| ลบ idempotency key เกิน 7 วัน, ไฟล์ export เกิน 24 ชม. | ทุกชั่วโมง | |

- ทุก job รันได้หลาย instance โดยใช้ lease lock ในฐานข้อมูล (ทำงานที่ instance เดียวต่อรอบ)
- การเรียกออก (MessageHub `api/v1/messages`, CustomerMgnt ภายใน, AuthServer) ใช้ `Microsoft.Extensions.Http.Resilience`: timeout 10 วินาที, retry 3 ครั้ง exponential + jitter เฉพาะ 408/429/5xx, circuit breaker เปิด 30 วินาทีเมื่อ error ≥ 50% ใน 30 วินาที (ขั้นต่ำ 20 คำขอ)
- ข้อความที่ส่งออกมี `eventId` ไม่ซ้ำ ปลายทางใช้กันซ้ำได้; ข้อความขาเข้า (เช่น returns) ตรวจลำดับด้วย state machine ของรายการขาย
- event ที่ outbox ส่ง: `member.registered`, `member.updated`, `member.tier_changed`, `points.earned`, `points.redeemed`, `points.expiring`, `coupon.issued`, `campaign.status_changed`

## 27. หน้าจอ (Blazor WASM) และการตั้งค่า

| หัวข้อ | รายละเอียด |
|---|---|
| โปรเจกต์ | `src/CustomerMgnt.Membership.Web` (Blazor WebAssembly, `StaticWebAssetBasePath=membership`) ใช้ `external/webcommon` (`WebCommon.UI`: AppShell, ListTemplate, FormTemplate, ReportTemplate) — หน้าสร้าง/ดู/แก้เป็นหน้าเต็ม ไม่ใช้ popup |
| URL | Back Office `/membership/`, Member Portal `/membership/portal/...` — API โฮสต์ไฟล์เอง (`/membership` → 302 `/membership/`, `/membership/_content/*` → `/_content/*`, SPA fallback `membership/{*path:nonfile}`) |
| Build | API อ้างอิงโปรเจกต์ UI เว้นแต่ `-p:IncludeMembershipUi=false` (ใช้กับ test / build ที่ไม่ต้องการ UI) — clone ต้อง `git submodule update --init --recursive` |
| Login เจ้าหน้าที่ | AuthServer `POST api/v1/auth/login` ด้วย client `customermgnt-membership` (ค่า `Auth:*` ใน `wwwroot/appsettings.json`) — เมนูซ่อนตามสิทธิ์ใน claim `permissions` |
| Login สมาชิก | Google Identity Services / Facebook SDK → `POST api/v1/auth/external` ด้วย client `membership-portal` (`Portal:ClientId`, `Portal:GoogleClientId`, `Portal:FacebookAppId`) → ยังไม่ผูกสมาชิก (`not_registered`) ไปหน้าสมัคร |
| CSP | `/membership/*` อนุญาต script/frame/connect ของ `accounts.google.com/gsi` และ Facebook เท่านั้น ไม่มี inline script; AuthServer คนละ origin ให้เพิ่มใน `Membership:Ui:ConnectSrc` (https origin คั่นด้วยช่องว่าง) |
| OTP ตอนพัฒนา | `Membership:OtpFixedCodeForDevelopment` (เช่น `123456`) = ไม่ส่ง SMS ใช้รหัสคงที่ — ตั้งได้เฉพาะ Development (environment อื่น API ไม่ start) |
| ภาษา / วันที่ | ข้อความ webcommon เป็นภาษาไทย (`UiText.Override = Thai`) วันที่แสดงเป็นปีพุทธศักราช |
| Library ใหม่ | `QRCoder` 1.6.0 (MIT) สร้าง QR ของคูปองเป็น SVG ฝั่ง client |
