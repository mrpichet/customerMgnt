# CustomerMgnt API catalog

Version: 1.0.0.0  
Authentication: `Authorization: Bearer <AuthServer access token>`

## REST endpoints

| Method | Route | Auth | Summary |
|---|---|---|---|
| POST | `/api/activities` | perm:activity.write | สร้างกิจกรรม (ถ้าเป็น Meeting และตั้งค่า MessageHub ไว้ จะสร้างการประชุม + เชิญ + ตั้งเตือนให้) |
| POST | `/api/activities/{id:long}/complete` | perm:activity.write | ปิดกิจกรรม (พร้อมสร้างงานติดตามต่อได้) |
| GET | `/api/activities/mine` | perm:customer.read | งานค้างของฉัน (เลยกำหนด + ภายใน N วันข้างหน้า) |
| GET | `/api/activities/customer/{customerId:long}` | perm:customer.read | กิจกรรมของลูกค้า |
| GET | `/api/customers/{customerId:long}/agenda` | perm:customer.read | วาระ/งานค้างของลูกค้า (นัดหมายถัดไป, งานเลยกำหนด, วาระประชุมจาก MessageHub) |
| GET | `/api/ai/tools` | perm:analytics.read | รายชื่อ tools พร้อมคำอธิบายและ JSON schema ของพารามิเตอร์ |
| GET | `/api/analytics/leads` | perm:analytics.read | คะแนน lead (โอกาสปิดการขาย) พร้อมปัจจัยที่มีผล |
| GET | `/api/analytics/churn` | perm:analytics.read | ความเสี่ยงลูกค้าเลิกใช้บริการ (churn) |
| GET | `/api/analytics/renewals` | perm:analytics.read | พยากรณ์การต่อสัญญา MA และโอกาส upsell ของสัญญาที่จะหมดอายุ |
| GET | `/api/analytics/forecast/sales` | perm:analytics.read | พยากรณ์ยอดขายรายเดือน (Holt-Winters + pipeline ถ่วงน้ำหนัก) |
| GET | `/api/analytics/forecast/budget/{customerId:long}` | perm:analytics.read | พยากรณ์งบประมาณ/การใช้จ่ายของลูกค้าตามฤดูกาล |
| GET | `/api/analytics/nba/{customerId:long}` | perm:analytics.read | สิ่งที่ควรทำต่อไปกับลูกค้า (next best action) |
| GET | `/api/analytics/recommendations/{customerId:long}` | perm:analytics.read | แนะนำสินค้า/บริการ/บทความ (item-based collaborative filtering) |
| GET | `/api/analytics/send-time/{customerId:long}` | perm:analytics.read | เวลาที่เหมาะสมที่สุดในการส่งข้อความหาลูกค้า |
| GET | `/api/analytics/segments` | perm:analytics.read | แบ่งกลุ่มลูกค้าแบบ dynamic (k-means) — ไม่บันทึกผล |
| POST | `/api/analytics/segments` | perm:analytics.read | แบ่งกลุ่มลูกค้าและบันทึกชื่อกลุ่มลงข้อมูลลูกค้า (ต้องมีสิทธิ์ customer.write ด้วย) |
| POST | `/api/analytics/sentiment` | perm:analytics.read | วิเคราะห์อารมณ์ข้อความ (ไทย/อังกฤษ) |
| GET | `/api/analytics/risk/{customerId:long}` | perm:analytics.read | คะแนนความเสี่ยงของลูกค้า (churn, SLA breach, อายุ issue, อารมณ์, สัญญาใกล้หมด, งานค้าง) |
| GET | `/api/analytics/risk/portfolio` | perm:analytics.read | ภาพรวมความเสี่ยงของลูกค้ากลุ่มเสี่ยงสูงสุด |
| GET | `/api/analytics/models` | perm:analytics.read | ข้อมูลโมเดลที่ใช้อยู่ (จำนวนตัวอย่าง, log loss, เวลาฝึก) |
| POST | `/api/analytics/models/retrain` | perm:data.ingest | ล้าง cache โมเดล (ฝึกใหม่ในการเรียกครั้งถัดไป) — ใช้หลังนำเข้าข้อมูลชุดใหญ่ |
| POST | `/api/v1/campaign-messages` | perm:campaign.message.send | ตั้งงานส่งข้อความ (ส่งเฉพาะคนที่ยินยอมช่องทางนั้น) |
| GET | `/api/v1/campaign-messages` | perm:campaign.message.send | สถานะงานส่งข้อความ |
| POST | `/api/contracts` | perm:customer.write | บันทึกสัญญา |
| GET | `/api/conversations` | perm:chat.read | รายการห้องสนทนา (ห้องที่ต้องดูแลขึ้นก่อน) |
| GET | `/api/conversations/{id:long}/messages` | perm:chat.read | ข้อความในห้อง |
| POST | `/api/conversations/{id:long}/reply` | perm:chat.reply | ตอบกลับลูกค้า (ส่งผ่าน MessageHub ตามช่องทางของห้อง) |
| PATCH | `/api/conversations/{id:long}` | perm:chat.reply | มอบหมาย/ผูกลูกค้า/เปลี่ยนสถานะห้อง |
| GET | `/api/v1/coupons/batches` | perm:coupon.read | ชุดคูปอง |
| POST | `/api/v1/coupons/batches` | perm:coupon.write | สร้างชุดคูปอง (สร้างรหัสเบื้องหลัง) |
| GET | `/api/v1/coupons` | perm:coupon.read | ค้นหาคูปอง |
| GET | `/api/v1/coupons/{code}` | perm:coupon.read | อ่านคูปองพร้อมประวัติ |
| POST | `/api/v1/coupons/{code}/cancel` | perm:coupon.write | ยกเลิกคูปอง |
| POST | `/api/v1/coupons/issue` | perm:coupon.write | แจกคูปองให้สมาชิก / Segment (เบื้องหลัง) |
| GET | `/api/customers` | perm:customer.read | ค้นหาลูกค้า |
| POST | `/api/customers` | perm:customer.write | ลงทะเบียนลูกค้าใหม่ (เลขผู้เสียภาษีถูกเข้ารหัส PQC และตรวจซ้ำด้วย blind index) |
| GET | `/api/customers/{id:long}` | perm:customer.read | อ่านลูกค้า |
| GET | `/api/customers/by-code/{code}` | perm:customer.read | อ่านลูกค้าจากรหัสลูกค้า (เช่น CUST-000123) |
| PUT | `/api/customers/{id:long}` | perm:customer.write | แก้ไขลูกค้า (ต้องส่ง version ล่าสุด) |
| GET | `/api/customers/{id:long}/360` | perm:customer.read | ภาพรวม 360° ของลูกค้า |
| POST | `/api/customers/{id:long}/identities` | perm:customer.write | เพิ่มตัวตนในช่องทาง (LINE userId, Facebook PSID, อีเมล, ...) |
| GET | `/api/customers/{id:long}/relationships` | perm:customer.read | ความสัมพันธ์ของลูกค้า (บุคคล → บริษัทที่สังกัด, บริษัท → ผู้ติดต่อ) |
| POST | `/api/customers/{id:long}/links` | perm:customer.write | ผูกลูกค้ากับโครงการ/issue/การประชุมในระบบอื่น |
| GET | `/api/customers/{id:long}/projects` | perm:customer.read | สรุปโครงการของลูกค้า (Active/Closed พร้อมสถานะและความคืบหน้าจาก ProjectMgnt) |
| GET | `/api/customers/{id:long}/contracts` | perm:customer.read | สัญญาของลูกค้า |
| POST | `/api/ingest/events` | perm:data.ingest | นำเข้าเหตุการณ์พฤติกรรม (ซื้อ, เปิดอีเมล, เข้าเว็บ, ...) สูงสุด 10,000 รายการต่อครั้ง |
| GET | `/api/ingest/catalog` | perm:customer.read | รายการแคตตาล็อก |
| PUT | `/api/ingest/catalog/{code}` | perm:data.ingest | เพิ่ม/แก้รายการแคตตาล็อก |
| GET | `/api/insights/issues/aging` | perm:customer.read | สรุป issue ที่ยังเปิด แยกตาม severity และช่วงอายุ (customerId ว่าง = ทุกลูกค้า) |
| GET | `/api/v1/members` | perm:member.read | ค้นหาสมาชิก (ข้อมูลส่วนบุคคลแบบ mask) |
| POST | `/api/v1/members` | perm:member.write | สมัครสมาชิกโดยเจ้าหน้าที่ (สถานะ PendingVerification และส่ง OTP) |
| GET | `/api/v1/members/{id:long}` | perm:member.read | อ่านสมาชิก |
| GET | `/api/v1/members/by-no/{memberNo}` | perm:member.read | อ่านสมาชิกจากเลขสมาชิก |
| PUT | `/api/v1/members/{id:long}` | perm:member.write | แก้ข้อมูลสมาชิก — เปลี่ยนเบอร์ได้ 202 พร้อม OTP ของเบอร์ใหม่ |
| POST | `/api/v1/members/{id:long}/reveal` | perm:member.pii | ดูข้อมูลส่วนบุคคลเต็ม (บันทึก audit) |
| POST | `/api/v1/members/{id:long}/otp` | perm:member.write | ส่ง OTP ใหม่ |
| POST | `/api/v1/members/{id:long}/verify` | perm:member.write | ยืนยัน OTP → Active |
| POST | `/api/v1/members/{id:long}/verify-override` | perm:member.verify.override | ยืนยันแทน OTP (ตรวจบัตรต่อหน้า) |
| PUT | `/api/v1/members/{id:long}/status` | perm:member.status | เปลี่ยนสถานะสมาชิก |
| PUT | `/api/v1/members/{id:long}/customer` | perm:member.write | ผูกกับลูกค้า (customerCode = null ให้ระบบสร้างลูกค้าบุคคลใหม่) |
| GET | `/api/v1/members/{id:long}/tier-history` | perm:member.read | ประวัติ Tier |
| GET | `/api/v1/members/{id:long}/consents` | perm:member.read | ประวัติความยินยอม |
| GET | `/api/v1/members/{id:long}/audit` | perm:audit.read | audit log ของสมาชิกคนนี้ |
| POST | `/api/v1/members/duplicates/check` | perm:member.write | ตรวจเบอร์/อีเมลซ้ำ |
| GET | `/api/v1/members/merges/preview` | perm:member.merge | เทียบข้อมูลก่อนรวมบัญชี |
| POST | `/api/v1/members/merges` | perm:member.merge | ยื่นขอรวมบัญชี (รออนุมัติ) |
| GET | `/api/v1/approvals` | authenticated | ค้นหาคำขอ |
| GET | `/api/v1/approvals/pending-count` | authenticated | จำนวนคำขอที่รอฉันพิจารณา |
| GET | `/api/v1/approvals/{id:long}` | authenticated | อ่านคำขอ |
| POST | `/api/v1/approvals/{id:long}/decision` | authenticated | อนุมัติ / ไม่อนุมัติ (ไม่อนุมัติต้องมีหมายเหตุ, อนุมัติงานตัวเองไม่ได้) |
| POST | `/api/v1/approvals/{id:long}/cancel` | authenticated | ผู้ยื่นยกเลิกคำขอ |
| GET | `/api/v1/dashboard` | perm:member.read | ตัวเลขภาพรวมตามสิทธิ์ของผู้เรียก |
| GET | `/api/v1/reports/{report:regex(^(members|points|promotions|transactions)$)}` | perm:report.read | รายงานบนหน้าจอ (members / points / promotions / transactions, ≤ 5,000 แถว) |
| POST | `/api/v1/reports/exports` | perm:report.export | ขอส่งออกรายงาน (เบื้องหลัง) |
| GET | `/api/v1/reports/exports` | perm:report.export | งานส่งออกของฉัน |
| GET | `/api/v1/reports/exports/{id:long}/file` | perm:report.export | ดาวน์โหลดไฟล์ (เจ้าของงานเท่านั้น หมดอายุ 24 ชม.) |
| GET | `/api/v1/settings/rules` | perm:settings.manage | กฎธุรกิจปัจจุบัน |
| PUT | `/api/v1/settings/rules` | perm:settings.manage | บันทึกกฎธุรกิจ (ต้องส่ง version) |
| GET | `/api/v1/settings/consents` | perm:settings.manage | ฉบับความยินยอม |
| POST | `/api/v1/settings/consents` | perm:settings.manage | ร่างฉบับใหม่ |
| POST | `/api/v1/settings/consents/{id:long}/publish` | perm:settings.manage | เผยแพร่ฉบับ (แก้ไม่ได้อีก) |
| GET | `/api/v1/settings/integrations` | perm:settings.manage | ระบบภายนอกที่เรียก API |
| PUT | `/api/v1/settings/integrations` | perm:settings.manage | เพิ่ม/แก้ระบบภายนอก |
| GET | `/api/v1/settings/roles` | perm:settings.manage | บทบาทและสิทธิ์ที่แนะนำให้สร้างใน AuthServer |
| GET | `/api/v1/audit` | perm:audit.read | ค้น audit log ของระบบสมาชิก |
| GET | `/api/v1/transactions` | perm:txn.read | ค้นหารายการขาย |
| GET | `/api/v1/transactions/{id:long}` | perm:txn.read | รายละเอียดรายการขาย (สินค้า สิทธิประโยชน์ คะแนน การคืน) |
| POST | `/api/opportunities` | perm:sales.write | สร้าง lead/opportunity |
| GET | `/api/opportunities` | perm:sales.read | รายการ opportunity |
| POST | `/api/opportunities/{id:long}/stage` | perm:sales.write | ย้าย stage (ต้องส่ง version ล่าสุด; Won/Lost ปิดดีล) |
| GET | `/api/opportunities/summary` | perm:sales.read | สรุป pipeline ตาม stage (มูลค่า, มูลค่าถ่วงน้ำหนัก, win rate) |
| GET | `/api/v1/points/balances/{memberId:long}` | perm:points.read | ยอดคะแนนของสมาชิก |
| GET | `/api/v1/points/ledger` | perm:points.read | รายการเคลื่อนไหวคะแนน (append-only) |
| GET | `/api/v1/points/lots/{memberId:long}` | perm:points.read | lot คะแนน (FEFO) |
| GET | `/api/v1/points/rules` | perm:points.read | กฎการได้คะแนน |
| POST | `/api/v1/points/rules` | perm:points.rule.manage | สร้างกฎ (รออนุมัติ) |
| PUT | `/api/v1/points/rules/{id:long}` | perm:points.rule.manage | แก้กฎ (field ที่ต้องอนุมัติ → PendingApproval) |
| POST | `/api/v1/points/adjustments` | perm:points.adjust | ยื่นปรับคะแนน (ในวงเงิน = Applied, เกิน = PendingApproval) |
| GET | `/api/v1/points/adjustments` | perm:points.read | รายการปรับคะแนน |
| GET | `/api/v1/me/member` | authenticated | ข้อมูลสมาชิกของฉัน (ไม่ mask) |
| POST | `/api/v1/me/registration` | authenticated | เริ่มสมัคร — ส่ง OTP ไปที่เบอร์ |
| POST | `/api/v1/me/registration/verify` | authenticated | ยืนยัน OTP แล้วสร้าง/ผูกสมาชิก |
| PUT | `/api/v1/me/member` | authenticated | แก้ข้อมูลของฉัน — เปลี่ยนเบอร์ได้ 202 + OTP |
| POST | `/api/v1/me/member/phone/verify` | authenticated | ยืนยัน OTP ของเบอร์ใหม่ |
| GET | `/api/v1/me/points` | authenticated | ยอดคะแนนของฉัน |
| GET | `/api/v1/me/points/ledger` | authenticated | รายการเคลื่อนไหวคะแนนของฉัน |
| GET | `/api/v1/me/rewards` | authenticated | ของรางวัลที่แลกด้วยคะแนนได้ |
| POST | `/api/v1/me/rewards/{rewardId:long}/redeem` | authenticated | แลกของรางวัล (ต้องมี Idempotency-Key) |
| GET | `/api/v1/me/coupons` | authenticated | คูปองของฉัน (มี qrPayload) |
| GET | `/api/v1/me/promotions` | authenticated | โปรโมชันที่ฉันมีสิทธิ์ |
| GET | `/api/v1/me/purchases` | authenticated | ประวัติการซื้อของฉัน |
| GET | `/api/v1/me/consents/current` | authenticated | ฉบับความยินยอมปัจจุบัน (ใช้ตอนสมัคร ยังไม่ต้องเป็นสมาชิก) |
| GET | `/api/v1/me/consents` | authenticated | ความยินยอมของฉันและฉบับปัจจุบัน |
| PUT | `/api/v1/me/consents` | authenticated | ให้/ถอนความยินยอม |
| POST | `/api/v1/me/privacy-requests` | authenticated | ยื่นคำขอใช้สิทธิ์ตาม PDPA |
| GET | `/api/v1/me/privacy-requests` | authenticated | คำขอ PDPA ของฉัน |
| GET | `/api/v1/pos/members/lookup` | perm:pos.lookup | ค้นหาสมาชิกด้วยเบอร์ / เลขสมาชิก / รหัสคูปอง |
| POST | `/api/v1/pos/checkout/quote` | perm:pos.checkout | คำนวณส่วนลด/คะแนน (ไม่จองอะไร) |
| POST | `/api/v1/pos/checkout/reserve` | perm:pos.checkout | จองคะแนน คูปอง และสิทธิ์ — ผลต่างจาก quoteId เดิม = 409 quote_changed พร้อม quote ใหม่ใน payload |
| POST | `/api/v1/pos/checkout/{reservationId}/commit` | perm:pos.checkout | ยืนยันการขาย (ตัดคะแนน ใช้คูปอง ให้คะแนน) |
| POST | `/api/v1/pos/checkout/{reservationId}/release` | perm:pos.checkout | ยกเลิกการจอง (ซ้ำได้) |
| POST | `/api/v1/pos/transactions` | perm:pos.transaction | บันทึกการขายที่ไม่ผ่าน checkout (offline — ได้คะแนนพื้นฐานอย่างเดียว) |
| GET | `/api/v1/pos/transactions/{transactionId}` | perm:pos.transaction | อ่านรายการขายของระบบต้นทางนี้ |
| POST | `/api/v1/pos/transactions/{transactionId}/cancel` | perm:pos.transaction | ยกเลิกรายการขาย (void) |
| POST | `/api/v1/pos/transactions/{transactionId}/returns` | perm:pos.transaction | คืนสินค้า — ยังไม่พบรายการขาย (มาผิดลำดับ) = 202 PendingOriginal แล้วประมวลผลภายหลัง |
| GET | `/api/v1/privacy-requests` | perm:privacy.manage | คำขอทั้งหมด |
| GET | `/api/v1/privacy-requests/{id:long}` | perm:privacy.manage | อ่านคำขอ |
| POST | `/api/v1/privacy-requests/{id:long}/complete` | perm:privacy.manage | ปิดคำขอ (Erase = ลบข้อมูลส่วนบุคคลและปิดบัญชี) |
| GET | `/api/products/{code}/customers` | perm:customer.read | ลูกค้าที่มีสัญญา Active ของผลิตภัณฑ์ |
| GET | `/api/me/profile` | perm:customer.read | โปรไฟล์ของฉัน |
| PUT | `/api/me/profile` | perm:customer.read | แก้โปรไฟล์ของฉัน |
| PUT | `/api/users/{subject}/profile` | perm:customer.write | ตั้งโปรไฟล์ของผู้ใช้อื่น (ผู้ดูแลระบบเท่านั้น — ตรวจใน service) |
| GET | `/api/v1/promotions` | perm:promotion.read | ค้นหาแคมเปญ |
| POST | `/api/v1/promotions` | perm:promotion.write | สร้างแคมเปญ (Draft) |
| GET | `/api/v1/promotions/{id:long}` | perm:promotion.read | อ่านแคมเปญ (รวมการใช้สิทธิ์) |
| PUT | `/api/v1/promotions/{id:long}` | perm:promotion.write | แก้แคมเปญ (field ที่ต้องอนุมัติหลังอนุมัติแล้ว → กลับไป PendingApproval) |
| POST | `/api/v1/promotions/{id:long}/submit` | perm:promotion.write | ส่งอนุมัติ |
| POST | `/api/v1/promotions/{id:long}/pause` | perm:promotion.write | หยุดชั่วคราว |
| POST | `/api/v1/promotions/{id:long}/resume` | perm:promotion.write | ใช้งานต่อ |
| POST | `/api/v1/promotions/{id:long}/cancel` | perm:promotion.write | ยกเลิก |
| POST | `/api/v1/promotions/simulate` | perm:promotion.read | จำลองการคำนวณ (รวมแคมเปญ Draft ได้ ไม่บันทึกอะไร) |
| POST | `/api/relationships` | perm:customer.write | ผูกบุคคลกับบริษัท |
| POST | `/api/relationships/{id:long}/end` | perm:customer.write | สิ้นสุดความสัมพันธ์ (เช่น ลาออก/ย้ายตำแหน่ง) |
| POST | `/api/requests` | perm:request.write | บันทึกคำร้อง/TOR ใหม่ |
| GET | `/api/requests` | perm:request.read | รายการคำร้อง |
| GET | `/api/requests/{id:long}` | perm:request.read | อ่านคำร้อง |
| POST | `/api/requests/{id:long}/decision` | perm:request.write | ตัดสินคำร้อง (เริ่มประเมิน / อนุมัติ / ปฏิเสธ) |
| POST | `/api/requests/{id:long}/forward` | perm:request.forward | ส่งต่อคำร้องที่อนุมัติแล้วไป ProjectMgnt / requirementMgnt (ลงนามด้วย PQC เพื่อพิสูจน์ความถูกต้องภายหลัง) |
| GET | `/api/requests/{id:long}/verify-signature` | perm:request.read | ตรวจลายเซ็นของคำร้องที่ส่งต่อแล้ว (ตรวจว่าเนื้อหาไม่ถูกแก้หลังส่ง) |
| GET | `/api/v1/segments` | perm:segment.read | Segment ทั้งหมด |
| GET | `/api/v1/segments/{id:long}` | perm:segment.read | อ่าน Segment |
| POST | `/api/v1/segments` | perm:segment.write | สร้าง Segment |
| PUT | `/api/v1/segments/{id:long}` | perm:segment.write | แก้ Segment (ต้องส่ง version) |
| POST | `/api/v1/segments/preview` | perm:segment.read | ดูจำนวนและตัวอย่างสมาชิกของเงื่อนไข |
| POST | `/api/v1/segments/{id:long}/refresh` | perm:segment.write | คำนวณสมาชิกใหม่ (เบื้องหลัง) |
| GET | `/api/v1/segments/{id:long}/members` | perm:segment.read | สมาชิกใน Segment |
| POST | `/api/session/login` | anonymous | เข้าสู่ระบบด้วยชื่อผู้ใช้/รหัสผ่าน (จำกัดอัตราแบบเข้ม) |
| GET | `/api/session/me` | authenticated | ข้อมูลผู้ใช้ปัจจุบัน (สิทธิ์ที่มี — ให้ UI ซ่อน/แสดงเมนู) |
| POST | `/api/sla/calculate` | perm:sla.read | คำนวณกำหนดเวลา response/resolution ของงานตาม SLA ของลูกค้าและ severity |
| POST | `/api/sla/evaluate` | perm:sla.read | ประเมินสถานะ SLA ของงาน (OnTrack / AtRisk / Breached) |
| GET | `/api/sla/customers/{code}/policy` | perm:sla.read | นโยบาย SLA ที่มีผลกับลูกค้า (ใช้ sync ระดับ severity ไปยัง IssueTracking) |
| GET | `/api/sla/customers/{id:long}/compliance` | perm:sla.read | สรุปการปฏิบัติตาม SLA ของลูกค้า (จาก issue ใน IssueTracking) |
| GET | `/api/sla/policies` | perm:sla.read | รายการนโยบาย (customerId = เฉพาะของลูกค้านั้น + นโยบายกลาง) |
| POST | `/api/sla/policies` | perm:sla.manage | สร้างนโยบาย SLA พร้อมระดับ severity ของลูกค้า |
| PUT | `/api/sla/policies/{id:long}` | perm:sla.manage | แก้ไขนโยบาย SLA (แทนที่ระดับ severity ทั้งชุด; ต้องส่ง version ล่าสุด) |
| GET | `/api/v1/tiers` | perm:member.read | Tier ทั้งหมด (เรียงตาม rank) |
| GET | `/api/v1/tiers/{id:long}` | perm:member.read | อ่าน Tier |
| POST | `/api/v1/tiers` | perm:tier.manage | สร้าง Tier |
| PUT | `/api/v1/tiers/{id:long}` | perm:tier.manage | แก้ Tier (ต้องส่ง version) |
| POST | `/api/v1/tiers/evaluate` | perm:tier.manage | สั่งประเมิน Tier (งานเบื้องหลัง) |
| POST | `/api/webhooks/line` | anonymous | LINE Messaging API webhook — ตรวจ X-Line-Signature = Base64(HMAC-SHA256(channelSecret, body)) แล้วนำข้อความ text เข้าสู่ inbox (ข้อความซ้ำจาก LINE retry ถูกกรองด้วย message id) |
| POST | `/api/webhooks/inbound` | anonymous | webhook กลางสำหรับช่องทางอื่น (email gateway, Facebook/Instagram relay, web chat) — body = InboundMessageRequest, header X-Signature: sha256=<hex HMAC-SHA256(secret, body)> |

