#!/usr/bin/env python3
"""
Fills a LOCAL SQL Server with a synthetic business-event journal in the shape the tool expects.

This is demo data, never customer data: every actor is 'u-<n>', every object id is a counter, and no name, address
or VVVO appears anywhere. It exists so the projection, the analytics and the miner can be developed and reviewed
before the real journal is deployed — and so a reviewer can see what the insights look like on a log that has the
same *shape* as the real one: the same event types, the same object types, the same actor kinds.

The shapes mirror a real journal: a document release workflow with
several approval roles and real rework, absence requests with self-approval, inbound handovers from an external system and
a master-data feed, and outbound declarations that sometimes only land on the second attempt.

Usage: python3 scripts/seed_demo_source.py --server localhost,14330 --database SourceLocal --user sa --password ...
"""

import argparse
import random
import subprocess
import uuid
from datetime import datetime, timedelta, timezone

# One seed, so a reviewer looking at "why is variant 3 slow" sees the same log tomorrow.
RNG = random.Random(20260802)

START = datetime(2026, 5, 4, 6, 0, tzinfo=timezone.utc)
WORK_START, WORK_END = 7, 17


def business_hours_add(ts: datetime, minutes: float) -> datetime:
    """Advances a timestamp by working minutes, skipping nights and weekends.

    Without this every duration statistic is a weekend detector: a Friday-17:00 handover picked up Monday morning
    would dominate every ranking, and the actual bottleneck would sit below it.
    """
    remaining = minutes
    cur = ts
    while remaining > 0:
        if cur.weekday() >= 5:
            cur = (cur + timedelta(days=1)).replace(hour=WORK_START, minute=0, second=0, microsecond=0)
            continue
        if cur.hour < WORK_START:
            cur = cur.replace(hour=WORK_START, minute=0, second=0, microsecond=0)
        if cur.hour >= WORK_END:
            cur = (cur + timedelta(days=1)).replace(hour=WORK_START, minute=0, second=0, microsecond=0)
            continue
        end_of_day = cur.replace(hour=WORK_END, minute=0, second=0, microsecond=0)
        available = (end_of_day - cur).total_seconds() / 60
        step = min(available, remaining)
        cur += timedelta(minutes=step)
        remaining -= step
    return cur


class Journal:
    """Collects events and their object references, then emits them as INSERT batches."""

    def __init__(self):
        self.events: list[dict] = []
        self.objects: list[tuple[int, str, str, str]] = []

    def add(self, event_type, ts, performer_type, performer_id, module, payload=None, objects=(),
            initiator_type=None, initiator_id=None, correlation=None):
        source_id = len(self.events) + 1
        self.events.append({
            "id": source_id,
            "event_id": str(uuid.UUID(int=RNG.getrandbits(128), version=4)),
            "event_type": event_type,
            "occurred_at": ts,
            "performer_type": performer_type,
            "performer_id": performer_id,
            "initiator_type": initiator_type,
            "initiator_id": initiator_id,
            "correlation_id": correlation,
            "source_application": "erp",
            "source_module": module,
            "payload": payload or {},
        })
        for object_type, object_id, qualifier in objects:
            self.objects.append((source_id, object_type, object_id, qualifier))
        return source_id


def payload_literal(payload: dict) -> str:
    if not payload:
        return "N'{}'"
    parts = []
    for key, value in payload.items():
        if isinstance(value, bool):
            rendered = "true" if value else "false"
        elif isinstance(value, (int, float)):
            rendered = str(value)
        elif value is None:
            rendered = "null"
        else:
            rendered = '"' + str(value).replace('"', '\\"') + '"'
        parts.append(f'"{key}":{rendered}')
    return "N'{" + ",".join(parts) + "}'"


def sql_str(value):
    return "NULL" if value is None else "N'" + str(value).replace("'", "''") + "'"


# --- the processes -----------------------------------------------------------------------------------------------

