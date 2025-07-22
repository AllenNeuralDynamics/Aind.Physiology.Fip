import json
import typing as t
from pathlib import Path

import pydantic
from aind_behavior_services.session import AindBehaviorSessionModel
from aind_behavior_services.utils import (
    CustomGenerateJsonSchema,
    bonsai_sgen,
)

from aind_physiology_fip import rig

SCHEMA_ROOT = Path("./src/DataSchemas/")
EXTENSIONS_ROOT = Path("./src/Extensions/")
NAMESPACE_PREFIX = "AindPhysiologyFip"


def main():
    models = [
        rig.AindPhysioFipRig,
        AindBehaviorSessionModel,
    ]

    model = pydantic.RootModel[t.Union[tuple(models)]]
    json_schema = model.model_json_schema(schema_generator=CustomGenerateJsonSchema, mode="serialization")

    for to_remove in ["$schema", "title", "description", "properties", "required", "type", "oneOf"]:
        json_schema.pop(to_remove, None)

    with open(schema_path := SCHEMA_ROOT / "aind-physiology-fip.json", "w", encoding="utf-8") as f:
        literal = json.dumps(json_schema, indent=2)
        f.write(literal)

    bonsai_sgen(
        schema_path=schema_path,
        root_element="Root",
        namespace=NAMESPACE_PREFIX,
        output_path=EXTENSIONS_ROOT / "AindPhysiologyFip.cs",
    )
    # with open(EXTENSIONS_ROOT / "AindPhysiologyFip.cs", "r+", encoding="utf-8") as f:
    #     raw = f.read()
    #     raw = raw.replace("IDictionary", "Dictionary")
    #     f.seek(0)
    #     f.write(raw)
    #     f.truncate()


if __name__ == "__main__":
    main()