## AI / MCP tools

### `analyze_sentiment`

Sentiment analysis of a Thai or English message: score -1..1, label, confidence, detected signals and whether it needs attention (escalation words).

```json
{"type":"object","properties":{"text":{"description":"Message text (max 8000 characters)","type":"string"}},"required":["text"]}
```

### `assess_portfolio_risk`

Ranks the riskiest customers in the portfolio (candidates from the churn model) with a full risk score each, plus a summary (average, P90, count per level).

```json
{"type":"object","properties":{"top":{"description":"How many customers to assess (1-50)","type":"integer","default":20}}}
```

### `calculate_customer_risk`

Calculates the overall account risk score (0-100) of one customer by combining churn probability (ML), SLA breach ratio and aging of open issues (IssueTracking), negative chat sentiment, contract expiry, overdue follow-ups and contact gap. Returns the level (Low/Medium/High/Critical), each factor value and the top contributors.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code, e.g. CUST-000123","type":"string"}},"required":["customerCode"]}
```

### `calculate_sla_targets`

Calculates SLA response and resolution due dates for a customer and one of its severity codes, honoring the policy's business hours, working days and holidays.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code","type":"string"},"severityCode":{"description":"Severity code defined in the customer\u0027s SLA, e.g. S1","type":"string"},"startedAt":{"description":"Start time (ISO 8601); default now","type":["string","null"],"format":"date-time","default":null}},"required":["customerCode","severityCode"]}
```

