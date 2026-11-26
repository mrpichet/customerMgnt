-- ระบบวิเคราะห์ข้อมูลสมาชิกและโปรโมชัน (ตาราง ana_*) — Claude.MD ส่วนที่ 3
-- รายการทดสอบไม่นับในการวิเคราะห์ (ค่าเดียวกับ field isTest ใน data)
ALTER TABLE mbr_transaction ADD is_test BIT NOT NULL DEFAULT 0;
GO

-- ค่าตั้งและ master data แบบเอกสาร: ขอบเขตข้อมูล, ตัวกรองที่บันทึก, รายงาน, ตั้งเวลา, กฎ/เหตุการณ์แจ้งเตือน, funnel, ตั้งค่า,
-- สถานะการคำนวณ, ปัญหาข้อมูล, สาขา, สินค้า, ต้นทุนแคมเปญ, ยอดขายรวม
CREATE TABLE ana_document (
    collection VARCHAR(50) NOT NULL,
    id VARCHAR(200) NOT NULL,
    owner VARCHAR(200) NULL,
    version BIGINT NOT NULL,
    updated_at DATETIMEOFFSET NOT NULL,
    data NVARCHAR(MAX) NOT NULL,
    PRIMARY KEY (collection, id)
);
GO
CREATE INDEX ix_ana_document_owner ON ana_document (collection, owner, updated_at);
GO
CREATE INDEX ix_ana_document_updated ON ana_document (collection, updated_at);
GO

-- read model: ยอดของสมาชิกต่อวัน/สาขา/ช่องทาง (แบรนด์ หมวด สิทธิ์แคมเปญ ต้นทุน อยู่ใน data)
CREATE TABLE ana_member_daily (
    day DATE NOT NULL,
    member_id BIGINT NOT NULL,
    branch_code VARCHAR(30) NOT NULL,
    channel VARCHAR(20) NOT NULL,
    net NUMERIC(18,2) NOT NULL,
    bills INT NOT NULL,
    data NVARCHAR(MAX) NOT NULL,
    PRIMARY KEY (day, member_id, branch_code, channel)
);
GO
CREATE INDEX ix_ana_member_daily_member ON ana_member_daily (member_id, day);
GO

CREATE TABLE ana_member_snapshot (
    member_id BIGINT NOT NULL PRIMARY KEY,
    refreshed_at DATETIMEOFFSET NOT NULL,
    data NVARCHAR(MAX) NOT NULL
);
GO

CREATE TABLE ana_points_daily (
    day DATE NOT NULL,
    type VARCHAR(20) NOT NULL,
    branch_code VARCHAR(30) NOT NULL,
    points BIGINT NOT NULL,
    entries INT NOT NULL,
    PRIMARY KEY (day, type, branch_code)
);
GO

CREATE TABLE ana_coupon_daily (
    day DATE NOT NULL,
    campaign_code VARCHAR(30) NOT NULL,
    batch_id BIGINT NOT NULL,
    event VARCHAR(20) NOT NULL,
    branch_code VARCHAR(30) NOT NULL,
    cnt INT NOT NULL,
    PRIMARY KEY (day, campaign_code, batch_id, event, branch_code)
);
GO

-- วันที่ต้องคำนวณใหม่ (จาก ingest / คืนสินค้า / เปลี่ยนตั้งค่า)
CREATE TABLE ana_dirty_day (
    tbl VARCHAR(50) NOT NULL,
    day DATE NOT NULL,
    marked_at DATETIMEOFFSET NOT NULL,
    PRIMARY KEY (tbl, day)
);
GO

CREATE TABLE ana_message_recipient (
    message_id VARCHAR(100) NOT NULL PRIMARY KEY,
    campaign_code VARCHAR(30) NULL,
    member_id BIGINT NOT NULL,
    sent_at DATETIMEOFFSET NOT NULL
);
GO
CREATE INDEX ix_ana_recipient_campaign ON ana_message_recipient (campaign_code, sent_at);
GO

CREATE TABLE ana_message_event (
    event_id VARCHAR(36) NOT NULL PRIMARY KEY,
    type VARCHAR(20) NOT NULL,
    message_id VARCHAR(100) NULL,
    campaign_code VARCHAR(30) NULL,
    member_id BIGINT NOT NULL,
    occurred_at DATETIMEOFFSET NOT NULL
);
GO
CREATE INDEX ix_ana_event_campaign ON ana_message_event (campaign_code, occurred_at);
GO
CREATE INDEX ix_ana_event_at ON ana_message_event (occurred_at);
GO
