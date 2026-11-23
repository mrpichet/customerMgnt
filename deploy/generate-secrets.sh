#!/usr/bin/env bash
# =====================================================================================
# สร้าง secret แบบสุ่มสำหรับ docker-compose (deploy/secrets/*.txt สิทธิ์ 640 + deploy/.env, อยู่ใน .gitignore)
# ไม่เขียนทับไฟล์ที่มีอยู่แล้ว (กันคีย์เปลี่ยนจนข้อมูลที่เข้ารหัส/blind index เดิมใช้ไม่ได้) — ลบไฟล์เองถ้าต้องการ rotate
# ค่าที่มาจากระบบอื่นส่งผ่าน env ตอนรัน: SERVICE_ACCOUNT_PASSWORD, LINE_CHANNEL_SECRET, LOGSERVER_API_KEY, DEVEXPRESS_LICENSE
# =====================================================================================
set -euo pipefail
cd "$(dirname "$0")"
umask 077
mkdir -p secrets

# Step 1: เขียนไฟล์เฉพาะเมื่อยังไม่มี (ไม่มี newline ท้ายไฟล์); put_empty_ok ยอมให้ไฟล์ว่างได้
put() { [ -s "secrets/$1" ] || printf '%s' "$2" > "secrets/$1"; }
put_empty_ok() { [ -e "secrets/$1" ] || printf '%s' "$2" > "secrets/$1"; }
rand() { head -c "$1" /dev/urandom | base64 -w0; }
# รหัสผ่านที่ผ่าน policy ของ SQL Server (มีตัวพิมพ์ใหญ่/เล็ก/ตัวเลข/สัญลักษณ์) และไม่มีอักขระที่ต้อง escape ใน connection string
password() { printf 'Cm%s#9' "$(head -c 24 /dev/urandom | base64 -w0 | tr -d '/+=')"; }

# Step 2: PostgreSQL (ค่าเริ่มต้น)
put db_password.txt "$(password)"
put db_connection.txt "Host=postgres;Database=customermgnt;Username=customermgnt;Password=$(cat secrets/db_password.txt);SSL Mode=Disable"

# Step 3: SQL Server (ใช้กับ docker-compose.sqlserver.yml) — แอปใช้ login ของตัวเอง ไม่ใช้ sa
put mssql_sa_password.txt "$(password)"
put mssql_app_password.txt "$(password)"
put db_connection_sqlserver.txt "Server=sqlserver,1433;Database=customermgnt;User Id=customermgnt;Password=$(cat secrets/mssql_app_password.txt);Encrypt=True;TrustServerCertificate=True"

# Step 4: คีย์ PQC (seed ของ ML-DSA-65 = 32 byte, ML-KEM-768 = 64 byte) และ classical (P-256 PKCS#8)
put pqc_mldsa_seed.txt "$(rand 32)"
put pqc_mlkem_seed.txt "$(rand 64)"
put pqc_ecdsa_key.txt "$(openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -outform DER 2>/dev/null | openssl pkcs8 -topk8 -nocrypt -inform DER -outform DER | base64 -w0)"
put pqc_ecdh_key.txt "$(openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -outform DER 2>/dev/null | openssl pkcs8 -topk8 -nocrypt -inform DER -outform DER | base64 -w0)"
put pqc_hmac_key.txt "$(rand 64)"

# Step 5: คีย์ blind index ของข้อมูลส่วนบุคคล (เลขผู้เสียภาษี/บัตรประชาชน/อีเมล) — ห้ามเปลี่ยนหลังมีข้อมูลแล้ว
put privacy_blind_index_key.txt "$(rand 32)"

# Step 6: webhook (LINE ใช้ channel secret จริงจาก LINE Developers; inbound ใช้ HMAC ของระบบที่ส่งเข้ามา)
put webhook_line_secret.txt "${LINE_CHANNEL_SECRET:-$(rand 32)}"
put webhook_inbound_secret.txt "$(rand 32)"

# Step 7: ค่าจากระบบอื่น (ใส่ค่าจริงแทน)
put service_account_password.txt "${SERVICE_ACCOUNT_PASSWORD:-change-me}"
put logserver_api_key.txt "${LOGSERVER_API_KEY:-change-me}"
put_empty_ok devexpress_license.txt "${DEVEXPRESS_LICENSE:-}"

# Step 8: สิทธิ์ 640 (เจ้าของอ่าน/เขียน, กลุ่มอ่านได้) — compose ใส่ group_add ของกลุ่มนี้ให้คอนเทนเนอร์ที่รันแบบ non-root
#         เพราะ docker compose (ไม่ใช่ swarm) bind-mount ไฟล์ secret พร้อมสิทธิ์เดิมบน host
chmod 640 secrets/*.txt
printf 'SECRETS_GID=%s\n' "$(id -g)" > .env
chmod 600 .env
echo "Secrets written to $(pwd)/secrets (keep them out of git; back up pqc_* and privacy_* keys securely)."
