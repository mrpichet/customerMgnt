-- =====================================================================================
-- CustomerMgnt schema (PostgreSQL 15+) — snake_case ↔ PascalCase ผ่าน Dapper MatchNamesWithUnderscores
-- หลักการ: ทุกตารางมี PK แบบ identity, FK ครบ, CHECK สำหรับค่า enum/ช่วง, index ตาม query จริงของ ADO
-- ข้อมูลอ่อนไหว (เลขผู้เสียภาษี) เก็บเป็น PQC envelope + blind index เท่านั้น ไม่มี plaintext
-- =====================================================================================

-- ลูกค้า (บุคคล = 1 / นิติบุคคล = 2)
CREATE TABLE customer (
    id                BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code              VARCHAR(40)    NOT NULL,
    kind              INT            NOT NULL CHECK (kind IN (1, 2)),
    display_name      VARCHAR(300)   NOT NULL,
    legal_name        VARCHAR(300),
    first_name        VARCHAR(150),
    last_name         VARCHAR(150),
    tax_id_masked     VARCHAR(40),
    tax_id_envelope   TEXT,
    tax_id_hash       CHAR(64),
    email             VARCHAR(254),
    phone             VARCHAR(40),
    industry          VARCHAR(120),
    employee_count    INT CHECK (employee_count IS NULL OR employee_count >= 0),
    annual_revenue    NUMERIC(18, 2) CHECK (annual_revenue IS NULL OR annual_revenue >= 0),
    website           VARCHAR(300),
    address           VARCHAR(1000),
    province          VARCHAR(120),
    country           CHAR(2)        NOT NULL DEFAULT 'TH',
    time_zone_id      VARCHAR(64)    NOT NULL DEFAULT 'Asia/Bangkok',
    status            INT            NOT NULL CHECK (status BETWEEN 1 AND 4),
    owner_subject     VARCHAR(128),
    tags              VARCHAR(500),
    segment           VARCHAR(60),
    marketing_consent BOOLEAN        NOT NULL DEFAULT FALSE,
    created_at        TIMESTAMPTZ    NOT NULL,
    updated_at        TIMESTAMPTZ    NOT NULL,
    version           BIGINT         NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX ux_customer_code ON customer (code);
CREATE UNIQUE INDEX ux_customer_tax_id_hash ON customer (tax_id_hash) WHERE tax_id_hash IS NOT NULL;
CREATE INDEX ix_customer_status ON customer (status);
CREATE INDEX ix_customer_owner ON customer (owner_subject);
CREATE INDEX ix_customer_segment ON customer (segment);
CREATE INDEX ix_customer_email ON customer (lower(email));

-- ความสัมพันธ์บุคคล ↔ นิติบุคคล (ตำแหน่ง + ช่วงเวลา; บุคคลหนึ่งอยู่ได้หลายบริษัท)
CREATE TABLE party_relationship (
    id                 BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    person_id          BIGINT       NOT NULL REFERENCES customer (id),
    organization_id    BIGINT       NOT NULL REFERENCES customer (id),
    position           VARCHAR(150) NOT NULL,
    department         VARCHAR(150),
    is_primary_contact BOOLEAN      NOT NULL DEFAULT FALSE,
    is_decision_maker  BOOLEAN      NOT NULL DEFAULT FALSE,
    valid_from         DATE         NOT NULL,
    valid_to           DATE,
    CHECK (person_id <> organization_id),
    CHECK (valid_to IS NULL OR valid_to >= valid_from)
);
CREATE INDEX ix_relationship_person ON party_relationship (person_id);
CREATE INDEX ix_relationship_org ON party_relationship (organization_id);

-- ตัวตนของลูกค้าในแต่ละช่องทาง (LINE userId, อีเมล, Facebook PSID, ...)
CREATE TABLE channel_identity (
    id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id  BIGINT       NOT NULL REFERENCES customer (id),
    channel      INT          NOT NULL,
    external_id  VARCHAR(200) NOT NULL,
    display_name VARCHAR(200),
    verified     BOOLEAN      NOT NULL DEFAULT FALSE
);
CREATE UNIQUE INDEX ux_identity_channel_external ON channel_identity (channel, external_id);
CREATE INDEX ix_identity_customer ON channel_identity (customer_id);

-- ห้องสนทนา omnichannel
CREATE TABLE conversation (
    id                BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id       BIGINT REFERENCES customer (id),
    channel           INT          NOT NULL,
    external_user_id  VARCHAR(200) NOT NULL,
    subject           VARCHAR(300),
    status            INT          NOT NULL CHECK (status BETWEEN 1 AND 3),
    assignee_subject  VARCHAR(128),
    last_sentiment    DOUBLE PRECISION NOT NULL DEFAULT 0,
    needs_attention   BOOLEAN      NOT NULL DEFAULT FALSE,
    last_message_at   TIMESTAMPTZ  NOT NULL,
    first_response_at TIMESTAMPTZ,
    created_at        TIMESTAMPTZ  NOT NULL
);
-- ห้องที่ยังไม่ปิดของผู้ใช้หนึ่งคนต่อช่องทางมีได้ห้องเดียว
CREATE UNIQUE INDEX ux_conversation_open ON conversation (channel, external_user_id) WHERE status <> 3;
CREATE INDEX ix_conversation_customer ON conversation (customer_id, last_message_at DESC);
CREATE INDEX ix_conversation_status ON conversation (status, needs_attention, last_message_at DESC);

CREATE TABLE chat_message (
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    conversation_id     BIGINT        NOT NULL REFERENCES conversation (id),
    direction           INT           NOT NULL CHECK (direction IN (1, 2)),
    body                VARCHAR(8000) NOT NULL,
    external_message_id VARCHAR(200),
    sender_subject      VARCHAR(128),
    sentiment           DOUBLE PRECISION NOT NULL DEFAULT 0,
    sentiment_label     INT           NOT NULL DEFAULT 0,
    sent_at             TIMESTAMPTZ   NOT NULL
);
-- กันข้อความซ้ำจาก webhook ที่ส่งซ้ำ (idempotency)
CREATE UNIQUE INDEX ux_message_external ON chat_message (conversation_id, external_message_id) WHERE external_message_id IS NOT NULL;
CREATE INDEX ix_message_conversation ON chat_message (conversation_id, sent_at DESC);

-- กิจกรรม (โทร, ประชุม, อีเมล, แชท, งาน) + การเตือน
CREATE TABLE activity (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id            BIGINT       NOT NULL REFERENCES customer (id),
    opportunity_id         BIGINT,
    kind                   INT          NOT NULL,
    subject                VARCHAR(300) NOT NULL,
    notes                  VARCHAR(4000),
    status                 INT          NOT NULL CHECK (status BETWEEN 1 AND 3),
    owner_subject          VARCHAR(128) NOT NULL,
    due_at                 TIMESTAMPTZ  NOT NULL,
    duration_minutes       INT,
    remind_before_minutes  INT,
    reminder_sent_at       TIMESTAMPTZ,
    completed_at           TIMESTAMPTZ,
    outcome                VARCHAR(2000),
    message_hub_meeting_id UUID,
    created_at             TIMESTAMPTZ  NOT NULL
);
CREATE INDEX ix_activity_customer ON activity (customer_id, due_at DESC);
CREATE INDEX ix_activity_owner_open ON activity (owner_subject, due_at) WHERE status = 1;
CREATE INDEX ix_activity_reminder ON activity (due_at) WHERE status = 1 AND reminder_sent_at IS NULL AND remind_before_minutes IS NOT NULL;

-- คำขอของลูกค้า / TOR
CREATE TABLE customer_request (
    id                BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    request_no        VARCHAR(40)   NOT NULL,
    customer_id       BIGINT        NOT NULL REFERENCES customer (id),
    contact_person_id BIGINT REFERENCES customer (id),
    kind              INT           NOT NULL,
    title             VARCHAR(300)  NOT NULL,
    description       VARCHAR(20000),
    status            INT           NOT NULL,
    severity_code     VARCHAR(20),
    budget            NUMERIC(18, 2),
    desired_date      DATE,
    owner_subject     VARCHAR(128),
    forward_signature TEXT,
    forwarded_at      TIMESTAMPTZ,
    created_at        TIMESTAMPTZ   NOT NULL,
    updated_at        TIMESTAMPTZ   NOT NULL,
    version           BIGINT        NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX ux_request_no ON customer_request (request_no);
CREATE INDEX ix_request_customer ON customer_request (customer_id, created_at DESC);
CREATE INDEX ix_request_status ON customer_request (status, created_at DESC);

CREATE TABLE tor_item (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    request_id      BIGINT        NOT NULL REFERENCES customer_request (id) ON DELETE CASCADE,
    item_no         VARCHAR(20)   NOT NULL,
    text            VARCHAR(8000) NOT NULL,
    priority        VARCHAR(20)   NOT NULL,
    mandatory       BOOLEAN       NOT NULL DEFAULT TRUE,
    requirement_key VARCHAR(60)
);
CREATE UNIQUE INDEX ux_tor_item_no ON tor_item (request_id, item_no);

-- เลขลำดับคำขอรายปี (นับแบบ atomic ด้วย UPSERT ... RETURNING)
CREATE TABLE request_sequence (
    year  INT PRIMARY KEY,
    value INT NOT NULL
);

-- โอกาสการขาย / pipeline
CREATE TABLE opportunity (
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id         BIGINT         NOT NULL REFERENCES customer (id),
    title               VARCHAR(300)   NOT NULL,
    stage               INT            NOT NULL CHECK (stage BETWEEN 1 AND 7),
    amount              NUMERIC(18, 2) NOT NULL CHECK (amount >= 0),
    probability         INT CHECK (probability IS NULL OR probability BETWEEN 0 AND 100),
    score               DOUBLE PRECISION,
    expected_close_date DATE           NOT NULL,
    source              VARCHAR(100),
    owner_subject       VARCHAR(128),
    lost_reason         VARCHAR(1000),
    stage_changed_at    TIMESTAMPTZ    NOT NULL,
    closed_at           TIMESTAMPTZ,
    created_at          TIMESTAMPTZ    NOT NULL,
    version             BIGINT         NOT NULL DEFAULT 1
);
CREATE INDEX ix_opportunity_customer ON opportunity (customer_id);
CREATE INDEX ix_opportunity_open ON opportunity (stage) WHERE stage NOT IN (6, 7);
CREATE INDEX ix_opportunity_closed ON opportunity (closed_at) WHERE closed_at IS NOT NULL;

CREATE TABLE stage_history (
    id             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    opportunity_id BIGINT       NOT NULL REFERENCES opportunity (id) ON DELETE CASCADE,
    from_stage     INT          NOT NULL,
    to_stage       INT          NOT NULL,
    changed_by     VARCHAR(128) NOT NULL,
    changed_at     TIMESTAMPTZ  NOT NULL
);
CREATE INDEX ix_stage_history_opp ON stage_history (opportunity_id, changed_at);

-- นโยบาย SLA (customer_id NULL = นโยบายมาตรฐาน) + ระดับ severity ที่ลูกค้ากำหนดเอง
CREATE TABLE sla_policy (
    id                 BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id        BIGINT REFERENCES customer (id),
    name               VARCHAR(150) NOT NULL,
    description        VARCHAR(1000),
    valid_from         DATE         NOT NULL,
    valid_to           DATE,
    time_zone_id       VARCHAR(64)  NOT NULL,
    business_start     TIME         NOT NULL,
    business_end       TIME         NOT NULL,
    working_days_mask  INT          NOT NULL CHECK (working_days_mask BETWEEN 1 AND 127),
    holidays           VARCHAR(4000),
    at_risk_threshold  DOUBLE PRECISION NOT NULL CHECK (at_risk_threshold > 0 AND at_risk_threshold < 1),
    is_active          BOOLEAN      NOT NULL DEFAULT TRUE,
    version            BIGINT       NOT NULL DEFAULT 1,
    CHECK (business_end > business_start),
    CHECK (valid_to IS NULL OR valid_to >= valid_from)
);
CREATE INDEX ix_sla_policy_customer ON sla_policy (customer_id);

CREATE TABLE severity_level (
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    policy_id           BIGINT       NOT NULL REFERENCES sla_policy (id) ON DELETE CASCADE,
    code                VARCHAR(20)  NOT NULL,
    name                VARCHAR(100) NOT NULL,
    description         VARCHAR(500),
    rank                INT          NOT NULL CHECK (rank BETWEEN 1 AND 5),
    response_minutes    INT          NOT NULL CHECK (response_minutes > 0),
    resolution_minutes  INT          NOT NULL CHECK (resolution_minutes > 0),
    business_hours_only BOOLEAN      NOT NULL DEFAULT TRUE,
    issue_priority      VARCHAR(20)  NOT NULL,
    CHECK (resolution_minutes >= response_minutes)
);
CREATE UNIQUE INDEX ux_severity_policy_code ON severity_level (policy_id, code);

-- ลิงก์ไปยังระบบอื่น (ProjectMgnt / IssueTracking / RequirementMgnt / MessageHub)
CREATE TABLE external_link (
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id         BIGINT       NOT NULL REFERENCES customer (id),
    system              INT          NOT NULL CHECK (system BETWEEN 1 AND 4),
    entity_type         VARCHAR(40)  NOT NULL,
    external_id         VARCHAR(64)  NOT NULL,
    title               VARCHAR(300),
    customer_request_id BIGINT REFERENCES customer_request (id),
    created_at          TIMESTAMPTZ  NOT NULL
);
CREATE UNIQUE INDEX ux_external_link ON external_link (customer_id, system, entity_type, external_id);
CREATE INDEX ix_external_link_request ON external_link (customer_request_id) WHERE customer_request_id IS NOT NULL;

-- พฤติกรรมลูกค้า (input ของ ML) — append-only, ปริมาณมาก
CREATE TABLE engagement_event (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id BIGINT         NOT NULL REFERENCES customer (id),
    kind        INT            NOT NULL,
    channel     INT,
    item_code   VARCHAR(60),
    value       NUMERIC(18, 2) NOT NULL DEFAULT 0,
    occurred_at TIMESTAMPTZ    NOT NULL
);
CREATE INDEX ix_engagement_customer_time ON engagement_event (customer_id, occurred_at);
CREATE INDEX ix_engagement_purchase ON engagement_event (occurred_at) WHERE kind = 5;

-- สัญญา MA/บริการ
CREATE TABLE contract (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id   BIGINT         NOT NULL REFERENCES customer (id),
    contract_no   VARCHAR(60)    NOT NULL,
    title         VARCHAR(300)   NOT NULL,
    start_date    DATE           NOT NULL,
    end_date      DATE           NOT NULL,
    annual_value  NUMERIC(18, 2) NOT NULL CHECK (annual_value >= 0),
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
    code      VARCHAR(60)    PRIMARY KEY,
    name      VARCHAR(200)   NOT NULL,
    kind      INT            NOT NULL CHECK (kind BETWEEN 1 AND 4),
    category  VARCHAR(100),
    price     NUMERIC(18, 2) NOT NULL DEFAULT 0,
    is_active BOOLEAN        NOT NULL DEFAULT TRUE
);

-- โปรไฟล์ผู้ใช้ภายใน (map subject ของ AuthServer → user id ของ MessageHub)
CREATE TABLE user_profile (
    subject             VARCHAR(128) PRIMARY KEY,
    display_name        VARCHAR(200) NOT NULL,
    message_hub_user_id UUID
);

-- นโยบาย SLA มาตรฐานเริ่มต้น (ลูกค้าที่ไม่มีนโยบายเฉพาะใช้ชุดนี้)
INSERT INTO sla_policy (customer_id, name, description, valid_from, time_zone_id, business_start, business_end, working_days_mask, at_risk_threshold)
VALUES (NULL, 'Standard SLA', 'Default policy for customers without a specific agreement', DATE '2020-01-01', 'Asia/Bangkok', TIME '08:30', TIME '17:30', 31, 0.8);

INSERT INTO severity_level (policy_id, code, name, description, rank, response_minutes, resolution_minutes, business_hours_only, issue_priority)
SELECT p.id, v.code, v.name, v.description, v.rank, v.response_minutes, v.resolution_minutes, v.bho, v.priority
FROM sla_policy p
CROSS JOIN (VALUES
    ('S1', 'Critical', 'Production down, no workaround', 1, 30, 240, FALSE, 'Immediate'),
    ('S2', 'High', 'Major function impaired', 2, 60, 960, TRUE, 'Urgent'),
    ('S3', 'Medium', 'Partial impact with workaround', 3, 240, 2400, TRUE, 'High'),
    ('S4', 'Low', 'Minor issue or question', 4, 480, 4800, TRUE, 'Normal')
) AS v(code, name, description, rank, response_minutes, resolution_minutes, bho, priority)
WHERE p.customer_id IS NULL;