# Employees are grouped by the role they act in. The handover matrix is only readable if a role's work actually
# clusters on a few people, the way it does in a real department.
CLERKS = [f"u-{n}" for n in range(101, 107)]
INVOICE_CHECKERS = [f"u-{n}" for n in range(201, 204)]
ACCOUNTANTS = [f"u-{n}" for n in range(301, 304)]
MANAGERS = [f"u-{n}" for n in range(401, 403)]
HR = [f"u-{n}" for n in range(501, 503)]
LEADS = [f"u-{n}" for n in range(601, 605)]
STAFF = [f"u-{n}" for n in range(701, 741)]


def emit_document_process(journal: Journal, document_id: int, created_at: datetime):
    """An incoming invoice: captured, classified, approved twice, filed and mailed out.

    The interesting parts are the deviations, because those are what a process view has to be able to surface:
    a share of documents is classified by hand after the automatic attempt failed, a share is rejected by
    accounting and comes back around, and a share never reaches the send step at all.
    """
    document = ("document", str(document_id), "processed")
    correlation = f"doc-{document_id}"
    clerk = RNG.choice(CLERKS)
    ts = created_at

    journal.add("demo.document.uploaded.v1", ts, "User", clerk, "Dms",
                {"entity": "document", "tier": "generic"}, [("document", str(document_id), "created")], correlation=correlation)

    # Classification: mostly automatic, and the manual share is the honest automation-rate signal.
    ts = business_hours_add(ts, RNG.uniform(1, 25))
    automatic = RNG.random() < 0.72
    if automatic:
        journal.add("demo.document.classification-resolved.v1", ts, "System", "dms-classifier", "Dms",
                    {"method": "template", "resolved": True}, [document], correlation=correlation)
    else:
        journal.add("demo.document.classification-resolved.v1", ts, "System", "dms-classifier", "Dms",
                    {"method": "template", "resolved": False, "reason": "no matching template"},
                    [document], correlation=correlation)
        ts = business_hours_add(ts, RNG.uniform(30, 900))
        journal.add("demo.document.classification-corrected.v1", ts, "User", RNG.choice(CLERKS), "Dms",
                    {"method": "manual", "resolved": True}, [document], correlation=correlation)

    workflow = ("workflow", "12", "executed")
    ts = business_hours_add(ts, RNG.uniform(1, 4))
    journal.add("demo.workflow-run.completed.v1", ts, "ScheduledJob", "dms-workflow-queue", "Dms",
                {"attempt": 1}, [document, workflow], correlation=correlation)

    # Invoice verification. A tenth of the documents bounce here and have to be reworked by the clerk.
    ts = business_hours_add(ts, RNG.uniform(20, 2400))
    checker = RNG.choice(INVOICE_CHECKERS)
    if RNG.random() < 0.11:
        journal.add("demo.document.release-discarded.v1", ts, "User", checker, "Dms",
                    {"role": "InvoiceVerification", "reason": "Betrag weicht ab", "previous": "Unset"},
                    [document], correlation=correlation)
        ts = business_hours_add(ts, RNG.uniform(60, 3000))
        journal.add("demo.document.status-changed.v1", ts, "User", clerk, "Dms",
                    {"entity": "document", "tier": "generic"}, [document], correlation=correlation)
        ts = business_hours_add(ts, RNG.uniform(30, 900))
    journal.add("demo.document.release-granted.v1", ts, "User", checker, "Dms",
                {"role": "InvoiceVerification", "previous": "Unset", "isReRelease": False},
                [document], correlation=correlation)

    # Accounting approval, then the stack entry and the mail. Both are workflow actions.
    ts = business_hours_add(ts, RNG.uniform(15, 3600))
    accountant = RNG.choice(ACCOUNTANTS)
    journal.add("demo.document.release-granted.v1", ts, "User", accountant, "Dms",
                {"role": "Accounting", "previous": "Unset", "isReRelease": False},
                [document], correlation=correlation)

    # High-value documents need management on top — the extra approval step is a real variant, not noise.
    if RNG.random() < 0.18:
        ts = business_hours_add(ts, RNG.uniform(120, 5400))
        journal.add("demo.document.release-granted.v1", ts, "User", RNG.choice(MANAGERS), "Dms",
                    {"role": "Management", "previous": "Unset", "isReRelease": False},
                    [document], correlation=correlation)

    ts = business_hours_add(ts, RNG.uniform(1, 10))
    journal.add("demo.workflow-action.executed.v1", ts, "ScheduledJob", "dms-workflow-queue", "Dms",
                {"actionType": "MoveDocumentToStack", "actionName": "Ablage Buchhaltung"},
                [document, workflow], correlation=correlation)

    # A tenth never gets sent. That gap is the point: the process view has to show where cases stop.
    if RNG.random() < 0.9:
        ts = business_hours_add(ts, RNG.uniform(1, 60))
        journal.add("demo.workflow-action.executed.v1", ts, "ScheduledJob", "dms-workflow-queue", "Dms",
                    {"actionType": "SendDocumentEmail", "actionName": "Versand Fachabteilung"},
                    [document, workflow], correlation=correlation)
        journal.add("demo.document.email-sent.v1", ts, "ScheduledJob", "dms-workflow-queue", "Dms",
                    {"kind": "document", "recipientCount": 1, "succeeded": RNG.random() > 0.04},
                    [("document", str(document_id), "sent")], correlation=correlation)


