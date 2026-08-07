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
from pm4py.visualization.dfg import visualizer as dfg_visualizer

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

    stats["processes"] = discover_per_process(ocel, minable, stats)


def discover_per_process(ocel, minable, stats):
    """
    One diagram per process, next to the combined one.

    The combined graph answers "where do processes meet" and nothing else: with twenty object types in one picture it
    is a wall of crossing edges, and the reader who wants to know how ONE process runs cannot find it in there. Per
    process the same data is a readable line of boxes.

    Each process also gets a "main paths" rendering with the one-off edges dropped. A directly-follows graph keeps
    every path that occurred even once, and in real data those single traces are most of the edges and none of the
    insight.
    """
    processes = []
    for object_type in sorted(minable):
        slug = slugify(object_type)
        try:
            single = pm4py.filter_ocel_object_types(ocel, [object_type])
            graph = pm4py.discover_ocdfg(single, business_hours=True, business_hour_slots=BUSINESS_HOURS)

            files = {}
            frequency = f"process-{slug}-frequency.svg"
            pm4py.save_vis_ocdfg(graph, os.path.join(ARTIFACTS, frequency), annotation="frequency",
                                 act_metric="events", edge_metric="event_couples", graph_title=object_type)
            files["frequency"] = frequency

            performance = f"process-{slug}-performance.svg"
            pm4py.save_vis_ocdfg(graph, os.path.join(ARTIFACTS, performance), annotation="performance",
                                 performance_aggregation="median", graph_title=object_type)
            files["performance"] = performance

            # Two occurrences, not a percentage: a share of the busiest edge would hide a whole branch in a process
            # whose main path is a thousand times more frequent than its exception path, and the exception path is
            # usually the interesting one. Twice means "happened again", which is the weakest claim to being a path.
            main = f"process-{slug}-main.svg"
            pm4py.save_vis_ocdfg(graph, os.path.join(ARTIFACTS, main), annotation="frequency",
                                 act_metric="events", edge_metric="event_couples",
                                 act_threshold=2, edge_threshold=2, graph_title=object_type)
            files["main"] = main

            # The classical analyses run on the flattened log of this one process. They are what pm4py is actually full
            # of, and the tool used three functions out of two hundred and seventy.
            frame = flatten(ocel, object_type)

            # The plain flow chart, and the one that answers "how long between these two steps". Boxes with names and
            # arrows between them: no gateways, no silent transitions, no places. The BPMN and the Petri net both spend
            # most of their nodes expressing branching, which is exactly the part nobody asked about when they asked to
            # see the flow.
            for name, file in flow(frame, slug, object_type, stats).items():
                files[name] = file

            bpmn_file = bpmn(frame, slug, object_type, stats)
            if bpmn_file:
                files["bpmn"] = bpmn_file

            # The Petri net per process, not only for all of them together. Read next to the BPMN it becomes legible:
            # the round nodes are states, the unlabelled boxes are the branches the miner needed to express the paths.
            try:
                single_net = pm4py.discover_oc_petri_net(single, inductive_miner_variant="imd", diagnostics_with_tbr=False)
                net_name = f"petri-{slug}.svg"
                pm4py.save_vis_ocpn(single_net, os.path.join(ARTIFACTS, net_name))
                files["petri"] = net_name
            except Exception as error:
                stats.setdefault("petri_errors", []).append({"object_type": object_type, "error": str(error)})

            rule_result = rules(frame, object_type, stats)
            if rule_result:
                stats.setdefault("rules", []).append(rule_result)

            # The pictures that answer WHEN and HOW EVENLY, rather than what the model looks like. Cheap, and each one
            # answers a question none of the models can: when is the work done, does it queue in order, is one median
            # hiding two populations.
            for name, file in rhythm(frame, slug, object_type, stats).items():
                files[name] = file

            people_result = people(frame, slug, object_type, stats)
            if people_result:
                stats.setdefault("people", []).append(people_result)

            segment_result = segments(frame, object_type)
            if segment_result:
                stats.setdefault("segments", []).append(segment_result)

            batch_result = batches(frame, object_type, stats)
            if batch_result:
                stats.setdefault("batches", []).append(batch_result)

            processes.append({"object_type": object_type, "slug": slug, "files": files})
        except Exception as error:  # one unminable process must not cost the other nineteen their diagrams
            processes.append({"object_type": object_type, "slug": slug, "error": str(error)})
            stats.setdefault("process_errors", []).append({"object_type": object_type, "error": str(error)})
    return processes