### `create_customer_request`

Records a new customer request (Request, Tor, ChangeRequest or Incident). Incidents require a severity code from the customer's SLA. Does not forward it to other systems. Requires request.write.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code","type":"string"},"kind":{"description":"Request, Tor, ChangeRequest or Incident","type":"string","enum":["Request","Tor","ChangeRequest","Incident"]},"title":{"description":"Title","type":"string"},"description":{"description":"Description","type":["string","null"],"default":null},"severityCode":{"description":"Severity code (required for Incident)","type":["string","null"],"default":null}},"required":["customerCode","kind","title"]}
```

### `create_follow_up`

Creates a follow-up activity (call, email, task, visit, note) for a customer, owned by the current user, with a reminder 30 minutes before. Requires activity.write.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code","type":"string"},"kind":{"description":"Call, Email, Task, Visit or Note (meetings are created through the API with attendees)","type":"string","enum":["Call","Meeting","Email","Chat","Task","Note","Visit"]},"subject":{"description":"Subject (max 300 characters)","type":"string"},"dueAt":{"description":"Due time (ISO 8601)","type":"string","format":"date-time"},"notes":{"description":"Notes (optional)","type":["string","null"],"default":null}},"required":["customerCode","kind","subject","dueAt"]}
```

### `forecast_renewals`