def emit_absence_process(journal: Journal, request_id: int, submitted_at: datetime):
    """A leave request: submitted, approved by the lead, then booked by HR.

    Self-approval is modelled on purpose. It is a real finding in this codebase (748 rows in production), and a
    process view that cannot show it is missing the one thing an auditor asks about first.
    """
    applicant = RNG.choice(STAFF)
    request = ("request", str(request_id), "requested")
    correlation = f"abs-{request_id}"
    ts = submitted_at

    journal.add("demo.request.submitted.v1", ts, "User", applicant, "Absence",
                {"days": RNG.choice([1, 1, 2, 3, 5, 5, 10])}, [request], correlation=correlation)

    ts = business_hours_add(ts, RNG.uniform(30, 4800))
    self_approved = RNG.random() < 0.07
    approver = applicant if self_approved else RNG.choice(LEADS)
    if RNG.random() < 0.08:
        journal.add("demo.request.rejected.v1", ts, "User", approver, "Absence",
                    {"previous": "Requested", "selfApproved": self_approved}, [request], correlation=correlation)
        return

    journal.add("demo.request.approved.v1", ts, "User", approver, "Absence",
                {"previous": "Requested", "selfApproved": self_approved}, [request], correlation=correlation)

    # HR books the entry. This second step is what makes the request real, and it is where the waiting sits.
    if RNG.random() < 0.85:
        ts = business_hours_add(ts, RNG.uniform(60, 9000))
        journal.add("demo.entry.released.v1", ts, "User", RNG.choice(HR), "Absence",
                    {}, [request, ("entry", str(request_id), "released")], correlation=correlation)


def emit_declaration_declaration(journal: Journal, delivery_id: int, day: datetime):
    """An the external declaration service delivery declaration, which sometimes only lands on a later attempt.

    Every attempt is a fact. The number of attempts before success, and how close to the deadline the successful
    one lands, is the entire reason to watch an outbound interface.
    """
    delivery = ("delivery", str(delivery_id), "reported")
    ts = day.replace(hour=14, minute=RNG.randint(0, 50), tzinfo=timezone.utc)
    attempts = RNG.choices([1, 2, 3], weights=[78, 17, 5])[0]
    for attempt in range(1, attempts + 1):
        succeeded = attempt == attempts and RNG.random() > 0.05
        journal.add("demo.delivery.reported.v1", ts, "ScheduledJob", "declaration-reporting", "Disposition",
                    {"action": "Report" if succeeded else "ReportingImpossible",
                     "status": "Success" if succeeded else "Error",
                     "details": None if succeeded else "Lieferung ohne Fahrzeuge kann nicht gemeldet werden"},
                    [delivery], initiator_type="ExternalSystem", initiator_id="declaration")
        ts = ts + timedelta(minutes=30)