def flatten(ocel, object_type):
    """
    One object type as a classical case log.

    The object-centric functions in pm4py are three; the classical ones are dozens, and they are the ones that answer
    "which rule does this process break" and "what is done in batches". Flattening loses the connections between object
    types, which is exactly why the landscape and the OC-DFG exist next to this — here it is the right trade, because a
    rule about one process does not need the others.
    """
    frame = pm4py.ocel_flattening(ocel, object_type)
    # A resource column is optional in OCEL and required by the batch detection. Absent it, batches would be reported per
    # activity only, which says "this happens in bursts" without saying who is bursting.
    if "ocel:attr:actor" in frame.columns and "org:resource" not in frame.columns:
        frame = frame.rename(columns={"ocel:attr:actor": "org:resource"})
    return frame


def flow(frame, slug, object_type, stats):
    """
    The directly-follows graph as a flow chart: how often, and how long.

    Every other picture here is a model — it generalises, it invents gateways, it inserts silent steps to express a
    branch. This one is a count: box A was followed by box B this many times. That makes it the only picture in the set
    that cannot be wrong about the process, and the one to read first.

    Two renderings from the same graph, because the frequent path and the slow path are rarely the same path.
    """
    files = {}

    # How often each step occurs, handed to the drawing instead of left to be inferred.
    #
    # pm4py.save_vis_dfg does not forward this, and the visualizer then derives the counts from the EDGES. A step that
    # only ever starts or ends a case has no edge, so it is missing from that dictionary and the drawing died with a
    # KeyError on its name — and a process whose cases are one event long has no edges at all, which came out as
    # "min() iterable argument is empty". Half the processes had no flow chart for those two reasons.
    counts = frame["concept:name"].value_counts().to_dict()

    def draw(variant, graph, starts, ends, name, extra=None):
        # Only steps that actually appear in the graph may be marked as a start or an end.
        #
        # The visualizer builds its node map from the EDGES and then looks up every start and end activity in it, so a
        # step that occurred exactly once, alone in its case, has no edge, is not in the map, and the drawing died with a
        # KeyError on its name. That cost nine of nineteen processes their flow chart. Passing the occurrence counts does
        # not help — the map is built from edges either way.
        drawn = {activity for edge in graph for activity in edge}
        dropped = sorted((set(starts) | set(ends)) - drawn)
        if dropped:
            stats.setdefault("flow_dropped_steps", []).append({"object_type": object_type, "steps": dropped})

        parameters = {
            "format": "svg",
            "bgcolor": "white",
            "rankdir": "LR",
            "start_activities": {a: n for a, n in starts.items() if a in drawn},
            "end_activities": {a: n for a, n in ends.items() if a in drawn},
            "enable_graph_title": True,
            "graph_title": object_type,
            **(extra or {}),
        }
        gviz = dfg_visualizer.apply(graph, activities_count=counts, parameters=parameters, variant=variant)
        dfg_visualizer.save(gviz, os.path.join(ARTIFACTS, name))

    try:
        graph, starts, ends = pm4py.discover_dfg(frame)
        # Without a single edge there is no flow to draw: the picture would be a row of loose boxes, which says less
        # than the step table above it already does.
        if graph:
            name = f"flow-{slug}.svg"
            draw(dfg_visualizer.Variants.FREQUENCY, graph, starts, ends, name)
            files["flow"] = name
        else:
            stats.setdefault("flow_skipped", []).append(
                {"object_type": object_type, "reason": "kein Schritt folgt auf einen anderen"}
            )
    except Exception as error:
        stats.setdefault("flow_errors", []).append({"object_type": object_type, "error": str(error)})

    try:
        # Working time, like every other duration in this tool: a step that waits over a weekend must not read as the
        # slowest step in the process.
        timed, starts, ends = pm4py.discover_performance_dfg(
            frame, business_hours=True, business_hour_slots=BUSINESS_HOURS
        )
        if timed:
            name = f"flow-time-{slug}.svg"
            draw(
                dfg_visualizer.Variants.PERFORMANCE, timed, starts, ends, name,
                {"aggregation_measure": "median"},
            )
            files["flowTime"] = name
    except Exception as error:
        stats.setdefault("flow_errors", []).append({"object_type": object_type, "error": str(error)})

    return files