Forecasts maintenance (MA) contract renewals ending within N days: renewal probability, expected renewal value, upsell/expansion probability and suggested upsell item.

```json
{"type":"object","properties":{"withinDays":{"description":"Contracts ending within this many days (1-730)","type":"integer","default":180}}}
```

### `forecast_sales`

Sales forecast per month (Holt-Winters on purchase history + weighted open pipeline) with ~80% confidence band and monthly seasonal index.

```json
{"type":"object","properties":{"horizonMonths":{"description":"Months ahead (1-24)","type":"integer","default":6},"customerCode":{"description":"Customer code (optional)","type":["string","null"],"default":null}}}
```

### `forecast_seasonal_budget`

Seasonal budget forecast of one customer: expected spend per month and the seasonal index (1.0 = average month) to time proposals with the customer's budget cycle.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code","type":"string"},"horizonMonths":{"description":"Months ahead (1-24)","type":"integer","default":12}},"required":["customerCode"]}
```

### `get_best_send_time`

Send-time optimization: best day/hour (customer's local time) to send a message on a channel, the next send time and expected open rate.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code","type":"string"},"channel":{"description":"Channel: Email, Line, Sms, ...","type":"string","enum":["Line","Email","Facebook","Instagram","WebChat","Phone","Sms","Teams","WhatsApp","Telegram","Review"],"default":"Email"}},"required":["customerCode"]}
```

