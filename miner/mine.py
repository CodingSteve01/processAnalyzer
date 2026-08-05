#!/usr/bin/env python3
"""
Discovers process models from an OCEL 2.0 log and writes them as SVG plus a statistics file.

Runs in its own container. pm4py is AGPL-3.0 and pulls in a heavy scientific stack; keeping it out of the
application image means the licence stays contained and a broken dependency cannot take the dashboard down with it.
The two exchange files through a shared directory rather than HTTP, so a restart on either side loses nothing.
"""

import json
import os
import sys
import traceback

import pm4py

ARTIFACTS = os.environ.get("PROCESSANALYZER_ARTIFACTS", "/artifacts")
LOG = os.path.join(ARTIFACTS, "log.sqlite")

# Business hours, so the performance edges show working time. Passed as second-of-week slots, which is what pm4py
# expects; they mirror analytics.business_slot so SQL and Python cannot disagree about what "two hours" means.
BUSINESS_HOURS = [((d * 24 + 7) * 3600, (d * 24 + 17) * 3600) for d in range(5)]


def discover(ocel, stats):
    """Object-centric directly-follows graph: what the process actually looks like, per object type."""
    ocdfg = pm4py.discover_ocdfg(ocel, business_hours=True, business_hour_slots=BUSINESS_HOURS)

    pm4py.save_vis_ocdfg(ocdfg, os.path.join(ARTIFACTS, "ocdfg-frequency.svg"),
                         annotation="frequency", act_metric="events", edge_metric="event_couples")
    pm4py.save_vis_ocdfg(ocdfg, os.path.join(ARTIFACTS, "ocdfg-performance.svg"),
                         annotation="performance", performance_aggregation="median")

    # A Petri net needs a process to discover. An object type with a single activity has none — its process tree
    # comes back empty and the converter dereferences it — so those types are excluded and NAMED. Silently dropping
    # them would present a partial model as the whole picture.
    minable, single_step = petri_net_candidates(ocel)
    stats["models"] = ["ocdfg-frequency.svg", "ocdfg-performance.svg"]
    stats["ocpn_excluded_single_activity"] = single_step

    if minable:
        # imd, not imf. The noise-filtering variant would be the better model, but pm4py 2.7.15 crashes converting
        # its process tree ('NoneType' has no attribute 'children') for every object type in this log — verified,
        # not assumed. imd is directly-follows based and produces a readable net; revisit the variant when pm4py
        # ships a fix, because without noise filtering rare paths stay in the model.
        filtered = pm4py.filter_ocel_object_types(ocel, minable)
        ocpn = pm4py.discover_oc_petri_net(filtered, inductive_miner_variant="imd", diagnostics_with_tbr=False)
        pm4py.save_vis_ocpn(ocpn, os.path.join(ARTIFACTS, "ocpn.svg"))
        stats["models"].append("ocpn.svg")
        stats["ocpn_object_types"] = minable


def petri_net_candidates(ocel):
    """Splits object types into those with a discoverable process and those with a single activity."""
    table = ocel.get_extended_table()
    minable, single_step = [], []
    for column in [c for c in table.columns if c.startswith("ocel:type:")]:
        object_type = column.split("ocel:type:", 1)[1]
        touched = table[table[column].apply(lambda v: isinstance(v, list) and len(v) > 0)]
        activities = touched["ocel:activity"].nunique() if len(touched) else 0
        (minable if activities >= 2 else single_step).append(object_type)
    return minable, single_step


def summarize(ocel, stats):
    table = ocel.get_extended_table()
    stats["events"] = int(len(ocel.events))
    stats["objects"] = int(len(ocel.objects))
    stats["relations"] = int(len(ocel.relations))
    stats["activities"] = sorted(ocel.events[ocel.event_activity].unique().tolist())
    stats["object_types"] = sorted(ocel.objects[ocel.object_type_column].unique().tolist())

    # Object-type interaction: which types appear together on one event. This is the picture flattening destroys,
    # and the reason an object-centric log was worth building in the first place.
    interactions = {}
    for column in [c for c in table.columns if c.startswith("ocel:type:")]:
        object_type = column.split("ocel:type:", 1)[1]
        touched = table[column].dropna().apply(lambda v: len(v) if isinstance(v, list) else 0)
        interactions[object_type] = {
            "events_touching": int((touched > 0).sum()),
            "max_objects_per_event": int(touched.max() if len(touched) else 0),
        }
    stats["object_interaction"] = interactions


def main():
    if not os.path.exists(LOG):
        print(f"no log at {LOG}", file=sys.stderr)
        return 2

    stats = {"log": LOG}
    ocel = pm4py.read_ocel2_sqlite(LOG)
    summarize(ocel, stats)
    discover(ocel, stats)

    with open(os.path.join(ARTIFACTS, "stats.json"), "w", encoding="utf-8") as handle:
        json.dump(stats, handle, indent=2, ensure_ascii=False)

    print(json.dumps({k: v for k, v in stats.items() if k != "activities"}, indent=2))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:
        traceback.print_exc()
        sys.exit(1)
