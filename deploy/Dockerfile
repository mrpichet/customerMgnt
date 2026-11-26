# syntax=docker/dockerfile:1
# =====================================================================================
# Hardened multi-stage image ของ CustomerMgnt.Api (REST API + MCP /mcp + Blazor WASM UI + หน้าจอระบบสมาชิก /membership/)
#   ต้อง clone พร้อม submodule: git submodule update --init --recursive (external/common, external/webcommon)
#   docker build -f deploy/Dockerfile -t customermgnt-api .
#   docker build -f deploy/Dockerfile --target test .                         ← รัน unit test ใน container
#   DevExpress license (ไม่บังคับ — ไม่มีจะเป็น evaluation mode และแสดงแถบแจ้งเตือนบน UI):
#   docker build -f deploy/Dockerfile --secret id=devexpress_license,src=$HOME/.config/DevExpress/DevExpress_License.txt -t customermgnt-api .
# - Build: SDK 10.0 (Ubuntu 26.04 "resolute" มี OpenSSL 3.5 → ML-DSA/ML-KEM ใช้งานได้)
# - Runtime: aspnet:10.0-resolute-chiseled-extra (distroless: ไม่มี shell/package manager, non-root uid 1654, มี ICU สำหรับภาษาไทย)
# - license ส่งผ่าน BuildKit secret: ไม่อยู่ใน layer/ประวัติของ image
# - ไม่มี pdb/apphost, ไฟล์แอปเป็น read-only (root:root 0555), รองรับ read-only root filesystem
# =====================================================================================

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0-resolute AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# Step 1: restore แยก layer (cache ได้เมื่อโค้ดเปลี่ยนแต่ package ไม่เปลี่ยน)
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY external/common/global.json external/common/Directory.Build.props external/common/Directory.Packages.props external/common/
COPY external/common/src/Common.Core/Common.Core.csproj external/common/src/Common.Core/
COPY external/common/src/Common.Data/Common.Data.csproj external/common/src/Common.Data/
COPY external/common/src/Common.Logging/Common.Logging.csproj external/common/src/Common.Logging/
COPY external/common/src/Common.AspNetCore/Common.AspNetCore.csproj external/common/src/Common.AspNetCore/
COPY external/common/src/Common.AI/Common.AI.csproj external/common/src/Common.AI/
COPY external/Directory.Packages.props external/
COPY external/webcommon/Directory.Build.props external/webcommon/
COPY external/webcommon/src/WebCommon.UI/WebCommon.UI.csproj external/webcommon/src/WebCommon.UI/
COPY src/CustomerMgnt.Core/CustomerMgnt.Core.csproj src/CustomerMgnt.Core/
COPY src/CustomerMgnt.Data/CustomerMgnt.Data.csproj src/CustomerMgnt.Data/
COPY src/CustomerMgnt.Integrations/CustomerMgnt.Integrations.csproj src/CustomerMgnt.Integrations/
COPY src/CustomerMgnt.AI/CustomerMgnt.AI.csproj src/CustomerMgnt.AI/
COPY src/CustomerMgnt.Web/CustomerMgnt.Web.csproj src/CustomerMgnt.Web/
COPY src/CustomerMgnt.Membership.Web/CustomerMgnt.Membership.Web.csproj src/CustomerMgnt.Membership.Web/
COPY src/CustomerMgnt.Api/CustomerMgnt.Api.csproj src/CustomerMgnt.Api/
COPY tests/CustomerMgnt.Tests/CustomerMgnt.Tests.csproj tests/CustomerMgnt.Tests/
RUN dotnet restore src/CustomerMgnt.Api/CustomerMgnt.Api.csproj -p:IncludeWebUi=true \
 && dotnet restore tests/CustomerMgnt.Tests/CustomerMgnt.Tests.csproj
# Step 2: copy source แล้ว publish API + UI (Release, ไม่มี debug symbol ใน image)
COPY external/common/src/ external/common/src/
COPY external/webcommon/src/WebCommon.UI/ external/webcommon/src/WebCommon.UI/
COPY src/ src/
COPY tests/ tests/
RUN --mount=type=secret,id=devexpress_license,required=false \
    if [ -s /run/secrets/devexpress_license ]; then export DevExpress_License="$(cat /run/secrets/devexpress_license)"; fi; \
    dotnet publish src/CustomerMgnt.Api/CustomerMgnt.Api.csproj -c Release --no-restore -o /app \
      -p:IncludeWebUi=true -p:DebugType=none -p:DebugSymbols=false -p:UseAppHost=false

# ---------- test (optional target) ----------
FROM build AS test
RUN dotnet test --project tests/CustomerMgnt.Tests -c Release

# ---------- runtime (distroless) ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-resolute-chiseled-extra AS runtime
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=1 \
    DOTNET_TieredPGO=1
WORKDIR /app
COPY --from=build --chown=root:root --chmod=0555 /app ./
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "/app/CustomerMgnt.Api.dll", "--health-probe"]
ENTRYPOINT ["dotnet", "/app/CustomerMgnt.Api.dll"]