def rhythm(frame, slug, object_type, stats):
    """
    When the work happens, and how evenly.

    Four pictures, none of them a model:

      dotted    one dot per event, x = time, y = case, colour = step. The exploratory picture: batches, night runs,
                backlogs and campaigns are visible in it before any statistic names them.
      spectrum  every case as a line across the main path. Shows overtaking and FIFO violations — a case that entered
                first and left last — which no average can show and which is what a queue actually feels like.
      duration  the distribution of case durations, not three percentiles of it. Two populations hiding in one median
                are the normal case in this company: a same-day document and one that waits for a weekly run.
      hours     which hour of the day the work falls into. The weekly trend says how much, this says when.
    """
    files = {}
    # The duration curve is a kernel density estimate. Cases that all took the same time have no distribution to
    # estimate, and the failure arrives as "the data appears to lie in a lower-dimensional subspace" — true, and useless
    # to a reader. Counted here instead, and skipped honestly.
    spread = frame.groupby("case:concept:name")["time:timestamp"].agg(lambda ts: ts.max() - ts.min()).nunique()

    pictures = [
        ("dotted", pm4py.save_vis_dotted_chart, None),
        ("hours", pm4py.save_vis_events_distribution_graph, "hours"),
    ]
    if spread >= 5:
        pictures.insert(1, ("duration", pm4py.save_vis_case_duration_graph, None))
    else:
        stats.setdefault("rhythm_skipped", []).append(
            {"object_type": object_type, "picture": "duration", "reason": "zu wenig verschiedene Durchlaufzeiten"}
        )

    for name, draw, argument in pictures:
        try:
            file = f"{name}-{slug}.svg"
            path = os.path.join(ARTIFACTS, file)
            if argument:
                draw(frame, path, distr_type=argument, graph_title=object_type)
            else:
                draw(frame, path, graph_title=object_type)
            files[name] = file
        except Exception as error:
            stats.setdefault("rhythm_errors", []).append(
                {"object_type": object_type, "picture": name, "error": str(error)}
            )

    # The spectrum needs a path that cases actually walk, and the most frequent variant IS one — it is a sequence that
    # happened, in the order it happened. Composing one from the busiest steps sorted by their median timestamp looked
    # reasonable and produced a sequence no case traverses, which the drawing reported as "min() iterable argument is
    # empty". A path nobody walks has no spectrum.
    try:
        # The spectrum divides by the observed span of each traversal, so a handful of cases that all took the same time
        # divides by zero. Its own guard rather than a caught exception: "float division by zero" tells a reader nothing.
        if frame["case:concept:name"].nunique() < 10:
            stats.setdefault("rhythm_skipped", []).append(
                {"object_type": object_type, "picture": "spectrum", "reason": "zu wenige Fälle für ein Spektrum"}
            )
            return files

        variants = pm4py.get_variants(frame)
        walked = [
            steps
            for steps in sorted(variants, key=lambda key: -variants[key])
            if len({step for step in steps}) >= 2
        ]
        path = list(dict.fromkeys(walked[0]))[:6] if walked else []
        if len(path) >= 2:
            file = f"spectrum-{slug}.svg"
            pm4py.save_vis_performance_spectrum(frame, path, os.path.join(ARTIFACTS, file), graph_title=object_type)
            files["spectrum"] = file
        else:
            stats.setdefault("rhythm_skipped", []).append(
                {"object_type": object_type, "picture": "spectrum", "reason": "kein Fall geht über zwei verschiedene Schritte"}
            )
    except Exception as error:
        stats.setdefault("rhythm_errors", []).append(
            {"object_type": object_type, "picture": "spectrum", "error": str(error)}
        )

    return files


