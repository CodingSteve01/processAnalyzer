#!/usr/bin/env bash
# Brings up a complete local stack with synthetic data, so the projection, the metrics and the miner can be
# reviewed before the real journal exists anywhere.
#
# Nothing here touches a real system: the source is a throwaway SQL Server container filled by
# scripts/seed_demo_source.py, and every actor and object in it is a counter.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

SOURCE_CONTAINER=processanalyzer-demo-source
SOURCE_PASSWORD='Local_dev_1234'
SOURCE_PORT=14330
ARTIFACTS="${PROCESSANALYZER_ARTIFACTS:-$ROOT/artifacts}"

log() { printf '\n\033[1;34m==>\033[0m %s\n' "$1"; }

log "Postgres"
[ -f .env ] || printf 'POSTGRES_PASSWORD=localdev\nTZ=Europe/Berlin\n' > .env
docker compose up -d postgres
until docker exec processanalyzer-db pg_isready -U processanalyzer -d process >/dev/null 2>&1; do sleep 2; done

log "Demo source (SQL Server)"
if ! docker ps -a --format '{{.Names}}' | grep -qx "$SOURCE_CONTAINER"; then
  docker run -d --name "$SOURCE_CONTAINER" \
    -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$SOURCE_PASSWORD" \
    -p "$SOURCE_PORT:1433" --platform linux/amd64 \
    mcr.microsoft.com/mssql/server:2022-latest >/dev/null
else
  docker start "$SOURCE_CONTAINER" >/dev/null
fi
until docker exec "$SOURCE_CONTAINER" /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$SOURCE_PASSWORD" -C -Q "SELECT 1" >/dev/null 2>&1; do sleep 3; done

log "Journal tables and demo events"
docker exec -i "$SOURCE_CONTAINER" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SOURCE_PASSWORD" -C <<'SQL'
IF DB_ID('SourceLocal') IS NULL CREATE DATABASE SourceLocal;
GO
USE SourceLocal;
IF OBJECT_ID('dbo.BusinessEvents') IS NULL
CREATE TABLE dbo.BusinessEvents (
  Id bigint IDENTITY(1,1) PRIMARY KEY, EventId uniqueidentifier NOT NULL UNIQUE,
  EventType nvarchar(200) NOT NULL, OccurredAt datetime2 NOT NULL, RecordedAt datetime2 NOT NULL,
  PerformerType nvarchar(50) NOT NULL, PerformerId nvarchar(100) NULL,
  InitiatorType nvarchar(50) NULL, InitiatorId nvarchar(100) NULL,
  CorrelationId nvarchar(100) NULL, CausationId uniqueidentifier NULL, TraceId nvarchar(100) NULL,
  SourceApplication nvarchar(50) NOT NULL, SourceModule nvarchar(100) NULL, SourceVersion nvarchar(50) NULL,
  Payload nvarchar(max) NULL, MandateId bigint NULL);
-- The directory: who exists and which group they act in. the source keeps this in Users/UserGroups/UserGroupMembers,
-- and without it every analysis can only speak in pseudonyms.
-- Named as at the source, so the demo exercises the same query the real source does.
IF OBJECT_ID('dbo.AspNetUsers') IS NULL
CREATE TABLE dbo.AspNetUsers (Id nvarchar(100) PRIMARY KEY, UserName nvarchar(200) NULL,
  FirstName nvarchar(100) NULL, Surname nvarchar(100) NULL,
  Blocked bit NOT NULL DEFAULT 0, LeaveDate datetime2 NULL);
IF OBJECT_ID('dbo.UserGroups') IS NULL
CREATE TABLE dbo.UserGroups (Id bigint PRIMARY KEY, Name nvarchar(200) NULL,
  IsPersonnelDepartment bit NOT NULL DEFAULT 0, Visible bit NOT NULL DEFAULT 1);
IF OBJECT_ID('dbo.UserGroupMembers') IS NULL
CREATE TABLE dbo.UserGroupMembers (UserGroupId bigint NOT NULL, UserId nvarchar(100) NOT NULL,
  PRIMARY KEY (UserGroupId, UserId));
IF OBJECT_ID('dbo.BusinessEventObjects') IS NULL
CREATE TABLE dbo.BusinessEventObjects (
  Id bigint IDENTITY(1,1) PRIMARY KEY, BusinessEventId bigint NOT NULL REFERENCES dbo.BusinessEvents(Id),
  ObjectType nvarchar(100) NOT NULL, ObjectId nvarchar(100) NOT NULL, Qualifier nvarchar(100) NOT NULL);
GO
SQL

python3 scripts/seed_demo_source.py --out /tmp/pa-demo.sql
docker cp /tmp/pa-demo.sql "$SOURCE_CONTAINER:/tmp/demo.sql" >/dev/null
docker exec "$SOURCE_CONTAINER" /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$SOURCE_PASSWORD" -C -i /tmp/demo.sql >/dev/null

log "Application"
mkdir -p "$ARTIFACTS"
export ASPNETCORE_CONTENTROOT="$ROOT/ProcessAnalyzer.Web"
export ASPNETCORE_ENVIRONMENT=Production
export PROCESSANALYZER_ARTIFACTS="$ARTIFACTS"
export ConnectionStrings__DefaultConnection="Host=localhost;Port=5434;Database=process;Username=processanalyzer;Password=localdev"
# AllowWriteCapableLogin is set only because the demo source is an sa login in a throwaway container. Against a
# real source database the guard must stay on — it is the only thing that makes "read only" more than a comment.
export ProcessAnalyzer__SourceConnectionString="Server=localhost,$SOURCE_PORT;Database=SourceLocal;User Id=sa;Password=$SOURCE_PASSWORD;ApplicationIntent=ReadOnly;TrustServerCertificate=True;Encrypt=False"
export ProcessAnalyzer__AllowWriteCapableLogin=true

dotnet build ProcessAnalyzer.Web/ProcessAnalyzer.Web.csproj -v q --nologo
nohup dotnet ProcessAnalyzer.Web/bin/Debug/net10.0/ProcessAnalyzer.Web.dll > /tmp/processanalyzer.log 2>&1 &
until curl -s http://localhost:5100/api/sync/status >/dev/null 2>&1; do sleep 2; done

log "Pull and projection"
until [ "$(curl -s http://localhost:5100/api/sync/status | python3 -c 'import json,sys;print(json.load(sys.stdin)["sync"]["eventCount"])')" -gt 0 ]; do sleep 3; done
curl -s -X POST http://localhost:5100/api/projection/run -m 900 >/dev/null

log "OCEL export and mining"
curl -s -X POST http://localhost:5100/api/export/ocel -m 900 >/dev/null
docker build -q -t processanalyzer-miner:local miner >/dev/null
docker run --rm -v "$ARTIFACTS:/artifacts" processanalyzer-miner:local >/dev/null

log "Ready"
echo "  Dashboard   http://localhost:5100"
echo "  Diagramme   $ARTIFACTS/ocdfg-frequency.svg, ocdfg-performance.svg, ocpn.svg"
echo "  Log         /tmp/processanalyzer.log"
echo
echo "  Stoppen:    pkill -f ProcessAnalyzer.Web.dll; docker compose down; docker stop $SOURCE_CONTAINER"
