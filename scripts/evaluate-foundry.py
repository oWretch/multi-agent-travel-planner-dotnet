# /// script
# requires-python = ">=3.11"
# dependencies = ["azure-ai-projects==2.9.0", "azure-identity>=1.17,<2", "azure-monitor-query>=2,<3"]
# ///

import argparse
import hashlib
import json
import os
import subprocess
import uuid
from datetime import datetime, timedelta, timezone
from itertools import islice
from pathlib import Path

from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import (
    AgentEvaluatorGenerationJobSource,
    EvaluationScheduleTask,
    EvaluatorGenerationInputs,
    HourlyRecurrenceSchedule,
    PromptEvaluatorGenerationJobSource,
    RecurrenceTrigger,
    Schedule,
    TracesEvaluatorGenerationJobSource,
)
from azure.core.exceptions import ResourceNotFoundError
from azure.identity import AzureCliCredential
from azure.monitor.query import LogsQueryClient, LogsQueryStatus


ROOT = Path(__file__).resolve().parents[1]
AGENT_ROOT = ROOT / "src" / "MultiAgentTravelPlanner"
GUIDANCE = ROOT / "scripts" / "travel-rubric.json"
HEADERS = {"Foundry-Features": "Evaluations=V1Preview"}
SCHEDULE_ID = "travel-planner-hourly-trace-eval"


def azd_values():
    environment = dict(os.environ, AZURE_DEV_USER_AGENT="microsoft_foundry_skill")
    result = subprocess.run(
        ["azd", "env", "get-values", "--output", "json"],
        cwd=ROOT, env=environment, capture_output=True, text=True, check=True,
    )
    return json.loads(result.stdout)


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, default=str) + "\n", encoding="utf-8")


def verify_rubric_snapshot(cache, rubric):
    snapshot = cache / "evaluators" / f"{rubric.name}-v{rubric.version}.json"
    definition = rubric.as_dict()["definition"]
    if snapshot.exists() and json.loads(snapshot.read_text())["definition"] != definition:
        raise RuntimeError("The rubric definition changed under the locked version. Review and deliberately select a new version before comparing runs.")
    return hashlib.sha256(json.dumps(definition, sort_keys=True).encode()).hexdigest()


def trace_ids(credential, resource_id, agent_id, hours, limit):
    query = f"""dependencies
| where timestamp > ago({hours}h)
| where tostring(customDimensions["gen_ai.operation.name"]) == "invoke_agent"
| where tostring(customDimensions["gen_ai.agent.id"]) == {json.dumps(agent_id)}
| where isnotempty(customDimensions["gen_ai.input.messages"])
    and isnotempty(customDimensions["gen_ai.output.messages"])
| summarize timestamp=max(timestamp) by operation_Id
| top {limit} by timestamp desc
| project operation_Id"""
    with LogsQueryClient(credential) as logs:
        result = logs.query_resource(resource_id, query, timespan=timedelta(hours=hours))
    if result.status != LogsQueryStatus.SUCCESS:
        raise RuntimeError("Trace query was incomplete; refusing to select partial evidence.")
    return [row[0] for table in result.tables for row in table.rows]