def people(frame, slug, object_type, stats):
    """
    The roles the work reveals, and the work that is handed out and comes back.

    Both are about people and neither is readable from the directory. `roles` groups whoever does the same steps —
    the org chart says who reports to whom, this says who does the same job, and the two disagree more often than
    anybody expects. `subcontracting` finds A → B → A: work given away and returned, which is the shape behind an
    approval loop that costs days and looks like two ordinary approvals in any count.
    """
    if "org:resource" not in frame.columns:
        return None

    # Roles are derived from who does the same steps, and subcontracting from who hands work on. Both reduce over an
    # array that is empty when there is barely anybody: three people and five steps produce a library error rather than
    # a finding, and a finding is what this is for.
    if frame["org:resource"].nunique() < 3 or len(frame) < 20:
        return None

    result = {"object_type": object_type}
    try:
        result["roles"] = [
            {"steps": sorted(role.activities), "people": len(role.originator_importance)}
            for role in pm4py.discover_organizational_roles(frame)
        ]
    except Exception as error:
        stats.setdefault("people_errors", []).append({"object_type": object_type, "error": str(error)})

    try:
        network = pm4py.discover_subcontracting_network(frame)
        # Self-pairs dropped: A → A → A is somebody doing their own step twice, and reporting "this person hands work to
        # themselves" as subcontracting is a finding that cannot be acted on. The top rows were exactly that.
        handed = sorted(
            (
                {"from": pair[0], "to": pair[1], "strength": round(float(strength), 3)}
                for pair, strength in network.connections.items()
                if pair[0] != pair[1]
            ),
            key=lambda row: -row["strength"],
        )[:10]
        if handed:
            result["subcontracting"] = handed
    except Exception as error:
        stats.setdefault("people_errors", []).append({"object_type": object_type, "error": str(error)})

    return result if len(result) > 1 else None


