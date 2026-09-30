import hashlib
import pathlib
import sys

sys.path.insert(0, sys.argv[1])
import yaml


class StrictLoader(yaml.BaseLoader):
    pass


def construct_mapping(loader, node, deep=False):
    mapping = {}
    for key_node, value_node in node.value:
        if key_node.tag == "tag:yaml.org,2002:merge":
            raise ValueError("YAML merge keys are not allowed")
        key = loader.construct_object(key_node, deep=deep)
        if not isinstance(key, str):
            raise ValueError("YAML mapping keys must decode to strings")
        if key == "<<":
            raise ValueError("YAML merge keys are not allowed")
        if key in mapping:
            raise ValueError(f"Duplicate YAML mapping key: {key}")
        mapping[key] = loader.construct_object(value_node, deep=deep)
    return mapping


StrictLoader.add_constructor(
    yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG,
    construct_mapping,
)


def selected_step(step):
    if not isinstance(step, dict):
        raise ValueError("Every QA step must decode to a mapping")
    selected = dict(step)
    if selected.get("name") == "Resolve source context":
        action_inputs = selected.get("with")
        if not isinstance(action_inputs, dict) or set(action_inputs) != {"script"}:
            raise ValueError("Source-context action inputs changed")
        script = action_inputs["script"]
        if not isinstance(script, str):
            raise ValueError("Source-context script must decode to a string")
        selected["with"] = {
            "script-sha256": hashlib.sha256(script.encode("utf-8")).hexdigest()
        }
    return selected


expected_steps = [
    {
        "name": "Resolve source context",
        "id": "context",
        "uses": "actions/github-script@v8",
        "with": {
            "script-sha256": (
                "8f8d1f25cd905fec169ecfea103d71733b2be0c1b20381ecfefc5c44dad0e418"
            )
        },
    },
    {
        "name": "Check out trusted QA policy",
        "uses": "actions/checkout@v4",
        "with": {
            "ref": "${{ github.event.repository.default_branch }}",
            "path": "trusted",
            "persist-credentials": "false",
        },
    },
    {
        "name": "Check out tested source",
        "uses": "actions/checkout@v4",
        "with": {
            "repository": "${{ steps.context.outputs.source-repository }}",
            "ref": "${{ steps.context.outputs.head-sha }}",
            "path": "source",
            "persist-credentials": "false",
        },
    },
    {
        "uses": "actions/setup-dotnet@v4",
        "with": {"dotnet-version": "10.0.x"},
    },
    {
        "name": "Restore",
        "run": "dotnet restore --locked-mode",
        "working-directory": "source",
    },
    {
        "name": "Build",
        "run": "dotnet build --no-restore",
        "working-directory": "source",
    },
    {
        "name": "Run independent QA tests",
        "run": (
            "dotnet test AgenticHotelBooking.slnx "
            "--configuration Debug --no-build "
            "--filter FullyQualifiedName!~AgenticHotelBooking.IntegrationTests."
            "SqlManagedIdentityBootstrapperSqlServerTests. "
            "--logger trx --results-directory TestResults"
        ),
        "working-directory": "source",
    },
    {
        "name": "Build Bicep",
        "uses": "azure/cli@v2",
        "with": {"inlineScript": "az bicep build --file source/infra/main.bicep"},
    },
    {
        "name": "Write pull request evidence",
        "run": (
            "New-Item -ItemType Directory -Path QaEvidence -Force | Out-Null\n"
            ". ./trusted/scripts/PullRequestClassification.ps1\n"
            "Write-Base64Utf8File -Base64 $env:BODY_BASE64 "
            "-Path 'QaEvidence/pr-body.md'\n"
            "Write-Base64Utf8File -Base64 $env:FILES_BASE64 "
            "-Path 'QaEvidence/changed-files.txt'\n"
        ),
        "shell": "pwsh",
        "env": {
            "BODY_BASE64": "${{ steps.context.outputs.body-base64 }}",
            "FILES_BASE64": "${{ steps.context.outputs.files-base64 }}",
        },
    },
    {
        "name": "Evaluate QA evidence",
        "id": "evidence",
        "run": (
            "./trusted/scripts/New-QaEvidence.ps1 `\n"
            "  -PullRequestNumber $env:PR_NUMBER `\n"
            "  -PullRequestTitle $env:PR_TITLE `\n"
            "  -PullRequestBodyPath QaEvidence/pr-body.md `\n"
            "  -HeadSha $env:HEAD_SHA `\n"
            "  -TrustedKnowledgeRevision $env:TRUSTED_KNOWLEDGE_REVISION `\n"
            "  -TestResultsPath source/TestResults `\n"
            "  -OutputDirectory QaEvidence `\n"
            "  -ChangedFilesPath QaEvidence/changed-files.txt\n"
            "$result = Get-Content QaEvidence/qa-result.json -Raw | ConvertFrom-Json\n"
            '"metadata-digest=$($result.metadataDigest)" >> $env:GITHUB_OUTPUT\n'
        ),
        "shell": "pwsh",
        "env": {
            "PR_NUMBER": "${{ steps.context.outputs.number }}",
            "PR_TITLE": "${{ steps.context.outputs.title }}",
            "HEAD_SHA": "${{ steps.context.outputs.head-sha }}",
            "TRUSTED_KNOWLEDGE_REVISION": (
                "${{ steps.context.outputs.trusted-knowledge-revision }}"
            ),
        },
    },
    {
        "name": "Upload QA evidence",
        "uses": "actions/upload-artifact@v4",
        "if": "always()",
        "with": {
            "name": (
                "qa-evidence-${{ steps.context.outputs.head-sha }}-"
                "${{ steps.evidence.outputs.metadata-digest }}"
            ),
            "path": "QaEvidence\nsource/TestResults\n",
            "if-no-files-found": "error",
            "retention-days": "30",
        },
    },
]