### `get_customer_360`

Customer 360 view: profile, relationships (people/companies and positions), channel identities, recent conversations, open activities, requests, open opportunities and links to projects/issues.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code","type":"string"}},"required":["customerCode"]}
```

### `get_customer_agenda`

Customer agenda: upcoming meetings (MessageHub), pending agenda items carried from past meetings, open activities and overdue count.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code","type":"string"}},"required":["customerCode"]}
```

### `get_issue_aging`

Summary of open issues in IssueTracking by aging bucket and customer-specific severity, SLA breached/at-risk counts and the oldest issues. Leave customerCode empty for all customers.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code (optional)","type":["string","null"],"default":null}}}
```

### `get_pipeline_summary`

Sales pipeline summary per stage: count, amount, weighted amount, average days in stage and win rate (12 months).

```json
{"type":"object","properties":{}}
```

### `get_project_portfolio`

Active and closed projects of a customer from ProjectMgnt with phase, actual vs planned progress, health and forecast end date.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code","type":"string"}},"required":["customerCode"]}
```

### `get_sla_compliance`

SLA compliance of a customer: issues met/breached/at-risk/on-track and compliance percentage per severity level.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code","type":"string"}},"required":["customerCode"]}
```

### `get_sla_policy`

Effective SLA policy of a customer (customer-specific or the default): business hours, holidays and the customer-defined severity levels with response/resolution minutes.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code","type":"string"}},"required":["customerCode"]}
```