def emit_inbound_handovers(journal: Journal, day: datetime, user_seq: list[int], address_seq: list[int]):
    """External systems hand master data over. Both are jobs performing work that happened elsewhere."""
    ts = day.replace(hour=5, minute=15, tzinfo=timezone.utc)
    created = RNG.choices([0, 0, 0, 1, 2], weights=[60, 15, 10, 10, 5])[0]
    updated = RNG.randint(0, 6)
    for _ in range(created + updated):
        user_seq[0] += 1
        journal.add("demo.employee.received-from-external.v1", ts, "ScheduledJob", "hr-sync", "Personnel",
                    {"outcome": "Created" if created else "Updated"},
                    [("user", str(user_seq[0]), "received")],
                    initiator_type="ExternalSystem", initiator_id="hr-system")
    journal.add("demo.employee-roster.received.v1", ts, "ScheduledJob", "hr-sync", "Personnel",
                {"mode": "incremental", "received": created + updated + RNG.randint(180, 200),
                 "created": created, "updated": updated, "failed": 0},
                [], initiator_type="ExternalSystem", initiator_id="hr-system")

    masterdata_ts = day.replace(hour=6, minute=5, tzinfo=timezone.utc)
    for _ in range(RNG.randint(0, 5)):
        address_seq[0] += 1
        journal.add("demo.address.received-from-external.v1", masterdata_ts, "ScheduledJob", "masterdata-sync", "MasterData",
                    {"outcome": RNG.choice(["Created", "Updated", "Updated"]), "kind": RNG.choice(
                        ["main-address", "order-address", "veterinary", "balance"])},
                    [("address", str(address_seq[0]), "received")],
                    initiator_type="ExternalSystem", initiator_id="masterdata-system")


def build(days: int, documents_per_day: int) -> Journal:
    journal = Journal()
    user_seq, address_seq = [900], [5000]
    day = START
    document_id, request_id, delivery_id = 480000, 9000, 70000

    for _ in range(days):
        if day.weekday() < 5:
            emit_inbound_handovers(journal, day, user_seq, address_seq)
            for _ in range(RNG.randint(max(1, documents_per_day - 6), documents_per_day + 6)):
                document_id += 1
                start = day.replace(hour=RNG.randint(7, 15), minute=RNG.randint(0, 59), tzinfo=timezone.utc)
                emit_document_process(journal, document_id, start)
            for _ in range(RNG.randint(0, 4)):
                request_id += 1
                start = day.replace(hour=RNG.randint(7, 16), minute=RNG.randint(0, 59), tzinfo=timezone.utc)
                emit_absence_process(journal, request_id, start)
            for _ in range(RNG.randint(2, 6)):
                delivery_id += 1
                emit_declaration_declaration(journal, delivery_id, day)
        day += timedelta(days=1)

    # Ids must be handed out in commit order, not in generation order: the pull walks the table by Id, and a log
    # whose ids disagree with its timestamps would make the watermark rule look broken when it is not.
    journal.events.sort(key=lambda e: e["occurred_at"])
    remap = {}
    for index, event in enumerate(journal.events, start=1):
        remap[event["id"]] = index
        event["id"] = index
    journal.objects = [(remap[e], t, o, q) for (e, t, o, q) in journal.objects]
    journal.objects.sort(key=lambda row: row[0])
    return journal


def render_sql(journal: Journal) -> list[str]:
    batches, rows = [], []
    header = ("INSERT INTO dbo.BusinessEvents (EventId, EventType, OccurredAt, RecordedAt, PerformerType, "
              "PerformerId, InitiatorType, InitiatorId, CorrelationId, SourceApplication, SourceModule, Payload) VALUES")
    for event in journal.events:
        # RecordedAt trails OccurredAt by a few hundred ms, exactly as the journal writes it.
        recorded = event["occurred_at"] + timedelta(milliseconds=RNG.randint(20, 400))
        rows.append(
            f"({sql_str(event['event_id'])},{sql_str(event['event_type'])},"
            f"'{event['occurred_at'].strftime('%Y-%m-%dT%H:%M:%S.%f')[:-3]}',"
            f"'{recorded.strftime('%Y-%m-%dT%H:%M:%S.%f')[:-3]}',"
            f"{sql_str(event['performer_type'])},{sql_str(event['performer_id'])},"
            f"{sql_str(event['initiator_type'])},{sql_str(event['initiator_id'])},"
            f"{sql_str(event['correlation_id'])},{sql_str(event['source_application'])},"
            f"{sql_str(event['source_module'])},{payload_literal(event['payload'])})")
        if len(rows) == 900:
            batches.append(header + "\n" + ",\n".join(rows) + ";\nGO")
            rows = []
    if rows:
        batches.append(header + "\n" + ",\n".join(rows) + ";\nGO")

    object_header = "INSERT INTO dbo.BusinessEventObjects (BusinessEventId, ObjectType, ObjectId, Qualifier) VALUES"
    rows = []
    for event_id, object_type, object_id, qualifier in journal.objects:
        rows.append(f"({event_id},{sql_str(object_type)},{sql_str(object_id)},{sql_str(qualifier)})")
        if len(rows) == 900:
            batches.append(object_header + "\n" + ",\n".join(rows) + ";\nGO")
            rows = []
    if rows:
        batches.append(object_header + "\n" + ",\n".join(rows) + ";\nGO")
    return batches