expected_job_if = (
    "github.event_name == 'workflow_dispatch' "
    "|| (github.event.workflow_run.name == 'Hotel code review'\n"
    "  && github.event.workflow_run.event == 'pull_request_target'\n"
    "  && github.event.workflow_run.conclusion == 'success')\n"
    "|| (github.event.workflow_run.name == 'PR validation'\n"
    "  && github.event.workflow_run.event == 'push'\n"
    "  && github.event.workflow_run.head_branch == 'main'\n"
    "  && github.event.workflow_run.conclusion == 'success')"
)

expected_triggers = {
    "workflow_dispatch": {
        "inputs": {
            "pull_request_number": {
                "description": "Open same-repository pull request number",
                "required": "true",
                "type": "string",
            },
            "head_sha": {
                "description": "Exact current 40-character pull request head SHA",
                "required": "true",
                "type": "string",
            },
        }
    },
    "workflow_run": {
        "workflows": ["Hotel code review", "PR validation"],
        "types": ["completed"],
    },
}

expected_permissions = {
    "actions": "read",
    "checks": "read",
    "contents": "read",
    "pull-requests": "read",
}

expected_concurrency = {
    "group": (
        "qa-evidence-${{ github.event_name == 'workflow_dispatch' "
        "&& inputs.head_sha || github.event.workflow_run.id }}"
    ),
    "cancel-in-progress": "false",
}


def reject_yaml_indirection(workflow_text):
    for event in yaml.parse(workflow_text, Loader=StrictLoader):
        if isinstance(event, yaml.events.AliasEvent) or getattr(event, "anchor", None):
            raise ValueError("YAML anchors and aliases are not allowed")


def main():
    workflow_path = pathlib.Path(sys.argv[2])
    workflow_text = workflow_path.read_text(encoding="utf-8")
    reject_yaml_indirection(workflow_text)
    workflow = yaml.load(workflow_text, Loader=StrictLoader)
    if not isinstance(workflow, dict):
        raise ValueError("Workflow must decode to a mapping")
    if set(workflow) != {"name", "on", "permissions", "concurrency", "jobs"}:
        raise ValueError("QA workflow execution surface changed")
    if workflow["name"] != "QA evidence":
        raise ValueError("QA workflow name changed")
    if workflow["on"] != expected_triggers:
        raise ValueError("QA workflow triggers or inputs changed")
    if workflow["permissions"] != expected_permissions:
        raise ValueError("QA workflow permissions changed")
    if workflow["concurrency"] != expected_concurrency:
        raise ValueError("QA workflow concurrency changed")
    jobs = workflow.get("jobs")
    if not isinstance(jobs, dict) or set(jobs) != {"qa"}:
        raise ValueError("Workflow must contain exactly the qa job")
    job = jobs["qa"]
    if not isinstance(job, dict):
        raise ValueError("QA job must decode to a mapping")
    if set(job) != {"name", "if", "runs-on", "steps"}:
        raise ValueError("QA job execution surface changed")
    if job.get("name") != "Independent QA evidence gate":
        raise ValueError("QA job name changed")
    if job.get("if") != expected_job_if:
        raise ValueError("QA job condition changed")
    if job.get("runs-on") != "ubuntu-24.04":
        raise ValueError("QA job runner must be ubuntu-24.04")
    steps = job.get("steps")
    if not isinstance(steps, list):
        raise ValueError("QA job steps must decode to a list")
    actual_steps = [selected_step(step) for step in steps]
    if actual_steps != expected_steps:
        raise ValueError("QA step command and execution surface changed")


if __name__ == "__main__":
    main()