def main():
    parser = argparse.ArgumentParser(description="Create and demo a trace-informed Foundry rubric. Cloud draft/run steps are billable.")
    parser.add_argument("phase", choices=["check", "draft", "review", "run", "results",
        "schedule", "schedule-status", "schedule-disable"])
    parser.add_argument("--agent-id", default="Coordinator")
    parser.add_argument("--judge", default="gpt-5.4-mini")
    parser.add_argument("--lookback-hours", type=int, default=72)
    parser.add_argument("--max-traces", type=int, default=3)
    parser.add_argument("--trace-id", action="append", default=[])
    parser.add_argument("--rubric-reviewed", action="store_true")
    parser.add_argument("--retry-failed-generation", action="store_true")
    args = parser.parse_args()
    if not 1 <= args.lookback_hours <= 720 or not 1 <= args.max_traces <= 20:
        parser.error("Use a 1-720 hour lookback and 1-20 traces for this demo.")
    guidance = json.loads(GUIDANCE.read_text(encoding="utf-8"))
    values = azd_values()
    environment = values["AZURE_ENV_NAME"]
    endpoint = values.get("AZURE_AI_FOUNDRY_PROJECT_ENDPOINT") or values.get("FOUNDRY_PROJECT_ENDPOINT")
    if not endpoint:
        raise RuntimeError("The selected azd environment has no Foundry project endpoint.")
    cache = AGENT_ROOT / ".foundry" / environment
    state_path = cache / "state.json"
    state = json.loads(state_path.read_text()) if state_path.exists() else {}
    print(f"Agent root: {AGENT_ROOT.relative_to(ROOT)}; azd environment: {environment}")
    print(f"Project: {endpoint}; agent: {args.agent_id}; judge: {args.judge}")

    with AzureCliCredential() as credential, AIProjectClient(
        endpoint=endpoint, credential=credential, allow_preview=True,
    ) as client:
        if args.phase == "results":
            with client.get_openai_client() as openai:
                evaluation = openai.evals.retrieve(state["eval_id"])
                save(cache / "results" / f"{evaluation.id}.json", evaluation.model_dump(mode="json", warnings=False))
                run = openai.evals.runs.retrieve(state["run_id"], eval_id=state["eval_id"])
                save(cache / "results" / f"{run.id}.json", run.model_dump(mode="json", warnings=False))
                print(f"Evaluation {run.id}: {run.status}; report: {run.report_url}")
                if run.status not in {"completed", "failed", "canceled", "cancelled"}:
                    return
                items = [item.model_dump(mode="json") for item in openai.evals.runs.output_items.list(
                    run.id, eval_id=state["eval_id"],
                )]
                save(cache / "results" / f"{run.id}-items.json", items)
                print(f"Downloaded {len(items)} scored items; inspect reasons and null results before presenting.")
                print(json.dumps(run.result_counts.model_dump() if run.result_counts else {}, indent=2))
                if run.status != "completed":
                    raise RuntimeError(f"Evaluation ended as {run.status}; see saved error details.")
            return

        agent = client.agents.get(args.agent_id)
        print(f"Registered external agent: {agent.name}")
        deployments = list(client.deployments.list())
        if args.judge not in {deployment.name for deployment in deployments}:
            raise RuntimeError(f"Judge deployment {args.judge} does not exist in this project.")
        try:
            versions = list(client.evaluators.list_versions(guidance["name"]))
        except ResourceNotFoundError:
            versions = []
        print(f"Existing scorecard versions: {[version.version for version in versions]}")
        if args.phase == "check":
            print("Read-only check passed. No evaluation or generation job created.")
            return

        existing_schedule = None
        if args.phase.startswith("schedule"):
            try:
                existing_schedule = client.beta.schedules.get(SCHEDULE_ID)
            except ResourceNotFoundError:
                pass
            if args.phase != "schedule":
                if existing_schedule is None:
                    print(f"No schedule named {SCHEDULE_ID} exists in this project.")
                    return
                if args.phase == "schedule-disable":
                    existing_schedule.enabled = False
                    existing_schedule = client.beta.schedules.create_or_update(SCHEDULE_ID, existing_schedule)
                save(cache / "schedules" / f"{SCHEDULE_ID}.json", existing_schedule.as_dict())
                print(json.dumps(existing_schedule.as_dict(), indent=2, default=str))
                runs = list(islice(client.beta.schedules.list_runs(SCHEDULE_ID), 5))
                print("Recent schedule runs:", json.dumps([run.as_dict() for run in runs], indent=2, default=str))
                return

        if args.phase == "draft":
            if args.retry_failed_generation:
                if versions or not state.get("generation_job_id"):
                    raise RuntimeError("Retry requires a failed generation job and no existing rubric.")
                previous = client.evaluators.get_generation_job(state["generation_job_id"], headers=HEADERS)
                if getattr(previous.status, "value", previous.status) not in {"failed", "cancelled"}:
                    raise RuntimeError("Only failed or cancelled jobs can be retried explicitly.")
                save(cache / "evaluators" / f"{previous.id}.json", previous.as_dict())
                state.setdefault("generation_history", []).append(state.pop("generation_job_id"))
                state.pop("generation_operation_id", None)
                save(state_path, state)
            if versions:
                rubric = client.evaluators.get_version(guidance["name"], state.get("rubric_version") or versions[0].version)
                print("Reusing the existing rubric version rather than regenerating it.")
            elif state.get("generation_job_id"):
                job = client.evaluators.get_generation_job(state["generation_job_id"], headers=HEADERS)
                print(f"Generation {state['generation_job_id']}: {job.status}")
                save(cache / "evaluators" / "generation.json", job.as_dict())
                if getattr(job.status, "value", job.status) != "succeeded":
                    raise RuntimeError("Generation has not succeeded; inspect the saved job before retrying.")
                rubric = client.evaluators.get_version(job.result.name, str(job.result.version))
            else:
                exploration_guidance = {key: value for key, value in guidance.items() if key != "testing"}
                prompt = "Draft a domain-specific quality rubric, not a compliance certificate.\n" + json.dumps(exploration_guidance, indent=2)
                for source in [AGENT_ROOT / "Agents" / "AgentCatalog.cs", AGENT_ROOT / "Tools" / "TravelTools.cs"]:
                    prompt += f"\n## Application source: {source.name}\n" + source.read_text(encoding="utf-8")
                now = datetime.now(timezone.utc)
                state.setdefault("generation_operation_id", "travel-scorecard-" + uuid.uuid4().hex)
                state["generation_inputs"] = {
                    "guidance_sha256": hashlib.sha256(GUIDANCE.read_bytes()).hexdigest(),
                    "prompt_sha256": hashlib.sha256(prompt.encode()).hexdigest(),
                    "max_traces": args.max_traces,
                    "start_time": (now - timedelta(hours=args.lookback_hours)).isoformat(),
                    "end_time": now.isoformat(),
                }
                save(state_path, state)
                poller = client.evaluators.begin_create_generation_job(
                    job=EvaluatorGenerationInputs(
                        model=args.judge, evaluator_name=guidance["name"],
                        evaluator_display_name=guidance["display_name"], evaluator_description=guidance["description"],
                        sources=[
                            TracesEvaluatorGenerationJobSource(agent_id=args.agent_id,
                                start_time=now - timedelta(hours=args.lookback_hours), end_time=now,
                                max_traces=args.max_traces),
                            AgentEvaluatorGenerationJobSource(agent_name=args.agent_id, agent_version="1"),
                            PromptEvaluatorGenerationJobSource(prompt=prompt, description="Instructions, mock tool evidence and exploration guidance; excludes held-out tests."),
                        ],
                    ),
                    operation_id=state["generation_operation_id"],
                    headers=HEADERS,
                )
                state["generation_job_id"] = poller.details["job_id"]
                save(state_path, state)
                print(f"Rubric generation started: {state['generation_job_id']}", flush=True)
                try:
                    rubric = poller.result(timeout=600)
                finally:
                    job = client.evaluators.get_generation_job(state["generation_job_id"], headers=HEADERS)
                    save(cache / "evaluators" / "generation.json", job.as_dict())
            state.update(rubric_name=rubric.name, rubric_version=str(rubric.version), judge=args.judge,
                rubric_definition_sha256=verify_rubric_snapshot(cache, rubric))
            save(state_path, state)
            save(cache / "evaluators" / f"{rubric.name}-v{rubric.version}.json", rubric.as_dict())
            print(f"Rubric ready: {rubric.name} v{rubric.version}. Review the saved definition before scoring.")
            return

        rubric = client.evaluators.get_version(state["rubric_name"], state["rubric_version"])
        if args.phase == "review":
            print(json.dumps(rubric.as_dict(), indent=2, default=str))
            return
        state["rubric_definition_sha256"] = verify_rubric_snapshot(cache, rubric)
        if not args.rubric_reviewed:
            parser.error("Review the generated rubric, then use --rubric-reviewed to score it.")
        if args.judge != state["judge"]:
            raise RuntimeError("Use the same judge deployment as the locked rubric baseline.")
        if args.phase == "schedule":
            if args.trace_id:
                parser.error("Scheduled evaluations use a rolling window, not fixed --trace-id values.")
            eval_id = existing_schedule.task.eval_id if existing_schedule else state["eval_id"]
            with client.get_openai_client() as openai:
                evaluation = openai.evals.retrieve(eval_id)
                criteria = evaluation.model_dump(warnings=False)["testing_criteria"]
                if len(criteria) != 1 or any(
                    criterion.get("evaluator_name") != state["rubric_name"]
                    or criterion.get("evaluator_version") != state["rubric_version"]
                    or criterion.get("initialization_parameters", {}).get("model") != args.judge
                    for criterion in criteria
                ):
                    raise RuntimeError("The evaluation group's rubric version or judge differs from the locked baseline.")
            start = datetime.now(timezone.utc).replace(minute=0, second=0, microsecond=0) + timedelta(hours=1)
            schedule = client.beta.schedules.create_or_update(SCHEDULE_ID, Schedule(
                schedule_id=SCHEDULE_ID,
                display_name="Travel planner hourly trace evaluation",
                description="Hourly demo quality monitoring of recent Coordinator traces with the reviewed rubric.",
                enabled=True,
                trigger=RecurrenceTrigger(start_time=start, time_zone="UTC", interval=1,
                    schedule=HourlyRecurrenceSchedule()),
                task=EvaluationScheduleTask(eval_id=eval_id, eval_run={
                    "name": "travel-planner-hourly-traces",
                    "data_source": {"type": "azure_ai_traces", "agent_id": args.agent_id,
                        "lookback_hours": 1, "max_traces": args.max_traces},
                    "metadata": {"purpose": "scheduled-demo-monitoring", "agent_id": args.agent_id,
                        "rubric_version": state["rubric_version"], "judge": args.judge,
                        "rubric_definition_sha256": state["rubric_definition_sha256"]},
                }),
                tags={"purpose": "demo", "agent": args.agent_id},
            ))
            state.update(schedule_id=SCHEDULE_ID, scheduled_eval_id=eval_id)
            save(state_path, state)
            save(cache / "schedules" / f"{SCHEDULE_ID}.json", schedule.as_dict())
            print(json.dumps(schedule.as_dict(), indent=2, default=str))
            print(f"Hourly evaluation enabled; first scheduled time: {start.isoformat()}. Each run selects at most {args.max_traces} traces from the previous hour.")
            return
        ids = args.trace_id
        if not ids:
            resources = json.loads(subprocess.run(
                ["az", "resource", "list", "--resource-group", values["AZURE_RESOURCE_GROUP"], "--output", "json"],
                capture_output=True, text=True, check=True,
            ).stdout)
            insights = [resource["id"] for resource in resources if resource["type"].lower() == "microsoft.insights/components"]
            if len(insights) != 1:
                raise RuntimeError("Expected one App Insights resource; provide explicit --trace-id values instead.")
            ids = trace_ids(credential, insights[0], args.agent_id, args.lookback_hours, args.max_traces)
        if not ids:
            raise RuntimeError("No content-bearing Coordinator traces found. Generate synthetic traffic and allow ingestion first.")
        with client.get_openai_client() as openai:
            evaluation = openai.evals.create(
                name="Travel planner rubric integration",
                data_source_config={"type": "azure_ai_source", "scenario": "traces"},
                testing_criteria=[{
                    "type": "azure_ai_evaluator", "name": "travel_quality",
                    "evaluator_name": state["rubric_name"], "evaluator_version": state["rubric_version"],
                    "initialization_parameters": {"model": args.judge},
                    "data_mapping": {"query": "{{item.query}}", "response": "{{item.response}}",
                        "tool_calls": "{{item.tool_calls}}", "tool_definitions": "{{item.tool_definitions}}"},
                }],
            )
            state["eval_id"] = evaluation.id
            save(state_path, state)
            save(cache / "results" / f"{evaluation.id}.json", evaluation.model_dump(mode="json", warnings=False))
            run = openai.evals.runs.create(
                eval_id=evaluation.id, name="travel-planner-trace-demo-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ"),
                data_source={"type": "azure_ai_traces", "trace_ids": ids, "lookback_hours": args.lookback_hours},
                metadata={"agent_id": args.agent_id, "purpose": "integration-smoke-not-held-out-benchmark",
                    "rubric_version": state["rubric_version"], "judge": args.judge,
                    "rubric_definition_sha256": state["rubric_definition_sha256"]},
            )
            state.update(run_id=run.id, trace_ids=ids, started_at=datetime.now(timezone.utc).isoformat())
            save(state_path, state)
            save(cache / "results" / f"{run.id}.json", run.model_dump(mode="json", warnings=False))
            print(f"Evaluation {evaluation.id}; run {run.id}; status {run.status}; report {run.report_url}")
            print("Run the results phase to fetch final status and per-item scores without submitting another run.")


if __name__ == "__main__":
    main()