### `list_customer_requests`

Lists customer requests / TOR / change requests / incidents with status.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code (optional)","type":["string","null"],"default":null},"status":{"description":"New, UnderReview, Approved, Forwarded, Rejected, Closed","type":["string","null"],"enum":["New","UnderReview","Approved","Forwarded","Rejected","Closed",null],"default":null}}}
```

### `predict_churn`

Predicts churn probability (logistic regression trained on churned vs. active customers) with the signals that drive it and a recommended retention action. Leave customerCode empty to rank all active customers.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code (optional)","type":["string","null"],"default":null},"minProbability":{"description":"Only return customers with probability \u003E= this value (0..1)","type":"number","default":0},"top":{"description":"Maximum rows (1-500)","type":"integer","default":20}}}
```

### `recommend_next_best_action`

Recommends the next best actions for a customer (respond to complaint, retention call, renewal, follow-up, discovery meeting, check-in, offer) with reason, priority, channel and best send time.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code","type":"string"},"max":{"description":"Maximum actions (1-10)","type":"integer","default":3}},"required":["customerCode"]}
```

### `recommend_products`

Product, service or content recommendations for a customer (item-based collaborative filtering on purchases and views, popularity fallback).

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code","type":"string"},"top":{"description":"Number of items (1-50)","type":"integer","default":5},"kind":{"description":"Filter: Product, Service, Content or Promotion","type":["string","null"],"enum":["Product","Service","Content","Promotion",null],"default":null}},"required":["customerCode"]}
```

### `score_leads`

Predictive lead scoring: probability (0-100) that each open opportunity will be won, grade A-D and the factors behind the score.

```json
{"type":"object","properties":{"customerCode":{"description":"Customer code (optional)","type":["string","null"],"default":null},"top":{"description":"Maximum rows (1-500)","type":"integer","default":20}}}
```

### `search_customers`

Searches customers (persons or juristic persons) by name, code, email or legal name, with optional filters.

```json
{"type":"object","properties":{"text":{"description":"Search text","type":["string","null"],"default":null},"kind":{"description":"Person or Juristic","type":["string","null"],"enum":["Person","Juristic",null],"default":null},"status":{"description":"Prospect, Active, Inactive or Churned","type":["string","null"],"enum":["Prospect","Active","Inactive","Churned",null],"default":null},"segment":{"description":"Segment name","type":["string","null"],"default":null},"page":{"description":"Page (1..)","type":"integer","default":1}}}
```

### `segment_customers`

Dynamic segmentation (RFM + engagement, k-means++): segment names (Champions, Loyal, At Risk, ...), sizes and centroids. Does not modify customers.

```json
{"type":"object","properties":{"segmentCount":{"description":"Number of segments (2-12)","type":"integer","default":5}}}
```