def segments(frame, object_type):
    """
    Step sequences that always travel together.

    A candidate for a name: four steps that occur in this order in hundreds of cases are one thing people do, and
    calling it one thing is what turns a twenty-box diagram into something a person can hold in their head. Nothing
    here decides anything — it is a list to read.
    """
    try:
        counted = pm4py.get_frequent_trace_segments(frame, min_occ=max(5, len(frame) // 200))
    except ImportError as error:
        # Said out loud: pm4py leaves prefixspan optional, and an empty list would read as "nothing travels together"
        # rather than "nobody installed the thing that looks".
        return {"object_type": object_type, "unavailable": str(error)}
    except Exception:
        return None

    found = []
    for segment, count in counted.most_common(40):
        steps = [step for step in segment if step != "..."]
        # Two REAL steps, and two different ones: a repeated step is rework and already has its own panel, while a
        # sequence worth a name is two things that always happen together.
        if len(steps) >= 2 and len(set(steps)) >= 2:
            found.append({"steps": steps, "occurrences": count})
        if len(found) == 12:
            break
    return {"object_type": object_type, "found": found} if found else None


def bpmn(frame, slug, object_type, stats):
    """
    A BPMN diagram per process.

    The Petri net was the wrong picture for anybody outside this repository: its round nodes are states and carry no
    label by definition, and its unlabelled boxes are silent transitions the miner inserts to express branching. Neither
    corresponds to anything a dispatcher does. BPMN says the same thing in the notation their process documentation is
    already written in.
    """
    try:
        model = pm4py.discover_bpmn_inductive(frame, noise_threshold=0.2)
        name = f"bpmn-{slug}.svg"
        pm4py.save_vis_bpmn(model, os.path.join(ARTIFACTS, name), graph_title=object_type)
        return name
    except Exception as error:
        stats.setdefault("bpmn_errors", []).append({"object_type": object_type, "error": str(error)})
        return None


def rules(frame, object_type, stats):
    """
    The rules this process keeps, and the cases that break them.

    Learned from the log rather than configured: what always precedes what, what never occurs together, how often an
    activity may occur. A violation is not automatically a defect — the rule was inferred from behaviour, so a rare but
    legitimate path shows up as one. It is a list worth reading, and nothing here produced it before.

    The noise threshold is what keeps one unusual case from erasing a rule that a thousand others keep.
    """
    kinds = {
        "never_together": "kommen nie zusammen vor",
        "always_before": "muss vorher passieren",
        "always_after": "muss danach passieren",
        "equivalence": "kommen gleich oft vor",
        "directly_follows": "folgt direkt auf",
        "activ_freq": "unerwartete Anzahl",
    }

    try:
        skeleton = pm4py.discover_log_skeleton(frame, noise_threshold=0.05)
        results = pm4py.conformance_log_skeleton(frame, skeleton)
    except Exception as error:
        stats.setdefault("rule_errors", []).append({"object_type": object_type, "error": str(error)})
        return None

    # A case counts as violating when the diagnostics say so. The first version of this read the fitness column of the
    # dataframe form and reported every case as a violation — 85 of 85 where the truth was 2, which is worse than no
    # analysis at all.
    broken = [row for row in results if isinstance(row, dict) and row.get("no_dev_total", 0) > 0]
    if not broken:
        return {"object_type": object_type, "cases": 0, "checked": len(results), "violations": []}

    counts = {}
    for row in broken:
        for deviation in row.get("deviations", []) or []:
            kind = deviation[0] if isinstance(deviation, (list, tuple)) and deviation else "unbekannt"
            detail = deviation[1] if isinstance(deviation, (list, tuple)) and len(deviation) > 1 else ""
            # The payload is nested and its shape depends on the rule type, so it is rendered here where the shape is
            # known rather than parsed again on the other side.
            pairs = []
            if isinstance(detail, (list, tuple, set)):
                for item in list(detail)[:2]:
                    pairs.append(" / ".join(str(x) for x in item) if isinstance(item, (list, tuple)) else str(item))
            key = (kinds.get(str(kind), str(kind)), "; ".join(pairs))
            counts[key] = counts.get(key, 0) + 1

    return {
        "object_type": object_type,
        "checked": len(results),
        "cases": len(broken),
        "violations": [
            {"regel": kind, "betrifft": detail, "faelle": count}
            for (kind, detail), count in sorted(counts.items(), key=lambda kv: -kv[1])[:6]
        ],
    }


def batches(frame, object_type, stats):
    """
    Work done in bursts rather than as it arrives.

    A dispatcher who signs off twenty papers at four in the afternoon is not slow, they are batching, and the two look
    identical in an average. It matters because a batch is a queue somebody built on purpose, and the waiting inside it is
    the part nobody sees.
    """
    if "org:resource" not in frame.columns:
        return None

    try:
        found = pm4py.discover_batches(frame, merge_distance=900, min_batch_size=3)
    except Exception as error:
        stats.setdefault("batch_errors", []).append({"object_type": object_type, "error": str(error)})
        return None

    rows = []
    for (activity, resource), count, detail in found[:10]:
        kinds = sorted(detail.keys()) if isinstance(detail, dict) else []
        rows.append({
            "schritt": activity,
            "person": resource,
            "stapel": int(count),
            "art": ", ".join(kinds),
        })
    return {"object_type": object_type, "batches": rows} if rows else None


def slugify(name):
    """
    A file name from a label. The labels are German and carry umlauts; those are folded rather than dropped, so
    "Aufträge" and "Auftrage" cannot collide into one file.
    """
    folded = (name.lower()
              .replace("ä", "ae").replace("ö", "oe").replace("ü", "ue")
              .replace("ß", "ss").replace("é", "e").replace("è", "e"))
    slug = "".join(character if character.isalnum() else "-" for character in folded)
    while "--" in slug:
        slug = slug.replace("--", "-")
    return slug.strip("-")[:60] or "prozess"


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
