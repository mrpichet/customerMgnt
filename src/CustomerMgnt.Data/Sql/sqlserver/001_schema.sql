-- =====================================================================================
-- CustomerMgnt schema (SQL Server 2022+ / Azure SQL) — โครงสร้างเดียวกับ Sql/postgres/001_schema.sql
-- ความต่างจาก PostgreSQL: ข้อความทุกคอลัมน์เป็น NVARCHAR (เก็บภาษาไทยได้โดยไม่ขึ้นกับ collation),
-- BIT แทน BIT, DATETIMEOFFSET แทน DATETIMEOFFSET(7), filtered index แทน partial index, ไม่มี expression index
-- หลักการ: ทุกตารางมี PK แบบ identity, FK ครบ, CHECK สำหรับค่า enum/ช่วง, index ตาม query จริงของ ADO
-- ข้อมูลอ่อนไหว (เลขผู้เสียภาษี) เก็บเป็น PQC envelope + blind index เท่านั้น ไม่มี plaintext
-- =====================================================================================

-- ลูกค้า (บุคคล = 1 / นิติบุคคล = 2)
CREATE TABLE customer (
    id                BIGINT IDENTITY(1,1) PRIMARY KEY,
    code              NVARCHAR(40)    NOT NULL,
    kind              INT            NOT NULL CHECK (kind IN (1, 2)),
    display_name      NVARCHAR(300)   NOT NULL,
    legal_name        NVARCHAR(300),
    first_name        NVARCHAR(150),
    last_name         NVARCHAR(150),
    tax_id_masked     NVARCHAR(40),
    tax_id_envelope   NVARCHAR(MAX),
    tax_id_hash       VARCHAR(64),
    email             NVARCHAR(254),
    phone             NVARCHAR(40),
    industry          NVARCHAR(120),
    employee_count    INT CHECK (employee_count IS NULL OR employee_count >= 0),
    annual_revenue    DECIMAL(18, 2) CHECK (annual_revenue IS NULL OR annual_revenue >= 0),
    website           NVARCHAR(300),
    address           NVARCHAR(1000),
    province          NVARCHAR(120),
    country           NCHAR(2)       NOT NULL DEFAULT 'TH',
    time_zone_id      NVARCHAR(64)    NOT NULL DEFAULT 'Asia/Bangkok',
    status            INT            NOT NULL CHECK (status BETWEEN 1 AND 4),
    owner_subject     NVARCHAR(128),
    tags              NVARCHAR(500),
    segment           NVARCHAR(60),
    marketing_consent BIT        NOT NULL DEFAULT 0,
    created_at        DATETIMEOFFSET(7)    NOT NULL,
    updated_at        DATETIMEOFFSET(7)    NOT NULL,
    version           BIGINT         NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX ux_customer_code ON customer (code);
CREATE UNIQUE INDEX ux_customer_tax_id_hash ON customer (tax_id_hash) WHERE tax_id_hash IS NOT NULL;
CREATE INDEX ix_customer_status ON customer (status);
CREATE INDEX ix_customer_owner ON customer (owner_subject);
CREATE INDEX ix_customer_segment ON customer (segment);
-- collation ปกติของ SQL Server ไม่สนตัวพิมพ์ จึงใช้ index ธรรมดาแทน lower(email)
CREATE INDEX ix_customer_email ON customer (email);

-- ความสัมพันธ์บุคคล ↔ นิติบุคคล (ตำแหน่ง + ช่วงเวลา; บุคคลหนึ่งอยู่ได้หลายบริษัท)
CREATE TABLE party_relationship (
    id                 BIGINT IDENTITY(1,1) PRIMARY KEY,
    person_id          BIGINT       NOT NULL REFERENCES customer (id),
    organization_id    BIGINT       NOT NULL REFERENCES customer (id),
    position           NVARCHAR(150) NOT NULL,
    department         NVARCHAR(150),
    is_primary_contact BIT      NOT NULL DEFAULT 0,
    is_decision_maker  BIT      NOT NULL DEFAULT 0,
    valid_from         DATE         NOT NULL,
    valid_to           DATE,
    CHECK (person_id <> organization_id),
    CHECK (valid_to IS NULL OR valid_to >= valid_from)
);
CREATE INDEX ix_relationship_person ON party_relationship (person_id);
CREATE INDEX ix_relationship_org ON party_relationship (organization_id);

-- ตัวตนของลูกค้าในแต่ละช่องทาง (LINE userId, อีเมล, Facebook PSID, ...)
CREATE TABLE channel_identity (
    id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    customer_id  BIGINT       NOT NULL REFERENCES customer (id),
    channel      INT          NOT NULL,
    external_id  NVARCHAR(200) NOT NULL,
    display_name NVARCHAR(200),
    verified     BIT      NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX ux_identity_channel_external ON channel_identity (channel, external_id);
CREATE INDEX ix_identity_customer ON channel_identity (customer_id);

-- ห้องสนทนา omnichannel
CREATE TABLE conversation (
    id                BIGINT IDENTITY(1,1) PRIMARY KEY,
    customer_id       BIGINT REFERENCES customer (id),
    channel           INT          NOT NULL,
    external_user_id  NVARCHAR(200) NOT NULL,
    subject           NVARCHAR(300),
    status            INT          NOT NULL CHECK (status BETWEEN 1 AND 3),
    assignee_subject  NVARCHAR(128),
    last_sentiment    FLOAT NOT NULL DEFAULT 0,
    needs_attention   BIT      NOT NULL DEFAULT 0,
    last_message_at   DATETIMEOFFSET(7)  NOT NULL,
    first_response_at DATETIMEOFFSET(7),
    created_at        DATETIMEOFFSET(7)  NOT NULL
);
-- ห้องที่ยังไม่ปิดของผู้ใช้หนึ่งคนต่อช่องทางมีได้ห้องเดียว
CREATE UNIQUE INDEX ux_conversation_open ON conversation (channel, external_user_id) WHERE status <> 3;
CREATE INDEX ix_conversation_customer ON conversation (customer_id, last_message_at DESC);
CREATE INDEX ix_conversation_status ON conversation (status, needs_attention, last_message_at DESC);

CREATE TABLE chat_message (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    conversation_id     BIGINT        NOT NULL REFERENCES conversation (id),
    direction           INT           NOT NULL CHECK (direction IN (1, 2)),
    body                NVARCHAR(MAX) NOT NULL,
    external_message_id NVARCHAR(200),
    sender_subject      NVARCHAR(128),
    sentiment           FLOAT NOT NULL DEFAULT 0,
    sentiment_label     INT           NOT NULL DEFAULT 0,
    sent_at             DATETIMEOFFSET(7)   NOT NULL
);
-- กันข้อความซ้ำจาก webhook ที่ส่งซ้ำ (idempotency)
CREATE UNIQUE INDEX ux_message_external ON chat_message (conversation_id, external_message_id) WHERE external_message_id IS NOT NULL;
CREATE INDEX ix_message_conversation ON chat_message (conversation_id, sent_at DESC);

-- กิจกรรม (โทร, ประชุม, อีเมล, แชท, งาน) + การเตือน
CREATE TABLE activity (
    id                     BIGINT IDENTITY(1,1) PRIMARY KEY,
    customer_id            BIGINT       NOT NULL REFERENCES customer (id),
    opportunity_id         BIGINT,
    kind                   INT          NOT NULL,
    subject                NVARCHAR(300) NOT NULL,
    notes                  NVARCHAR(4000),
    status                 INT          NOT NULL CHECK (status BETWEEN 1 AND 3),
    owner_subject          NVARCHAR(128) NOT NULL,
    due_at                 DATETIMEOFFSET(7)  NOT NULL,
    duration_minutes       INT,
    remind_before_minutes  INT,
    reminder_sent_at       DATETIMEOFFSET(7),
    completed_at           DATETIMEOFFSET(7),
    outcome                NVARCHAR(2000),
    message_hub_meeting_id UNIQUEIDENTIFIER,
    created_at             DATETIMEOFFSET(7)  NOT NULL
);
CREATE INDEX ix_activity_customer ON activity (customer_id, due_at DESC);
CREATE INDEX ix_activity_owner_open ON activity (owner_subject, due_at) WHERE status = 1;
CREATE INDEX ix_activity_reminder ON activity (due_at) WHERE status = 1 AND reminder_sent_at IS NULL AND remind_before_minutes IS NOT NULL;

-- คำขอของลูกค้า / TOR
CREATE TABLE customer_request (
    id                BIGINT IDENTITY(1,1) PRIMARY KEY,
    request_no        NVARCHAR(40)   NOT NULL,
    customer_id       BIGINT        NOT NULL REFERENCES customer (id),
    contact_person_id BIGINT REFERENCES customer (id),
    kind              INT           NOT NULL,
    title             NVARCHAR(300)  NOT NULL,
    description       NVARCHAR(MAX),
    status            INT           NOT NULL,
    severity_code     NVARCHAR(20),
    budget            DECIMAL(18, 2),
    desired_date      DATE,
    owner_subject     NVARCHAR(128),
    forward_signature NVARCHAR(MAX),
    forwarded_at      DATETIMEOFFSET(7),
    created_at        DATETIMEOFFSET(7)   NOT NULL,
    updated_at        DATETIMEOFFSET(7)   NOT NULL,
    version           BIGINT        NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX ux_request_no ON customer_request (request_no);
CREATE INDEX ix_request_customer ON customer_request (customer_id, created_at DESC);
CREATE INDEX ix_request_status ON customer_request (status, created_at DESC);

CREATE TABLE tor_item (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    request_id      BIGINT        NOT NULL REFERENCES customer_request (id) ON DELETE CASCADE,
    item_no         NVARCHAR(20)   NOT NULL,
    text            NVARCHAR(MAX) NOT NULL,
    priority        NVARCHAR(20)   NOT NULL,
    mandatory       BIT       NOT NULL DEFAULT 1,
    requirement_key NVARCHAR(60)
);
CREATE UNIQUE INDEX ux_tor_item_no ON tor_item (request_id, item_no);

-- เลขลำดับคำขอรายปี (นับแบบ atomic ด้วย MERGE ... HOLDLOCK ... OUTPUT)
CREATE TABLE request_sequence (
    year  INT PRIMARY KEY,
    value INT NOT NULL
);

-- โอกาสการขาย / pipeline
CREATE TABLE opportunity (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    customer_id         BIGINT         NOT NULL REFERENCES customer (id),
    title               NVARCHAR(300)   NOT NULL,
    stage               INT            NOT NULL CHECK (stage BETWEEN 1 AND 7),
    amount              DECIMAL(18, 2) NOT NULL CHECK (amount >= 0),
    probability         INT CHECK (probability IS NULL OR probability BETWEEN 0 AND 100),
    score               FLOAT,
    expected_close_date DATE           NOT NULL,
    source              NVARCHAR(100),
    owner_subject       NVARCHAR(128),
    lost_reason         NVARCHAR(1000),
    stage_changed_at    DATETIMEOFFSET(7)    NOT NULL,
    closed_at           DATETIMEOFFSET(7),
    created_at          DATETIMEOFFSET(7)    NOT NULL,
    version             BIGINT         NOT NULL DEFAULT 1
);
CREATE INDEX ix_opportunity_customer ON opportunity (customer_id);
CREATE INDEX ix_opportunity_open ON opportunity (stage) WHERE stage < 6; -- filtered index ไม่รองรับ NOT IN (stage 6 = Won, 7 = Lost)
CREATE INDEX ix_opportunity_closed ON opportunity (closed_at) WHERE closed_at IS NOT NULL;

CREATE TABLE stage_history (
    id             BIGINT IDENTITY(1,1) PRIMARY KEY,
    opportunity_id BIGINT       NOT NULL REFERENCES opportunity (id) ON DELETE CASCADE,
    from_stage     INT          NOT NULL,
    to_stage       INT          NOT NULL,
    changed_by     NVARCHAR(128) NOT NULL,
    changed_at     DATETIMEOFFSET(7)  NOT NULL
);
CREATE INDEX ix_stage_history_opp ON stage_history (opportunity_id, changed_at);

-- นโยบาย SLA (customer_id NULL = นโยบายมาตรฐาน) + ระดับ severity ที่ลูกค้ากำหนดเอง
CREATE TABLE sla_policy (
    id                 BIGINT IDENTITY(1,1) PRIMARY KEY,
    customer_id        BIGINT REFERENCES customer (id),
    name               NVARCHAR(150) NOT NULL,
    description        NVARCHAR(1000),
    valid_from         DATE         NOT NULL,
    valid_to           DATE,
    time_zone_id       NVARCHAR(64)  NOT NULL,
    business_start     TIME         NOT NULL,
    business_end       TIME         NOT NULL,
    working_days_mask  INT          NOT NULL CHECK (working_days_mask BETWEEN 1 AND 127),
    holidays           NVARCHAR(4000),
    at_risk_threshold  FLOAT NOT NULL CHECK (at_risk_threshold > 0 AND at_risk_threshold < 1),
    is_active          BIT      NOT NULL DEFAULT 1,
    version            BIGINT       NOT NULL DEFAULT 1,
    CHECK (business_end > business_start),
    CHECK (valid_to IS NULL OR valid_to >= valid_from)
);
CREATE INDEX ix_sla_policy_customer ON sla_policy (customer_id);

CREATE TABLE severity_level (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    policy_id           BIGINT       NOT NULL REFERENCES sla_policy (id) ON DELETE CASCADE,
    code                NVARCHAR(20)  NOT NULL,
    name                NVARCHAR(100) NOT NULL,
    description         NVARCHAR(500),
    rank                INT          NOT NULL CHECK (rank BETWEEN 1 AND 5),
    response_minutes    INT          NOT NULL CHECK (response_minutes > 0),
    resolution_minutes  INT          NOT NULL CHECK (resolution_minutes > 0),
    business_hours_only BIT      NOT NULL DEFAULT 1,
    issue_priority      NVARCHAR(20)  NOT NULL,
    CHECK (resolution_minutes >= response_minutes)
);
CREATE UNIQUE INDEX ux_severity_policy_code ON severity_level (policy_id, code);

-- ลิงก์ไปยังระบบอื่น (ProjectMgnt / IssueTracking / RequirementMgnt / MessageHub)
CREATE TABLE external_link (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    customer_id         BIGINT       NOT NULL REFERENCES customer (id),
    system              INT          NOT NULL CHECK (system BETWEEN 1 AND 4),
    entity_type         NVARCHAR(40)  NOT NULL,
    external_id         NVARCHAR(64)  NOT NULL,
    title               NVARCHAR(300),
    customer_request_id BIGINT REFERENCES customer_request (id),
    created_at          DATETIMEOFFSET(7)  NOT NULL
);
CREATE UNIQUE INDEX ux_external_link ON external_link (customer_id, system, entity_type, external_id);
CREATE INDEX ix_external_link_request ON external_link (customer_request_id) WHERE customer_request_id IS NOT NULL;

-- พฤติกรรมลูกค้า (input ของ ML) — append-only, ปริมาณมาก
CREATE TABLE engagement_event (
    id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    customer_id BIGINT         NOT NULL REFERENCES customer (id),
    kind        INT            NOT NULL,
    channel     INT,
    item_code   NVARCHAR(60),
    value       DECIMAL(18, 2) NOT NULL DEFAULT 0,
    occurred_at DATETIMEOFFSET(7)    NOT NULL
);
CREATE INDEX ix_engagement_customer_time ON engagement_event (customer_id, occurred_at);
CREATE INDEX ix_engagement_purchase ON engagement_event (occurred_at) WHERE kind = 5;

-- สัญญา MA/บริการ
CREATE TABLE contract (
    id            BIGINT IDENTITY(1,1) PRIMARY KEY,
    customer_id   BIGINT         NOT NULL REFERENCES customer (id),
    contract_no   NVARCHAR(60)    NOT NULL,
    title         NVARCHAR(300)   NOT NULL,
    start_date    DATE           NOT NULL,
    end_date      DATE           NOT NULL,
    annual_value  DECIMAL(18, 2) NOT NULL CHECK (annual_value >= 0),
    status        INT            NOT NULL CHECK (status BETWEEN 1 AND 4),
    renewal_count INT            NOT NULL DEFAULT 0,
    sla_policy_id BIGINT REFERENCES sla_policy (id),
    CHECK (end_date >= start_date)
);
CREATE UNIQUE INDEX ux_contract_no ON contract (contract_no);
CREATE INDEX ix_contract_customer ON contract (customer_id);
CREATE INDEX ix_contract_end ON contract (end_date);

-- แคตตาล็อกสินค้า/บริการ/เนื้อหา (ใช้กับระบบแนะนำ)
CREATE TABLE catalog_item (
    code      NVARCHAR(60)    PRIMARY KEY,
    name      NVARCHAR(200)   NOT NULL,
    kind      INT            NOT NULL CHECK (kind BETWEEN 1 AND 4),
    category  NVARCHAR(100),
    price     DECIMAL(18, 2) NOT NULL DEFAULT 0,
    is_active BIT        NOT NULL DEFAULT 1
);

-- โปรไฟล์ผู้ใช้ภายใน (map subject ของ AuthServer → user id ของ MessageHub)
CREATE TABLE user_profile (
    subject             NVARCHAR(128) PRIMARY KEY,
    display_name        NVARCHAR(200) NOT NULL,
    message_hub_user_id UNIQUEIDENTIFIER
);

-- นโยบาย SLA มาตรฐานเริ่มต้น (ลูกค้าที่ไม่มีนโยบายเฉพาะใช้ชุดนี้)
INSERT INTO sla_policy (customer_id, name, description, valid_from, time_zone_id, business_start, business_end, working_days_mask, at_risk_threshold)
VALUES (NULL, 'Standard SLA', 'Default policy for customers without a specific agreement', '2020-01-01', 'Asia/Bangkok', '08:30', '17:30', 31, 0.8);

INSERT INTO severity_level (policy_id, code, name, description, rank, response_minutes, resolution_minutes, business_hours_only, issue_priority)
SELECT p.id, v.code, v.name, v.description, v.rank, v.response_minutes, v.resolution_minutes, v.bho, v.priority
FROM sla_policy p
CROSS JOIN (VALUES
    ('S1', 'Critical', 'Production down, no workaround', 1, 30, 240, 0, 'Immediate'),
    ('S2', 'High', 'Major function impaired', 2, 60, 960, 1, 'Urgent'),
    ('S3', 'Medium', 'Partial impact with workaround', 3, 240, 2400, 1, 'High'),
    ('S4', 'Low', 'Minor issue or question', 4, 480, 4800, 1, 'Normal')
) AS v(code, name, description, rank, response_minutes, resolution_minutes, bho, priority)
WHERE p.customer_id IS NULL;
