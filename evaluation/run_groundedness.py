import json
import os
from pathlib import Path

from azure.ai.evaluation import GroundednessEvaluator
from azure.identity import DefaultAzureCredential


output_directory = Path("EvaluationResults")
output_directory.mkdir(exist_ok=True)
model_config = {
    "azure_endpoint": os.environ["AZURE_OPENAI_ENDPOINT"],
    "azure_deployment": os.environ["AZURE_OPENAI_DEPLOYMENT"],
    "api_version": "2024-10-21",
}
evaluator = GroundednessEvaluator(
    model_config=model_config,
    credential=DefaultAzureCredential(),
)

results = []
with Path("evaluation/grounding.jsonl").open(encoding="utf-8") as dataset:
    for line in dataset:
        case = json.loads(line)
        score = evaluator(
            query=case["query"],
            context=case["context"],
            response=case["response"],
        )
        results.append({**case, "evaluation": score})

report = {
    "evaluator": "azure.ai.evaluation.GroundednessEvaluator",
    "deployment": model_config["azure_deployment"],
    "cases": results,
}
(output_directory / "groundedness.json").write_text(
    json.dumps(report, indent=2),
    encoding="utf-8",
)

failed = [
    result
    for result in results
    if float(result["evaluation"].get("gpt_groundedness", 0)) < 4
]
if failed:
    raise SystemExit(f"{len(failed)} groundedness cases scored below 4.")