# Which group each demo actor belongs to. At the source this lives in UserGroups/UserGroupMembers; the analysis is
# only readable when a pseudonym can be resolved to the role somebody acts in, so the demo source carries the same
# shape.
GROUPS = {
    "Sachbearbeitung Beleg": CLERKS,
    "Rechnungsprüfung": INVOICE_CHECKERS,
    "Buchhaltung": ACCOUNTANTS,
    "Geschäftsleitung": MANAGERS,
    "Personalabteilung": HR,
    "Teamleitung": LEADS,
    "Mitarbeiter": STAFF,
}


def render_directory_sql() -> str:
    """Users, groups and memberships, so the log can be read by role instead of by pseudonym."""
    lines = ["DELETE FROM dbo.UserGroupMembers;", "DELETE FROM dbo.UserGroups;", "DELETE FROM dbo.AspNetUsers;", "GO"]
    users = sorted({user for members in GROUPS.values() for user in members})
    for index in range(0, len(users), 200):
        chunk = users[index : index + 200]
        # Every twentieth demo person has left: the analysis has to survive somebody who is in a group but gone.
        values = ",".join(
            f"({sql_str(u)},{sql_str(u)},{sql_str('Demo')},{sql_str(u)},{1 if i % 20 == 19 else 0})"
            for i, u in enumerate(chunk)
        )
        lines.append(f"INSERT INTO dbo.AspNetUsers (Id, UserName, FirstName, Surname, Blocked) VALUES {values};")
    lines.append("GO")
    for group_id, (name, members) in enumerate(GROUPS.items(), start=1):
        is_personnel = 1 if name == "Personalabteilung" else 0
        lines.append(
            f"INSERT INTO dbo.UserGroups (Id, Name, IsPersonnelDepartment, Visible) "
            f"VALUES ({group_id},{sql_str(name)},{is_personnel},1);"
        )
        values = ",".join(f"({group_id},{sql_str(u)})" for u in members)
        lines.append(f"INSERT INTO dbo.UserGroupMembers (UserGroupId, UserId) VALUES {values};")
    lines.append("GO")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default="/tmp/pa-demo.sql")
    parser.add_argument("--days", type=int, default=63)
    parser.add_argument("--documents-per-day", type=int, default=18)
    args = parser.parse_args()

    journal = build(args.days, args.documents_per_day)
    with open(args.out, "w", encoding="utf-8") as handle:
        handle.write("USE SourceLocal;\nGO\nDELETE FROM dbo.BusinessEventObjects;\nDELETE FROM dbo.BusinessEvents;\n"
                     "DBCC CHECKIDENT ('dbo.BusinessEvents', RESEED, 0);\n"
                     "DBCC CHECKIDENT ('dbo.BusinessEventObjects', RESEED, 0);\nGO\n")
        handle.write("SET IDENTITY_INSERT dbo.BusinessEvents OFF;\nGO\n")
        handle.write(render_directory_sql() + "\n")
        for batch in render_sql(journal):
            handle.write(batch + "\n")
    print(f"{len(journal.events)} events, {len(journal.objects)} object refs -> {args.out}")


if __name__ == "__main__":
    main()
