-- ระบบสมาชิกและโปรโมชัน (ตาราง mbr_*) — คอลัมน์ที่ใช้ค้นหาแยกออกมา ข้อมูลเต็มของแถวเก็บใน data (JSON)
CREATE TABLE mbr_member (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    member_no VARCHAR(20) NOT NULL,
    auth_subject VARCHAR(200) NULL,
    phone_hash VARCHAR(100) NULL,
    email_hash VARCHAR(100) NULL,
    status VARCHAR(30) NOT NULL,
    tier_code VARCHAR(20) NULL,
    customer_code VARCHAR(30) NULL,
    tier_review_at DATETIMEOFFSET NULL,
    first_name NVARCHAR(100) NOT NULL,
    last_name NVARCHAR(100) NOT NULL,
    created_at DATETIMEOFFSET NOT NULL,
    version BIGINT NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_member_consent (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    member_id BIGINT NOT NULL,
    at DATETIMEOFFSET NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_tier_history (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    member_id BIGINT NOT NULL,
    changed_at DATETIMEOFFSET NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_tier (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    code VARCHAR(20) NOT NULL,
    rank INT NOT NULL,
    version BIGINT NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_point_account (
    member_id BIGINT NOT NULL PRIMARY KEY,
    lot_balance INT NOT NULL,
    reserved INT NOT NULL,
    debt INT NOT NULL,
    lifetime_earned BIGINT NOT NULL,
    version BIGINT NOT NULL
);
GO
CREATE TABLE mbr_point_lot (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    member_id BIGINT NOT NULL,
    points INT NOT NULL,
    remaining INT NOT NULL,
    earned_at DATETIMEOFFSET NOT NULL,
    expires_at DATETIMEOFFSET NOT NULL,
    source_key VARCHAR(200) NOT NULL
);
GO
CREATE TABLE mbr_point_ledger (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    member_id BIGINT NOT NULL,
    type VARCHAR(20) NOT NULL,
    points INT NOT NULL,
    balance_after INT NOT NULL,
    source_system VARCHAR(100) NULL,
    transaction_id VARCHAR(100) NULL,
    reference VARCHAR(200) NULL,
    reason NVARCHAR(500) NULL,
    actor VARCHAR(200) NOT NULL,
    request_id VARCHAR(100) NULL,
    idempotency_key VARCHAR(250) NOT NULL,
    created_at DATETIMEOFFSET NOT NULL
);
GO
CREATE TABLE mbr_lot_allocation (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    ledger_key VARCHAR(250) NOT NULL,
    lot_id BIGINT NOT NULL,
    points INT NOT NULL
);
GO
CREATE TABLE mbr_earn_rule (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    code VARCHAR(30) NOT NULL,
    version BIGINT NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_adjustment (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    member_id BIGINT NOT NULL,
    status VARCHAR(30) NOT NULL,
    requested_by VARCHAR(200) NOT NULL,
    points INT NOT NULL,
    created_at DATETIMEOFFSET NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_campaign (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    code VARCHAR(30) NOT NULL,
    name NVARCHAR(200) NOT NULL,
    status VARCHAR(30) NOT NULL,
    reward_type VARCHAR(30) NOT NULL,
    start_at DATETIMEOFFSET NOT NULL,
    end_at DATETIMEOFFSET NOT NULL,
    priority INT NOT NULL,
    version BIGINT NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_campaign_counter (
    campaign_id BIGINT NOT NULL,
    scope VARCHAR(80) NOT NULL,
    uses INT NOT NULL,
    reserved INT NOT NULL,
    budget_used DECIMAL(18,2) NOT NULL,
    budget_reserved DECIMAL(18,2) NOT NULL,
    PRIMARY KEY (campaign_id, scope)
);
GO
CREATE TABLE mbr_coupon_batch (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    campaign_id BIGINT NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_coupon (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    code VARCHAR(60) NOT NULL,
    batch_id BIGINT NOT NULL,
    campaign_id BIGINT NOT NULL,
    member_id BIGINT NULL,
    status VARCHAR(20) NOT NULL,
    expires_at DATETIMEOFFSET NOT NULL,
    created_at DATETIMEOFFSET NOT NULL,
    version BIGINT NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_coupon_event (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    code VARCHAR(60) NOT NULL,
    at DATETIMEOFFSET NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_segment (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    version BIGINT NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_segment_member (
    segment_id BIGINT NOT NULL,
    member_id BIGINT NOT NULL,
    PRIMARY KEY (segment_id, member_id)
);
GO
CREATE TABLE mbr_reservation (
    id VARCHAR(80) NOT NULL PRIMARY KEY,
    status VARCHAR(20) NOT NULL,
    expires_at DATETIMEOFFSET NOT NULL,
    version BIGINT NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_transaction (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    source_system VARCHAR(100) NOT NULL,
    transaction_id VARCHAR(100) NOT NULL,
    member_id BIGINT NOT NULL,
    branch_code VARCHAR(30) NULL,
    occurred_at DATETIMEOFFSET NOT NULL,
    status VARCHAR(30) NOT NULL,
    net_paid DECIMAL(18,2) NOT NULL,
    eligible DECIMAL(18,2) NOT NULL,
    returned_eligible DECIMAL(18,2) NOT NULL,
    version BIGINT NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_return (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    source_system VARCHAR(100) NOT NULL,
    transaction_id VARCHAR(100) NOT NULL,
    return_id VARCHAR(100) NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_pending_return (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    source_system VARCHAR(100) NOT NULL,
    transaction_id VARCHAR(100) NOT NULL,
    return_id VARCHAR(100) NOT NULL,
    processed_at DATETIMEOFFSET NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_approval (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    type VARCHAR(30) NOT NULL,
    status VARCHAR(30) NOT NULL,
    requested_by VARCHAR(200) NOT NULL,
    version BIGINT NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_audit (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    at DATETIMEOFFSET NOT NULL,
    actor VARCHAR(200) NOT NULL,
    action VARCHAR(100) NOT NULL,
    target VARCHAR(200) NOT NULL,
    prev_hash VARCHAR(64) NULL,
    hash VARCHAR(64) NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_outbox (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    event_id VARCHAR(64) NOT NULL,
    topic VARCHAR(100) NOT NULL,
    payload NVARCHAR(MAX) NOT NULL,
    attempts INT NOT NULL,
    next_attempt_at DATETIMEOFFSET NOT NULL,
    created_at DATETIMEOFFSET NOT NULL,
    sent_at DATETIMEOFFSET NULL,
    dead_lettered BIT NOT NULL,
    last_error NVARCHAR(500) NULL
);
GO
CREATE TABLE mbr_idempotency (
    source_system VARCHAR(100) NOT NULL,
    idem_key VARCHAR(200) NOT NULL,
    request_hash VARCHAR(100) NOT NULL,
    status_code INT NOT NULL,
    response NVARCHAR(MAX) NULL,
    created_at DATETIMEOFFSET NOT NULL,
    expires_at DATETIMEOFFSET NOT NULL,
    PRIMARY KEY (source_system, idem_key)
);
GO
CREATE TABLE mbr_settings (
    id INT NOT NULL PRIMARY KEY,
    version BIGINT NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_consent_version (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_integration_client (
    client_id VARCHAR(100) NOT NULL PRIMARY KEY,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_otp (
    id VARCHAR(64) NOT NULL PRIMARY KEY,
    phone_hash VARCHAR(100) NOT NULL,
    created_at DATETIMEOFFSET NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_privacy_request (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    member_id BIGINT NOT NULL,
    status VARCHAR(20) NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE TABLE mbr_job_lease (
    job VARCHAR(100) NOT NULL PRIMARY KEY,
    holder VARCHAR(200) NOT NULL,
    until_at DATETIMEOFFSET NOT NULL
);
GO
CREATE TABLE mbr_export (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    requested_by VARCHAR(200) NOT NULL,
    status VARCHAR(20) NOT NULL,
    expires_at DATETIMEOFFSET NULL,
    data NVARCHAR(MAX) NOT NULL,
    content VARBINARY(MAX) NULL
);
GO
CREATE TABLE mbr_message_job (
    id BIGINT IDENTITY(1,1) PRIMARY KEY,
    status VARCHAR(20) NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO
CREATE UNIQUE INDEX ux_mbr_member_no ON mbr_member (member_no);
GO
CREATE INDEX ix_mbr_member_subject ON mbr_member (auth_subject);
GO
CREATE INDEX ix_mbr_member_phone ON mbr_member (phone_hash);
GO
CREATE INDEX ix_mbr_member_email ON mbr_member (email_hash);
GO
CREATE INDEX ix_mbr_member_review ON mbr_member (tier_review_at);
GO
CREATE INDEX ix_mbr_consent_member ON mbr_member_consent (member_id);
GO
CREATE INDEX ix_mbr_tier_history_member ON mbr_tier_history (member_id);
GO
CREATE UNIQUE INDEX ux_mbr_tier_code ON mbr_tier (code);
GO
CREATE INDEX ix_mbr_lot_member ON mbr_point_lot (member_id, expires_at);
GO
CREATE INDEX ix_mbr_lot_expiry ON mbr_point_lot (expires_at, remaining);
GO
CREATE UNIQUE INDEX ux_mbr_ledger_key ON mbr_point_ledger (idempotency_key);
GO
CREATE INDEX ix_mbr_ledger_member ON mbr_point_ledger (member_id, created_at);
GO
CREATE INDEX ix_mbr_ledger_txn ON mbr_point_ledger (transaction_id);
GO
CREATE INDEX ix_mbr_alloc_key ON mbr_lot_allocation (ledger_key);
GO
CREATE UNIQUE INDEX ux_mbr_rule_code ON mbr_earn_rule (code);
GO
CREATE INDEX ix_mbr_adjust_by ON mbr_adjustment (requested_by, created_at);
GO
CREATE UNIQUE INDEX ux_mbr_campaign_code ON mbr_campaign (code);
GO
CREATE INDEX ix_mbr_campaign_status ON mbr_campaign (status);
GO
CREATE INDEX ix_mbr_batch_campaign ON mbr_coupon_batch (campaign_id);
GO
CREATE UNIQUE INDEX ux_mbr_coupon_code ON mbr_coupon (code);
GO
CREATE INDEX ix_mbr_coupon_member ON mbr_coupon (member_id, status);
GO
CREATE INDEX ix_mbr_coupon_batch ON mbr_coupon (batch_id, status);
GO
CREATE INDEX ix_mbr_coupon_expiry ON mbr_coupon (status, expires_at);
GO
CREATE INDEX ix_mbr_coupon_event_code ON mbr_coupon_event (code);
GO
CREATE INDEX ix_mbr_segment_member_member ON mbr_segment_member (member_id);
GO
CREATE INDEX ix_mbr_reservation_expiry ON mbr_reservation (status, expires_at);
GO
CREATE UNIQUE INDEX ux_mbr_txn_source ON mbr_transaction (source_system, transaction_id);
GO
CREATE INDEX ix_mbr_txn_member ON mbr_transaction (member_id, occurred_at);
GO
CREATE INDEX ix_mbr_txn_date ON mbr_transaction (occurred_at);
GO
CREATE UNIQUE INDEX ux_mbr_return ON mbr_return (source_system, transaction_id, return_id);
GO
CREATE UNIQUE INDEX ux_mbr_pending_return ON mbr_pending_return (source_system, transaction_id, return_id);
GO
CREATE INDEX ix_mbr_approval_status ON mbr_approval (status, type);
GO
CREATE INDEX ix_mbr_audit_target ON mbr_audit (target);
GO
CREATE INDEX ix_mbr_audit_at ON mbr_audit (at);
GO
CREATE UNIQUE INDEX ux_mbr_outbox_event ON mbr_outbox (event_id);
GO
CREATE INDEX ix_mbr_outbox_due ON mbr_outbox (sent_at, dead_lettered, next_attempt_at);
GO
CREATE INDEX ix_mbr_idem_expiry ON mbr_idempotency (expires_at);
GO
CREATE INDEX ix_mbr_otp_phone ON mbr_otp (phone_hash, created_at);
GO
CREATE INDEX ix_mbr_privacy_member ON mbr_privacy_request (member_id);
GO
INSERT INTO mbr_settings (id, version, data) VALUES (1, 0, '{}');
