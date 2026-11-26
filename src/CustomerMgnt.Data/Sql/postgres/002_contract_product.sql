-- ผูกสัญญากับผลิตภัณฑ์ใน ProductMgnt (รหัสตัวพิมพ์ใหญ่) เพื่อตอบ GET /api/products/{code}/customers
ALTER TABLE contract ADD product_code VARCHAR(50) NULL;
CREATE INDEX ix_contract_product ON contract (product_code, status, end_date);
